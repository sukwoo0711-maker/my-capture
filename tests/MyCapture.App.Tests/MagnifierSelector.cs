using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Tests;

/// <summary>Uses each renderer's actual magnifier drawing for the shared footer contract.</summary>
internal sealed class MagnifierSelector : IDisposable
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly Func<CaptureOverlayView, double, RenderTargetBitmap> _renderWpf;
    private readonly RectD _bounds;
    private readonly double _scale;
    private NativeSelectorChrome? _chrome;
    private Func<PointD> _pointer = static () => new PointD(0, 0);

    internal MagnifierSelector(bool native, FrozenFrame frame, double scale,
        Func<CaptureOverlayView, double, RenderTargetBitmap> renderWpf)
    {
        _renderWpf = renderWpf;
        _bounds = frame.ScreenBounds;
        _scale = scale;
        if (native)
        {
            MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds,
                (uint)Math.Round(96 * scale), true)];
            _native = new NativeCaptureOverlay(frame.ScreenBounds, false, true)
            {
                ReadMonitors = () => monitors,
                PlaceCursor = _ => false,
            };
            _native.AttachFrame(frame);
        }
        else _wpf = new CaptureOverlayView(frame, showSelectionGrid: false) { PlaceCursor = _ => false };
    }

    internal Func<PointD> ReadCursor
    {
        set
        {
            _pointer = value;
            if (_native is { } native) native.ReadCursor = value; else _wpf!.ReadCursor = value;
        }
    }

    internal void SetMouseOver(bool value)
    {
        if (_wpf is not null) _wpf.SimulatedPointerInside = value;
    }

    internal bool? SimulatedPointerInside
    {
        set { if (_wpf is not null) _wpf.SimulatedPointerInside = value; }
    }

    internal void InitializePointer()
    {
        if (_native is { } native) native.UpdatePointer(false, false); else _wpf!.InitializePointer();
    }

    internal void Render(double scale)
    {
        if (_wpf is not null) _renderWpf(_wpf, scale);
        else
        {
            _chrome ??= new NativeSelectorChrome(_native!, true, false);
            _chrome.Update(_pointer(), Int32Rect.Empty);
        }
    }

    internal void FlushFeedbackForTest()
    {
        if (_wpf is not null) _wpf.FlushFeedbackForTest();
        else
        {
            Render(_scale);
            _ = _chrome!.MagnifierForTest!.DrawingForTest;
        }
    }

    internal RenderTargetBitmap RenderFrame(double scale)
    {
        if (_wpf is not null) return _renderWpf(_wpf, scale);
        Render(scale);
        return NativeSelectorTestRenderer.Render(_native!, _chrome!);
    }

    internal Rect? MagnifierImageBoundsForTest
    {
        get
        {
            if (_wpf is not null) return _wpf.MagnifierImageBoundsForTest;
            if (_chrome is null || _chrome.MagnifierImageBoundsForTest.IsEmpty) return null;
            RectD image = _chrome.MagnifierImageBoundsForTest;
            return new Rect((image.Left - _bounds.Left) / _scale, (image.Top - _bounds.Top) / _scale,
                image.Width / _scale, image.Height / _scale);
        }
    }

    internal Drawing? MagnifierDrawing => _native is not null
        ? _chrome!.MagnifierForTest!.DrawingForTest
        : ((DrawingVisual)typeof(CaptureOverlayView).GetField("_magnifierVisual",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_wpf)!).Drawing;

    internal int MagnifierGridFillCount => _native is not null
        ? _chrome!.MagnifierForTest!.GridFillCount
        : _wpf!.MagnifierGridFillCount;

    internal void ReleaseResources() => Dispose();
    public void Dispose() { _chrome?.Dispose(); _native?.Close(); _wpf?.ReleaseResources(); }
}
