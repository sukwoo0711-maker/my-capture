using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal sealed record PinOverlayBlock(string Text, OcrRect Bounds, IReadOnlyList<OcrLine> Lines)
{
    internal bool PreservePixels { get; init; }
    internal IReadOnlyList<OcrRect> ProtectedRegions { get; init; } = [];
}
internal sealed record PinOverlayTranslation(PinOverlayBlock Block, string? Text);
internal readonly record struct PinOverlayBackground(Color Color, double Variance)
{
    internal bool NeedsPlate => Variance > 18 * 18;
}
internal sealed record PinOverlayFit(OcrRect Bounds, double FontSize, string Text, bool Grew, string RenderText)
{
    internal IReadOnlyList<double> LineOffsets { get; init; } = [];
}
/// <summary>
/// The untouched source and, over it in the source's native pixels, a frozen layer with the translated
/// surfaces and text. A pin shows the two as layers of one drawing, so no picture-sized bitmap or texture
/// exists for the translation; <see cref="Image"/> flattens them only when a bitmap is asked for (copy,
/// the text selection's background).
/// </summary>
internal sealed class PinOverlayComposition(BitmapSource source, Drawing? overlay, bool needsReview, int renderedBlocks)
{
    private BitmapSource? image;
    private ImageSource? display;

    internal void ReleaseFlattenedImage() => image = null;

    internal BitmapSource Source { get; } = source;
    internal Drawing? Overlay { get; } = overlay;
    internal bool NeedsReview { get; } = needsReview;
    internal int RenderedBlocks { get; } = renderedBlocks;

    /// <summary>The layer flattened onto the source, or the source itself when nothing was drawn.</summary>
    internal BitmapSource Image => image ??= Overlay is null ? Source : PinTranslationOverlay.Flatten(Source, Overlay);

    /// <summary>Both layers at the source's DIP size, for an Image element.</summary>
    internal ImageSource Display => display ??= Overlay is null ? Source : PinTranslationOverlay.Layered(Source, Overlay);
}

/// <summary>
/// Composition inputs that do not depend on the translated text. It owns a Bgra32 copy of the
/// source pixels until it is disposed, so it can be sampled while the translator runs.
/// </summary>
internal sealed class PinOverlayPreparation : IDisposable
{
    private byte[]? pixels;

    internal PinOverlayPreparation(BitmapSource source, IReadOnlyList<PinOverlayBlock> blocks, byte[] pixels,
        int width, int height, int stride)
    {
        Source = source;
        Blocks = blocks;
        this.pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Layouts = new PinTranslationOverlay.BlockLayout?[blocks.Count];
    }

    internal BitmapSource Source { get; }
    internal IReadOnlyList<PinOverlayBlock> Blocks { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal byte[] Pixels => pixels ?? throw new ObjectDisposedException(nameof(PinOverlayPreparation));
    internal double[] FontSizes { get; set; } = [];
    internal PinTranslationOverlay.BlockLayout?[] Layouts { get; }

    public void Dispose() => pixels = null;
}

/// <summary>Native-pixel layout and colour sampling, recorded as a frozen layer over the source picture.</summary>
internal static partial class PinTranslationOverlay
{
    internal const double MinimumFontSize = TypeScale.Content.TranslationOverlayMinimum;
    private const double Padding = 2;

    internal static bool CanOverlay(OcrResult result)
    {
        double angle = result.TextAngle ?? 0;
        if (!double.IsFinite(angle) || Math.Abs(((angle + 180) % 360 + 360) % 360 - 180) > 2) return false;
        return result.HasText && result.Lines.Any(line => OcrGeometry.IsValid(line.Bounds)
            || line.Words.Any(word => OcrGeometry.IsValid(word.Bounds)));
    }

    internal static string NormalizeWhitespace(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static double[] EstimateFontSizes(IReadOnlyList<PinOverlayBlock> blocks) => EstimateFontSizes(blocks, null, NewUnitInk());

    /// <summary>
    /// Estimates every block and then limits it to the em its source pixels show, before equal
    /// sizes are grouped. Both divide by the same text's ink at em 1, so labels with and without
    /// descenders keep one size. OCR rectangles include padding and blur rows, so the pixel em is
    /// the better measure; the rectangle estimate remains for blocks without measurable ink.
    /// </summary>
    internal static double[] EstimateFontSizes(IReadOnlyList<PinOverlayBlock> blocks, byte[] pixels, int width, int height, int stride)
    {
        Func<string, double> wordInk = NewWordInk();
        double[] sourceEms = blocks.Select(block => SourceInkEm(block, pixels, width, height, stride, wordInk)).ToArray();
        return EstimateFontSizes(blocks, sourceEms, NewUnitInk());
    }

    private static readonly ConcurrentDictionary<(string Culture, string Element), (double Top, double Bottom)> GlyphExtents = new();

    /// <summary>
    /// A word's glyph ink height at em 1, composed from per-character extents around the baseline.
    /// Characters repeat far more than words, and the typeface never changes, so the process measures
    /// each glyph once (per UI culture, which can choose the fallback font) instead of every word.
    /// </summary>
    private static Func<string, double> NewWordInk()
    {
        string culture = CultureInfo.CurrentUICulture.Name;
        return text =>
        {
            double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (!GlyphExtents.TryGetValue((culture, element), out (double Top, double Bottom) extent))
                {
                    FormattedText formatted = Format(element, 1, Brushes.Black);
                    Rect bounds = formatted.BuildGeometry(new Point()).Bounds;
                    extent = bounds.IsEmpty ? (double.PositiveInfinity, double.NegativeInfinity)
                        : (bounds.Top - formatted.Baseline, bounds.Bottom - formatted.Baseline);
                    // Bounded: a page of unusual combining sequences cannot grow the cache without limit.
                    if (GlyphExtents.Count < 32768) GlyphExtents.TryAdd((culture, element), extent);
                }
                top = Math.Min(top, extent.Top);
                bottom = Math.Max(bottom, extent.Bottom);
            }
            double ink = bottom - top;
            return double.IsFinite(ink) && ink > 0 ? ink : .85;
        };
    }

