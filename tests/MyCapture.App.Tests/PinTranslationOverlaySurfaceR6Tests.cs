using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlaySurfaceR6Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OnlyRepeatedParagraphEdgesConstrainColumnGrowth(int peers) => StaTestHost.Run(() =>
    {
        PinOverlayBlock Paragraph(int top, double width)
        {
            OcrLine[] lines = [PinTranslationOverlayTests.Line("first source line", 20, top, width, 16),
                PinTranslationOverlayTests.Line("second source line", 20, top + 26, width - 20, 16)];
            return new(string.Join(" ", lines.Select(line => line.Text)), new(20, top, width, 42), lines);
        }
        PinOverlayBlock target = Paragraph(200, 190);
        PinOverlayTranslation[] translations = Enumerable.Range(0, peers)
            .Select(index => new PinOverlayTranslation(Paragraph(20 + index * 70, 200), null))
            .Append(new PinOverlayTranslation(target, "This translated paragraph needs additional horizontal space.")).ToArray();
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(Draw(900, 400, _ => { }), translations,
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        if (peers == 2) Assert.True(chosen.Bounds.Right <= 220.01);
        else Assert.True(chosen.Bounds.Right > 220.01);
    });

    [PinOverlayReplayFact]
    public void RecordedLongWikipediaParagraphKeepsTheRightColumnMargin() => StaTestHost.Run(() =>
    {
        string directory = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!);
        const string name = "web-en-wiki-screenshot";
        string sourcePath = DiagnosticOutputPaths.Child(directory, name + "-source.png");
        if (!File.Exists(sourcePath)) return;
        var source = new BitmapImage();
        source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(sourcePath); source.EndInit(); source.Freeze();
        OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(DiagnosticOutputPaths.Child(directory, name + "-ocr.json"));
        PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, source).ToArray();
        string[] paragraphs = File.ReadAllText(DiagnosticOutputPaths.Child(directory, name + ".en-ko-text.txt"))
            .ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.None);
        Assert.Equal(blocks.Length, paragraphs.Length);
        int target = Array.FindIndex(blocks, block => block.Text.StartsWith("The first screenshots", StringComparison.Ordinal));
        Assert.True(target >= 0);
        PinOverlayFit? chosen = null;
        PinOverlayTranslation[] translations = blocks.Select((block, index) => new PinOverlayTranslation(block, index == target ? paragraphs[index] : null)).ToArray();
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, translations, observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.True(chosen.Bounds.Right <= 1016, $"The article grew into its right margin: {chosen.Bounds}.");
        AssertAreaUnchanged(source, result.Image, new Int32Rect(1016, (int)blocks[target].Bounds.Y,
            source.PixelWidth - 1016, (int)Math.Ceiling(blocks[target].Bounds.Height)));
    });

    [Fact]
    public void TinyUnreadGlyphAtImageEdgeKeepsItsWholeLineFootprint() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(200, 100, drawing => drawing.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(255, 232, 167)), null, new Rect(198, 26, 2, 4)));
        OcrLine[] lines = [PinTranslationOverlayTests.Line("first recognized line", 20, 24, 145, 20),
            PinTranslationOverlayTests.Line("second recognized line", 20, 54, 180, 20)];
        var block = new PinOverlayBlock("first recognized line second recognized line", new(20, 24, 180, 50), lines);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "처음 인식한 문장과 다음 인식한 문장입니다")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(source, result.Image, new Int32Rect(198, 24, 2, 20));
    });

    [Theory]
    [InlineData("일반")]
    [InlineData("녹화")]
    [InlineData("편집")]
    [InlineData("General")]
    [InlineData("Dark")]
    public void PlainGlyphCrossbarsDoNotAddAnUnderline(string original) => StaTestHost.Run(() =>
    {
        var text = new FormattedText(original, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 18, Brushes.Black, 1);
        Point origin = new(30, 20);
        Rect ink = text.BuildGeometry(origin).Bounds;
        BitmapSource source = Draw(240, 80, drawing => drawing.DrawText(text, origin));
        OcrLine line = PinTranslationOverlayTests.Line(original, Math.Floor(ink.X), Math.Floor(ink.Y),
            Math.Ceiling(ink.Right) - Math.Floor(ink.X), Math.Ceiling(ink.Bottom) - Math.Floor(ink.Y));
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "i i i i i i")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        byte[] pixels = new byte[240 * 80 * 4];
        new FormatConvertedBitmap(result.Image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 240 * 4, 0);
        int longest = 0;
        for (int y = (int)chosen.Bounds.Y; y < Math.Min(80, Math.Ceiling(chosen.Bounds.Bottom) + 4); y++)
        {
            int run = 0;
            for (int x = (int)chosen.Bounds.X; x < Math.Min(240, Math.Ceiling(chosen.Bounds.Right)); x++)
            {
                run = pixels[(y * 240 + x) * 4] < 150 ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }
        Assert.True(longest < chosen.FontSize * .6, $"A {longest}px horizontal stroke was added below plain text.");
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BoundedInlineKeyBackgroundBelongsToItsWordButAWholeCellDoesNot(bool grid, bool punctuation) => StaTestHost.Run(() =>
    {
        Rect container = grid ? new Rect(0, 18, 260, 30) : new Rect(60, 18, 30, 30);
        BitmapSource source = Draw(260, 100, drawing =>
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 240, 243)), null, container);
            if (grid) drawing.DrawRectangle(Brushes.Gray, null, new Rect(0, 47, 260, 1));
            drawing.DrawRectangle(Brushes.Black, null, new Rect(69, 25, 12, 14));
            if (punctuation) drawing.DrawRectangle(Brushes.Black, null, new Rect(91, 36, 2, 3));
        });
        OcrLine[] lines = [new("before # after", new(20, 25, 150, 14),
            [new("before", new(20, 25, 20, 14)), new(punctuation ? "#," : "#", new(69, 25, punctuation ? 24 : 12, 14)), new("after", new(110, 25, 60, 14))]),
            PinTranslationOverlayTests.Line("following text", 20, 64, 150, 14)];
        var block = new PinOverlayBlock("# following text", new(20, 25, 150, 53), lines);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "번역한 문장")], drawText: false);
        if (grid)
        {
            Assert.Equal(0, result.RenderedBlocks);
            AssertAreaUnchanged(source, result.Image, new Int32Rect(0, 0, 260, 100));
        }
        else
        {
            Assert.Equal(1, result.RenderedBlocks);
            byte[] erased = new byte[30 * 30 * 4];
            new FormatConvertedBitmap(result.Image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(60, 18, 30, 30), erased, 120, 0);
            Assert.All(erased, channel => Assert.Equal(255, channel));
        }
    });

    [Fact]
    public void ShortLabelUsesFreeLeftPaddingWithoutErasingItsDropdown() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(180, 70, drawing =>
        {
            var pen = new Pen(Brushes.Black, 2);
            drawing.DrawLine(pen, new Point(100, 27), new Point(104, 31));
            drawing.DrawLine(pen, new Point(104, 31), new Point(108, 27));
        });
        OcrLine line = PinTranslationOverlayTests.Line("제품", 70, 22, 24, 14);
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "Products")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.True(chosen.Bounds.X < line.Bounds.X);
        AssertAreaUnchanged(source, result.Image, new Int32Rect(98, 24, 14, 10));
    });

    [Fact]
    public void OneUnreadKoreanGlyphBetweenWordsKeepsItsWholeFootprint() => StaTestHost.Run(() =>
    {
        var unread = new FormattedText("앱", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 18, Brushes.Black, 1);
        Point origin = new(90, 18);
        Rect ink = unread.BuildGeometry(origin).Bounds;
        BitmapSource source = Draw(320, 80, drawing => drawing.DrawText(unread, origin));
        OcrLine line = new("설정 바로 가기", new(20, 21, 180, 18),
            [new("설정", new(20, 21, 50, 18)), new("바로 가기", new(125, 21, 75, 18))]);
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "Go to settings")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(source, result.Image, new Int32Rect((int)Math.Floor(ink.X), (int)Math.Floor(ink.Y),
            (int)Math.Ceiling(ink.Right) - (int)Math.Floor(ink.X), (int)Math.Ceiling(ink.Bottom) - (int)Math.Floor(ink.Y)));
    });

    [Fact]
    public void ReservedMarkerDoesNotChangeTheTranslatedLinksUnderline() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(260, 80, drawing =>
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(40, 39, 120, 1));
            drawing.DrawEllipse(Brushes.Black, null, new Point(20, 28), 2, 2);
        });
        OcrLine line = PinTranslationOverlayTests.Line("Original link", 40, 20, 120, 16);
        var ordinary = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        var marked = ordinary with { ProtectedRegions = [new OcrRect(18, 26, 4, 4)] };
        PinOverlayComposition first = PinTranslationOverlay.Compose(source, [new(ordinary, "Translated link")]);
        PinOverlayComposition second = PinTranslationOverlay.Compose(source, [new(marked, "Translated link")]);
        Assert.Equal(1, first.RenderedBlocks);
        Assert.Equal(1, second.RenderedBlocks);
        AssertAreaUnchanged(first.Image, second.Image, new Int32Rect(0, 0, 260, 80));
    });

    [Fact]
    public void UnreadInternalWordKeepsItsWholeFootprintAndTranslationFlowsAroundIt() => StaTestHost.Run(() =>
    {
        var unread = new FormattedText("and", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 18, Brushes.Black, 1);
        Point origin = new(150, 40);
        Rect ink = unread.BuildGeometry(origin).Bounds;
        BitmapSource source = Draw(340, 130, drawing => drawing.DrawText(unread, origin));
        OcrLine[] lines =
        [
            PinTranslationOverlayTests.Line("first recognized line", 20, 18, 280, 18),
            new("recurrence convolutions", new(20, 44, 280, 18),
                [new("recurrence", new(20, 44, 110, 18)), new("convolutions", new(200, 44, 100, 18))]),
            PinTranslationOverlayTests.Line("last recognized line", 20, 70, 280, 18),
        ];
        var block = new PinOverlayBlock(string.Join(" ", lines.Select(line => line.Text)), new(20, 18, 280, 70), lines);
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(block, "첫 번째 문장은 다음 문장으로 이어집니다 순환 구조와 합성곱 없이 새로운 모델을 설명하는 마지막 문장입니다")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.NotEmpty(chosen.LineOffsets);
        AssertAreaUnchanged(source, result.Image, new Int32Rect((int)Math.Floor(ink.X), (int)Math.Floor(ink.Y),
            (int)Math.Ceiling(ink.Right) - (int)Math.Floor(ink.X), (int)Math.Ceiling(ink.Bottom) - (int)Math.Floor(ink.Y)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextUnderlineBelowOcrInkIsOwnedButFullGridRuleIsNot(bool grid) => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(320, 100, drawing =>
        {
            drawing.DrawRectangle(Brushes.Black, null, grid ? new Rect(0, 39, 320, 1) : new Rect(20, 39, 100, 1));
            drawing.DrawRectangle(Brushes.Black, null, new Rect(69, 25, 2, 14));
        });
        OcrLine[] lines = [PinTranslationOverlayTests.Line("linked words", 20, 20, 100, 16),
            PinTranslationOverlayTests.Line("following line", 20, 50, 100, 16)];
        var block = new PinOverlayBlock("linked words following line", new(20, 20, 100, 46), lines);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "번역 문장")], drawText: false);
        if (grid)
        {
            Assert.Equal(0, result.RenderedBlocks);
            AssertAreaUnchanged(source, result.Image, new Int32Rect(0, 0, 320, 100));
        }
        else
        {
            Assert.Equal(1, result.RenderedBlocks);
            byte[] pixels = new byte[400];
            new FormatConvertedBitmap(result.Image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(20, 39, 100, 1), pixels, 400, 0);
            Assert.All(pixels, value => Assert.Equal(255, value));
        }
    });

    [PinOverlayReplayFact]
    public void RecordedFitFailuresReportTheirExactSurfaceAndObstacles() => StaTestHost.Run(() =>
    {
        string directory = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!);
        string[][] decisions = File.ReadLines(DiagnosticOutputPaths.Child(directory, "decisions.tsv")).Skip(1)
            .Select(line => line.Split('\t')).Where(fields => fields[2] == "after").ToArray();
        foreach (var direction in decisions.GroupBy(fields => (Name: fields[0], Direction: fields[1])))
        {
            if (!direction.Any(fields => fields[3] == "fit")) continue;
            string sourcePath = DiagnosticOutputPaths.Child(directory, direction.Key.Name + "-source.png");
            if (!File.Exists(sourcePath)) continue;
            var source = new BitmapImage();
            source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.UriSource = new Uri(sourcePath); source.EndInit(); source.Freeze();
            OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(DiagnosticOutputPaths.Child(directory, direction.Key.Name + "-ocr.json"));
            PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, source).ToArray();
            string[] paragraphs = File.ReadAllText(DiagnosticOutputPaths.Child(directory, direction.Key.Name + "." + direction.Key.Direction + "-text.txt"))
                .ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.None);
            string[][] saved = direction.ToArray();
            if (saved.Length != blocks.Length)
            {
                output.WriteLine($"NOT_RUN {direction.Key.Name}.{direction.Key.Direction}: cached {saved.Length} blocks, current {blocks.Length}; refresh replay before diagnosing changed segmentation.");
                continue;
            }
            Assert.Equal(saved.Length, paragraphs.Length);
            PinOverlayTranslation[] translations = blocks.Select((block, index) => new PinOverlayTranslation(block,
                saved[index][3] is "rendered" or "fit" ? paragraphs[index] : null)).ToArray();
            var fits = new Dictionary<PinOverlayBlock, PinOverlayFit>();
            PinTranslationOverlay.Compose(source, translations, observeFit: (block, fit, _) => fits.Add(block, fit));
            double[] sizes = PinTranslationOverlay.EstimateFontSizes(blocks);
            for (int ordinal = 0; ordinal < blocks.Length; ordinal++)
            {
                if (saved[ordinal][3] != "fit") continue;
                PinOverlayBlock block = blocks[ordinal];
                int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
                byte[] pixels = new byte[stride * height];
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
                Color[] backgrounds = block.Lines.Select(line => PinTranslationOverlay.EstimateBackground(pixels, width, height, stride, line.Bounds).Color).ToArray();
                static byte Median(IEnumerable<byte> values) { byte[] ordered = values.Order().ToArray(); return (byte)((ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2); }
                Color background = Color.FromRgb(Median(backgrounds.Select(color => color.R)), Median(backgrounds.Select(color => color.G)), Median(backgrounds.Select(color => color.B)));
                OcrRect[] obstacles = blocks.Where(other => !ReferenceEquals(other, block)).SelectMany(other => other.Lines.Select(line => line.Bounds))
                    .Concat(blocks.SelectMany(other => other.ProtectedRegions)).Concat(blocks.Take(ordinal).Where(fits.ContainsKey).Select(other => fits[other].Bounds)).ToArray();
                Type type = typeof(PinTranslationOverlay).GetNestedType("BackgroundSurface", BindingFlags.NonPublic)!;
                object surface = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [pixels, width, height, stride, block, background, obstacles], CultureInfo.InvariantCulture)!;
                var canOccupy = type.GetMethod("CanOccupy", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<OcrRect, bool>>(surface);
                var matches = type.GetMethod("Matches", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<int, bool>>(surface);
                OcrRect[] occupied = (OcrRect[])type.GetField("occupiedLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
                OcrRect limits = (OcrRect)type.GetMethod("HorizontalLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [TextAlignment.Left])!;
                double inset = (double)type.GetProperty("LeadingInset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
                var exclusions = (IReadOnlyList<OcrRect>)type.GetProperty("TextExclusions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
                double pitch = PinTranslationOverlay.SourceLinePitch(block.Lines), size = sizes[ordinal];
                double pitchScale = pitch > 0 ? pitch / size : 0;
                FormattedText Format(string text, double em)
                {
                    var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, em, Brushes.Black, 1);
                    if (pitchScale > 0) formatted.LineHeight = pitchScale * em;
                    return formatted;
                }
                Size Final(string text, double em) { var formatted = Format(text, em); return new(formatted.WidthIncludingTrailingWhitespace, Math.Max(0, formatted.Extent - 2 * Math.Min(1, em / 7))); }
                Size Placement(string text, double em) => new(Format(text, em).WidthIncludingTrailingWhitespace, Format(text, em).BuildGeometry(new Point()).Bounds.Height);
                PinOverlayFit? Try(IReadOnlyList<OcrRect> barriers, bool constrain) => PinTranslationOverlay.Fit(paragraphs[ordinal], block.Bounds, size, width, height,
                    PinTranslationOverlay.Measure, barriers, block.Lines.Count, pitch > 0 ? pitch : Format("Ag", size).Height,
                    Final, placementMeasure: Placement, alignment: PinTranslationOverlay.InferAlignment(block.Lines),
                    horizontalLimits: constrain ? limits : null, canOccupy: constrain ? canOccupy : null, textExclusions: exclusions);
                PinOverlayFit? natural = Try([], false), obstructed = Try(obstacles, false), constrained = Try(obstacles, true);
                OcrRect candidate = obstructed?.Bounds ?? natural?.Bounds ?? block.Bounds;
                string FirstFailure(OcrRect rectangle)
                {
                for (int y = (int)rectangle.Y; y < Math.Ceiling(rectangle.Bottom); y++)
                    for (int x = (int)rectangle.X; x < Math.Ceiling(rectangle.Right); x++)
                    {
                        if (occupied.Any(rect => x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom) || matches(y * stride + x * 4)) continue;
                        int i = y * stride + x * 4;
                        OcrRect nearest = block.Lines.SelectMany(line => line.Words.Select(word => word.Bounds))
                            .MinBy(rect => Math.Max(0, Math.Max(rect.X - x, x - rect.Right)) + Math.Max(0, Math.Max(rect.Y - y, y - rect.Bottom)));
                        return $"{x},{y} RGB({pixels[i + 2]},{pixels[i + 1]},{pixels[i]}) nearestWord={nearest}";
                    }
                    return "none";
                }
                string obstaclesHere = string.Join(";", obstacles.Where(rect => rect.X < block.Bounds.Right + 2 && rect.Right > block.Bounds.X - 2
                    && rect.Y < block.Bounds.Bottom + 2 && rect.Bottom > block.Bounds.Y - 2).Select(rect => rect.ToString()));
                output.WriteLine($"SURFACE {direction.Key.Name}.{direction.Key.Direction} #{ordinal} {block.Text[..Math.Min(60, block.Text.Length)]}\nbounds={block.Bounds} lines={block.Lines.Count} size={size:F2} pitch={pitch:F2} bg={background} noise={type.GetField("backgroundTolerance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)} noisy={type.GetField("noisySurface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)} exclusions={exclusions.Count} inset={inset:F2} limits={limits}\nsourceOK={canOccupy(block.Bounds)} natural={natural?.Bounds} obstructionFit={obstructed?.Bounds} surfaceFit={constrained?.Bounds} firstBad={FirstFailure(candidate)}\nsourceBad={FirstFailure(block.Bounds)}\nnearObstacles={obstaclesHere}");
                if (block.Bounds.Right == width && block.Lines[0].Bounds.Right < width)
                {
                    OcrRect first = block.Lines[0].Bounds;
                    var seen = new HashSet<int>();
                    for (int y = (int)first.Y; y < Math.Ceiling(first.Bottom); y++)
                        for (int x = (int)Math.Ceiling(first.Right); x < width; x++)
                        {
                            int start = y * width + x;
                            if (!seen.Add(start) || matches(y * stride + x * 4)) continue;
                            var queue = new List<int> { start };
                            int minX = x, maxX = x, minY = y, maxY = y;
                            for (int cursor = 0; cursor < queue.Count; cursor++)
                            {
                                int cx = queue[cursor] % width, cy = queue[cursor] / width;
                                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                                for (int dy = -1; dy <= 1; dy++)
                                    for (int dx = -1; dx <= 1; dx++)
                                    {
                                        int xx = cx + dx, yy = cy + dy, key = yy * width + xx;
                                        if (xx < first.Right || xx >= width || yy < first.Y || yy >= first.Bottom
                                            || !seen.Add(key) || matches(yy * stride + xx * 4)) continue;
                                        queue.Add(key);
                                    }
                            }
                            output.WriteLine($"edge component {minX},{minY}..{maxX},{maxY} count={queue.Count}");
                        }
                }
            }
        }
    });

    [Fact]
    public void ExactPlacementRetainsReservedFirstLineWidth()
    {
        static Size Measure(string _, double __, double ___) => new(10, 10);
        PinOverlayFit? fit = PinTranslationOverlay.Fit("word", new OcrRect(10, 10, 19, 10), 10, 100, 80,
            Measure, [new OcrRect(150, 0, 5, 5)], sourceLineHeight: 10,
            placementMeasure: (_, _) => new Size(10, 10), firstLineInset: 10);
        Assert.NotNull(fit);
        Assert.Equal(20, fit.Bounds.Width);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortLabelGrowsAcrossWhiteWithRasterFringesAndJpegNoise(bool jpeg) => StaTestHost.Run(() =>
    {
        var text = new FormattedText("각주", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 18, new SolidColorBrush(Color.FromRgb(48, 85, 151)), 1);
        Point origin = new(30, 20);
        Rect actualInk = text.BuildGeometry(origin).Bounds;
        BitmapSource source = Draw(220, 70, drawing => drawing.DrawText(text, origin));
        if (jpeg)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 35 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var bytes = new MemoryStream();
            encoder.Save(bytes);
            bytes.Position = 0;
            source = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            source.Freeze();
        }
        // Windows OCR's integer word box can omit the outer ClearType colour fringe.
        OcrRect recognized = new(Math.Ceiling(actualInk.X), Math.Floor(actualInk.Y),
            Math.Floor(actualInk.Right) - Math.Ceiling(actualInk.X), Math.Ceiling(actualInk.Bottom) - Math.Floor(actualInk.Y));
        OcrLine line = new("각주", recognized, [new("각주", recognized)]);
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayFit? fit = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "Footnotes")],
            observeFit: (_, selected, _) => fit = selected);
        Assert.True(result.RenderedBlocks == 1, DescribeSurface(source, block));
        Assert.NotNull(fit);
        Assert.True(fit.Bounds.Width > recognized.Width);
        Assert.InRange(fit.FontSize, 15, 22);
        AssertAreaUnchanged(source, result.Image, new Int32Rect(0, 0, 20, 70));
    });

    [Fact]
    public void UnreadLeadingWordInsideLooseLineBoxKeepsItsInk() => StaTestHost.Run(() =>
    {
        var unread = new FormattedText("The", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 18, Brushes.Black, 1);
        BitmapSource source = Draw(300, 100, drawing => drawing.DrawText(unread, new Point(20, 15)));
        OcrRect recognized = new(75, 18, 180, 18);
        OcrLine[] lines =
        [
            new("dominant sequence models", new(20, 18, 235, 18), [new("dominant sequence models", recognized)]),
            PinTranslationOverlayTests.Line("continue on the next line", 20, 46, 230, 18),
        ];
        var block = new PinOverlayBlock(string.Join(" ", lines.Select(line => line.Text)), new(20, 18, 235, 46), lines);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "시퀀스 모델은 다음 문장으로 이어집니다")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(source, result.Image, new Int32Rect(20, 18, 33, 18));
    });

    [Fact]
    public void LeadingWordCrossingParagraphCropIsPreservedAsAWholeRun() => StaTestHost.Run(() =>
    {
        var unread = new FormattedText("The", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 22, Brushes.Black, 1);
        Point origin = new(20, 15);
        Rect ink = unread.BuildGeometry(origin).Bounds;
        double left = Math.Ceiling(ink.X) + 1, top = Math.Floor(ink.Y), lineHeight = Math.Ceiling(ink.Bottom) - top;
        BitmapSource source = Draw(320, 110, drawing => drawing.DrawText(unread, origin));
        OcrLine[] lines =
        [
            PinTranslationOverlayTests.Line("dominant sequence models", Math.Ceiling(ink.Right) + 9, top, 200, lineHeight),
            PinTranslationOverlayTests.Line("continue on the next line", left, 55, 260, lineHeight),
        ];
        var block = new PinOverlayBlock(string.Join(" ", lines.Select(line => line.Text)),
            lines.Select(line => line.Bounds).Aggregate(default(OcrRect), OcrRect.Union), lines);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "시퀀스 모델은 다음 문장으로 이어집니다")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(source, result.Image, new Int32Rect((int)Math.Floor(ink.X) - 1, (int)top - 1,
            (int)Math.Ceiling(ink.Width) + 3, (int)lineHeight + 3));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PaleLongRuleNearStrongRecognizedStemNeverBecomesAFringe(int gap) => StaTestHost.Run(() =>
    {
        int ruleX = 60 + gap;
        BitmapSource source = Draw(180, 90, drawing =>
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(58, 20, 2, 16));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 200, 200)), null, new Rect(ruleX, 0, 1, 90));
        });
        OcrLine line = PinTranslationOverlayTests.Line("가나다", 20, 20, 40, 16);
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "A")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(source, result.Image, new Int32Rect(ruleX, 0, 1, 90));
    });

    private static string DescribeSurface(BitmapSource source, PinOverlayBlock block)
    {
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        Color background = PinTranslationOverlay.EstimateBackground(pixels, width, height, stride, block.Bounds).Color;
        Type type = typeof(PinTranslationOverlay).GetNestedType("BackgroundSurface", BindingFlags.NonPublic)!;
        object surface = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [pixels, width, height, stride, block, background], CultureInfo.InvariantCulture)!;
        var matches = type.GetMethod("Matches", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<int, bool>>(surface);
        object? limits = type.GetMethod("HorizontalLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [TextAlignment.Left]);
        var failures = new List<string>();
        for (int x = (int)Math.Ceiling(block.Bounds.Right); x < Math.Min(width, block.Bounds.Right + 12); x++)
            for (int y = (int)block.Bounds.Y; y < block.Bounds.Bottom; y++)
            {
                int index = y * stride + x * 4;
                if (!matches(index) && failures.Count < 8) failures.Add($"({x},{y})={pixels[index + 2]},{pixels[index + 1]},{pixels[index]}");
            }
        return $"bounds={block.Bounds}; background={background}; limits={limits}; failed={string.Join(';', failures)}";
    }

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            paint(drawing);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static void AssertAreaUnchanged(BitmapSource source, BitmapSource result, Int32Rect area)
    {
        byte[] Pixels(BitmapSource image)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[area.Width * area.Height * 4];
            converted.CopyPixels(area, pixels, area.Width * 4, 0);
            return pixels;
        }
        Assert.Equal(Pixels(source), Pixels(result));
    }
}
