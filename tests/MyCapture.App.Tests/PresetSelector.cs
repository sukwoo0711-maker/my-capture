using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Routes preset gestures through real selector windows and their original crop events.</summary>
internal sealed class PresetSelector : IDisposable
{
    private readonly CaptureOverlayWindow? _window;
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly NativeSelectorChrome? _chrome;
    private readonly FrozenFrame _frame;
    private readonly Func<PointD> _pointer;

    internal PresetSelector(bool native, FrozenFrame frame, Func<PointD> pointer, double scale)
    {
        _frame = frame;
        _pointer = pointer;
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, (uint)Math.Round(96 * scale), true)];
        if (native)
        {
            _native = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
            {
                ReadCursor = pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            };
            _native.AttachFrame(frame);
            _native.SelectionCompleted += (_, e) => SelectionCompleted?.Invoke(this, e);
            _chrome = new NativeSelectorChrome(_native, false, false);
        }
        else
        {
            _window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false);
            _wpf = Assert.IsType<CaptureOverlayView>(_window.Content);
            _wpf.ReadCursor = pointer;
            _wpf.PlaceCursor = _ => false;
            _wpf.ReadMonitors = () => monitors;
            _wpf.SimulatedPointerInside = true;
            _window.SelectionCompleted += (_, e) => SelectionCompleted?.Invoke(this, e);
        }
    }

    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;
    internal RectD? Selection => _native is not null ? _native.Selection : _wpf!.Selection;
    internal SelectionRatio Ratio => _native?.Ratio ?? _wpf!.Ratio;
    internal System.Drawing.Size? PresetSize
    {
        set { if (_native is not null) _native.PresetSize = value; else _wpf!.PresetSize = value; }
    }
    internal void SelectRatio(SelectionRatio ratio)
    {
        if (_native is not null) _native.SelectRatio(ratio); else _wpf!.SelectRatio(ratio);
    }
    internal void Detect(IReadOnlyList<WindowCandidate> windows)
    {
        if (_native is not null) { _native.SourceWindows = windows; _native.DetectWindows = true; }
        else { _window!.SourceWindows = windows; _wpf!.WindowCandidates = windows; _wpf.DetectWindows = true; }
    }
    internal void InitializePointer()
    {
        if (_native is not null) { _native.UpdatePointer(false, false); _native.RefreshHover(); }
        else _wpf!.InitializePointer();
    }
    private PointD LocalPointer() => _pointer().Offset(-_frame.ScreenBounds.Left, -_frame.ScreenBounds.Top);
    internal void Press()
    {
        if (_native is not null) _native.BeginSelection(LocalPointer()); else _wpf!.SimulatePointerDown();
    }
    internal void Move()
    {
        if (_native is not null) { _native.UpdatePointer(false, false); _native.MoveSelection(LocalPointer()); }
        else _wpf!.SimulatePointerMove();
    }
    internal void Release()
    {
        if (_native is not null) _native.EndSelection(LocalPointer()); else _wpf!.SimulatePointerUp();
    }
    internal void Render(double scale)
    {
        if (_native is not null)
        {
            _chrome!.Update(_native.EffectivePointer, _native.SelectionRectangle);
            _ = NativeSelectorTestRenderer.Render(_native, _chrome);
            return;
        }
        _wpf!.Measure(new Size(_frame.PixelWidth / scale, _frame.PixelHeight / scale));
        _wpf.Arrange(new Rect(0, 0, _frame.PixelWidth / scale, _frame.PixelHeight / scale));
        _wpf.UpdateLayout();
        var image = new RenderTargetBitmap(_frame.PixelWidth, _frame.PixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(_wpf);
        _wpf.FlushFeedbackForTest();
        image.Render(_wpf);
    }
    public void Dispose() { _chrome?.Dispose(); _native?.Close(); _window?.Close(); }
}
