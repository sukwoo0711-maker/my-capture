using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Tests;

/// <summary>Runs the pointer contract against each selector with an injected physical cursor.</summary>
internal sealed class PrecisionSelector : IDisposable
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;

    internal PrecisionSelector(bool native, RectD bounds)
    {
        if (native) _native = new NativeCaptureOverlay(bounds, false, false) { PlaceCursor = _ => false };
        else _wpf = new CaptureOverlayView(bounds, showMagnifier: false);
    }

    internal Func<PointD> ReadCursor
    {
        set { if (_native is { } native) native.ReadCursor = value; else _wpf!.ReadCursor = value; }
    }

    internal Func<PointD, bool> PlaceCursor
    {
        set { if (_native is { } native) native.PlaceCursor = value; else _wpf!.PlaceCursor = value; }
    }

    internal PointD UpdatePointer(bool precision, bool active) =>
        _native?.UpdatePointer(precision, active) ?? _wpf!.UpdatePointer(precision, active);

    internal void RaiseEvent(KeyEventArgs key)
    {
        if (_native is { } native) key.Handled = native.HandleKey(key.Key, ModifierKeys.None);
        else _wpf!.RaiseEvent(key);
    }

    public void Dispose() { _native?.Close(); _wpf?.ReleaseResources(); }
}