    /// <summary>The height of a text's glyph geometry at em 1, cached by text.</summary>
    private static Func<string, double> NewUnitInk()
    {
        var inkHeights = new Dictionary<string, double>(StringComparer.Ordinal);
        return text =>
        {
            // Extent includes WPF's raster padding (even at em=1); glyph geometry is scale-linear ink.
            if (!inkHeights.TryGetValue(text, out double ink))
            {
                ink = Format(text, 1, Brushes.Black).BuildGeometry(new Point()).Bounds.Height;
                inkHeights.Add(text, ink);
            }
            return double.IsFinite(ink) && ink > 0 ? ink : .85;
        };
    }

    private static double[] EstimateFontSizes(IReadOnlyList<PinOverlayBlock> blocks, IReadOnlyList<double>? sourceEms, Func<string, double> unitInk)
    {
        // OCR rectangles describe ink, not the font's em or its line box.
        double[] sizes = blocks.Select(block => Math.Clamp(Median(block.Lines.Select(line =>
            line.Bounds.Height / unitInk(line.Text))), MinimumFontSize, 32768)).ToArray();
        if (sourceEms is not null)
            for (int index = 0; index < sizes.Length; index++)
                if (sourceEms[index] > 0) sizes[index] = Math.Min(sizes[index], sourceEms[index]);
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
        Func<string, double, double, double>? measureWidth = null, Func<string, double, Size>? placementMeasure = null,
        TextAlignment alignment = TextAlignment.Left, OcrRect? horizontalLimits = null,
        Func<OcrRect, bool>? canOccupy = null, double firstLineInset = 0, IReadOnlyList<OcrRect>? textExclusions = null,
        double minimumFontSize = MinimumFontSize)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || !double.IsFinite(estimatedEm) || estimatedEm <= 0) return null;
        if (!double.IsFinite(minimumFontSize) || minimumFontSize <= 0) return null;
        OcrRect bounds = Clamp(block, imageWidth, imageHeight);
        if (!OcrGeometry.IsValid(bounds) || string.IsNullOrWhiteSpace(text)) return null;
        double minimumEm = Math.Min(MinimumFontSize, minimumFontSize);
        double initial = Math.Clamp(estimatedEm, minimumEm, 32768);
        double lineAreaHeight = Math.Min(imageHeight - bounds.Y,
            Math.Max(bounds.Height, Math.Max(1, sourceLineCount) * (sourceLineHeight ?? initial * 1.2)));
        string normalized = NormalizeWhitespace(text);
        IReadOnlyList<OcrRect> blockers = obstacles ?? [];
        double leftLimit = horizontalLimits?.X ?? 0, rightLimit = horizontalLimits?.Right ?? imageWidth;
        double anchor = alignment == TextAlignment.Center ? bounds.X + bounds.Width / 2 : bounds.Right;
        double maxWidth = alignment switch
        {
            TextAlignment.Center => Math.Max(bounds.Width, 2 * Math.Min(anchor - leftLimit, rightLimit - anchor)),
            TextAlignment.Right => Math.Max(bounds.Width, bounds.Right - leftLimit),
            _ => Math.Max(bounds.Width, rightLimit - bounds.X),
        };
        foreach (OcrRect obstacle in blockers)
        {
            if (obstacle.Y >= bounds.Y + lineAreaHeight + Padding || obstacle.Bottom <= bounds.Y - Padding) continue;
            if (obstacle.X >= bounds.Right - .01 && alignment != TextAlignment.Right)
                maxWidth = Math.Min(maxWidth, Math.Max(bounds.Width, alignment == TextAlignment.Center
                    ? 2 * (obstacle.X - Padding * 2 - anchor) : obstacle.X - Padding * 2 - bounds.X));
            if (obstacle.Right <= bounds.X + .01 && alignment != TextAlignment.Left)
                maxWidth = Math.Min(maxWidth, Math.Max(bounds.Width, alignment == TextAlignment.Center
                    ? 2 * (anchor - obstacle.Right - Padding * 2) : bounds.Right - obstacle.Right - Padding * 2));
        }

