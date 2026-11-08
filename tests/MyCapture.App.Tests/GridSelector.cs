using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Tests;

/// <summary>Uses each renderer's real guides, pointer artwork, drag state, and screenshot completion.</summary>
internal sealed class GridSelector
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly NativeSelectorChrome? _chrome;
    private PointD _cursor;

    internal GridSelector(bool native, FrozenFrame frame, double scale = 1, bool? showSelectionGrid = null)
    {
        if (!native)
        {
            _wpf = showSelectionGrid is { } enabled
                ? new CaptureOverlayView(frame, showMagnifier: false, showSelectionGrid: enabled)
                : new CaptureOverlayView(frame, showMagnifier: false);
            _wpf.PlaceCursor = _ => false;
            return;
        }
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, (uint)Math.Round(96 * scale), true)];
        _native = showSelectionGrid is { } show
            ? new NativeCaptureOverlay(frame.ScreenBounds, false, false, show)
            : new NativeCaptureOverlay(frame.ScreenBounds, false, false);
        _native.ReadCursor = () => _cursor;
        _native.PlaceCursor = _ => false;
        _native.ReadMonitors = () => monitors;
        _native.AttachFrame(frame);
        _native.SelectionCompleted += (_, e) => SelectionCompleted?.Invoke(this, e);
        _chrome = new NativeSelectorChrome(_native, false, false);
    }

    internal GridSelector(CaptureOverlayView view)
    {
        _wpf = view;
        _wpf.PlaceCursor = _ => false;
    }

    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;
    internal RectD? Selection => _native is not null ? _native.Selection : _wpf!.Selection;
    internal IReadOnlyDictionary<string, Brush> GuidePalette => _native?.GuidePalette ?? _wpf!.GuidePalette;

    internal void SetCursor(PointD cursor)
    {
        _cursor = cursor;
        if (_native is not null) _native.UpdatePointer(false, false);
        else
        {
            _wpf!.ReadCursor = () => _cursor;
            _wpf.InitializePointer();
        }
    }

    internal void InvokeInput(string method, InputEventArgs args)
    {
        if (_wpf is not null)
            typeof(CaptureOverlayView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_wpf, [args]);
        else if (method == "OnMouseLeftButtonDown") _native!.BeginSelection(_cursor);
        else if (method == "OnMouseMove") _native!.MoveSelection(_cursor);
        else throw new ArgumentOutOfRangeException(nameof(method));
    }

    internal void StopDragging()
    {
        if (_wpf is not null)
        {
            FieldInfo interaction = typeof(CaptureOverlayView).GetField("_interaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            interaction.SetValue(_wpf, Enum.Parse(interaction.FieldType, "None"));
        }
        else typeof(NativeCaptureOverlay).GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_native, false);
    }

    internal void SetSelection(RectD selection)
    {
        if (_wpf is not null)
            typeof(CaptureOverlayView).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_wpf, selection);
        else typeof(NativeCaptureOverlay).GetMethod("SetSelection", BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(RectD)])!.Invoke(_native, [selection]);
    }

    internal RenderTargetBitmap Render(double scale = 1)
    {
        if (_wpf is not null)
        {
            _wpf.InvalidateVisual();
            _wpf.Measure(new Size(900 / scale, 600 / scale));
            _wpf.Arrange(new Rect(0, 0, 900 / scale, 600 / scale));
            _wpf.UpdateLayout();
            var image = new RenderTargetBitmap(900, 600, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(_wpf);
            return image;
        }
        _native!.FlushFeedbackForTest();
        _chrome!.Update(_native.EffectivePointer, _native.DisplayedRegionForTest);
        return NativeSelectorTestRenderer.Render(_native, _chrome);
    }

    internal void CompleteSelection(RectD selection) => _native!.CompleteSelection(selection);
    internal void ReleaseResources() { _chrome?.Dispose(); _native?.Close(); _wpf?.ReleaseResources(); }
}
