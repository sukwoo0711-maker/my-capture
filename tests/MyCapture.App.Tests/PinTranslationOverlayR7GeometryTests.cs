using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayR7GeometryTests
{
    [Fact]
    public void ShortTranslationEmCannotOutgrowActualSourceInk() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new OcrWord("Other (see instructions)", new(20, 20, 180, 24)));
        BitmapSource source = Picture(280, 80, (x, y) =>
            x >= 20 && x < 200 && y >= 25 && y < 35 && x % 7 < 3 ? Colors.Black : Colors.White);
        PinOverlayFit? fit = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(new(line.Text, line.Bounds, [line]), "기타 (명령 참조)")],
            observeFit: (_, chosen, _) => fit = chosen);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(fit);
        Assert.InRange(fit.FontSize, PinTranslationOverlay.MinimumFontSize, 11);
    });

    [Fact]
    public void CheckboxHeightDoesNotInflateTextEm() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new("口", new(20, 20, 24, 24)), new("Other", new(60, 25, 40, 15)),
            new("(see", new(106, 25, 30, 15)), new("instructions)", new(142, 25, 90, 15)));
        BitmapSource source = Picture(300, 80, (x, y) =>
        {
            if (x >= 20 && x < 44 && y >= 20 && y < 44
                && (x < 22 || x >= 42 || y < 22 || y >= 42)) return Colors.Black;
            return x >= 60 && x < 232 && y >= 25 && y < 40 && x % 7 < 3 ? Colors.Black : Colors.White;
        });
        PinOverlayFit? fit = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(new(line.Text, line.Bounds, [line]), "기타 (명령 참조)")],
            observeFit: (_, chosen, _) => fit = chosen);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(fit);
        Assert.InRange(fit.FontSize, PinTranslationOverlay.MinimumFontSize, 16.5);
    });

    [Fact]
    public void TinySourceKeepsItsOwnScaleBelowTheOrdinaryMinimum() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new OcrWord("Small label", new(20, 20, 90, 14)));
        BitmapSource source = Picture(160, 60, (x, y) =>
            x >= 20 && x < 110 && y >= 24 && y < 30 && x % 6 < 2 ? Colors.Black : Colors.White);
        PinOverlayFit? fit = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(new(line.Text, line.Bounds, [line]), "작은 레이블")],
            observeFit: (_, chosen, _) => fit = chosen);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(fit);
        Assert.InRange(fit.FontSize, 6, 6.6 + .00001);
    });

    [Fact]
    public void LowContrastSourceInkStillCapsTheTranslationEm() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new OcrWord("Faint source", new(20, 20, 110, 24)));
        BitmapSource source = Picture(200, 75, (x, y) =>
            x >= 20 && x < 130 && y >= 26 && y < 36 && x % 7 < 3
                ? Color.FromRgb(220, 220, 220) : Colors.White);
        PinOverlayFit? fit = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(new(line.Text, line.Bounds, [line]), "연한 글자")],
            observeFit: (_, chosen, _) => fit = chosen);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(fit);
        Assert.InRange(fit.FontSize, PinTranslationOverlay.MinimumFontSize, 11);
    });

    [Fact]
    public void NavigationGapsKeepLabelsAndProductNamesSeparate()
    {
        OcrLine line = Words(new("Microsoft", new(103, 17, 79, 16)),
            new("고객", new(230, 18, 33, 17)), new("지원", new(271, 18, 33, 17)),
            new("Microsoft", new(324, 23, 53, 10)), new("365", new(382, 24, 19, 9)),
            new("Office", new(424, 23, 33, 10)), new("제품", new(484, 22, 24, 12)),
            new("장치", new(548, 22, 23, 12)), new("계정", new(607, 22, 24, 12)),
            new("및", new(638, 22, 10, 12)), new("청구", new(653, 22, 25, 12)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        Assert.Contains(blocks, block => block.Text == "고객 지원");
        Assert.Contains(blocks, block => block.Text == "Microsoft 365");
        Assert.Contains(blocks, block => block.Text == "Office");
        Assert.Contains(blocks, block => block.Text == "계정 및 청구");
        Assert.DoesNotContain(blocks, block => block.Text.Contains("지원 Microsoft", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("The first line")]
    [InlineData("첫 번째 문장")]
    public void ReservedPixelsDoNotShiftCappedTextAboveItsMeasuredTop(string translation) => StaTestHost.Run(() =>
    {
        OcrLine first = Words(new OcrWord("First source line", new(20, 20, 180, 10)));
        OcrLine second = Words(new OcrWord("Second source line", new(20, 50, 180, 10)));
        var block = new PinOverlayBlock(first.Text + " " + second.Text,
            OcrRect.Union(first.Bounds, second.Bounds), [first, second]);
        BitmapSource source = Picture(240, 90, (x, y) =>
            x >= 20 && x < 200 && (y >= 20 && y < 30 || y >= 50 && y < 60) && x % 7 < 3
                ? Colors.Black : Colors.White);
        PinOverlayComposition ordinary = PinTranslationOverlay.Compose(source, [new(block, translation)]);
        PinOverlayFit? fit = null;
        PinOverlayComposition reserved = PinTranslationOverlay.Compose(source,
            [new(block with { ProtectedRegions = [new(190, 20, 5, 10)] }, translation)],
            observeFit: (_, chosen, _) => fit = chosen);
        Assert.Equal(1, ordinary.RenderedBlocks);
        Assert.Equal(1, reserved.RenderedBlocks);
        Assert.NotNull(fit);
        Assert.InRange(fit.FontSize, 10, 11);
        Assert.Single(fit.LineOffsets);
        byte[] ordinaryPixels = Pixels(ordinary.Image, new(20, 15, 150, 30));
        byte[] reservedPixels = Pixels(reserved.Image, new(20, 15, 150, 30));
        // The extra clip geometry can round an antialiased edge by one channel
        // level. A shifted baseline or clipped glyph changes it by far more.
        Assert.All(ordinaryPixels.Zip(reservedPixels), pair =>
            Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 2));
        Assert.Equal(Pixels(source, new(190, 20, 5, 10)), Pixels(reserved.Image, new(190, 20, 5, 10)));
    });

    [Theory]
    [InlineData("*")]
    [InlineData("†")]
    [InlineData("‡")]
    [InlineData("§")]
    [InlineData("¹")]
    [InlineData("²")]
    [InlineData("⁹")]
    public void FootnotesStartSeparateBlocksAndRetainMarkerPixels(string marker) => StaTestHost.Run(() =>
    {
        OcrLine first = Words(new(marker, new(20, 20, 7, 12)), new("Work performed", new(34, 23, 115, 14)));
        OcrLine second = Words(new(marker, new(20, 43, 7, 12)), new("Other research", new(34, 46, 115, 14)));
        BitmapSource source = Picture(260, 100, (x, y) =>
        {
            if (x >= 20 && x < 27 && (y >= 20 && y < 32 || y >= 43 && y < 55)) return Colors.Black;
            return x >= 34 && x < 149 && (y >= 23 && y < 37 || y >= 46 && y < 60) && x % 7 < 3
                ? Colors.Black : Colors.White;
        });
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(first, second), source);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Work performed", blocks[0].Text);
        Assert.Equal("Other research", blocks[1].Text);
        Assert.Single(blocks[0].ProtectedRegions);
        Assert.Single(blocks[1].ProtectedRegions);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            blocks.Select(block => new PinOverlayTranslation(block, "각주 번역")).ToArray());
        Assert.Equal(2, result.RenderedBlocks);
        foreach (int y in new[] { 20, 43 })
        {
            var area = new Int32Rect(20, y, 7, 12);
            Assert.Equal(Pixels(source, area), Pixels(result.Image, area));
        }
    });

    [Theory]
    [InlineData("†Work performed")]
    [InlineData("‡Work performed")]
    [InlineData("¹Work performed")]
    [InlineData("*Work performed")]
    public void AttachedFootnoteMarkerIsExcludedFromTranslation(string text)
    {
        OcrLine line = Words(new OcrWord(text, new(20, 20, 160, 18)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line)));
        Assert.Equal("Work performed", block.Text);
        Assert.Single(block.ProtectedRegions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OcrLetterTRequiresAnIsolatedRaisedGlyphBeforeItBecomesAMarker(bool raised)
    {
        OcrLine line = Words(new("tWork", new(20, raised ? 20 : 25, 50, raised ? 20 : 15)),
            new("performed", new(76, 25, 90, 15)), new("here", new(172, 25, 40, 15)));
        BitmapSource source = Picture(260, 75, (x, y) =>
        {
            if (x >= 20 && x < 24 && y >= (raised ? 20 : 25) && y < 32) return Colors.Black;
            return x >= 29 && x < 212 && y >= 25 && y < 40 && x % 7 < 3 ? Colors.Black : Colors.White;
        });
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), source));
        Assert.Equal(raised ? "Work performed here" : line.Text, block.Text);
        Assert.Equal(raised ? 1 : 0, block.ProtectedRegions.Count);
    }

    [Theory]
    [InlineData("The", false)]
    [InlineData("Type", true)]
    public void CapitalAscenderBesideDescenderWordsIsNotAFootnote(string firstWord, bool descender)
    {
        OcrLine line = Words(new(firstWord, new(20, 20, 40, descender ? 20 : 16)), new("young", new(66, 24, 44, 16)),
            new("puppy", new(116, 24, 44, 16)));
        BitmapSource source = Picture(200, 75, (x, y) =>
        {
            if (x >= 20 && x < 29 && y >= 20 && y < 36) return Colors.Black;
            if (x >= 31 && x < 60 && y >= 24 && y < (descender ? 40 : 36) && x % 7 < 3) return Colors.Black;
            return x >= 66 && x < 160 && y >= 24 && y < 40 && x % 7 < 3 ? Colors.Black : Colors.White;
        });
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), source));
        Assert.Equal(line.Text, block.Text);
        Assert.Empty(block.ProtectedRegions);
    }

    [PinOverlayReplayFact]
    public void RecordedSmallScreenshotRendersAtItsCappedSourceScale() => StaTestHost.Run(() =>
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        const string name = "var-small60-en-wiki";
        OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(Path.Combine(replay, name + "-ocr.json"));
        using FileStream stream = File.OpenRead(Path.Combine(replay, name + "-source.png"));
        BitmapFrame source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        source.Freeze();
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(recognized, source);
        var fits = new List<PinOverlayFit>();
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            blocks.Select(block => new PinOverlayTranslation(block, block.PreservePixels ? null : "번역")).ToArray(),
            observeFit: (_, fit, _) => fits.Add(fit));
        Assert.True(result.RenderedBlocks >= 10);
        Assert.True(fits.Count(fit => fit.FontSize < PinTranslationOverlay.MinimumFontSize) >= 10);
        Assert.All(fits.Where(fit => fit.FontSize < PinTranslationOverlay.MinimumFontSize),
            fit => Assert.InRange(fit.FontSize, 1, 8.8 + .00001));
    });

    [PinOverlayReplayFact]
    public void RecordedArxivFootnotesRetainTwoIndependentBodies() => StaTestHost.Run(() =>
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        const string name = "pdf-en-arxiv-attention-p1";
        OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(Path.Combine(replay, name + "-ocr.json"));
        using FileStream stream = File.OpenRead(Path.Combine(replay, name + "-source.png"));
        BitmapFrame source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        source.Freeze();
        PinOverlayBlock[] footnotes = PinTranslationOverlay.BuildBlocks(recognized, source)
            .Where(block => block.Text.Contains("performed while", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, footnotes.Length);
        Assert.All(footnotes, block =>
        {
            Assert.Single(block.Lines);
            Assert.NotEmpty(block.ProtectedRegions);
            Assert.False(block.Text.StartsWith("tWork", StringComparison.Ordinal));
            Assert.False(block.Text.StartsWith("twork", StringComparison.Ordinal));
        });
    });

    private static OcrLine Words(params OcrWord[] words) => new(string.Join(" ", words.Select(word => word.Text)),
        words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), words);

    private static OcrResult Recognition(params OcrLine[] lines) => PinTranslationOverlayTests.Recognition(lines);

    private static BitmapSource Picture(int width, int height, Func<int, int, Color> sample)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color color = sample(x, y);
                int index = (y * width + x) * 4;
                pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image, Int32Rect area)
    {
        var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[area.Width * area.Height * 4];
        converted.CopyPixels(area, pixels, area.Width * 4, 0);
        return pixels;
    }
}
