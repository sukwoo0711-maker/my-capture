using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>
/// The start-point chip: one bounded stamp per drag, outside the selection on the side the drag
/// leaves behind. Moving the selection reuses its drawing and pixels; only crossing the start
/// point, which turns the drag the other way, places it again.
/// </summary>
internal sealed class NativeSelectorAnchor(IReadOnlyDictionary<string, Brush> palette)
{
    /// <summary>The chip's corner radius, DIPs: the small DWM radius the read-out window gets.</summary>
    internal const double Radius = 4;

    /// <summary>The chip's height, DIPs: the read-out's, so the two sit level beside each other.</summary>
    internal const double Height = NativeSelectorChrome.ReadoutHeight;

    // AnchorChipBounds pads the label by five DIPs above and below.
    private const double LabelPaddingY = 5;

    private readonly Typeface _typeface = new(
        Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private PointD? _anchor;
    private bool _right = true, _down = true, _rounded = true;
    internal DrawingGroup? Drawing { get; private set; }
    internal Rect? Bounds { get; private set; }
    internal NativeSelectorStamp? Stamp { get; private set; }
    internal double Scale { get; private set; } = 1;
    internal int BuildCount { get; private set; }

    /// <summary>
    /// Places the chip for a drag that started at <paramref name="anchor"/> and is now at
    /// <paramref name="pointer"/> (both selector pixels). <paramref name="rounded"/> gives it the
    /// read-out's rounded corners; otherwise it is square like a read-out DWM does not round.
    /// True when the stamp changed.
    /// </summary>
    internal bool Update(PointD? anchor, RectD screen, double scale, PointD? pointer = null, bool rounded = true)
    {
        bool right = true, down = true;
        if (anchor is { } origin && pointer is { } toward)
        {
            right = toward.X >= origin.X;
            down = toward.Y >= origin.Y;
        }
        if (_anchor == anchor && (anchor is null || (Scale == scale && _right == right && _down == down && _rounded == rounded)))
            return false;
        _anchor = anchor;
        _right = right;
        _down = down;
        _rounded = rounded;
        if (anchor is not { } point) { Drawing = null; Bounds = null; Stamp = null; return true; }
        Scale = scale;
        double pixel = 1 / scale;
        var text = new FormattedText(UiText.Get("CaptureSelectionStart"), UiText.Culture,
            FlowDirection.LeftToRight, _typeface, TypeScale.Caption, palette["CaptureGuide.Text"], scale);
        var start = new Point(point.X / scale, point.Y / scale);
        Rect bounds = CaptureOverlayView.AnchorChipBounds(start, new Size(text.Width, Math.Max(text.Height, Height - 2 * LabelPaddingY)),
            new Size(screen.Width / scale, screen.Height / scale),
            new Point(start.X + (right ? 1 : -1), start.Y + (down ? 1 : -1)), CaptureFrameStyle.Outset * pixel);
        // Whole physical pixels, so the one-pixel edge is crisp.
        bounds = new Rect(new Point(Snap(bounds.Left, scale), Snap(bounds.Top, scale)),
            new Point(Snap(bounds.Right, scale), Snap(bounds.Bottom, scale)));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // The edge runs along the inside of the chip; its outer silhouette has the chip's radius.
            var edge = new Rect(bounds.X + pixel / 2, bounds.Y + pixel / 2, bounds.Width - pixel, bounds.Height - pixel);
            double radius = rounded ? Radius - pixel / 2 : 0;
            dc.DrawRoundedRectangle(palette["CaptureGuide.ChipSurface"], new Pen(palette["CaptureGuide.ChipBorder"], pixel),
                edge, radius, radius);
            dc.DrawText(text, new Point(bounds.X + (bounds.Width - text.Width) / 2,
                bounds.Y + Snap((bounds.Height - text.Height) / 2, scale)));
        }
        Drawing = visual.Drawing;
        Bounds = bounds;
        int left = (int)Math.Floor(bounds.Left * scale - 1), top = (int)Math.Floor(bounds.Top * scale - 1);
        int width = (int)Math.Ceiling(bounds.Right * scale + 1) - left;
        int height = (int)Math.Ceiling(bounds.Bottom * scale + 1) - top;
        var raster = new DrawingVisual();
        using (DrawingContext dc = raster.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-left / scale, -top / scale));
            dc.DrawDrawing(Drawing);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(raster);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Stamp = new NativeSelectorStamp(new Int32Rect(left, top, width, height), pixels);
        BuildCount++;
        return true;
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
