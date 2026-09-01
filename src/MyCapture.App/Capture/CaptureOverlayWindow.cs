using System.Windows;
using System.Windows.Interop;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// Borderless, selection-only frozen-frame overlay. It never hosts editing UI.
/// </summary>
internal sealed class CaptureOverlayWindow : Window
{
    private readonly FrozenFrame _frame;
    private readonly CaptureOverlayView _view;
    private readonly bool _abortOnFocusLoss;
    private bool _completed;
    private bool _placingPhysicalBounds;

    internal CaptureOverlayWindow(
        FrozenFrame frame,
        bool abortOnFocusLoss,
        bool showMagnifier = true)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _abortOnFocusLoss = abortOnFocusLoss;
        _view = new CaptureOverlayView(frame, showMagnifier);

        // Frozen pixels and selection geometry must appear immediately and exactly; a reveal
        // transform here would expose the live desktop for a frame and make edge selection feel
        // imprecise. All normal application windows still use the shared entrance motion.
        FluidMotion.SetWindowEntrance(this, false);
        ConfigureWindowChrome(frame);
        Content = _view;

        _view.SelectionConfirmed += OnSelectionConfirmed;
        _view.CancelRequested += OnCancelRequested;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
        Deactivated += OnDeactivated;
    }

    private void ConfigureWindowChrome(FrozenFrame frame)
    {
        Title = "MyCapture — 자유 영역 선택";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Black;
        // Anchor HWND creation on the virtual desktop's top-left display. SourceInitialized then
        // applies the exact physical-pixel rectangle with SetWindowPos; this initial placement
        // prevents WPF from choosing the primary monitor's DPI for a negative-origin desktop.
        MonitorInfo anchor = MonitorEnumerator.GetFromPoint(
            new PointD(frame.ScreenBounds.Left, frame.ScreenBounds.Top));
        double scale = anchor.ScaleFactor > 0 ? anchor.ScaleFactor : 1.0;
        Left = frame.ScreenBounds.Left / scale;
        Top = frame.ScreenBounds.Top / scale;
        Width = Math.Max(1, frame.PixelWidth / scale);
        Height = Math.Max(1, frame.PixelHeight / scale);
    }

    /// <summary>Raised after a valid drag is cropped and the overlay has been hidden.</summary>
    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;

    internal event EventHandler? SelectionCancelled;

    private void OnSourceInitialized(object? sender, EventArgs e)
        => PlacePhysicalBounds();

    private void PlacePhysicalBounds()
    {
        if (_placingPhysicalBounds)
        {
            return;
        }

        _placingPhysicalBounds = true;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                PhysicalWindowPositioner.PlaceTopmost(hwnd, _frame.ScreenBounds);
            }
        }
        finally
        {
            _placingPhysicalBounds = false;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // WPF can perform one final DPI/layout adjustment after SourceInitialized. Reassert the
        // virtual-desktop physical rectangle before accepting input so mixed-DPI edges stay exact.
        PlacePhysicalBounds();
        _view.Focus();
        _ = Activate();
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (!_placingPhysicalBounds)
        {
            _ = Dispatcher.BeginInvoke(new Action(PlacePhysicalBounds));
        }
    }

    private void OnSelectionConfirmed(object? sender, RegionSelectionEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        System.Windows.Media.Imaging.BitmapSource crop = ScreenCaptureEngine.Crop(_frame, e.BitmapRegion);

        // Remove the dimmed overlay before the normal editor window is created. The original
        // frozen frame and cropped physical pixels are retained; the desktop is never recaptured.
        Hide();
        try
        {
            SelectionCompleted?.Invoke(
                this,
                new CaptureSelectionCompletedEventArgs(_frame, e.BitmapRegion, crop));
        }
        finally
        {
            Close();
        }
    }

    private void OnCancelRequested(object? sender, EventArgs e) => Cancel();

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_abortOnFocusLoss && IsVisible && !_completed)
        {
            Cancel();
        }
    }

    private void Cancel()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _view.SelectionConfirmed -= OnSelectionConfirmed;
        _view.CancelRequested -= OnCancelRequested;
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnContentRendered;
        Deactivated -= OnDeactivated;
        DpiChanged -= OnDpiChanged;
        base.OnClosed(e);
    }
}

internal sealed class CaptureSelectionCompletedEventArgs : EventArgs
{
    internal CaptureSelectionCompletedEventArgs(
        FrozenFrame frame,
        RectD bitmapRegion,
        System.Windows.Media.Imaging.BitmapSource selectedBitmap,
        string sourceTitle = "",
        bool recordForRepeat = true,
        bool copyToClipboardImmediately = true)
    {
        Frame = frame;
        BitmapRegion = bitmapRegion;
        SelectedBitmap = selectedBitmap;
        SourceTitle = sourceTitle ?? string.Empty;
        RecordForRepeat = recordForRepeat;
        CopyToClipboardImmediately = copyToClipboardImmediately;
    }

    internal FrozenFrame Frame { get; }

    internal RectD BitmapRegion { get; }

    /// <summary>True only for an explicitly completed manual-region drag.</summary>
    internal bool RecordForRepeat { get; }

    /// <summary>
    /// True for the explicit free-region selector. This is intentionally independent of repeat
    /// history so future history-policy changes cannot silently disable Ctrl+Shift+C copying.
    /// </summary>
    internal bool CopyToClipboardImmediately { get; }

    internal System.Windows.Media.Imaging.BitmapSource SelectedBitmap { get; }

    internal string SourceTitle { get; }
}
