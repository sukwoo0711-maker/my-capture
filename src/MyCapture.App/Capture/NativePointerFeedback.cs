using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Cached two-pixel guides and small pointer sprites; moving only changes native bounds.</summary>
internal sealed class NativePointerFeedback : IDisposable
{
    private readonly NativeCaptureOverlay _owner;
    private readonly NativeSelectorSprite _horizontal, _vertical;
    private readonly MonitorInfo[] _monitors;
    private readonly Profile[] _profiles;
    private readonly List<NativeSelectorSprite> _sprites = [];
    private readonly IntPtr[] _handles;
    private Int32Rect? _pointer, _anchor;
    private Profile? _pointerProfile, _anchorProfile;
    internal Int32Rect? Horizontal { get; private set; }
    internal Int32Rect? Vertical { get; private set; }
    internal IReadOnlyList<IntPtr> Handles => _handles;
    internal int LiveHandles => _sprites.Count * 2;

    internal NativePointerFeedback(NativeCaptureOverlay owner, IReadOnlyDictionary<string, Brush> palette, IReadOnlyList<MonitorInfo> monitors)
    {
        _owner = owner;
        _monitors = monitors.ToArray();
        try
        {
            _horizontal = Add(CaptureGuideStrip.Get(palette["CaptureGuide.LineShadow"], palette["CaptureGuide.Line"], (int)owner.ScreenBounds.Width, true));
            _vertical = Add(CaptureGuideStrip.Get(palette["CaptureGuide.LineShadow"], palette["CaptureGuide.Line"], (int)owner.ScreenBounds.Height, false));
            double[] scales = _monitors.Select(monitor => monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1).Append(1).Distinct().ToArray();
            _profiles = scales.Select(scale => new Profile(scale, Add(Crosshair(palette, scale, false)), Add(Crosshair(palette, scale, true)))).ToArray();
            _handles = _sprites.SelectMany(sprite => new[] { sprite.Dc, sprite.Bitmap }).ToArray();
        }
        catch { Dispose(); throw; }
    }

    private NativeSelectorSprite Add(BitmapSource bitmap)
    {
        var sprite = new NativeSelectorSprite(bitmap);
        _sprites.Add(sprite);
        return sprite;
    }

    internal void Update(PointD? pointer, PointD? anchor, bool grid, bool overBar)
    {
        Profile? pointerProfile = pointer is { } point ? At(point) : null;
        Profile? anchorProfile = anchor is { } start ? At(start) : null;
        Int32Rect? nextPointer = pointer is { } p && !overBar ? Center(pointerProfile!.Pointer, p) : null;
        Int32Rect? nextAnchor = anchor is { } a ? Center(anchorProfile!.Anchor, a) : null;
        Int32Rect? horizontal = grid && pointer is { } h && !overBar
            ? new Int32Rect(0, (int)Math.Round(h.Y) - 1, _horizontal.Width, 2) : null;
        Int32Rect? vertical = grid && pointer is { } v && !overBar
            ? new Int32Rect((int)Math.Round(v.X) - 1, 0, 2, _vertical.Height) : null;
        if (_pointer == nextPointer && _anchor == nextAnchor && Horizontal == horizontal && Vertical == vertical
            && ReferenceEquals(_pointerProfile, pointerProfile) && ReferenceEquals(_anchorProfile, anchorProfile)) return;
        Invalidate(_pointer); Invalidate(_anchor); Invalidate(Horizontal); Invalidate(Vertical);
        _pointer = nextPointer; _anchor = nextAnchor; Horizontal = horizontal; Vertical = vertical;
        _pointerProfile = pointerProfile; _anchorProfile = anchorProfile;
        Invalidate(_pointer); Invalidate(_anchor); Invalidate(Horizontal); Invalidate(Vertical);
    }

    private static Int32Rect Center(NativeSelectorSprite sprite, PointD point) =>
        new((int)Math.Round(point.X) - sprite.Width / 2, (int)Math.Round(point.Y) - sprite.Height / 2, sprite.Width, sprite.Height);

    private Profile At(PointD local)
    {
        PointD point = local.Offset(_owner.ScreenBounds.Left, _owner.ScreenBounds.Top);
        double scale = 1;
        for (int index = 0; index < _monitors.Length; index++)
        {
            MonitorInfo monitor = _monitors[index];
            if (point.X >= monitor.Bounds.Left && point.X < monitor.Bounds.Right
                && point.Y >= monitor.Bounds.Top && point.Y < monitor.Bounds.Bottom)
            { scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1; break; }
        }
        for (int index = 0; index < _profiles.Length; index++) if (_profiles[index].Scale == scale) return _profiles[index];
        return _profiles[0];
    }

    private void Invalidate(Int32Rect? bounds)
    {
        if (bounds is not { } region || _owner.Handle == IntPtr.Zero) return;
        Int32Rect clipped = NativeSelectorDirtyRegions.Clip(region, (int)_owner.ScreenBounds.Width, (int)_owner.ScreenBounds.Height);
        if (clipped.Width == 0 || clipped.Height == 0) return;
        var rectangle = new NativeSelectorInterop.Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height);
        _ = NativeSelectorInterop.InvalidateRect(_owner.Handle, ref rectangle, false);
    }

    internal void Paint(IntPtr dc)
    {
        if (Horizontal is { } horizontal) _horizontal.Draw(dc, horizontal.X, horizontal.Y);
        if (Vertical is { } vertical) _vertical.Draw(dc, vertical.X, vertical.Y);
        if (_anchor is { } anchor) _anchorProfile!.Anchor.Draw(dc, anchor.X, anchor.Y);
        if (_pointer is { } pointer) _pointerProfile!.Pointer.Draw(dc, pointer.X, pointer.Y);
    }

    private static BitmapSource Crosshair(IReadOnlyDictionary<string, Brush> palette, double scale, bool anchor)
    {
        int middle = (int)Math.Ceiling(11 * scale) + 2, side = middle * 2;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Draw(new Pen(palette["CaptureGuide.CrosshairShadow"], 3));
            Draw(new Pen(palette[anchor ? "CaptureGuide.StartPoint" : "CaptureGuide.Crosshair"], 1));
            void Draw(Pen pen)
            {
                dc.DrawLine(pen, new Point(middle - 11 * scale, middle), new Point(middle + 11 * scale, middle));
                dc.DrawLine(pen, new Point(middle, middle - 11 * scale), new Point(middle, middle + 11 * scale));
                if (anchor) dc.DrawEllipse(null, pen, new Point(middle, middle), 5 * scale, 5 * scale);
            }
        }
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose()
    {
        foreach (NativeSelectorSprite sprite in _sprites) sprite.Dispose();
        _sprites.Clear();
    }

    private sealed record Profile(double Scale, NativeSelectorSprite Pointer, NativeSelectorSprite Anchor);
}
