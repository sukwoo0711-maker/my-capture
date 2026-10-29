using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal sealed record PinOverlayBlock(string Text, OcrRect Bounds, IReadOnlyList<OcrLine> Lines);
internal sealed record PinOverlayTranslation(PinOverlayBlock Block, string? Text);
internal readonly record struct PinOverlayBackground(Color Color, double Variance)
{
    internal bool NeedsPlate => Variance > 18 * 18;
}
internal sealed record PinOverlayFit(OcrRect Bounds, double FontSize, string Text, bool Grew);
internal sealed record PinOverlayComposition(BitmapSource Image, bool NeedsReview, int RenderedBlocks);

/// <summary>Native-pixel layout and colour sampling, followed by one window-free WPF render.</summary>
internal static class PinTranslationOverlay
{
    internal const double MinimumFontSize = 9;
    private const double Padding = 2;

    internal static bool CanOverlay(OcrResult result)
    {
        double angle = result.TextAngle ?? 0;
        if (!double.IsFinite(angle) || Math.Abs(((angle + 180) % 360 + 360) % 360 - 180) > 2) return false;
        return result.HasText && result.Lines.Any(line => OcrGeometry.IsValid(line.Bounds)
            || line.Words.Any(word => OcrGeometry.IsValid(word.Bounds)));
    }

    /// <summary>Paragraph reading order with table cells substituted; a wide word gap always splits a line.</summary>
    internal static IReadOnlyList<PinOverlayBlock> BuildBlocks(OcrResult result)
    {
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        if (!extraction.HasGeometry) return [];
        var blocks = new List<PinOverlayBlock>();
        OcrTable table = extraction.Table;
        bool useTable = table.HasStructure && OcrGeometry.IsValid(table.Bounds)
            && (table.Columns.Count >= 3 || table.HasHeader || table.Columns.Any(column => column.IsNumeric));
        bool tableAdded = false;
        foreach (OcrParagraph paragraph in extraction.Paragraphs)
        {
            if (useTable && OcrGeometry.Contains(table.Bounds,
                    paragraph.Bounds.X + paragraph.Bounds.Width / 2, paragraph.Bounds.Y + paragraph.Bounds.Height / 2))
            {
                if (!tableAdded)
                {
                    foreach (OcrTableCell cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        if (string.IsNullOrWhiteSpace(cell.Text) || !OcrGeometry.IsValid(cell.Bounds)) continue;
                        var lines = OcrExtractionAnalyzer.Analyze(result, cell.Bounds).Paragraphs
                            .SelectMany(item => item.SourceLines).ToArray();
                        AddParagraph(lines.Length > 0 ? lines : [new(cell.Text, cell.Bounds, [])], blocks);
                    }
                    tableAdded = true;
                }
            }
            else AddParagraph(paragraph.SourceLines, blocks);
        }
        return blocks;
    }

    private static void AddParagraph(IReadOnlyList<OcrLine> lines, List<PinOverlayBlock> output)
    {
        var groups = new List<List<OcrLine>>();
        foreach (OcrLine line in lines)
        {
            foreach (OcrLine part in SplitLine(line))
            {
                // Rejoin only vertically adjacent lines with horizontal overlap; never bridge a column gutter.
                List<OcrLine>? group = groups.LastOrDefault(items =>
                    part.Bounds.Y >= items[^1].Bounds.Bottom - Math.Min(part.Bounds.Height, items[^1].Bounds.Height) * .2
                    && part.Bounds.Y - items[^1].Bounds.Bottom <= Math.Max(part.Bounds.Height, items[^1].Bounds.Height) * .85
                    && Math.Min(part.Bounds.Right, items[^1].Bounds.Right) - Math.Max(part.Bounds.X, items[^1].Bounds.X)
                        > Math.Min(part.Bounds.Width, items[^1].Bounds.Width) * .25);
                if (group is null) { group = []; groups.Add(group); }
                group.Add(part);
            }
        }
        foreach (List<OcrLine> group in groups)
            output.Add(new(string.Join(" ", group.Select(line => line.Text.Trim())),
                group.Select(line => line.Bounds).Aggregate(default(OcrRect), OcrRect.Union), group.ToArray()));
    }

