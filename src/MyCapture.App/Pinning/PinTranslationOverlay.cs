using System.Buffers;
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
internal sealed record PinOverlayFit(OcrRect Bounds, double FontSize, string Text, bool Grew, string RenderText);
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

    internal static string NormalizeWhitespace(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static double[] EstimateFontSizes(IReadOnlyList<PinOverlayBlock> blocks)
    {
        var inkHeights = new Dictionary<string, double>(StringComparer.Ordinal);
        double[] sizes = blocks.Select(block => Math.Clamp(Median(block.Lines.Select(line =>
        {
            // OCR rectangles describe ink, not the font's em or its line box.
            // Extent includes WPF's raster padding (even at em=1); glyph geometry is scale-linear ink.
            if (!inkHeights.TryGetValue(line.Text, out double ink))
            {
                ink = Format(line.Text, 1, Brushes.Black).BuildGeometry(new Point()).Bounds.Height;
                inkHeights.Add(line.Text, ink);
            }
            return line.Bounds.Height / (double.IsFinite(ink) && ink > 0 ? ink : .85);
        })), MinimumFontSize, 32768)).ToArray();
        int[] ordered = Enumerable.Range(0, sizes.Length).OrderBy(index => sizes[index]).ToArray();
        for (int start = 0; start < ordered.Length;)
        {
            int end = start + 1;
            while (end < ordered.Length)
            {
                double candidate = Median(ordered.Skip(start).Take(end - start + 1).Select(index => sizes[index]));
                if (sizes[ordered[start]] < candidate * .88 || sizes[ordered[end]] > candidate * 1.12) break;
                end++;
            }
            double shared = Median(ordered.Skip(start).Take(end - start).Select(index => sizes[index]));
            for (int i = start; i < end; i++) sizes[ordered[i]] = shared;
            start = end;
        }
        return sizes;
    }

    /// <summary>Measurement is unwrapped; explicit legal line breaks prevent WPF emergency word splitting.</summary>
    internal static PinOverlayFit? Fit(string text, OcrRect block, double estimatedEm, int imageWidth, int imageHeight,
        Func<string, double, double, Size> measure, IReadOnlyList<OcrRect>? obstacles = null, int sourceLineCount = 1,
        double? sourceLineHeight = null, Func<string, double, Size>? finalMeasure = null,
        Func<string, double, double, double>? measureWidth = null, Func<string, double, Size>? placementMeasure = null)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || !double.IsFinite(estimatedEm) || estimatedEm <= 0) return null;
        OcrRect bounds = Clamp(block, imageWidth, imageHeight);
        if (!OcrGeometry.IsValid(bounds) || string.IsNullOrWhiteSpace(text)) return null;
        double initial = Math.Clamp(estimatedEm, MinimumFontSize, 32768);
        double lineAreaHeight = Math.Min(imageHeight - bounds.Y,
            Math.Max(bounds.Height, Math.Max(1, sourceLineCount) * (sourceLineHeight ?? initial * 1.2)));
        string normalized = NormalizeWhitespace(text);
        IReadOnlyList<OcrRect> blockers = obstacles ?? [];
        double maxWidth = imageWidth - bounds.X;
        foreach (OcrRect obstacle in blockers)
            if (obstacle.X >= bounds.Right - .01 && obstacle.Y < bounds.Y + lineAreaHeight + Padding
                && obstacle.Bottom > bounds.Y - Padding)
                maxWidth = Math.Min(maxWidth, Math.Max(bounds.Width, obstacle.X - Padding * 2 - bounds.X));

        double nextWrapWidth = double.PositiveInfinity;
        bool findingWidth = false;
        PinOverlayFit? Attempt(double size, double width, double height, int lineLimit)
        {
            string? wrapped = WrapCore(normalized, size, width, measure, out nextWrapWidth, measureWidth,
                findingWidth ? finalMeasure : null);
            if (wrapped is null || wrapped.Count(character => character == '\n') + 1 > lineLimit) return null;
            Size measured = finalMeasure?.Invoke(wrapped, size) ?? measure(wrapped, size, double.PositiveInfinity);
            if (placementMeasure is not null && double.IsFinite(measured.Height))
            {
                // Layout uses doubles; the final WPF outline bounds use floats. Only a finalist
                // on a height/physical-pixel boundary needs its exact placement bounds before
                // acceptance. Otherwise a few millionths of a pixel can change ceil by one pixel.
                double precision = Math.Max(.0001, measured.Height * .000001);
                double bottom = bounds.Y + Math.Max(bounds.Height, measured.Height) + Padding;
                if (Math.Abs(measured.Height - (height + .01)) <= precision
                    || (blockers.Count > 0 && Math.Abs(bottom - Math.Round(bottom)) <= precision))
                    measured = placementMeasure(wrapped, size);
            }
            if (!double.IsFinite(measured.Width) || !double.IsFinite(measured.Height)
                || measured.Width > width + .01 || measured.Height > height + .01) return null;
            var fitted = bounds with
            {
                Width = Math.Max(bounds.Width, measured.Width),
                Height = Math.Max(bounds.Height, measured.Height)
            };
            if (blockers.Any(obstacle => Intersects(EraseBounds(fitted, imageWidth, imageHeight), obstacle))) return null;
            return new(fitted, size, text, fitted.Width > bounds.Width + .01 || fitted.Height > bounds.Height + .01, wrapped);
        }

        // Preserve size and line count first, then use the free space to the right.
        PinOverlayFit? fit = Attempt(initial, bounds.Width, lineAreaHeight, Math.Max(1, sourceLineCount))
            ?? Attempt(initial, maxWidth, lineAreaHeight, Math.Max(1, sourceLineCount));
        if (fit is not null) return fit;
        double comfortableMinimum = Math.Max(MinimumFontSize, initial * .85);
        PinOverlayFit? SearchSizes(double first, double minimum, double factor, int lineLimit)
        {
            // Keep the original multiplicative grid, including its exact endpoint and rounding.
            var sizes = new List<double>();
            for (double size = first; ; size = Math.Max(minimum, size * factor))
            {
                sizes.Add(size);
                if (size == minimum) break;
            }
            var attempted = new bool[sizes.Count];
            var results = new PinOverlayFit?[sizes.Count];
            PinOverlayFit? At(int index)
            {
                if (!attempted[index])
                {
                    results[index] = Attempt(sizes[index], maxWidth, lineAreaHeight, lineLimit);
                    attempted[index] = true;
                }
                return results[index];
            }
            int low = 0, high = sizes.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (At(middle) is null) low = middle + 1;
                else high = middle;
            }
            // Ink ascenders/descenders and a changed wrap can make the predicate non-monotone.
            // Verify the larger candidates with the cached metrics before accepting the binary result.
            for (int index = 0; index < low; index++)
                if (At(index) is { } earlier) return earlier;
            return low < sizes.Count ? At(low) : null;
        }
        fit = SearchSizes(Math.Max(comfortableMinimum, initial * .95), comfortableMinimum, .95, Math.Max(1, sourceLineCount));
        if (fit is not null) return fit;

        // Additional lines are allowed only after the modest shrink, then the existing minimum.
        fit = SearchSizes(comfortableMinimum, MinimumFontSize, .9, int.MaxValue);
        if (fit is not null) return fit;
        PinOverlayFit? GrowDown(double width)
        {
            findingWidth = true;
            double bottom = imageHeight;
            foreach (OcrRect obstacle in blockers)
                if (obstacle.X < bounds.X + width + Padding && obstacle.Right > bounds.X - Padding
                    && obstacle.Bottom > bounds.Y && obstacle.Y >= bounds.Bottom - .01)
                    bottom = Math.Min(bottom, obstacle.Y - Padding * 2);
            return Attempt(MinimumFontSize, width, Math.Max(bounds.Height, bottom - bounds.Y), int.MaxValue);
        }
        fit = GrowDown(maxWidth);
        if (fit is not null) return fit;
        // A staggered block below may obstruct the wide area but leave a narrow vertical passage.
        for (double width = bounds.Width; width < maxWidth;)
        {
            fit = GrowDown(width);
            if (fit is not null) return fit;
            // Until a legal line break changes, the actual rectangle is identical and the free
            // bottom can only decrease as width grows. Jump directly to the next break threshold,
            // retaining the old 8-pixel grid and its first successful width even around obstacles.
            if (!double.IsFinite(nextWrapWidth)) break;
            double step = Math.Max(Math.Floor((width - bounds.Width) / 8) + 1,
                Math.Ceiling((nextWrapWidth - bounds.Width) / 8));
            width = bounds.Width + step * 8;
        }
        return null;
    }

    internal static string? Wrap(string text, double size, double width, Func<string, double, double, Size> measure)
        => WrapCore(text, size, width, measure, out _);

    private static string? WrapCore(string text, double size, double width, Func<string, double, double, Size> measure,
        out double nextWidth, Func<string, double, double, double>? measureWidth = null,
        Func<string, double, Size>? exactThreshold = null)
    {
        nextWidth = double.PositiveInfinity;
        text = NormalizeWhitespace(text);
        if (text.Length == 0) return text;
        var breaks = new List<int>();
        bool characterBreaks = !text.Contains(' ') && text.Any(IsCjk)
            && !text.Any(character => char.IsLetter(character) && !IsCjk(character));
        if (characterBreaks)
        {
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext()) breaks.Add(elements.ElementIndex + elements.GetTextElement().Length);
        }
        else
            for (int i = 0; i < text.Length; i++)
                if (text[i] is ' ' or '-' or '\u2010') breaks.Add(i + 1);
        if (breaks.Count == 0 || breaks[^1] != text.Length) breaks.Add(text.Length);
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int last = -1;
            foreach (int end in breaks)
            {
                if (end <= start) continue;
                string candidate = text[start..end].Trim();
                double measured = measureWidth?.Invoke(candidate, size, width)
                    ?? measure(candidate, size, double.PositiveInfinity).Width;
                if (measured > width + .01)
                {
                    nextWidth = Math.Min(nextWidth, (exactThreshold?.Invoke(candidate, size).Width ?? measured) - .01);
                    break;
                }
                last = end;
            }
            if (last < 0) return null;
            lines.Add(text[start..last].Trim());
            start = last;
            while (start < text.Length && text[start] == ' ') start++;
        }
        return string.Join("\n", lines);

        static bool IsCjk(char character) => character is >= '\u1100' and <= '\u11ff'
            or >= '\u2e80' and <= '\ua4cf' or >= '\ua960' and <= '\ua97f'
            or >= '\uac00' and <= '\ud7ff' or >= '\uf900' and <= '\ufaff'
            or >= '\uff66' and <= '\uff9f';
    }

    internal static PinOverlayBackground EstimateBackground(byte[] bgra, int width, int height, int stride, OcrRect line)
    {
        OcrRect outside = Inflate(line, 5, width, height), inside = Inflate(line, 2, width, height);
        Span<int> histogram = stackalloc int[768];
        histogram.Clear();
        int count = 0;
        long red = 0, green = 0, blue = 0, squares = 0;
        for (int y = (int)outside.Y; y < (int)Math.Ceiling(outside.Bottom); y++)
            for (int x = (int)outside.X; x < (int)Math.Ceiling(outside.Right); x++)
                if (x < inside.X || x >= inside.Right || y < inside.Y || y >= inside.Bottom)
                    AddBackgroundSample(bgra, y * stride + x * 4, histogram, ref count, ref red, ref green, ref blue, ref squares);
        // Edge-to-edge text has no outer ring. Read the image-edge pixels rather than inventing white.
        bool edgeSamples = count == 0;
        if (edgeSamples)
        {
            OcrRect edge = Clamp(line, width, height);
            if (!OcrGeometry.IsValid(edge)) return new(Colors.White, 0);
            for (int x = (int)edge.X; x < (int)Math.Ceiling(edge.Right); x++)
            {
                AddBackgroundSample(bgra, (int)edge.Y * stride + x * 4, histogram, ref count, ref red, ref green, ref blue, ref squares);
                AddBackgroundSample(bgra, Math.Min(height - 1, (int)Math.Ceiling(edge.Bottom) - 1) * stride + x * 4,
                    histogram, ref count, ref red, ref green, ref blue, ref squares);
            }
        }
        Color median = HistogramColor(histogram, count);
        double variance = (squares - 2d * (median.R * red + median.G * green + median.B * blue)
            + (double)count * (median.R * median.R + median.G * median.G + median.B * median.B)) / (3d * count);
        // Preserve the original floating-point accumulation at the plate threshold. Expanding
        // the variance algebraically otherwise changes 324.00000000000006 into exactly 324.
        if (Math.Abs(variance - 18 * 18) < 1e-8)
        {
            double sum = 0;
            if (edgeSamples)
            {
                OcrRect edge = Clamp(line, width, height);
                for (int x = (int)edge.X; x < (int)Math.Ceiling(edge.Right); x++)
                {
                    sum += PixelVariance(bgra, (int)edge.Y * stride + x * 4, median);
                    sum += PixelVariance(bgra, Math.Min(height - 1, (int)Math.Ceiling(edge.Bottom) - 1) * stride + x * 4, median);
                }
            }
            else
                for (int y = (int)outside.Y; y < (int)Math.Ceiling(outside.Bottom); y++)
                    for (int x = (int)outside.X; x < (int)Math.Ceiling(outside.Right); x++)
                        if (x < inside.X || x >= inside.Right || y < inside.Y || y >= inside.Bottom)
                            sum += PixelVariance(bgra, y * stride + x * 4, median);
            variance = sum / count;
        }
        return new(median, variance);
    }

    private static double PixelVariance(byte[] pixels, int index, Color median) =>
        (Square(pixels[index + 2] - median.R) + Square(pixels[index + 1] - median.G) + Square(pixels[index] - median.B)) / 3d;

    private static void AddBackgroundSample(byte[] pixels, int index, Span<int> histogram, ref int count,
        ref long red, ref long green, ref long blue, ref long squares)
    {
        int r = pixels[index + 2], g = pixels[index + 1], b = pixels[index];
        histogram[r]++; histogram[256 + g]++; histogram[512 + b]++;
        count++; red += r; green += g; blue += b; squares += r * r + g * g + b * b;
    }

    private static Color HistogramColor(ReadOnlySpan<int> histogram, int count)
    {
        if (count == 0) return Colors.White;
        static byte Channel(ReadOnlySpan<int> values, int count)
        {
            int lower = (count - 1) / 2, upper = count / 2, seen = 0, first = -1;
            for (int value = 0; value < values.Length; value++)
            {
                seen += values[value];
                if (first < 0 && seen > lower) first = value;
                if (seen > upper) return (byte)((first + value) / 2);
            }
            return 255;
        }
        return Color.FromRgb(Channel(histogram[..256], count), Channel(histogram.Slice(256, 256), count),
            Channel(histogram.Slice(512, 256), count));
    }

    private readonly record struct InkSample(int Rgb, double Distance, int Order);

    internal static Color EstimateTextColor(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<OcrLine> lines, Color background)
        => EstimateTextColor(bgra, width, height, stride, lines, background, []);

    private static Color EstimateTextColor(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<OcrLine> lines, Color background, List<InkSample> samples)
    {
        samples.Clear();
        double backgroundLuminance = Luminance(background);
        int sampleCount = 0, uniformRgb = -1;
        foreach (OcrLine line in lines)
        {
            OcrRect rect = Clamp(line.Bounds, width, height);
            int step = Math.Max(1, (int)Math.Sqrt(rect.Width * rect.Height / 4096));
            for (int y = (int)rect.Y; y < (int)Math.Ceiling(rect.Bottom); y += step)
                for (int x = (int)rect.X; x < (int)Math.Ceiling(rect.Right); x += step)
                {
                    int index = y * stride + x * 4;
                    int r = bgra[index + 2], g = bgra[index + 1], b = bgra[index];
                    int rgb = (r << 16) | (g << 8) | b;
                    if (sampleCount == 0) uniformRgb = rgb;
                    if (samples.Count == 0 && rgb == uniformRgb)
                    {
                        sampleCount++;
                        continue;
                    }
                    if (samples.Count == 0)
                    {
                        double uniformDistance = Math.Abs(.2126 * LinearChannels[uniformRgb >> 16]
                            + .7152 * LinearChannels[(uniformRgb >> 8) & 255] + .0722 * LinearChannels[uniformRgb & 255] - backgroundLuminance);
                        for (int prior = 0; prior < sampleCount; prior++) samples.Add(new(uniformRgb, uniformDistance, prior));
                    }
                    double distance = Math.Abs(.2126 * LinearChannels[r] + .7152 * LinearChannels[g]
                        + .0722 * LinearChannels[b] - backgroundLuminance);
                    samples.Add(new(rgb, distance, sampleCount++));
                }
        }
        if (sampleCount == 0) return EnsureContrast(background, background);
        if (samples.Count == 0)
            return EnsureContrast(Color.FromRgb((byte)(uniformRgb >> 16), (byte)(uniformRgb >> 8), (byte)uniformRgb), background);
        samples.Sort(static (first, second) =>
        {
            int distance = second.Distance.CompareTo(first.Distance);
            return distance == 0 ? first.Order.CompareTo(second.Order) : distance;
        });
        Span<int> histogram = stackalloc int[768];
        histogram.Clear();
        int count = Math.Max(1, samples.Count / 5);
        for (int i = 0; i < count; i++)
        {
            int rgb = samples[i].Rgb;
            histogram[rgb >> 16]++; histogram[256 + ((rgb >> 8) & 255)]++; histogram[512 + (rgb & 255)]++;
        }
        return EnsureContrast(HistogramColor(histogram, count), background);
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

    internal static PinOverlayComposition Compose(BitmapSource source, IReadOnlyList<PinOverlayTranslation> translations, bool drawText = true,
        Action<PinOverlayBlock, PinOverlayFit, bool>? observeFit = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        var converted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(stride * height));
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            return ComposePixels(source, translations, drawText, observeFit, pixels, width, height, stride);
        }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
    }

    private static PinOverlayComposition ComposePixels(BitmapSource source, IReadOnlyList<PinOverlayTranslation> translations,
        bool drawText, Action<PinOverlayBlock, PinOverlayFit, bool>? observeFit, byte[] pixels, int width, int height, int stride)
    {
        var draws = new List<(PinOverlayFit Fit, PinOverlayBackground[] Backgrounds, Color Color, PinOverlayBlock Block, double InkTop)>();
        var metrics = new CompositionMetrics();
        var inkBounds = new Dictionary<(string Text, double Size), Rect>();
        var renderedText = new Dictionary<(string Text, double Size, Color Color), FormattedText>();
        var samples = new List<InkSample>();
        Rect PlacementBounds(string text, double size)
        {
            var key = (text, size);
            if (!inkBounds.TryGetValue(key, out Rect ink))
            {
                ink = Format(text, size, Brushes.Black).BuildGeometry(new Point()).Bounds;
                inkBounds.Add(key, ink);
            }
            return ink;
        }
        Size MeasurePlacement(string text, double size) =>
            new(metrics.MeasureFinal(text, size).Width, PlacementBounds(text, size).Height);
        bool needsReview = false;
        double[] fontSizes = EstimateFontSizes(translations.Select(item => item.Block).ToArray());
        for (int index = 0; index < translations.Count; index++)
        {
            PinOverlayTranslation item = translations[index];
            if (item.Text is null || string.Equals(NormalizeWhitespace(item.Text), NormalizeWhitespace(item.Block.Text), StringComparison.Ordinal)) continue;
            OcrRect[] obstacles = translations.Where(other => !ReferenceEquals(other, item))
                .SelectMany(other => other.Block.Lines.Select(line => line.Bounds))
                .Concat(draws.Select(draw => draw.Fit.Bounds)).ToArray();
            PinOverlayFit? fit = Fit(item.Text, item.Block.Bounds, fontSizes[index],
                width, height, Measure, obstacles, item.Block.Lines.Count, metrics.LineHeight(fontSizes[index]),
                metrics.MeasureFinal, metrics.MeasureWidth, MeasurePlacement);
            if (fit is null)
            { needsReview = true; continue; }
            // Reuse the chosen output's exact placement bounds (including any boundary validation)
            // to retain the round-two ink rectangle and WPF's single-precision vertical position.
            Rect ink = PlacementBounds(fit.RenderText, fit.FontSize);
            OcrRect original = Clamp(item.Block.Bounds, width, height);
            OcrRect finalBounds = fit.Bounds with { Height = Math.Max(original.Height, ink.Height) };
            fit = fit with { Bounds = finalBounds,
                Grew = finalBounds.Width > original.Width + .01 || finalBounds.Height > original.Height + .01 };
            if (obstacles.Any(obstacle => Intersects(EraseBounds(fit.Bounds, width, height), obstacle)))
            { needsReview = true; continue; }
            PinOverlayBackground[] backgrounds = item.Block.Lines.Select(line =>
                EstimateBackground(pixels, width, height, stride, line.Bounds)).ToArray();
            Color background = MedianColor(backgrounds.Select(value => value.Color).ToArray());
            Color foreground = EstimateTextColor(pixels, width, height, stride, item.Block.Lines, background, samples);
            draws.Add((fit, backgrounds, foreground, item.Block, ink.Y));
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
                // Erase the complete source ink before putting any translucent surface over it.
                // Snap outwards to physical pixels so fractional OCR edges cannot leave antialiased remnants.
                for (int i = 0; i < draw.Block.Lines.Count; i++)
                {
                    drawing.DrawRectangle(new SolidColorBrush(draw.Backgrounds[i].Color), null,
                        Rect(EraseBounds(draw.Block.Lines[i].Bounds, width, height)));
                }
                bool needsPlate = draw.Backgrounds.Any(value => value.NeedsPlate || Contrast(value.Color, background) > 1.5
                    || Contrast(foreground, value.Color) < 4.5);
                observeFit?.Invoke(draw.Block, draw.Fit, needsPlate);
                if (needsPlate)
                {
                    (foreground, double opacity) = PlateStyle(background, foreground);
                    drawing.DrawRoundedRectangle(new SolidColorBrush(background) { Opacity = opacity * .2 }, null,
                        Rect(Inflate(draw.Fit.Bounds, Padding, width, height)), 3, 3);
                    var brush = new SolidColorBrush(background) { Opacity = opacity };
                    drawing.DrawRoundedRectangle(brush, null, Rect(Inflate(draw.Fit.Bounds, Padding - 1, width, height)), 2, 2);
                }
                else if (draw.Fit.Grew)
                    drawing.DrawRectangle(new SolidColorBrush(background), null, Rect(Inflate(draw.Fit.Bounds, Padding, width, height)));
                if (drawText)
                {
                    var key = (draw.Fit.RenderText, draw.Fit.FontSize, foreground);
                    if (!renderedText.TryGetValue(key, out FormattedText? text))
                    {
                        text = Format(draw.Fit.RenderText, draw.Fit.FontSize, new SolidColorBrush(foreground));
                        renderedText.Add(key, text);
                    }
                    drawing.DrawText(text, new Point(draw.Fit.Bounds.X, draw.Fit.Bounds.Y - draw.InkTop));
                }
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return new(bitmap, needsReview, draws.Count);
    }

    internal static Size Measure(string text, double size, double width)
    {
        FormattedText formatted = Format(text, size, Brushes.Black);
        // Empty edge lines have no glyph ink, although WPF includes their line boxes in Extent.
        // Preserve their width contract while excluding them from the ink-height calculation.
        string inkText = text.Trim();
        FormattedText ink = inkText.Length == text.Length ? formatted : Format(inkText, size, Brushes.Black);
        return new(formatted.WidthIncludingTrailingWhitespace, InkHeight(ink, size));
    }

    // WPF's layout extent includes one raster pixel above/below the glyph box. Below em=7
    // that margin scales with its minimum rasterization size. Removing it preserves the old
    // ink-height contract while using layout metrics, without constructing glyph outlines.
    private static double InkHeight(FormattedText text, double size) => Math.Max(0, text.Extent - 2 * Math.Min(1, size / 7));

    private sealed class CompositionMetrics
    {
        private const double ReferenceSize = 128;
        private readonly Dictionary<string, double> words = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> lines = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Text, double Size), Size> exact = [];
        private readonly Dictionary<double, double> lineHeights = [];

        internal double LineHeight(double size)
        {
            if (!lineHeights.TryGetValue(size, out double height))
            {
                height = Format("Ag", size, Brushes.Black).Height;
                lineHeights.Add(size, height);
            }
            return height;
        }

        internal Size MeasureFinal(string text, double size)
        {
            var key = (text, size);
            if (!exact.TryGetValue(key, out Size measured))
            {
                measured = Measure(text, size, double.PositiveInfinity);
                exact.Add(key, measured);
            }
            return measured;
        }

        internal double MeasureWidth(string text, double size, double limit)
        {
            if (exact.TryGetValue((text, size), out Size measured)) return measured.Width;
            if (!lines.TryGetValue(text, out double reference))
            {
                reference = 0;
                int start = 0;
                bool characterBreaks = !text.Contains(' ') && text.Any(IsCjk)
                    && !text.Any(character => char.IsLetter(character) && !IsCjk(character));
                if (characterBreaks)
                {
                    TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
                    while (elements.MoveNext()) reference += Word(elements.GetTextElement());
                }
                else
                {
                    for (int index = 0; index < text.Length; index++)
                    {
                        if (text[index] is not (' ' or '-' or '\u2010')) continue;
                        if (text[index] == ' ')
                        {
                            if (index > start) reference += Word(text[start..index]);
                            reference += Word(" ");
                        }
                        else reference += Word(text[start..(index + 1)]);
                        start = index + 1;
                    }
                    if (start < text.Length) reference += Word(text[start..]);
                }
                lines.Add(text, reference);
            }
            double approximate = reference * size / ReferenceSize;
            // Keep WPF's actual shaping/rounding at a break boundary. A final accepted line is
            // always measured exactly as well, so cached sums never become its stored rectangle.
            return Math.Abs(approximate - (limit + .01)) <= 1
                ? MeasureFinal(text, size).Width : approximate;
        }

        private double Word(string text)
        {
            if (!words.TryGetValue(text, out double width))
            {
                width = Format(text, ReferenceSize, Brushes.Black).WidthIncludingTrailingWhitespace;
                words.Add(text, width);
            }
            return width;
        }

        private static bool IsCjk(char character) => character is >= '\u1100' and <= '\u11ff'
            or >= '\u2e80' and <= '\ua4cf' or >= '\ua960' and <= '\ua97f'
            or >= '\uac00' and <= '\ud7ff' or >= '\uf900' and <= '\ufaff'
            or >= '\uff66' and <= '\uff9f';
    }

    private static FormattedText Format(string text, double size, Brush color) => new(text,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, size, color, 1)
    {
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
    private static OcrRect EraseBounds(OcrRect rect, int width, int height)
    {
        OcrRect inflated = Inflate(rect, Padding, width, height);
        return new(Math.Floor(inflated.X), Math.Floor(inflated.Y),
            Math.Ceiling(inflated.Right) - Math.Floor(inflated.X), Math.Ceiling(inflated.Bottom) - Math.Floor(inflated.Y));
    }
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
        return .2126 * LinearChannels[color.R] + .7152 * LinearChannels[color.G] + .0722 * LinearChannels[color.B];
    }

    private static readonly double[] LinearChannels = Enumerable.Range(0, 256).Select(value =>
    {
        double n = value / 255d;
        return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4);
    }).ToArray();
}
