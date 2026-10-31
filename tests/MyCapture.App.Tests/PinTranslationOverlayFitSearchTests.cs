using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayFitSearchTests
{
    [Fact]
    public void NarrowPassageKeepsTheFirstSuccessfulEightPixelWidth()
    {
        string text = new('가', 300);
        OcrRect[] obstacles = [new(100, 100, 40, 20), new(220, 24, 70, 240)];
        PinOverlayFit expected = Assert.IsType<PinOverlayFit>(RoundTwoFit(text, new(10, 10, 50, 10), 10, 300, 300, obstacles));
        Assert.Equal(9, expected.FontSize);
        Assert.Equal(new OcrRect(10, 10, 54, 270), expected.Bounds);
        Assert.Equal(string.Join("\n", Enumerable.Repeat(new string('가', 12), 25)), expected.RenderText);
        Assert.Equal(expected, PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10, 300, 300, Measure, obstacles));
    }

    [Fact]
    public void QuantizedSearchMatchesRoundTwoAcrossObstaclesAndLegalBreaks()
    {
        string[] texts = ["Show notifications before closing the window", "Automatic", "well-known setting with word-breaks",
            "문서를 저장합니다 그리고 원본을 보관합니다", new('가', 60), "가나다라마바사아자차카타",
            "Latin 한글 words remain together", new('가', 300)];
        OcrRect[][] blockers = [[], [new(120, 10, 90, 24)], [new(100, 24, 190, 240)],
            [new(10, 96, 210, 35)], [new(100, 100, 40, 20), new(220, 24, 70, 240)],
            [new(91.99, 43.01, 100, 24), new(10, 160, 50, 20)]];
        int cases = 0;
        foreach (string text in texts)
            foreach (double em in new[] { 9d, 10d, 20d, 36d })
                foreach (double width in new[] { 35d, 50d, 90d })
                    foreach (double height in new[] { 10d, 22.01d, 45d })
                        foreach (OcrRect[] obstacles in blockers)
                        {
                            int sourceLines = cases % 2 + 1;
                            double sourceHeight = em * 1.2;
                            var block = new OcrRect(10, 10, width, height);
                            PinOverlayFit? expected = RoundTwoFit(text, block, em, 300, 300, obstacles, sourceLines, sourceHeight);
                            PinOverlayFit? actual = PinTranslationOverlay.Fit(text, block, em, 300, 300, Measure, obstacles, sourceLines, sourceHeight);
                            Assert.True(Equals(expected, actual), $"Case {cases}: text '{text}', em {em}, bounds {block}, source lines {sourceLines}; "
                                + $"expected {expected}; actual {actual}.");
                            cases++;
                        }
        Assert.Equal(1_728, cases);
    }

    [Theory]
    [InlineData(-0.011, "Save\nfile")]
    [InlineData(-0.009, "Save file")]
    [InlineData(0, "Save file")]
    [InlineData(0.009, "Save file")]
    public void WrappingKeepsTheOriginalOneHundredthPixelTolerance(double delta, string expected)
    {
        double width = Measure("Save file", 20, double.PositiveInfinity).Width + delta;
        Assert.Equal(expected, PinTranslationOverlay.Wrap("Save file", 20, width, Measure));
    }

    [Fact]
    public void RewrappingCannotHideAnEarlierSuccessfulFontSize()
    {
        static Size InkMeasure(string text, double size, double width)
        {
            string[] lines = text.Split('\n');
            return new(lines.Max(line => line.Length) * size * .5,
                size * ((lines.Length - 1) * 1.2 + (lines[0].Contains('^') ? 1 : .6) + (lines[^1].Contains('g') ? .2 : 0)));
        }
        // 30 fails, 28.5 fits; 27.075 moves ^ onto the first line and fails again.
        // A single monotone search over the complete decision would skip 28.5.
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit("AAAA ^ gg", new(10, 10, 82, 58),
            30, 92, 200, InkMeasure, sourceLineCount: 2, sourceLineHeight: 29));
        Assert.Equal(28.5, fit.FontSize);
        Assert.Equal(new OcrRect(10, 10, 82, 58), fit.Bounds);
        Assert.Equal("AAAA\n^ gg", fit.RenderText);
        Assert.False(fit.Grew);
    }

    [Fact]
    public void LayoutMetricsMatchRoundTwoInkForMixedScripts() => StaTestHost.Run(() =>
    {
        string[] texts = ["Save document", "Copy picture", "gypqj", "ÉÉÉÉÉÉÉ", "AVATAR To office ffi",
            "well-known value ‐ keep-original", "문서를 저장합니다", "가나다라마바", "Latin 한글 mixed",
            "Cafe\u0301 nai\u0308ve", "가나다", "Save document\nCopy picture", "ÉÉÉ\njg",
            "한글\nLatin descenders", "Latin\n한글", "\nSave\n", "Save\n\npicture",
            "\n\nSave", "Save\n\n", " \nSave\n \n", "    Save    ", "\tSave\t\n"];
        var differences = new List<string>();
        foreach (string text in texts)
            foreach (double size in new[] { 1d, 7d, 9d, 20d, 26.375d, 60d })
            {
                Size expected = GeometryMeasure(text, size, double.PositiveInfinity);
                Size actual = PinTranslationOverlay.Measure(text, size, double.PositiveInfinity);
                if (Math.Abs(expected.Width - actual.Width) > .0000000001 || Math.Abs(expected.Height - actual.Height) > .0001)
                    differences.Add($"'{text.Replace("\n", "\\n", StringComparison.Ordinal)}', em {size}: "
                        + $"geometry {expected.Width:R} × {expected.Height:R}, layout {actual.Width:R} × {actual.Height:R}.");
            }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    });

    [Fact]
    public void CachedCompositionKeepsGeometryOracleDecisions() => StaTestHost.Run(() =>
    {
        const string sourceText = "Save document";
        string[] translations = ["Show notifications before closing the window", "ÉÉÉÉÉÉÉ", "Latin 한글 mixed",
            "문서를 저장합니다 그리고 원본을 보관합니다", new('가', 60), "well-known setting with word-breaks",
            "Cafe\u0301 nai\u0308ve", "가나다라마바"];
        foreach (double sourceEm in new[] { 9d, 20d, 36d })
            foreach (double sourceWidth in new[] { 45.2, 82.99, 160.2 })
            {
                double sourceInk = GeometryMeasure(sourceText, sourceEm, double.PositiveInfinity).Height;
                double estimated = Math.Clamp(sourceInk / GeometryMeasure(sourceText, 1, double.PositiveInfinity).Height, 9, 32768);
                OcrLine line = PinTranslationOverlayTests.Line(sourceText, 10, 10, sourceWidth, sourceInk);
                var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
                var image = PinTranslationOverlayTests.Bitmap(300, 200);
                foreach (string text in translations)
                {
                    PinOverlayFit? expected = RoundTwoFit(text, block.Bounds, estimated, 300, 200, [],
                        sourceLineHeight: Format("Ag", estimated).Height, measureOverride: GeometryMeasure);
                    PinOverlayFit? actual = null;
                    PinOverlayComposition composition = PinTranslationOverlay.Compose(image, [new(block, text)],
                        observeFit: (_, fit, _) => actual = fit);
                    Assert.Equal(expected is null, actual is null);
                    Assert.Equal(expected is null, composition.NeedsReview);
                    Assert.Equal(expected is null ? 0 : 1, composition.RenderedBlocks);
                    if (expected is null || actual is null) continue;
                    Assert.Equal(expected.FontSize, actual.FontSize, 10);
                    Assert.Equal(expected.RenderText, actual.RenderText);
                    Assert.Equal(expected.Grew, actual.Grew);
                    Assert.Equal(expected.Bounds.X, actual.Bounds.X, 6);
                    Assert.Equal(expected.Bounds.Y, actual.Bounds.Y, 6);
                    Assert.Equal(expected.Bounds.Width, actual.Bounds.Width, 6);
                    Assert.Equal(expected.Bounds.Height, actual.Bounds.Height, 6);
                }
            }
    });

    [Fact]
    public void FractionalGrowDownGridsKeepTheOriginalCollisionBoundary()
    {
        string text = new('가', 300);
        foreach (double x in new[] { 10d, 10.2, 10.99 })
            foreach (double width in new[] { 49.99, 50d, 50.2, 50.99 })
                foreach (double obstacleX in new[] { 99.99, 100d, 100.2, 100.99 })
                {
                    var bounds = new OcrRect(x, 10.2, width, 10.99);
                    OcrRect[] obstacles = [new(obstacleX, 100.2, 40, 20), new(220.99, 24.2, 70, 240)];
                    PinOverlayFit? expected = RoundTwoFit(text, bounds, 10, 300, 300, obstacles);
                    PinOverlayFit? actual = PinTranslationOverlay.Fit(text, bounds, 10, 300, 300, Measure, obstacles);
                    Assert.True(Equals(expected, actual), $"Bounds {bounds}, obstacle X {obstacleX}: expected {expected}; actual {actual}.");
                }
    }

    [Fact]
    public void FractionalInkHeightCannotMoveTheErasedEdgeIntoANeighbor() => StaTestHost.Run(() =>
    {
        const string sourceText = "Save document";
        int checkedBoundaries = 0;
        foreach ((string text, double targetEm) in new[] { ("Copy picture", 27.364738), ("Copy picture", 24.01), ("Show notifications", 20.1) })
        {
            double unitInk = GeometryMeasure(sourceText, 1, double.PositiveInfinity).Height;
            double sourceHeight = unitInk * targetEm;
            double estimated = sourceHeight / unitInk;
            double inkHeight = GeometryMeasure(text, estimated, double.PositiveInfinity).Height;
            // Independent reproduction of the fast layout metric before its float-boundary correction.
            double rawLayoutHeight = Format(text, estimated).Extent - 2;
            if (rawLayoutHeight <= inkHeight || inkHeight <= sourceHeight) continue;
            double y = 100 - inkHeight - 2;
            OcrLine source = PinTranslationOverlayTests.Line(sourceText, 10, y, 250, sourceHeight);
            OcrLine neighbor = PinTranslationOverlayTests.Line(sourceText, 10, 100, 250, sourceHeight);
            var block = new PinOverlayBlock(source.Text, source.Bounds, [source]);
            var blocked = new PinOverlayBlock(neighbor.Text, neighbor.Bounds, [neighbor]);
            PinOverlayFit expected = Assert.IsType<PinOverlayFit>(RoundTwoFit(text, source.Bounds, estimated, 500, 200, [neighbor.Bounds],
                sourceLineHeight: Format("Ag", estimated).Height, measureOverride: GeometryMeasure));
            Assert.Equal(estimated, expected.FontSize, 10);
            Assert.Equal(100, Math.Ceiling(expected.Bounds.Bottom + 2));
            PinOverlayFit? actual = null;
            PinOverlayComposition composition = PinTranslationOverlay.Compose(PinTranslationOverlayTests.Bitmap(500, 200),
                [new(block, text), new(blocked, null)], observeFit: (_, fit, _) => actual = fit);
            Assert.True(Equals(expected, actual), $"'{text}', em {estimated:R}, geometry {inkHeight:R}, raw layout {rawLayoutHeight:R}: "
                + $"expected {expected}; actual {actual}.");
            Assert.False(composition.NeedsReview);
            Assert.Equal(1, composition.RenderedBlocks);
            checkedBoundaries++;
        }
        Assert.True(checkedBoundaries > 0, "The fixture must exercise at least one positive layout/geometry rounding difference.");
    });

    private static FormattedText Format(string text, double size) => new(text, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, size, Brushes.Black, 1) { Trimming = TextTrimming.None };
    private static Size GeometryMeasure(string text, double size, double width)
    {
        FormattedText formatted = Format(text, size);
        return new(formatted.WidthIncludingTrailingWhitespace, formatted.BuildGeometry(new Point()).Bounds.Height);
    }

    private static Size Measure(string text, double fontSize, double width) => new(text.Split('\n').Max(line => line.Length) * fontSize * .5,
        text.Split('\n').Length * fontSize * 1.2);

    // Frozen reference from 16fc3590. This deliberately retains the sequential size and width
    // searches so the optimized search is checked against the previous selection order.
    private static PinOverlayFit? RoundTwoFit(string text, OcrRect block, double estimatedEm, int imageWidth, int imageHeight,
        IReadOnlyList<OcrRect> blockers, int sourceLineCount = 1, double? sourceLineHeight = null,
        Func<string, double, double, Size>? measureOverride = null)
    {
        const double minimum = 9, padding = 2;
        Func<string, double, double, Size> measure = measureOverride ?? Measure;
        if (imageWidth <= 0 || imageHeight <= 0 || !double.IsFinite(estimatedEm) || estimatedEm <= 0) return null;
        OcrRect bounds = Clamp(block, imageWidth, imageHeight);
        if (!OcrGeometry.IsValid(bounds) || string.IsNullOrWhiteSpace(text)) return null;
        double initial = Math.Clamp(estimatedEm, minimum, 32768);
        double lineAreaHeight = Math.Min(imageHeight - bounds.Y,
            Math.Max(bounds.Height, Math.Max(1, sourceLineCount) * (sourceLineHeight ?? initial * 1.2)));
        string normalized = Normalize(text);
        double maxWidth = imageWidth - bounds.X;
        foreach (OcrRect obstacle in blockers)
            if (obstacle.X >= bounds.Right - .01 && obstacle.Y < bounds.Y + lineAreaHeight + padding
                && obstacle.Bottom > bounds.Y - padding)
                maxWidth = Math.Min(maxWidth, Math.Max(bounds.Width, obstacle.X - padding * 2 - bounds.X));

        PinOverlayFit? Attempt(double size, double width, double height, int lineLimit)
        {
            string? wrapped = RoundTwoWrap(normalized, size, width, measure);
            if (wrapped is null || wrapped.Count(character => character == '\n') + 1 > lineLimit) return null;
            Size measured = measure(wrapped, size, double.PositiveInfinity);
            if (!double.IsFinite(measured.Width) || !double.IsFinite(measured.Height)
                || measured.Width > width + .01 || measured.Height > height + .01) return null;
            var fitted = bounds with { Width = Math.Max(bounds.Width, measured.Width), Height = Math.Max(bounds.Height, measured.Height) };
            if (blockers.Any(obstacle => Intersects(Erase(fitted, imageWidth, imageHeight), obstacle))) return null;
            return new(fitted, size, text, fitted.Width > bounds.Width + .01 || fitted.Height > bounds.Height + .01, wrapped);
        }

        PinOverlayFit? fit = Attempt(initial, bounds.Width, lineAreaHeight, Math.Max(1, sourceLineCount))
            ?? Attempt(initial, maxWidth, lineAreaHeight, Math.Max(1, sourceLineCount));
        if (fit is not null) return fit;
        double comfortableMinimum = Math.Max(minimum, initial * .85);
        for (double size = Math.Max(comfortableMinimum, initial * .95); ; size = Math.Max(comfortableMinimum, size * .95))
        {
            fit = Attempt(size, maxWidth, lineAreaHeight, Math.Max(1, sourceLineCount));
            if (fit is not null) return fit;
            if (size == comfortableMinimum) break;
        }
        for (double size = comfortableMinimum; ; size = Math.Max(minimum, size * .9))
        {
            fit = Attempt(size, maxWidth, lineAreaHeight, int.MaxValue);
            if (fit is not null) return fit;
            if (size == minimum) break;
        }
        PinOverlayFit? GrowDown(double width)
        {
            double bottom = imageHeight;
            foreach (OcrRect obstacle in blockers)
                if (obstacle.X < bounds.X + width + padding && obstacle.Right > bounds.X - padding
                    && obstacle.Bottom > bounds.Y && obstacle.Y >= bounds.Bottom - .01)
                    bottom = Math.Min(bottom, obstacle.Y - padding * 2);
            return Attempt(minimum, width, Math.Max(bounds.Height, bottom - bounds.Y), int.MaxValue);
        }
        fit = GrowDown(maxWidth);
        if (fit is not null) return fit;
        for (double width = bounds.Width; width < maxWidth; width += 8)
        {
            fit = GrowDown(width);
            if (fit is not null) return fit;
        }
        return null;
    }

    private static string? RoundTwoWrap(string text, double size, double width, Func<string, double, double, Size> measure)
    {
        text = Normalize(text);
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
                if (measure(candidate, size, double.PositiveInfinity).Width > width + .01) break;
                last = end;
            }
            if (last < 0) return null;
            lines.Add(text[start..last].Trim());
            start = last;
            while (start < text.Length && text[start] == ' ') start++;
        }
        return string.Join("\n", lines);
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool IsCjk(char character) => character is >= '\u1100' and <= '\u11ff'
        or >= '\u2e80' and <= '\ua4cf' or >= '\ua960' and <= '\ua97f'
        or >= '\uac00' and <= '\ud7ff' or >= '\uf900' and <= '\ufaff'
        or >= '\uff66' and <= '\uff9f';
    private static OcrRect Clamp(OcrRect value, int width, int height)
    {
        if (!OcrGeometry.IsValid(value)) return default;
        double x = Math.Clamp(value.X, 0, width), y = Math.Clamp(value.Y, 0, height);
        return new(x, y, Math.Max(0, Math.Min(width, value.Right) - x), Math.Max(0, Math.Min(height, value.Bottom) - y));
    }
    private static OcrRect Erase(OcrRect rect, int width, int height)
    {
        OcrRect inflated = Clamp(new(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), width, height);
        return new(Math.Floor(inflated.X), Math.Floor(inflated.Y),
            Math.Ceiling(inflated.Right) - Math.Floor(inflated.X), Math.Ceiling(inflated.Bottom) - Math.Floor(inflated.Y));
    }
    private static bool Intersects(OcrRect a, OcrRect b) => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
}
