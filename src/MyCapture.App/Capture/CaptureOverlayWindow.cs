using System.Windows;
using System.Windows.Interop;
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

    internal CaptureOverlayWindow(
        FrozenFrame frame,
        bool abortOnFocusLoss,
        bool showMagnifier = true)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _abortOnFocusLoss = abortOnFocusLoss;
        _view = new CaptureOverlayView(frame, showMagnifier);

        ConfigureWindowChrome(frame);
        Content = _view;

        _view.SelectionConfirmed += OnSelectionConfirmed;
        _view.CancelRequested += OnCancelRequested;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
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
        Left = 0;
        Top = 0;
        Width = Math.Max(1, frame.PixelWidth / frame.DpiScale);
        Height = Math.Max(1, frame.PixelHeight / frame.DpiScale);
    }

    /// <summary>Raised after a valid drag is cropped and the overlay has been hidden.</summary>
    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;

    internal event EventHandler? SelectionCancelled;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        PhysicalWindowPositioner.PlaceTopmost(hwnd, _frame.ScreenBounds);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _view.Focus();
        _ = Activate();
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
        bool recordForRepeat = true)
    {
        Frame = frame;
        BitmapRegion = bitmapRegion;
        SelectedBitmap = selectedBitmap;
        SourceTitle = sourceTitle ?? string.Empty;
        RecordForRepeat = recordForRepeat;
    }

    internal FrozenFrame Frame { get; }

    internal RectD BitmapRegion { get; }

    /// <summary>True only for an explicitly completed manual-region drag.</summary>
    internal bool RecordForRepeat { get; }

    internal System.Windows.Media.Imaging.BitmapSource SelectedBitmap { get; }

    internal string SourceTitle { get; }
}