        double nextWrapWidth = double.PositiveInfinity;
        bool findingWidth = false;
        PinOverlayFit? Attempt(double size, double width, double height, int lineLimit)
        {
            bool reserved = textExclusions is { Count: > 0 } && alignment == TextAlignment.Left;
            (double Offset, double Width) Slot(int line)
            {
                double pitch = (sourceLineHeight ?? initial * 1.2) * size / initial;
                double top = bounds.Y + line * pitch;
                double start = bounds.X, end = bounds.X + width;
                var intervals = new List<(double Start, double End)> { (start, end) };
                foreach (OcrRect excluded in textExclusions!)
                {
                    if (excluded.Y >= top + pitch || excluded.Bottom <= top) continue;
                    for (int i = intervals.Count - 1; i >= 0; i--)
                    {
                        (double a, double b) = intervals[i];
                        if (excluded.X >= b || excluded.Right <= a) continue;
                        intervals.RemoveAt(i);
                        if (excluded.X - Padding > a) intervals.Add((a, Math.Min(b, excluded.X - Padding)));
                        if (excluded.Right + Padding < b) intervals.Add((Math.Max(a, excluded.Right + Padding), b));
                    }
                }
                if (intervals.Count == 0) return (0, 0);
                var best = intervals.OrderByDescending(interval => interval.End - interval.Start).ThenBy(interval => interval.Start).First();
                return (best.Start - start, best.End - best.Start);
            }
            string? wrapped = WrapCore(normalized, size, width, measure, out nextWrapWidth, measureWidth,
                findingWidth ? finalMeasure : null, reserved ? 0 : firstLineInset, reserved ? Slot : null);
            if (wrapped is null || wrapped.Count(character => character == '\n') + 1 > lineLimit) return null;
            Size measured = finalMeasure?.Invoke(wrapped, size) ?? measure(wrapped, size, double.PositiveInfinity);
            if (firstLineInset > 0)
            {
                string first = wrapped.Split('\n')[0];
                double firstWidth = finalMeasure?.Invoke(first, size).Width ?? measure(first, size, double.PositiveInfinity).Width;
                measured.Width = Math.Max(measured.Width, firstWidth + firstLineInset);
            }
            double[] offsets = [];
            if (reserved)
            {
                string[] lines = wrapped.Split('\n');
                offsets = new double[lines.Length];
                for (int i = 0; i < lines.Length; i++)
                {
                    offsets[i] = Slot(i).Offset;
                    double lineWidth = finalMeasure?.Invoke(lines[i], size).Width ?? measure(lines[i], size, double.PositiveInfinity).Width;
                    measured.Width = Math.Max(measured.Width, offsets[i] + lineWidth);
                }
            }
            if (placementMeasure is not null && double.IsFinite(measured.Height))
            {
                // Layout uses doubles; the final WPF outline bounds use floats. Only a finalist
                // on a height/physical-pixel boundary needs its exact placement bounds before
                // acceptance. Otherwise a few millionths of a pixel can change ceil by one pixel.
                double precision = Math.Max(.0001, measured.Height * .000001);
                double bottom = bounds.Y + Math.Max(bounds.Height, measured.Height) + Padding;
                if (Math.Abs(measured.Height - (height + .01)) <= precision
                    || (blockers.Count > 0 && Math.Abs(bottom - Math.Round(bottom)) <= precision))
                {
                    Size exact = placementMeasure(wrapped, size);
                    measured = firstLineInset > 0 || reserved ? new Size(Math.Max(measured.Width, exact.Width), exact.Height) : exact;
                }
            }
            if (!double.IsFinite(measured.Width) || !double.IsFinite(measured.Height)
                || measured.Width > width + .01 || measured.Height > height + .01) return null;
            var fitted = bounds with
            {
                Width = Math.Max(bounds.Width, measured.Width),
                Height = Math.Max(bounds.Height, measured.Height)
            };
            fitted = fitted with { X = AlignedX(fitted.Width) };
            if (blockers.Any(obstacle => Intersects(EraseBounds(fitted, imageWidth, imageHeight), obstacle))) return null;
            if (canOccupy is not null && !canOccupy(fitted)) return null;
            return new(fitted, size, text, fitted.Width > bounds.Width + .01 || fitted.Height > bounds.Height + .01, wrapped) { LineOffsets = offsets };
        }

        double AlignedX(double width) => alignment switch
        {
            TextAlignment.Center => bounds.X + (bounds.Width - width) / 2,
            TextAlignment.Right => bounds.Right - width,
            _ => bounds.X,
        };

        // The free space to the right is a limit, not a target. A greedy wrap at the widest
        // allowed width can make a block as wide as the page; take the narrowest width that keeps
        // the stage's line limit (balanced lines), validated by the same Attempt.
        PinOverlayFit? Narrowest(PinOverlayFit? wide, int lineLimit, Func<double, int, PinOverlayFit?> attempt)
        {
            if (wide is null || wide.Bounds.Width <= bounds.Width + .01) return wide;
            // An unlimited stage keeps the line count it found; a source-limited stage may use all of its lines.
            int lines = lineLimit == int.MaxValue ? wide.RenderText.Count(character => character == '\n') + 1 : lineLimit;
            if (lines <= 1) return wide;
            double low = bounds.Width, high = wide.Bounds.Width;
            PinOverlayFit best = wide;
            while (high - low > 1)
            {
                double middle = (low + high) / 2;
                if (attempt(middle, lines) is { } narrower)
                {
                    best = narrower;
                    high = Math.Min(middle, narrower.Bounds.Width);
                }
                else low = middle;
            }
            return best;
        }

