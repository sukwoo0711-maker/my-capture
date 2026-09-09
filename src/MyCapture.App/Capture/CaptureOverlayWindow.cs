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
    private FrozenFrame? _frame;
    private readonly RectD _screenBounds;
    private readonly CaptureOverlayView _view;
    private readonly bool _abortOnFocusLoss;
    private bool _completed;
    private bool _placingPhysicalBounds;
    private RectD? _pendingSelection;

    internal CaptureOverlayWindow(
        FrozenFrame frame,
        bool abortOnFocusLoss,
        bool showMagnifier = true)
        : this(frame.ScreenBounds, abortOnFocusLoss, showMagnifier, frame)
    {
    }

    internal CaptureOverlayWindow(
        RectD screenBounds,
        bool abortOnFocusLoss,
        bool showMagnifier,
        FrozenFrame? frame = null)
    {
        _screenBounds = screenBounds.ToPixelBounds();
        if (_screenBounds.IsEmpty)
        {
            throw new ArgumentException("A non-empty virtual-desktop rectangle is required.", nameof(screenBounds));
        }

        _frame = frame;
        _abortOnFocusLoss = abortOnFocusLoss;
        _view = frame is null
            ? new CaptureOverlayView(_screenBounds, frame: null, showMagnifier)
            : new CaptureOverlayView(frame, showMagnifier);

        // Frozen pixels and selection geometry must appear immediately and exactly; a reveal
        // transform here would expose the live desktop for a frame and make edge selection feel
        // imprecise. All normal application windows still use the shared entrance motion.
        FluidMotion.SetWindowEntrance(this, false);
        ConfigureWindowChrome(_screenBounds);
        Content = _view;

        _view.SelectionConfirmed += OnSelectionConfirmed;
        _view.CancelRequested += OnCancelRequested;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
        Deactivated += OnDeactivated;
    }

    private void ConfigureWindowChrome(RectD screenBounds)
    {
        Title = UiText.Get("Text_3816B940D53C");
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
            new PointD(screenBounds.Left, screenBounds.Top));
        double scale = anchor.ScaleFactor > 0 ? anchor.ScaleFactor : 1.0;
        Left = screenBounds.Left / scale;
        Top = screenBounds.Top / scale;
        Width = Math.Max(1, screenBounds.Width / scale);
        Height = Math.Max(1, screenBounds.Height / scale);
    }

    internal void AttachFrame(FrozenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
        _view.AttachFrame(frame);
        if (IsVisible)
        {
            PlacePhysicalBounds();
        }

        if (_pendingSelection is RectD pending)
        {
            _pendingSelection = null;
            CompleteSelection(pending);
        }
    }

    /// <summary>Raised after a valid drag is cropped and the overlay has been hidden.</summary>
    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;

    internal event EventHandler? SelectionCancelled;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // EnsureHandle may create this HWND solely to apply recording exclusion.
        // PlaceTopmost uses SWP_SHOWWINDOW, so never call it while still hidden.
        // ContentRendered reasserts physical placement after the authorized Show.
        if (IsVisible) PlacePhysicalBounds();
    }

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
                PhysicalWindowPositioner.PlaceTopmost(hwnd, _frame?.ScreenBounds ?? _screenBounds);
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

        if (_frame is null)
        {
            _pendingSelection = e.BitmapRegion;
            return;
        }

        CompleteSelection(e.BitmapRegion);
    }

    private void CompleteSelection(RectD bitmapRegion)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        FrozenFrame frame = _frame ?? throw new InvalidOperationException("A frozen capture frame is required.");
        System.Windows.Media.Imaging.BitmapSource crop = ScreenCaptureEngine.Crop(frame, bitmapRegion);

        // Remove the dimmed overlay before the normal editor window is created. The original
        // frozen frame and cropped physical pixels are retained; the desktop is never recaptured.
        Hide();
        try
        {
            SelectionCompleted?.Invoke(
                this,
                new CaptureSelectionCompletedEventArgs(frame, bitmapRegion, crop));
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
