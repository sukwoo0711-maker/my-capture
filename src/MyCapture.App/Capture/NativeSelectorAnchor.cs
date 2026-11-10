using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>One bounded stamp per drag. Moving the selection reuses its drawing and pixels.</summary>
internal sealed class NativeSelectorAnchor(IReadOnlyDictionary<string, Brush> palette)
{
    private readonly Typeface _typeface = new(
        Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private PointD? _anchor;
    internal DrawingGroup? Drawing { get; private set; }
    internal Rect? Bounds { get; private set; }
    internal NativeSelectorStamp? Stamp { get; private set; }
    internal double Scale { get; private set; } = 1;
    internal int BuildCount { get; private set; }

    internal bool Update(PointD? anchor, RectD screen, double scale)
    {
        if (_anchor == anchor && (anchor is null || Scale == scale)) return false;
        _anchor = anchor;
        if (anchor is not { } point) { Drawing = null; Bounds = null; Stamp = null; return true; }
        Scale = scale;
        var text = new FormattedText(UiText.Get("CaptureSelectionStart"), UiText.Culture,
            FlowDirection.LeftToRight, _typeface, TypeScale.Caption, palette["CaptureGuide.Text"], scale);
        Rect bounds = CaptureOverlayView.AnchorChipBounds(new Point(point.X / scale, point.Y / scale),
            new Size(text.Width, text.Height), new Size(screen.Width / scale, screen.Height / scale));
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(palette["CaptureGuide.ChipSurface"],
                new Pen(palette["CaptureGuide.ChipBorder"], 1 / scale), bounds, 7, 7);
            dc.DrawText(text, new Point(bounds.X + 9, bounds.Y + 5));
        }
        Drawing = visual.Drawing;
        Bounds = bounds;
        // Include the half-pixel border coverage; retain the nominal drawing coordinates for parity.
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
}