        // Preserve size and line count first, then use the free space to the right.
        PinOverlayFit? fit = Attempt(initial, bounds.Width, lineAreaHeight, Math.Max(1, sourceLineCount))
            ?? Narrowest(Attempt(initial, maxWidth, lineAreaHeight, Math.Max(1, sourceLineCount)), Math.Max(1, sourceLineCount),
                (width, lines) => Attempt(initial, width, lineAreaHeight, lines));
        if (fit is not null) return fit;
        double comfortableMinimum = Math.Max(minimumEm, initial * .85);
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
            PinOverlayFit? chosen = null;
            int chosenIndex = low;
            for (int index = 0; index < low && chosen is null; index++)
                if (At(index) is { } earlier) { chosen = earlier; chosenIndex = index; }
            chosen ??= low < sizes.Count ? At(low) : null;
            return Narrowest(chosen, lineLimit, (width, lines) => Attempt(sizes[chosenIndex], width, lineAreaHeight, lines));
        }
        fit = SearchSizes(Math.Max(comfortableMinimum, initial * .95), comfortableMinimum, .95, Math.Max(1, sourceLineCount));
        if (fit is not null) return fit;

        // Additional lines are allowed only after the modest shrink, then the existing minimum.
        fit = SearchSizes(comfortableMinimum, minimumEm, .9, int.MaxValue);
        if (fit is not null) return fit;
        PinOverlayFit? GrowDown(double width, int lineLimit = int.MaxValue)
        {
            findingWidth = true;
            double bottom = imageHeight;
            double x = AlignedX(width);
            foreach (OcrRect obstacle in blockers)
                if (obstacle.X < x + width + Padding && obstacle.Right > x - Padding
                    && obstacle.Bottom > bounds.Y && obstacle.Y >= bounds.Bottom - .01)
                    bottom = Math.Min(bottom, obstacle.Y - Padding * 2);
            return Attempt(minimumEm, width, Math.Max(bounds.Height, bottom - bounds.Y), lineLimit);
        }
        fit = Narrowest(GrowDown(maxWidth), int.MaxValue, GrowDown);
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
        Func<string, double, Size>? exactThreshold = null, double firstLineInset = 0,
        Func<int, (double Offset, double Width)>? lineSlot = null)
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
                double inset = start == 0 ? firstLineInset : 0;
                double available = lineSlot?.Invoke(lines.Count).Width ?? width;
                if (measured + inset > available + .01)
                {
                    nextWidth = Math.Min(nextWidth, width - available + (exactThreshold?.Invoke(candidate, size).Width ?? measured) + inset - .01);
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
        // Only the ring between the two rectangles, in the same row order: for whole numbers, x < inside.X holds
        // exactly below ceil(inside.X) and x >= inside.Right from ceil(inside.Right).
        int ringLeft = (int)outside.X, ringRight = (int)Math.Ceiling(outside.Right);
        int innerLeft = (int)Math.Ceiling(inside.X), innerRight = (int)Math.Ceiling(inside.Right);
        for (int y = (int)outside.Y; y < (int)Math.Ceiling(outside.Bottom); y++)
        {
            bool beside = y >= inside.Y && y < inside.Bottom;
            int leftEnd = beside ? Math.Min(ringRight, innerLeft) : ringRight;
            int rightStart = beside ? Math.Max(Math.Max(ringLeft, leftEnd), innerRight) : ringRight;
            for (int x = ringLeft; x < leftEnd; x++)
                AddBackgroundSample(bgra, y * stride + x * 4, histogram, ref count, ref red, ref green, ref blue, ref squares);
            for (int x = rightStart; x < ringRight; x++)
                AddBackgroundSample(bgra, y * stride + x * 4, histogram, ref count, ref red, ref green, ref blue, ref squares);
        }
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

    internal static Color EstimateTextColor(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<OcrLine> lines, Color background)
        => EstimateTextColor(bgra, width, height, stride, lines, background, new InkDistances());

    /// <summary>
    /// The median colour of the fifth of the line samples farthest in luminance from the background, ties going
    /// to the sample scanned first. Selection finds the fifth's least distance, so no sample list is sorted or kept.
    /// </summary>
    private static Color EstimateTextColor(byte[] bgra, int width, int height, int stride,
        IReadOnlyList<OcrLine> lines, Color background, InkDistances scratch)
    {
        double backgroundLuminance = Luminance(background);
        int sampleCount = 0;
        foreach (OcrLine line in lines) sampleCount += new InkGrid(Clamp(line.Bounds, width, height)).Count;
        if (sampleCount == 0) return EnsureContrast(background, background);
        Span<double> distances = scratch.Take(sampleCount);
        int sample = 0, firstRgb = -1;
        bool uniform = true;
        foreach (OcrLine line in lines)
        {
            var grid = new InkGrid(Clamp(line.Bounds, width, height));
            for (int y = grid.Top; y < grid.Bottom; y += grid.Step)
                for (int x = grid.Left; x < grid.Right; x += grid.Step)
                {
                    int index = y * stride + x * 4;
                    int r = bgra[index + 2], g = bgra[index + 1], b = bgra[index];
                    int rgb = (r << 16) | (g << 8) | b;
                    if (sample == 0) firstRgb = rgb;
                    else uniform &= rgb == firstRgb;
                    distances[sample++] = InkDistance(r, g, b, backgroundLuminance);
                }
        }
        if (uniform)
            return EnsureContrast(Color.FromRgb((byte)(firstRgb >> 16), (byte)(firstRgb >> 8), (byte)firstRgb), background);
        int count = Math.Max(1, sampleCount / 5);
        double least = KthLargest(distances, count);
        int ties = count;
        foreach (double distance in distances)
            if (distance > least) ties--;
        Span<int> histogram = stackalloc int[768];
        histogram.Clear();
        foreach (OcrLine line in lines)
        {
            var grid = new InkGrid(Clamp(line.Bounds, width, height));
            for (int y = grid.Top; y < grid.Bottom; y += grid.Step)
                for (int x = grid.Left; x < grid.Right; x += grid.Step)
                {
                    int index = y * stride + x * 4;
                    int r = bgra[index + 2], g = bgra[index + 1], b = bgra[index];
                    double distance = InkDistance(r, g, b, backgroundLuminance);
                    if (distance < least) continue;
                    if (distance == least)
                    {
                        if (ties == 0) continue;
                        ties--;
                    }
                    histogram[r]++; histogram[256 + g]++; histogram[512 + b]++;
                }
        }
        return EnsureContrast(HistogramColor(histogram, count), background);
    }

    private static double InkDistance(int r, int g, int b, double backgroundLuminance) =>
        Math.Abs(.2126 * LinearChannels[r] + .7152 * LinearChannels[g] + .0722 * LinearChannels[b] - backgroundLuminance);

    /// <summary>The pixels a line contributes to its text colour: every pixel of a short line, about 4096 of a long one.</summary>
    private readonly struct InkGrid
    {
        internal InkGrid(OcrRect rect)
        {
            Step = Math.Max(1, (int)Math.Sqrt(rect.Width * rect.Height / 4096));
            Left = (int)rect.X;
            Top = (int)rect.Y;
            Right = (int)Math.Ceiling(rect.Right);
            Bottom = (int)Math.Ceiling(rect.Bottom);
        }

        internal int Step { get; }
        internal int Left { get; }
        internal int Top { get; }
        internal int Right { get; }
        internal int Bottom { get; }

        internal int Count => Right > Left && Bottom > Top
            ? (Right - Left + Step - 1) / Step * ((Bottom - Top + Step - 1) / Step) : 0;
    }

    /// <summary>One distance buffer for the text colours of every block of a preparation.</summary>
    private sealed class InkDistances
    {
        private double[] _values = [];

        internal Span<double> Take(int count)
        {
            if (_values.Length < count) _values = GC.AllocateUninitializedArray<double>(count);
            return _values.AsSpan(0, count);
        }
    }

    /// <summary>The <paramref name="k"/>-th largest of <paramref name="values"/>, counting from one; reorders them.</summary>
    private static double KthLargest(Span<double> values, int k)
    {
        int low = 0, high = values.Length - 1, target = k - 1, rounds = 0;
        while (low < high)
        {
            if (++rounds > 64)
            {
                // Pathological orders only; sorting what is left is exact as well.
                values[low..(high + 1)].Sort();
                return values[high - (target - low)];
            }
            double first = values[low], middle = values[low + (high - low) / 2], last = values[high];
            double pivot = Math.Max(Math.Min(first, middle), Math.Min(Math.Max(first, middle), last));
            // Larger values move to the front and smaller ones to the back; the pivot's equals stay between.
            int larger = low, cursor = low, smaller = high;
            while (cursor <= smaller)
            {
                double value = values[cursor];
                if (value > pivot)
                {
                    values[cursor++] = values[larger];
                    values[larger++] = value;
                }
                else if (value < pivot)
                {
                    values[cursor] = values[smaller];
                    values[smaller--] = value;
                }
                else cursor++;
            }
            if (target < larger) high = larger - 1;
            else if (target > smaller) low = smaller + 1;
            else return pivot;
        }
        return values[low];
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
        Action<PinOverlayBlock, PinOverlayFit, bool>? observeFit = null,
        Action<PinOverlayBlock, string>? observeDecision = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translations);
        using PinOverlayPreparation preparation = Prepare(source, translations.Select(item => item.Block).ToArray(),
            index => EarlyDecision(translations[index]) is null);
        return Compose(preparation, translations, drawText, observeFit, observeDecision);
    }

    /// <summary>Translation-independent placement inputs of one block.</summary>
    internal sealed record BlockLayout(PinOverlayBackground[] Backgrounds, Color Background, double SourcePitch,
        TextAlignment Alignment, Color Foreground)
    {
        internal BackgroundSurface Surface { get; init; } = null!;
        internal OcrRect[] Obstacles { get; init; } = [];
        internal OcrRect HorizontalLimits { get; init; }
    }

    /// <summary>
    /// Samples font sizes, ring colours, surfaces, columns and text colours before the translation
    /// exists. Only blocks that <paramref name="needsLayout"/> selects get a layout. The preparation
    /// takes over <paramref name="sourcePixels"/>, the copy the block scan returned, when it is given.
    /// </summary>
    internal static PinOverlayPreparation Prepare(BitmapSource source, IReadOnlyList<PinOverlayBlock> blocks,
        Func<int, bool>? needsLayout = null, CancellationToken cancellationToken = default, byte[]? sourcePixels = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(blocks);
        int width = source.PixelWidth, height = source.PixelHeight, stride = checked(width * 4);
        if (sourcePixels is not null && sourcePixels.Length != checked(stride * height))
            throw new ArgumentException("The pixels are not a Bgra32 copy of the source.", nameof(sourcePixels));
        var preparation = new PinOverlayPreparation(source, blocks, sourcePixels ?? Bgra32Pixels(source), width, height, stride);
        try
        {
            byte[] pixels = preparation.Pixels;
            preparation.FontSizes = EstimateFontSizes(blocks, pixels, width, height, stride);
            OcrRect[] protectedRegions = blocks.SelectMany(block => block.ProtectedRegions).ToArray();
            var inkDistances = new InkDistances();
            for (int index = 0; index < blocks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PinOverlayBlock block = blocks[index];
                if (block.PreservePixels || needsLayout?.Invoke(index) == false) continue;
                // Other blocks and protected regions only; composition checks the earlier placements.
                OcrRect[] obstacles = blocks.Where((_, otherIndex) => otherIndex != index)
                    .SelectMany(other => other.Lines.Select(line => line.Bounds))
                    .Where(region => !block.ProtectedRegions.Contains(region))
                    .Concat(protectedRegions.Where(region => !block.ProtectedRegions.Contains(region))).ToArray();
                preparation.Layouts[index] = PrepareLayout(preparation, index, obstacles, inkDistances);
            }
            return preparation;
        }
        catch
        {
            preparation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A new Bgra32 copy with rows of width × 4 bytes. It is not pooled: returned on a pool thread,
    /// a picture-sized buffer would stay cached after the pin closes.
    /// </summary>
    internal static byte[] Bgra32Pixels(BitmapSource source)
    {
        int stride = checked(source.PixelWidth * 4);
        BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * source.PixelHeight));
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BlockLayout PrepareLayout(PinOverlayPreparation preparation, int index, OcrRect[] obstacles, InkDistances inkDistances)
    {
        byte[] pixels = preparation.Pixels;
        int width = preparation.Width, height = preparation.Height, stride = preparation.Stride;
        PinOverlayBlock block = preparation.Blocks[index];
        PinOverlayBackground[] backgrounds = block.Lines.Select(line =>
            EstimateBackground(pixels, width, height, stride, line.Bounds)).ToArray();
        Color background = MedianColor(backgrounds.Select(value => value.Color).ToArray());
        var layout = new BlockLayout(backgrounds, background, SourceLinePitch(block.Lines), InferAlignment(block.Lines),
            EstimateTextColor(pixels, width, height, stride, block.Lines, background, inkDistances));
        return WithSurface(layout, preparation, index, obstacles);
    }

    private static BlockLayout WithSurface(BlockLayout layout, PinOverlayPreparation preparation, int index, OcrRect[] obstacles)
    {
        var surface = new BackgroundSurface(preparation.Pixels, preparation.Width, preparation.Height, preparation.Stride,
            preparation.Blocks[index], layout.Background, obstacles);
        return layout with
        {
            Surface = surface,
            Obstacles = obstacles,
            HorizontalLimits = EstablishedColumnLimits(surface.HorizontalLimits(layout.Alignment), preparation.Blocks,
                preparation.FontSizes, index, layout.Alignment),
        };
    }

    private static string? EarlyDecision(PinOverlayTranslation item) => item.Block.PreservePixels ? "protected"
        : item.Text is null ? "all-rejected"
        : string.Equals(NormalizeWhitespace(item.Text), NormalizeWhitespace(item.Block.Text), StringComparison.Ordinal) ? "identity"
        : null;

    /// <summary>Places the translations, which must follow the prepared blocks in order.</summary>
    internal static PinOverlayComposition Compose(PinOverlayPreparation preparation, IReadOnlyList<PinOverlayTranslation> translations,
        bool drawText = true, Action<PinOverlayBlock, PinOverlayFit, bool>? observeFit = null,
        Action<PinOverlayBlock, string>? observeDecision = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(translations);
        if (translations.Count != preparation.Blocks.Count
            || Enumerable.Range(0, translations.Count).Any(index => !ReferenceEquals(translations[index].Block, preparation.Blocks[index])))
            throw new ArgumentException("The translations must follow the prepared blocks.", nameof(translations));
        BitmapSource source = preparation.Source;
        int width = preparation.Width, height = preparation.Height;
        var draws = new List<(PinOverlayFit Fit, PinOverlayBackground[] Backgrounds, Color Color, PinOverlayBlock Block,
            double InkTop, double PitchScale, TextAlignment Alignment, Geometry? Clip, BackgroundSurface Surface)>();
        var metrics = new CompositionMetrics();
        var inkBounds = new Dictionary<(string Text, double Size, double Pitch), Rect>();
        var paragraphMetrics = new Dictionary<(string Text, double Size, double Pitch), Size>();
        var renderedText = new Dictionary<(string Text, double Size, Color Color, double Pitch, TextAlignment Alignment, double Width, bool Underline), FormattedText>();
        var inkDistances = new InkDistances();
        Rect PlacementBounds(string text, double size, double pitch)
        {
            var key = (text, size, pitch);
            if (!inkBounds.TryGetValue(key, out Rect ink))
            {
                ink = Format(text, size, Brushes.Black, pitch * size).BuildGeometry(new Point()).Bounds;
                inkBounds.Add(key, ink);
            }
            return ink;
        }
        Size MeasureParagraph(string text, double size, double pitch)
        {
            if (pitch == 0) return metrics.MeasureFinal(text, size);
            var key = (text, size, pitch);
            if (!paragraphMetrics.TryGetValue(key, out Size measured))
            {
                FormattedText formatted = Format(text, size, Brushes.Black, pitch * size);
                measured = new(formatted.WidthIncludingTrailingWhitespace, InkHeight(formatted, size));
                paragraphMetrics.Add(key, measured);
            }
            return measured;
        }
        bool needsReview = false;
        double[] fontSizes = preparation.FontSizes;
        OcrRect[] protectedRegions = translations.SelectMany(item => item.Block.ProtectedRegions).ToArray();
        for (int index = 0; index < translations.Count; index++)
        {
            PinOverlayTranslation item = translations[index];
            if (EarlyDecision(item) is { } skipped)
            { observeDecision?.Invoke(item.Block, skipped); continue; }
            string translated = item.Text!; // Only a block with a translation gets past the early decision.
            // Small source screenshots already have ink below the ordinary minimum.
            // Preserve their capped scale instead of either enlarging or rejecting them.
            double minimumEm = Math.Min(MinimumFontSize, fontSizes[index]);
            OcrRect[] obstacles = translations.Where(other => !ReferenceEquals(other, item))
                .SelectMany(other => other.Block.Lines.Select(line => line.Bounds))
                .Where(region => !item.Block.ProtectedRegions.Contains(region))
                .Concat(protectedRegions.Where(region => !item.Block.ProtectedRegions.Contains(region)))
                .Concat(draws.Select(draw => draw.Fit.Bounds)).ToArray();
            // A surface sampled before the translation stays exact unless an earlier placement touches it.
            BlockLayout layout = preparation.Layouts[index] is not { } prepared ? PrepareLayout(preparation, index, obstacles, inkDistances)
                : prepared.Surface.Admits(prepared.Obstacles, obstacles) ? prepared : WithSurface(prepared, preparation, index, obstacles);
            PinOverlayBackground[] backgrounds = layout.Backgrounds;
            Color background = layout.Background;
            BackgroundSurface surface = layout.Surface;
            double sourcePitch = layout.SourcePitch;
            double pitchScale = sourcePitch > 0 ? sourcePitch / fontSizes[index] : 0;
            TextAlignment alignment = layout.Alignment;
            OcrRect horizontalLimits = layout.HorizontalLimits;
            Size FinalMeasure(string text, double size) => MeasureParagraph(text, size, pitchScale);
            Size MeasurePlacement(string text, double size) =>
                new(FinalMeasure(text, size).Width, PlacementBounds(text, size, pitchScale).Height);
            PinOverlayFit? fit = Fit(translated, item.Block.Bounds, fontSizes[index],
                width, height, Measure, obstacles, item.Block.Lines.Count,
                sourcePitch > 0 ? sourcePitch : metrics.LineHeight(fontSizes[index]),
                FinalMeasure, metrics.MeasureWidth, MeasurePlacement, alignment, horizontalLimits, surface.CanOccupy,
                textExclusions: surface.TextExclusions, minimumFontSize: minimumEm);
            // A short label can have a dropdown or the end of its pill immediately to the
            // right. Its unused left padding is legal only after the usual fit has failed.
            if (fit is null && item.Block.Lines.Count == 1 && alignment == TextAlignment.Left
                && surface.TextExclusions.Count == 0)
                fit = Fit(translated, item.Block.Bounds, fontSizes[index], width, height,
                    Measure, obstacles, 1, metrics.LineHeight(fontSizes[index]),
                    FinalMeasure, metrics.MeasureWidth, MeasurePlacement, TextAlignment.Right,
                    surface.HorizontalLimits(TextAlignment.Right), surface.CanOccupy, minimumFontSize: minimumEm);
            if (fit is null)
            { needsReview = true; observeDecision?.Invoke(item.Block, "fit"); continue; }
            // Reuse the chosen output's exact placement bounds (including any boundary validation)
            // to retain the round-two ink rectangle and WPF's single-precision vertical position.
            Rect ink = PlacementBounds(fit.RenderText, fit.FontSize, pitchScale);
            OcrRect original = Clamp(item.Block.Bounds, width, height);
            OcrRect finalBounds = fit.Bounds with { Height = Math.Max(original.Height, ink.Height) };
            fit = fit with { Bounds = finalBounds,
                Grew = finalBounds.Width > original.Width + .01 || finalBounds.Height > original.Height + .01 };
            if (obstacles.Any(obstacle => Intersects(EraseBounds(fit.Bounds, width, height), obstacle))
                || !surface.CanOccupy(fit.Bounds))
            { needsReview = true; observeDecision?.Invoke(item.Block, "fit"); continue; }
            Color foreground = layout.Foreground;
            OcrRect clipBounds = surface.ExtraInk.Aggregate(EraseBounds(fit.Bounds, width, height), OcrRect.Union);
            draws.Add((fit, backgrounds, foreground, item.Block, ink.Y, pitchScale, alignment,
                surface.DrawingClip(clipBounds), surface));
            observeDecision?.Invoke(item.Block, "rendered");
        }
        if (draws.Count == 0) return new(source, null, needsReview, 0);
        var layer = new DrawingGroup();
        using (DrawingContext drawing = layer.Open())
        {
            // Native pixels even when the source has non-square or non-96 DPI; Layered and Flatten scale the layer.
            foreach (var draw in draws)
            {
                if (draw.Clip is not null) drawing.PushClip(draw.Clip);
                Color background = MedianColor(draw.Backgrounds.Select(value => value.Color).ToArray());
                Color foreground = draw.Color;
                // Erase the complete source ink before putting any translucent surface over it.
                // Snap outwards to physical pixels so fractional OCR edges cannot leave antialiased remnants.
                for (int i = 0; i < draw.Block.Lines.Count; i++)
                {
                    OcrRect erase = EraseBounds(draw.Block.Lines[i].Bounds, width, height);
                    drawing.DrawRectangle(draw.Surface.Brush(erase, draw.Backgrounds[i].Color), null, Rect(erase));
                }
                foreach (OcrRect extra in draw.Surface.ExtraInk)
                    drawing.DrawRectangle(draw.Surface.Brush(extra, background), null, Rect(extra));
                bool needsPlate = draw.Backgrounds.Any(value => value.NeedsPlate || Contrast(value.Color, background) > 1.5
                    || Contrast(foreground, value.Color) < 4.5);
                observeFit?.Invoke(draw.Block, draw.Fit, needsPlate);
                if (needsPlate)
                {
                    (foreground, double opacity) = PlateStyle(background, foreground);
                    OcrRect outer = Inflate(draw.Fit.Bounds, Padding, width, height);
                    drawing.DrawRoundedRectangle(draw.Surface.Brush(outer, background, opacity * .2), null, Rect(outer), 3, 3);
                    OcrRect inner = Inflate(draw.Fit.Bounds, Padding - 1, width, height);
                    drawing.DrawRoundedRectangle(draw.Surface.Brush(inner, background, opacity), null, Rect(inner), 2, 2);
                }
                if (drawText)
                {
                    var key = (draw.Fit.RenderText, draw.Fit.FontSize, foreground, draw.PitchScale, draw.Alignment,
                        draw.Alignment == TextAlignment.Left ? 0 : draw.Fit.Bounds.Width, draw.Surface.Underline);
                    if (!renderedText.TryGetValue(key, out FormattedText? text))
                    {
                        text = Format(draw.Fit.RenderText, draw.Fit.FontSize, new SolidColorBrush(foreground),
                            draw.PitchScale * draw.Fit.FontSize);
                        if (draw.Surface.Underline) text.SetTextDecorations(TextDecorations.Underline);
                        if (draw.Alignment != TextAlignment.Left)
                        {
                            text.TextAlignment = draw.Alignment;
                            // Wrapping was already selected at the legal break points by Fit.
                            text.MaxTextWidth = draw.Fit.Bounds.Width + .02;
                        }
                        renderedText.Add(key, text);
                    }
                    Point origin = new(draw.Fit.Bounds.X, draw.Fit.Bounds.Y - draw.InkTop);
                    if (draw.Fit.LineOffsets.Count > 0)
                    {
                        string[] lines = draw.Fit.RenderText.Split('\n');
                        double pitch = draw.PitchScale * draw.Fit.FontSize;
                        if (pitch <= 0) pitch = Format("Ag", draw.Fit.FontSize, Brushes.Black).Height;
                        for (int i = 0; i < lines.Length; i++)
                        {
                            // PlacementBounds includes the source line pitch in the first
                            // baseline. Keep it when rendering separate exclusion-aware lines,
                            // especially when the source ink cap makes the em much smaller.
                            var line = Format(lines[i], draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                            if (draw.Surface.Underline) line.SetTextDecorations(TextDecorations.Underline);
                            drawing.DrawText(line, new Point(origin.X + draw.Fit.LineOffsets[i], origin.Y + i * pitch));
                        }
                    }
                    else if (draw.Surface.LeadingInset > 0 && draw.Alignment == TextAlignment.Left)
                    {
                        int newline = draw.Fit.RenderText.IndexOf('\n');
                        string first = newline < 0 ? draw.Fit.RenderText : draw.Fit.RenderText[..newline];
                        double pitch = draw.PitchScale * draw.Fit.FontSize;
                        var firstLine = Format(first, draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                        if (draw.Surface.Underline) firstLine.SetTextDecorations(TextDecorations.Underline);
                        drawing.DrawText(firstLine, new Point(origin.X + draw.Surface.LeadingInset, origin.Y));
                        if (newline >= 0)
                        {
                            var rest = Format(draw.Fit.RenderText[(newline + 1)..], draw.Fit.FontSize, new SolidColorBrush(foreground), pitch);
                            if (draw.Surface.Underline) rest.SetTextDecorations(TextDecorations.Underline);
                            drawing.DrawText(rest, new Point(origin.X, origin.Y + (pitch > 0 ? pitch : firstLine.Height)));
                        }
                    }
                    else drawing.DrawText(text, origin);
                }
                if (draw.Clip is not null) drawing.Pop();
            }
        }
        // Frozen, the layer leaves the thread that measured it for the pin's UI thread.
        layer.Freeze();
        return new(source, layer, needsReview, draws.Count);
    }

    /// <summary>
    /// The source and the layer as one drawing at the source's DIP size. The source keeps its own bitmap
    /// (the texture the untranslated pin already uses); the layer is drawn as vectors over it.
    /// </summary>
    internal static ImageSource Layered(BitmapSource source, Drawing layer)
    {
        var bounds = new Rect(0, 0, source.Width, source.Height);
        var layers = new DrawingGroup();
        // Text can reach past the picture's edge, where a bitmap cut it off. Only then does a clip do the same
        // and keep the drawing's bounds, and with them the Image element's size, at exactly the picture's size:
        // WPF draws a clipped group through a picture-sized intermediate surface whose video memory the driver
        // keeps after the pin closes.
        Rect drawn = layer.Bounds;
        if (!drawn.IsEmpty && (drawn.Left < 0 || drawn.Top < 0 || drawn.Right > source.PixelWidth || drawn.Bottom > source.PixelHeight))
            layers.ClipGeometry = new RectangleGeometry(bounds);
        layers.Children.Add(new ImageDrawing(source, bounds));
        layers.Children.Add(new DrawingGroup
        {
            Transform = new ScaleTransform(96 / source.DpiX, 96 / source.DpiY),
            Children = { layer },
        });
        var image = new DrawingImage(layers);
        image.Freeze();
        return image;
    }

    /// <summary>One window-free WPF render of the source with the layer, in native pixels.</summary>
    internal static BitmapSource Flatten(BitmapSource source, Drawing layer)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            // Work in native pixels even when the source has non-square or non-96 DPI.
            drawing.PushTransform(new ScaleTransform(96 / source.DpiX, 96 / source.DpiY));
            drawing.DrawImage(source, new Rect(0, 0, width, height));
            drawing.DrawDrawing(layer);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// The em the source pixels show: each word's measured ink rows divided by that word's
    /// glyph ink at em 1, so a word without descenders reports the same em as one with them.
    /// Zero when no word has measurable ink.
    /// </summary>
    internal static double SourceInkEm(PinOverlayBlock block, byte[] pixels, int width, int height, int stride) =>
        SourceInkEm(block, pixels, width, height, stride, NewWordInk());

    private static double SourceInkEm(PinOverlayBlock block, byte[] pixels, int width, int height, int stride, Func<string, double> unitInk)
    {
        var heights = new List<double>();
        foreach (OcrLine line in block.Lines)
        {
            Color background = EstimateBackground(pixels, width, height, stride, line.Bounds).Color;
            var words = new List<double>();
            IEnumerable<(OcrRect Bounds, string Text)> areas = line.Words.Count > 0
                ? line.Words.Select(word => (word.Bounds, string.IsNullOrWhiteSpace(word.Text) ? line.Text : word.Text))
                : [(line.Bounds, line.Text)];
            foreach ((OcrRect area, string text) in areas)
            {
                OcrRect bounds = Clamp(area, width, height);
                if (!OcrGeometry.IsValid(bounds)) continue;
                int top = int.MaxValue, bottom = -1;
                int firstRow = (int)bounds.Y;
                var rowContrast = new double[(int)Math.Ceiling(bounds.Bottom) - firstRow];
                double strongest = 0;
                for (int y = (int)bounds.Y; y < (int)Math.Ceiling(bounds.Bottom); y++)
                    for (int x = (int)bounds.X; x < (int)Math.Ceiling(bounds.Right); x++)
                    {
                        double contrast = PixelVariance(pixels, y * stride + x * 4, background);
                        rowContrast[y - firstRow] = Math.Max(rowContrast[y - firstRow], contrast);
                        strongest = Math.Max(strongest, contrast);
                    }
                if (strongest < 4) continue;
                // A row is ink when its strongest pixel reaches half the word's contrast
                // (variance is squared, so a quarter). Antialiased edge rows below half
                // coverage are blur, not ink; counting them made every estimate too large.
                double threshold = strongest * .25;
                for (int row = 0; row < rowContrast.Length; row++)
                    if (rowContrast[row] >= threshold)
                    { top = Math.Min(top, firstRow + row); bottom = Math.Max(bottom, firstRow + row); }
                if (bottom >= top) words.Add((bottom - top + 1) / unitInk(text));
            }
            // A checkbox, superscript or inflated OCR union must not set the text size.
            // Word medians also avoid treating short translations as a reason to grow.
            if (words.Count > 0) heights.Add(Median(words));
        }
        return heights.Count > 0 ? Median(heights) : 0;
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

    private static FormattedText Format(string text, double size, Brush color, double lineHeight = 0) => new(text,
        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, size, color, 1)
    {
        Trimming = TextTrimming.None,
        LineHeight = lineHeight,
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