    private static IEnumerable<OcrLine> SplitLine(OcrLine line)
    {
        OcrWord[] words = line.Words.Where(word => OcrGeometry.IsValid(word.Bounds)
            && !string.IsNullOrWhiteSpace(word.Text)).OrderBy(word => word.Bounds.X).ToArray();
        if (words.Length == 0)
        {
            if (OcrGeometry.IsValid(line.Bounds) && !string.IsNullOrWhiteSpace(line.Text)) yield return line;
            yield break;
        }
        double gap = Math.Max(6, Median(words.Select(word => word.Bounds.Height)) * 1.5);
        var run = new List<OcrWord>();
        foreach (OcrWord word in words)
        {
            if (run.Count > 0 && word.Bounds.X - run[^1].Bounds.Right > gap)
            {
                yield return MakeLine(run);
                run.Clear();
            }
            run.Add(word);
        }
        if (run.Count > 0) yield return MakeLine(run);

        static OcrLine MakeLine(List<OcrWord> source) => new(string.Join(" ", source.Select(word => word.Text)),
            source.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), source.ToArray());
    }

    /// <summary>Pure fitting policy. Measurement is injected; no text-height limit or trimming is applied.</summary>
    internal static PinOverlayFit? Fit(string text, OcrRect block, double lineHeight, int imageWidth, int imageHeight,
        Func<string, double, double, Size> measure, IReadOnlyList<OcrRect>? obstacles = null)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || !double.IsFinite(lineHeight) || lineHeight <= 0) return null;
        OcrRect bounds = Clamp(block, imageWidth, imageHeight);
        if (!OcrGeometry.IsValid(bounds) || string.IsNullOrWhiteSpace(text)) return null;
        double initial = Math.Clamp(lineHeight / .85, MinimumFontSize, 32768);
        for (double size = initial; ; size = Math.Max(MinimumFontSize, size * .9))
        {
            if (Fits(measure(text, size, bounds.Width), bounds)) return new(bounds, size, text, false);
            if (size == MinimumFontSize) break;
        }

        // First grow down, then right. Other blocks are hard obstacles, including failed translations.
        double maxWidth = imageWidth - bounds.X;
        for (double width = bounds.Width; ; width = Math.Min(maxWidth, width + 8))
        {
            double bottom = imageHeight;
            foreach (OcrRect obstacle in obstacles ?? [])
                if (obstacle.X < bounds.X + width + Padding && obstacle.Right > bounds.X - Padding
                    && obstacle.Bottom > bounds.Y && obstacle.Y >= bounds.Bottom - .01)
                    bottom = Math.Min(bottom, obstacle.Y - Padding * 2);
            var area = new OcrRect(bounds.X, bounds.Y, width, Math.Max(bounds.Height, bottom - bounds.Y));
            Size measured = measure(text, MinimumFontSize, width);
            var fitted = area with { Height = Math.Max(bounds.Height, measured.Height) };
            bool intersects = (obstacles ?? []).Any(obstacle => Intersects(fitted, obstacle));
            if (!intersects && Fits(measured, area)) return new(fitted, MinimumFontSize, text, true);
            if (width == maxWidth) break;
        }
        return null;

        static bool Fits(Size measured, OcrRect area) => double.IsFinite(measured.Height) && double.IsFinite(measured.Width)
            && measured.Width <= area.Width + .01 && measured.Height <= area.Height + .01;
    }

    internal static PinOverlayBackground EstimateBackground(byte[] bgra, int width, int height, int stride, OcrRect line)
    {
        OcrRect outside = Inflate(line, 5, width, height), inside = Inflate(line, 2, width, height);
        var samples = new List<Color>();
        for (int y = (int)outside.Y; y < (int)Math.Ceiling(outside.Bottom); y++)
            for (int x = (int)outside.X; x < (int)Math.Ceiling(outside.Right); x++)
                if (x < inside.X || x >= inside.Right || y < inside.Y || y >= inside.Bottom)
                    samples.Add(Pixel(bgra, stride, x, y));
        // Edge-to-edge text has no outer ring. Read the image-edge pixels rather than inventing white.
        if (samples.Count == 0)
        {
            OcrRect edge = Clamp(line, width, height);
            if (!OcrGeometry.IsValid(edge)) return new(Colors.White, 0);
            for (int x = (int)edge.X; x < (int)Math.Ceiling(edge.Right); x++)
            {
                samples.Add(Pixel(bgra, stride, x, (int)edge.Y));
                samples.Add(Pixel(bgra, stride, x, Math.Min(height - 1, (int)Math.Ceiling(edge.Bottom) - 1)));
            }
        }
        Color median = MedianColor(samples);
        double variance = samples.Average(color => (Square(color.R - median.R)
            + Square(color.G - median.G) + Square(color.B - median.B)) / 3d);
        return new(median, variance);
    }

    internal static Color EstimateTextColor(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<OcrLine> lines, Color background)
    {
        var samples = new List<Color>();
        foreach (OcrLine line in lines)
        {
            OcrRect rect = Clamp(line.Bounds, width, height);
            int step = Math.Max(1, (int)Math.Sqrt(rect.Width * rect.Height / 4096));
            for (int y = (int)rect.Y; y < (int)Math.Ceiling(rect.Bottom); y += step)
                for (int x = (int)rect.X; x < (int)Math.Ceiling(rect.Right); x += step)
                    samples.Add(Pixel(bgra, stride, x, y));
        }
        Color[] far = samples.OrderByDescending(color => Math.Abs(Luminance(color) - Luminance(background)))
            .Take(Math.Max(1, samples.Count / 5)).ToArray();
        return EnsureContrast(far.Length == 0 ? background : MedianColor(far), background);
    }

    internal static Color EnsureContrast(Color foreground, Color background)
    {
        if (Contrast(foreground, background) >= 4.5) return foreground;
        Color dark = Color.FromRgb(0x14, 0x14, 0x14), light = Color.FromRgb(0xfa, 0xfa, 0xfa);
        Color best = Contrast(dark, background) >= Contrast(light, background) ? dark : light;
        return Contrast(best, background) >= 4.5 ? best
            : Contrast(Colors.Black, background) >= Contrast(Colors.White, background) ? Colors.Black : Colors.White;
    }

    /// <summary>Use 88% first; increase only as far as needed to guarantee contrast over any underlying pixel.</summary>
    internal static (Color Foreground, double Opacity) PlateStyle(Color background, Color preferred)
    {
        Color[] candidates = [preferred, Color.FromRgb(20, 20, 20), Color.FromRgb(250, 250, 250), Colors.Black, Colors.White];
        for (double alpha = .88; ; alpha = Math.Min(1, alpha + .02))
        {
            Color dark = Blend(background, Colors.Black, alpha), light = Blend(background, Colors.White, alpha);
            double Score(Color color) => Luminance(color) >= Luminance(dark) && Luminance(color) <= Luminance(light)
                ? 1 : Math.Min(Contrast(color, dark), Contrast(color, light));
            if (Score(preferred) >= 4.5) return (preferred, alpha);
            Color best = candidates.MaxBy(Score);
            if (Score(best) >= 4.5 || alpha == 1) return (best, alpha);
        }
    }

    private static Color Blend(Color plate, Color behind, double alpha) => Color.FromRgb(
        (byte)Math.Round(plate.R * alpha + behind.R * (1 - alpha)),
        (byte)Math.Round(plate.G * alpha + behind.G * (1 - alpha)),
        (byte)Math.Round(plate.B * alpha + behind.B * (1 - alpha)));

    internal static double Contrast(Color a, Color b)
    {
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    internal static PinOverlayComposition Compose(BitmapSource source, IReadOnlyList<PinOverlayTranslation> translations)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        var converted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        var draws = new List<(PinOverlayFit Fit, PinOverlayBackground[] Backgrounds, Color Color, PinOverlayBlock Block)>();
        bool needsReview = false;
        foreach (PinOverlayTranslation item in translations)
        {
            if (item.Text is null) continue;
            OcrRect[] obstacles = translations.Where(other => !ReferenceEquals(other, item))
                .SelectMany(other => other.Block.Lines.Select(line => line.Bounds))
                .Concat(draws.Select(draw => draw.Fit.Bounds)).ToArray();
            PinOverlayFit? fit = Fit(item.Text, item.Block.Bounds, Median(item.Block.Lines.Select(line => line.Bounds.Height)),
                width, height, Measure, obstacles);
            if (fit is null || obstacles.Any(obstacle => Intersects(Inflate(fit.Bounds, Padding, width, height), obstacle)))
            { needsReview = true; continue; }
            PinOverlayBackground[] backgrounds = item.Block.Lines.Select(line =>
                EstimateBackground(pixels, width, height, stride, line.Bounds)).ToArray();
            Color background = MedianColor(backgrounds.Select(value => value.Color).ToArray());
            Color foreground = EstimateTextColor(pixels, width, height, stride, item.Block.Lines, background);
            draws.Add((fit, backgrounds, foreground, item.Block));
        }
        if (draws.Count == 0) return new(source, needsReview, 0);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            // Work in native pixels even when the source has non-square or non-96 DPI.
            drawing.PushTransform(new ScaleTransform(96 / source.DpiX, 96 / source.DpiY));
            drawing.DrawImage(source, new Rect(0, 0, width, height));
            foreach (var draw in draws)
            {
                Color background = MedianColor(draw.Backgrounds.Select(value => value.Color).ToArray());
                Color foreground = draw.Color;
                if (draw.Backgrounds.Any(value => value.NeedsPlate || Contrast(value.Color, background) > 1.5
                    || Contrast(foreground, value.Color) < 4.5))
                {
                    (foreground, double opacity) = PlateStyle(background, foreground);
                    var brush = new SolidColorBrush(background) { Opacity = opacity };
                    drawing.DrawRoundedRectangle(brush, null, Rect(Inflate(draw.Fit.Bounds, Padding, width, height)), 3, 3);
                }
                else if (draw.Fit.Grew)
                    drawing.DrawRectangle(new SolidColorBrush(background), null, Rect(Inflate(draw.Fit.Bounds, Padding, width, height)));
                else
                    for (int i = 0; i < draw.Block.Lines.Count; i++)
                        drawing.DrawRectangle(new SolidColorBrush(draw.Backgrounds[i].Color), null,
                            Rect(Inflate(draw.Block.Lines[i].Bounds, Padding, width, height)));
                FormattedText text = Format(draw.Fit.Text, draw.Fit.FontSize, draw.Fit.Bounds.Width, new SolidColorBrush(foreground));
                drawing.DrawText(text, new Point(draw.Fit.Bounds.X, draw.Fit.Bounds.Y));
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new(bitmap, needsReview, draws.Count);
    }

    private static Size Measure(string text, double size, double width)
    {
        FormattedText formatted = Format(text, size, width, Brushes.Black);
        return new(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    private static FormattedText Format(string text, double size, double width, Brush color) => new(text,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, size, color, 1)
    {
        MaxTextWidth = Math.Max(1, width),
        Trimming = TextTrimming.None,
    };

    internal static OcrRect Clamp(OcrRect value, int width, int height)
    {
        if (!OcrGeometry.IsValid(value)) return default;
        double x = Math.Clamp(value.X, 0, width), y = Math.Clamp(value.Y, 0, height);
        return new(x, y, Math.Max(0, Math.Min(width, value.Right) - x), Math.Max(0, Math.Min(height, value.Bottom) - y));
    }

    private static OcrRect Inflate(OcrRect rect, double amount, int width, int height) =>
        Clamp(new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2), width, height);
    private static bool Intersects(OcrRect a, OcrRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    private static Rect Rect(OcrRect value) => new(value.X, value.Y, value.Width, value.Height);
    private static double Square(double value) => value * value;
    private static Color Pixel(byte[] bgra, int stride, int x, int y)
    {
        int index = y * stride + x * 4;
        return Color.FromRgb(bgra[index + 2], bgra[index + 1], bgra[index]);
    }
    private static Color MedianColor(IReadOnlyList<Color> colors) => colors.Count == 0 ? Colors.White
        : Color.FromRgb((byte)Median(colors.Select(value => (double)value.R)), (byte)Median(colors.Select(value => (double)value.G)),
            (byte)Median(colors.Select(value => (double)value.B)));
    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        return values.Length == 0 ? 12 : values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { double n = value / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}
