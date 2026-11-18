using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>The pointer the native selector draws for its mode.</summary>
internal enum NativePointerStyle
{
    /// <summary>The crosshair of the region, window, freeform and text modes.</summary>
    Crosshair,

    /// <summary>The colour mode's eyedropper, its tip on the sampled pixel.</summary>
    Eyedropper,

    /// <summary>Nothing drawn: the system cursor shows the mode (scroll).</summary>
    None,
}

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
            _profiles = scales.Select(scale => new Profile(scale, Add(Crosshair(palette, scale, false)), Add(Crosshair(palette, scale, true)),
                Add(Eyedropper(palette, scale, out Int32Rect tip)), tip)).ToArray();
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

    /// <summary>The pointer style last drawn.</summary>
    internal NativePointerStyle Style { get; private set; }

    /// <summary>Where the pointer sprite was last placed, in selector pixels.</summary>
    internal Int32Rect? PointerBounds => _pointer;

    internal void Update(PointD? pointer, PointD? anchor, bool grid, bool overBar,
        NativePointerStyle style = NativePointerStyle.Crosshair)
    {
        Profile? pointerProfile = pointer is { } point ? At(point) : null;
        Profile? anchorProfile = anchor is { } start ? At(start) : null;
        Int32Rect? nextPointer = pointer is { } p && !overBar ? style switch
        {
            NativePointerStyle.Crosshair => Center(pointerProfile!.Pointer, p),
            // The tip pixel of the sprite lands on the pointer's pixel.
            NativePointerStyle.Eyedropper => new Int32Rect((int)Math.Floor(p.X) - pointerProfile!.Tip.X,
                (int)Math.Floor(p.Y) - pointerProfile.Tip.Y, pointerProfile.Eyedropper.Width, pointerProfile.Eyedropper.Height),
            _ => null,
        } : null;
        Int32Rect? nextAnchor = anchor is { } a ? Center(anchorProfile!.Anchor, a) : null;
        Int32Rect? horizontal = grid && pointer is { } h && !overBar
            ? new Int32Rect(0, (int)Math.Round(h.Y) - 1, _horizontal.Width, 2) : null;
        Int32Rect? vertical = grid && pointer is { } v && !overBar
            ? new Int32Rect((int)Math.Round(v.X) - 1, 0, 2, _vertical.Height) : null;
        if (_pointer == nextPointer && _anchor == nextAnchor && Horizontal == horizontal && Vertical == vertical
            && ReferenceEquals(_pointerProfile, pointerProfile) && ReferenceEquals(_anchorProfile, anchorProfile)
            && Style == style) return;
        Style = style;
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
        if (_pointer is { } pointer)
            (Style == NativePointerStyle.Eyedropper ? _pointerProfile!.Eyedropper : _pointerProfile!.Pointer).Draw(dc, pointer.X, pointer.Y);
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

    /// <summary>
    /// An eyedropper on the bar's colour glyph, outlined like the crosshair. <paramref name="tip"/> is the
    /// pixel of its tip inside the sprite.
    /// </summary>
    private static BitmapSource Eyedropper(IReadOnlyDictionary<string, Brush> palette, double scale, out Int32Rect tip)
    {
        // The bar's 20-unit colour glyph, its tip at (4.2, 16.8) moved to the origin and drawn 1.2 times larger.
        Geometry glyph = Geometry.Parse(
            "M12.4,3.6 A2.1,2.1 0 0 1 16.4,7.6 L14.6,9.4 L10.6,5.4 Z M11.6,8.4 L5.2,14.8 L4.2,16.8 L6.2,15.8 L12.6,9.4 M9.6,4.4 L15.6,10.4");
        double unit = 1.2 * scale, pad = Math.Ceiling(2 * scale) + 1;
        int width = (int)Math.Ceiling((17.6 - 4.2) * unit + 2 * pad) + 2, height = (int)Math.Ceiling((16.8 - 1.4) * unit + 2 * pad) + 2;
        int tipX = (int)pad, tipY = height - (int)pad - 1;
        var transform = new TransformGroup();
        transform.Children.Add(new TranslateTransform(-4.2, -16.8));
        transform.Children.Add(new ScaleTransform(unit, unit));
        transform.Children.Add(new TranslateTransform(tipX + 0.5, tipY + 0.5));
        Geometry placed = glyph.Clone();
        placed.Transform = transform;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var shadow = new Pen(palette["CaptureGuide.CrosshairShadow"], 3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var ink = new Pen(palette["CaptureGuide.Crosshair"], 1.25) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, shadow, placed);
            dc.DrawGeometry(null, ink, placed);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        tip = new Int32Rect(tipX, tipY, 1, 1);
        return bitmap;
    }

    public void Dispose()
    {
        foreach (NativeSelectorSprite sprite in _sprites) sprite.Dispose();
        _sprites.Clear();
    }

    private sealed record Profile(double Scale, NativeSelectorSprite Pointer, NativeSelectorSprite Anchor,
        NativeSelectorSprite Eyedropper, Int32Rect Tip);
}
