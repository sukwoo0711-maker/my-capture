using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayBlockProtectionTests
{
    [Theory]
    [InlineData("O")]
    [InlineData("M")]
    [InlineData("2")]
    [InlineData("(0")]
    [InlineData("[")]
    [InlineData("[애")]
    public void SeparatedLeadingIconIsExcludedFromTextAndBounds(string icon)
    {
        OcrLine line = Words(new(icon, new(10, 20, 10, 20)), new("Open document", new(32, 20, 120, 20)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line)));
        Assert.Equal("Open document", block.Text);
        Assert.Equal(32, block.Bounds.X);
        Assert.Contains(line.Words[0].Bounds, block.ProtectedRegions);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("V")]
    [InlineData(">")]
    [InlineData("›")]
    [InlineData("∨")]
    public void SeparatedTrailingChevronStaysOutsideEraseBounds(string icon)
    {
        OcrLine line = Words(new("Open document", new(10, 20, 120, 20)), new(icon, new(142, 20, 10, 20)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line)));
        Assert.Equal("Open document", block.Text);
        Assert.Equal(130, block.Bounds.Right);
        Assert.Contains(line.Words[^1].Bounds, block.ProtectedRegions);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("I")]
    [InlineData("to")]
    [InlineData("of")]
    public void ActualShortWordsAreNotIcons(string word)
    {
        OcrLine line = Words(new(word, new(10, 20, 10, 20)), new("document", new(32, 20, 90, 20)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line)));
        Assert.Equal(word + " document", block.Text);
        Assert.Empty(block.ProtectedRegions);
    }

    [Fact]
    public void InternalOneLetterWordsStayInSentence()
    {
        OcrLine line = Words(new("Save", new(10, 20, 40, 20)), new("a", new(60, 20, 9, 20)),
            new("document", new(79, 20, 80, 20)), new("I", new(169, 20, 9, 20)), new("created", new(188, 20, 70, 20)));
        Assert.Equal("Save a document I created", Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line))).Text);
    }

    [Fact]
    public void IconPixelsSurviveARealComposition() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new("O", new(10, 20, 10, 20)), new("Open document", new(32, 20, 120, 20)));
        BitmapSource source = Picture(220, 80, (x, y) => x >= 10 && x < 20 && y >= 20 && y < 40 ? Colors.Red : Colors.White);
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), source));
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source, [new(block, "문서 열기")]);
        Assert.Equal(1, composed.RenderedBlocks);
        Assert.Equal(Pixels(source, new(8, 18, 14, 24)), Pixels(composed.Image, new(8, 18, 14, 24)));
        Assert.NotEqual(Pixels(source, new(32, 20, 120, 20)), Pixels(composed.Image, new(32, 20, 120, 20)));
    });

    [Theory]
    [InlineData("public async Task<bool> SaveAsync(Bitmap image)")]
    [InlineData("string path = NextFreePath(\"capture.png\");")]
    [InlineData("await image.SaveAsync(path);")]
    [InlineData("ShowNotice(\"저장했습니다.\");")]
    [InlineData("return true;")]
    [InlineData("int count;")]
    [InlineData("string caption;")]
    [InlineData("{")]
    public void CodeLinesNeverJoinTheCommentAbove(string code)
    {
        OcrLine comment = Monospace("// 캡처한 그림을 저장합니다.", 20, 20);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(comment, Monospace(code, 20, 42)));
        PinOverlayBlock body = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("캡처한 그림을 저장합니다.", body.Text);
        Assert.Single(body.Lines);
        Assert.Contains(blocks, block => block.Text == code && block.PreservePixels);
        Assert.Contains(blocks, block => block.Text == "//" && block.PreservePixels);
    }

    [Theory]
    [InlineData("//", "")]
    [InlineData("#", "")]
    [InlineData("/*", " */")]
    [InlineData("--", "")]
    [InlineData("'", "")]
    public void CommentMarkersAndClosingSyntaxStayLiteral(string marker, string ending)
    {
        OcrLine line = Monospace(marker + " Save the picture" + ending, 20, 20);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        PinOverlayBlock comment = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("Save the picture", comment.Text);
        Assert.True(comment.Bounds.X > line.Bounds.X);
        Assert.Contains(blocks, block => block.PreservePixels && block.Text == marker);
        if (ending.Length > 0) Assert.Contains(blocks, block => block.PreservePixels && block.Text == "*/");
    }

    [Fact]
    public void InlineCodeAndQuotedCommentMarkersStayLiteral()
    {
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(
            Monospace("Save(image); // Save the picture", 20, 20),
            Monospace("string marker = \"// not a comment\";", 20, 42)));
        PinOverlayBlock comment = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("Save the picture", comment.Text);
        Assert.Contains(blocks, block => block.PreservePixels && block.Text == "Save(image); //");
        Assert.Contains(blocks, block => block.PreservePixels && block.Text.Contains("not a comment", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("char c = 'x'; // Save the picture")]
    [InlineData("char c = '\\n'; // Save the picture")]
    [InlineData("string c = @\"quoted \"\" // literal\"; // Save the picture")]
    public void ClosedStringAndCharacterLiteralsAreNotApostropheComments(string source)
    {
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(Monospace(source, 20, 20)));
        PinOverlayBlock comment = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("Save the picture", comment.Text);
    }

    [Theory]
    [InlineData("item = 1 # Save the picture")]
    [InlineData("SELECT id FROM pictures; -- Save the picture")]
    [InlineData("Dim item = 1 ' Save the picture")]
    [InlineData("Save(image); /* Save the picture */")]
    public void EveryInlineCommentStyleKeepsItsCodePrefix(string source)
    {
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(Monospace(source, 20, 20)));
        PinOverlayBlock comment = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("Save the picture", comment.Text);
        Assert.Contains(blocks, block => block.PreservePixels && block.Bounds.X == 20);
    }

    [Theory]
    [InlineData("Open https://example.com/foo//bar for details")]
    [InlineData("Open http://example.com/foo//bar for details")]
    [InlineData("A short count; remains prose")]
    [InlineData("The int count; example is explained here")]
    public void UrlPathsAndProseRemainWhole(string source)
    {
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(Monospace(source, 20, 20))));
        Assert.False(block.PreservePixels);
        Assert.Equal(source, block.Text);
    }

    [Fact]
    public void DamagedCallWithWideStringGapProtectsEveryExtractedFragment()
    {
        OcrLine line = Words(new("ShowNot", new(20, 20, 52, 14)), new("i", new(76, 20, 2, 14)),
            new("ce(", new(82, 20, 21, 14)), new("\"", new(108, 20, 4, 4)),
            new("습", new(163, 20, 13, 14)), new("니", new(180, 20, 12, 14)), new("다.", new(196, 20, 20, 14)),
            new("\"", new(221, 20, 4, 4)), new(");", new(230, 20, 11, 14)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(block.PreservePixels));
        Assert.Contains(blocks.SelectMany(block => block.Lines).SelectMany(item => item.Words), word => word.Text == "습");
    }

    [Fact]
    public void TwelveLineJustifiedParagraphDoesNotSplitAtOneUnusuallyWideWordGap()
    {
        OcrLine[] lines = Enumerable.Range(0, 12).Select(index => index == 4
            ? Words(new("A justified sentence has a gap", new(20, 20 + index * 24, 190, 18)),
                new("between two of its words", new(260, 20 + index * 24, 210, 18)))
            : Words(new OcrWord("The surrounding paragraph fills the same text column", new(20, 20 + index * 24, 450, 18)))).ToArray();
        PinOverlayBlock paragraph = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(lines)));
        Assert.Equal(12, paragraph.Lines.Count);
        Assert.Contains("gap between", paragraph.Text, StringComparison.Ordinal);
        Assert.Equal(450, paragraph.Bounds.Width);
    }

    [Fact]
    public void ShortHeadingDoesNotBecomePartOfTheDenseParagraphBelow()
    {
        OcrLine[] lines = [Words(new OcrWord("Digital techniques", new(20, 20, 105, 14))),
            .. Enumerable.Range(0, 7).Select(index => Words(new OcrWord(
                "The surrounding paragraph fills the same text column", new(20, 44 + index * 24, 450, 18))))];
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(lines));
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Digital techniques", blocks[0].Text);
        Assert.Single(blocks[0].Lines);
        Assert.Equal(7, blocks[1].Lines.Count);
    }

    [Fact]
    public void DuplicateRemovalKeepsIdenticalTextAtDistinctSourceCoordinates()
    {
        OcrLine first = Words(new OcrWord("Save document", new(20, 20, 120, 16)));
        OcrLine second = Words(new OcrWord("Save document", new(20, 80, 120, 16)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(first, first, second));
        OcrWord[] words = blocks.SelectMany(block => block.Lines).SelectMany(line => line.Words).ToArray();
        Assert.Equal(2, words.Length);
        Assert.All(words, word => Assert.Equal("Save document", word.Text));
        Assert.Equal([20d, 80d], words.Select(word => word.Bounds.Y).Order());
    }

    [Fact]
    public void OnlyCommentBodyChangesWhileCodeAndMarkerPixelsStay() => StaTestHost.Run(() =>
    {
        OcrLine comment = Monospace("// Save the picture", 20, 20);
        OcrLine code = Monospace("public async Task<bool> SaveAsync(Bitmap image)", 20, 50);
        BitmapSource source = Picture(600, 120, (x, y) =>
            x >= 20 && x < 36 && y >= 20 && y < 36 ? Colors.Green
            : x >= 20 && x < 400 && y >= 50 && y < 66 ? Colors.Red : Colors.White);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(comment, code), source);
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source,
            blocks.Select(block => new PinOverlayTranslation(block, "그림을 저장합니다")).ToArray());
        Assert.Equal(1, composed.RenderedBlocks);
        Assert.Equal(Pixels(source, new(20, 20, 16, 16)), Pixels(composed.Image, new(20, 20, 16, 16)));
        Assert.Equal(Pixels(source, new(20, 48, 380, 20)), Pixels(composed.Image, new(20, 48, 380, 20)));
        Assert.NotEqual(Pixels(source, new(44, 20, 128, 16)), Pixels(composed.Image, new(44, 20, 128, 16)));
    });

    [Fact]
    public void FullHeightVerticalGridSplitsRowNumberFromCellAndKeepsBothSurfaces() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new("1", new(8, 20, 8, 16)), new("Task", new(30, 20, 42, 16)));
        BitmapSource source = Picture(180, 65, (x, _) => x == 25 ? Color.FromRgb(160, 160, 160)
            : x < 25 ? Color.FromRgb(240, 240, 240) : Color.FromRgb(220, 235, 249));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line), source);
        Assert.Equal(["1", "Task"], blocks.Select(block => block.Text));
        Assert.All(blocks, block => Assert.Contains(block.ProtectedRegions, region => region.X == 25 && region.Width == 1));
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source,
            blocks.Select(block => new PinOverlayTranslation(block, block.Text == "Task" ? "작업" : null)).ToArray());
        Assert.Equal(1, composed.RenderedBlocks);
        Assert.Equal(Pixels(source, new(0, 0, 26, 65)), Pixels(composed.Image, new(0, 0, 26, 65)));
        Assert.NotEqual(Pixels(source, new(30, 20, 42, 16)), Pixels(composed.Image, new(30, 20, 42, 16)));
    });

    [Fact]
    public void ShortVerticalInkIsNotMistakenForAFullHeightRule()
    {
        OcrLine line = Words(new("Save", new(10, 20, 36, 16)), new("document", new(60, 20, 80, 16)));
        BitmapSource source = Picture(180, 65, (x, y) => x == 52 && y >= 24 && y < 31 ? Colors.Black : Colors.White);
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), source));
        Assert.Equal("Save document", block.Text);
        Assert.Empty(block.ProtectedRegions);
    }

    private static OcrLine Monospace(string text, double x, double y)
    {
        var words = new List<OcrWord>();
        foreach (string word in text.Split(' '))
        {
            double width = OcrGeometry.CharacterColumns(word) * 8;
            words.Add(new(word, new(x, y, width, 16)));
            x += width + 8;
        }
        return Words(words.ToArray());
    }

    private static OcrLine Words(params OcrWord[] words) => new(string.Join(" ", words.Select(word => word.Text)),
        words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), words);
    private static OcrResult Recognition(params OcrLine[] lines) => PinTranslationOverlayTests.Recognition(lines);

    private static BitmapSource Picture(int width, int height, Func<int, int, Color> color)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Color value = color(x, y);
                int index = (y * width + x) * 4;
                pixels[index] = value.B; pixels[index + 1] = value.G; pixels[index + 2] = value.R; pixels[index + 3] = 255;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image, Int32Rect rect)
    {
        var pixels = new byte[rect.Width * rect.Height * 4];
        image.CopyPixels(rect, pixels, rect.Width * 4, 0);
        return pixels;
    }
}
