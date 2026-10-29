using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayBlockR6Tests(ITestOutputHelper output)
{
    [Fact]
    public void OutlinedKeyboardShortcutKeepsRecognizedAndUnreadKeycaps() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new("Press", new(20, 28, 50, 16)), new("Ctrl", new(180, 28, 35, 16)),
            new("+", new(232, 30, 5, 12)), new("to continue", new(320, 28, 100, 16)));
        BitmapSource source = Picture(500, 85, (x, y) =>
        {
            foreach (OcrRect cap in new[] { new OcrRect(170, 20, 58, 30), new OcrRect(240, 20, 52, 30) })
            {
                if (x < cap.X || x >= cap.Right || y < cap.Y || y >= cap.Bottom) continue;
                if (x < cap.X + 2 || x >= cap.Right - 2 || y < cap.Y + 2 || y >= cap.Bottom - 2) return Color.FromRgb(170, 170, 170);
                if (y >= 28 && y < 44 && x > cap.X + 8 && x < cap.Right - 8 && x % 7 < 2) return Colors.Black;
                return Color.FromRgb(239, 239, 239);
            }
            return x >= 232 && x < 237 && y >= 33 && y < 36 ? Colors.Black : Colors.White;
        });
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line), source);
        PinOverlayBlock shortcut = Assert.Single(blocks, block => block.PreservePixels);
        Assert.True(shortcut.Bounds.X <= 170 && shortcut.Bounds.Right >= 292);
        Assert.True(shortcut.Bounds.Y <= 20 && shortcut.Bounds.Bottom >= 50);
        Assert.Equal(shortcut.Bounds, Assert.Single(shortcut.Lines).Bounds);
        Assert.DoesNotContain(blocks.Where(block => !block.PreservePixels), block => block.Text.Contains("Ctrl", StringComparison.Ordinal));
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source, blocks.Select(block =>
            new PinOverlayTranslation(block, block.PreservePixels ? null : "계속")).ToArray());
        Assert.Equal(2, composed.RenderedBlocks);
        var region = new Int32Rect((int)shortcut.Bounds.X, (int)shortcut.Bounds.Y, (int)shortcut.Bounds.Width, (int)shortcut.Bounds.Height);
        Assert.Equal(Pixels(source, region), Pixels(composed.Image, region));
    });

    [Fact]
    public void KeyboardNamesWithoutKeycapGraphicsRemainProse()
    {
        OcrLine line = Words(new OcrWord("Press Ctrl to continue", new(20, 20, 220, 20)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), Picture(300, 70, (_, _) => Colors.White)));
        Assert.False(block.PreservePixels);
        Assert.Equal(line.Text, block.Text);
    }

    [Fact]
    public void RemovedKeyboardOcrWordKeepsItsAttachedSuffixPixels() => StaTestHost.Run(() =>
    {
        OcrLine line = Words(new("Press", new(10, 25, 55, 20)), new("Ctrl으로", new(90, 25, 80, 20)),
            new("continue", new(190, 25, 90, 20)));
        BitmapSource source = Picture(340, 80, (x, y) =>
        {
            if (x >= 154 && x < 170 && y >= 25 && y < 45 && x % 6 < 2) return Colors.Black;
            if (x < 80 || x >= 150 || y < 17 || y >= 51) return Colors.White;
            if (x < 82 || x >= 148 || y < 19 || y >= 49) return Color.FromRgb(170, 170, 170);
            if (x >= 90 && x < 140 && y >= 25 && y < 45 && x % 8 < 2) return Colors.Black;
            return Color.FromRgb(239, 239, 239);
        });
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line), source);
        PinOverlayBlock shortcut = Assert.Single(blocks, block => block.PreservePixels);
        Assert.True(shortcut.Bounds.Right >= 170);
        Assert.DoesNotContain(blocks, block => !block.PreservePixels && block.Text.Contains("Ctrl", StringComparison.Ordinal));
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source, blocks.Select(block =>
            new PinOverlayTranslation(block, block.PreservePixels ? null : "계속")).ToArray());
        Assert.True(composed.RenderedBlocks > 0);
        var region = new Int32Rect((int)shortcut.Bounds.X, (int)shortcut.Bounds.Y, (int)shortcut.Bounds.Width, (int)shortcut.Bounds.Height);
        Assert.Equal(Pixels(source, region), Pixels(composed.Image, region));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutlinedDeleteNeedsKeyboardInstructionsBeforeItBecomesProtected(bool keyboardInstruction)
    {
        OcrWord label = new("Delete", new(90, 25, 50, 20));
        OcrLine line = keyboardInstruction ? Words(new("Press", new(10, 25, 55, 20)), label) : Words(label);
        BitmapSource source = Picture(240, 80, (x, y) =>
        {
            if (x < 80 || x >= 150 || y < 17 || y >= 51) return Colors.White;
            if (x < 82 || x >= 148 || y < 19 || y >= 49) return Color.FromRgb(170, 170, 170);
            if (x >= 90 && x < 140 && y >= 25 && y < 45 && x % 8 < 2) return Colors.Black;
            return Color.FromRgb(239, 239, 239);
        });
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line), source);
        Assert.Equal(keyboardInstruction ? 1 : 0, blocks.Count(block => block.PreservePixels));
        Assert.Contains(blocks, block => !block.PreservePixels && block.Text == (keyboardInstruction ? "Press" : "Delete"));
    }

    [PinOverlayReplayFact]
    public async Task RecordedKoreanPdfKeepsEveryShortcutCapsuleAndItsLabel()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        const string name = "pdf-ko-wiki-screenshot-p2";
        string savedSource = Path.Combine(replay, name + "-source.png");
        BitmapSource source;
        if (File.Exists(savedSource)) source = LoadPicture(savedSource);
        else
        {
            string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
            IReadOnlyList<(string, BitmapSource)> pages = await PinTranslationOverlayCorpusTests.RenderPdfAsync(
                Path.Combine(corpus, "pdf-ko-wiki-screenshot.pdf"));
            source = pages[1].Item2;
        }
        StaTestHost.Run(() =>
        {
            OcrResult recognized = ReadRecognized(Path.Combine(replay, name + "-ocr.json"));
            IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(recognized, source);
            OcrRect[] shortcuts = blocks.Where(block => block.PreservePixels).Select(block => block.Bounds).ToArray();
            foreach (Point point in new[] { new Point(1150, 810), new Point(460, 944), new Point(600, 944), new Point(690, 944),
                new Point(550, 1002), new Point(690, 1002), new Point(790, 1002), new Point(1100, 1002),
                new Point(490, 1580), new Point(465, 1640), new Point(600, 1640), new Point(450, 1875) })
                Assert.True(shortcuts.Any(cap => point.X >= cap.X && point.X < cap.Right && point.Y >= cap.Y && point.Y < cap.Bottom),
                    $"Missing keycap or attached OCR suffix at {point}; detected {string.Join("; ", shortcuts)}");
            OcrWord footer = Assert.Single(recognized.Lines.SelectMany(line => line.Words),
                word => word.Bounds.X == 121 && word.Bounds.Y == 1856);
            Assert.Contains(shortcuts, cap => cap.X <= footer.Bounds.X && cap.Y <= footer.Bounds.Y
                && cap.Right >= footer.Bounds.Right && cap.Bottom >= footer.Bounds.Bottom);
            foreach (PinOverlayBlock block in blocks.Where(block => !block.PreservePixels))
                foreach (OcrRect cap in shortcuts.Where(cap => cap.X < block.Bounds.Right && cap.Right > block.Bounds.X
                    && cap.Y < block.Bounds.Bottom && cap.Bottom > block.Bounds.Y))
                    Assert.Contains(cap, block.ProtectedRegions);
            PinOverlayComposition composed = PinTranslationOverlay.Compose(source, blocks.Select(block =>
                new PinOverlayTranslation(block, block.PreservePixels ? null : "Translated surrounding instructions.")).ToArray());
            Assert.True(composed.RenderedBlocks > 0);
            foreach (OcrRect cap in shortcuts)
            {
                var region = new Int32Rect((int)cap.X, (int)cap.Y, (int)cap.Width, (int)cap.Height);
                Assert.Equal(Pixels(source, region), Pixels(composed.Image, region));
            }
            output.WriteLine($"Preserved {shortcuts.Length} keyboard shortcut spans including unread Cmd/Shift labels.");
        });
    }

    [Fact]
    public void OcrOnlyColumnsKeepSameSizeLinesWithDifferentDescendersTogether() => StaTestHost.Run(() =>
    {
        var lines = new List<OcrLine>();
        PinTranslationOverlayEvidenceTests.Draw("columns", false, lines);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(lines.ToArray()));
        Assert.Equal(3, blocks.Count);
        PinOverlayBlock left = Assert.Single(blocks, block => block.Text.StartsWith("Create a new document.", StringComparison.Ordinal));
        PinOverlayBlock right = Assert.Single(blocks, block => block.Text.StartsWith("Add pictures to your library.", StringComparison.Ordinal));
        Assert.Equal(4, left.Lines.Count);
        Assert.Equal(4, right.Lines.Count);
    });

    [PinOverlayReplayFact]
    public void RecordedMenusBadgesAndDamagedNumericCommentsStayIndependent()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        PinOverlayBlock[] Blocks(string name) => [.. PinTranslationOverlay.BuildBlocks(
            ReadRecognized(Path.Combine(replay, name + "-ocr.json")), LoadPicture(Path.Combine(corpus, name + ".png")))];
        PinOverlayBlock[] mdn = Blocks("web-en-mdn-dark");
        Assert.Contains(mdn, block => block.Text == "Web APIs");
        Assert.DoesNotContain(mdn, block => block.Text.Contains("HTML", StringComparison.Ordinal)
            && block.Text.Contains("Web APIs", StringComparison.Ordinal));
        PinOverlayBlock[] github = Blocks("web-en-github-readme");
        Assert.Contains(github, block => block.Text == "windows-app");
        Assert.Contains(github, block => block.Text == "wpf");
        PinOverlayBlock[] python = Blocks("web-en-python-tutorial");
        Assert.Contains(python, block => block.Text == "and this 15 the second comment" && !block.PreservePixels);
        Assert.DoesNotContain(python, block => block.Text.Contains("second comment", StringComparison.Ordinal)
            && block.Text.Contains("third", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedDropdownsKeepMenuLabelsSeparateAndProtectChevrons()
    {
        OcrLine line = Words(new("HTML", new(20, 20, 40, 12)), new("v", new(66, 22, 8, 8)),
            new("CSS", new(98, 20, 30, 12)), new("v", new(134, 22, 8, 8)),
            new("Web APIs", new(166, 20, 72, 12)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        Assert.Equal(["HTML", "CSS", "Web APIs"], blocks.Select(block => block.Text));
        Assert.All(blocks, block => Assert.Equal(2, block.ProtectedRegions.Count));
    }

    [Fact]
    public void HierarchicalNumberedNavigationKeepsEachItemSeparate()
    {
        OcrLine[] lines = [Words(new("3.", new(10, 20, 12, 12)), new("Introduction", new(28, 20, 120, 12))),
            Words(new("3.1.", new(10, 38, 24, 12)), new("Using the calculator", new(40, 38, 160, 12))),
            Words(new("3.1.1.", new(10, 56, 36, 12)), new("Numbers", new(52, 56, 80, 12)))];
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(lines));
        Assert.Equal(["Introduction", "Using the calculator", "Numbers"], blocks.Select(block => block.Text));
        Assert.All(blocks, block => Assert.Single(block.ProtectedRegions));
    }

    [Fact]
    public void NumericAssignmentWithLostEqualsKeepsTheCommentSeparate()
    {
        OcrLine line = Words(new("5", new(20, 20, 8, 12)), new("1", new(36, 20, 8, 12)),
            new("#", new(52, 20, 8, 12)), new("the second comment", new(68, 20, 160, 12)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        PinOverlayBlock comment = Assert.Single(blocks, block => !block.PreservePixels);
        Assert.Equal("the second comment", comment.Text);
        Assert.Contains(blocks, block => block.PreservePixels && block.Text == "5 1 #");
    }

    [Fact]
    public void IsolatedBadgesDoNotMergeAcrossTwoGlyphHeightsOfSpace()
    {
        OcrLine line = Words(new("Windows", new(20, 20, 48, 9)),
            new("windows-app", new(96, 20, 73, 12)), new("wpf", new(197, 20, 21, 12)));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line));
        Assert.Equal(["Windows", "windows-app", "wpf"], blocks.Select(block => block.Text));
    }

    [Theory]
    [InlineData("Comments in Python start with the hash character, # and extend to the end of the line.")]
    [InlineData("해시 문자 # 로 시작하고 줄의 끝까지 이어집니다.")]
    [InlineData("the number of operations required to relate signals #이11 two arbitrary positions grows")]
    public void HashMentionInProseDoesNotProtectOrSplitTheSentence(string text)
    {
        OcrLine line = Words(new OcrWord(text, new(20, 20, 600, 20)));
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line)));
        Assert.False(block.PreservePixels);
        Assert.Empty(block.ProtectedRegions);
        Assert.Equal(text, block.Text);
    }

    [PinOverlayReplayFact]
    public async Task RecordedRowFragmentsDoNotOverlapAnotherBlocksText()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        IReadOnlyList<(string, BitmapSource)> pages = await PinTranslationOverlayCorpusTests.RenderPdfAsync(
            Path.Combine(corpus, "pdf-en-irs-w9.pdf"));
        foreach ((string name, BitmapSource source, double top, double bottom) in new[]
        {
            ("pdf-en-irs-w9-p1", pages[0].Item2, 235d, 320d),
            ("pdf-en-irs-w9-p1", pages[0].Item2, 560d, 635d),
            ("pdf-en-irs-w9-p1", pages[0].Item2, 1335d, 1418d),
            ("pdf-en-irs-w9-p2", pages[1].Item2, 740d, 830d),
            ("var-small60-en-wiki", LoadPicture(Path.Combine(corpus, "var-small60-en-wiki.png")), 125d, 225d),
        })
        {
            OcrResult recognized = ReadRecognized(Path.Combine(replay, name + "-ocr.json"));
            PinOverlayBlock[] blocks = [.. PinTranslationOverlay.BuildBlocks(recognized, source)
                .Where(block => block.Bounds.Y >= top && block.Bounds.Y < bottom)];
            OcrTable table = OcrExtractionAnalyzer.Analyze(recognized).Table;
            output.WriteLine($"{name} y{top}: inferred table {table.Rows.Count}x{table.Columns.Count}, header={table.HasHeader}");
            foreach (PinOverlayBlock block in blocks)
                output.WriteLine($"  {block.Bounds}: {block.Text}; lines={string.Join(" | ", block.Lines.Select(line => line.Bounds))}");
            foreach (PinOverlayBlock block in blocks)
                foreach (OcrRect other in blocks.Where(other => !ReferenceEquals(other, block)).SelectMany(other => other.Lines).Select(line => line.Bounds))
                    Assert.False(block.Bounds.X < other.Right && block.Bounds.Right > other.X
                        && block.Bounds.Y < other.Bottom && block.Bounds.Bottom > other.Y,
                        $"{name}: block {block.Bounds} overlaps another text line {other}: {block.Text}");
            if (name == "pdf-en-irs-w9-p2")
            {
                PinOverlayBlock item = Assert.Single(blocks, block => block.Text.StartsWith("Form 1099-A", StringComparison.Ordinal));
                Assert.DoesNotContain("Use Form W-9", item.Text, StringComparison.Ordinal);
                Assert.Single(item.Lines);
            }
        }
    }

    private static BitmapSource LoadPicture(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path);
        bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    [PinOverlayReplayFact]
    public async Task RecordedDescenderDifferencesKeepAbstractAndSubtitleTogether()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        OcrResult abstractOcr = ReadRecognized(Path.Combine(replay, "pdf-en-arxiv-attention-p1-ocr.json"));
        IReadOnlyList<(string, BitmapSource)> pages = await PinTranslationOverlayCorpusTests.RenderPdfAsync(
            Path.Combine(corpus, "pdf-en-arxiv-attention.pdf"));
        IReadOnlyList<PinOverlayBlock> abstractBlocks = PinTranslationOverlay.BuildBlocks(abstractOcr, pages[0].Item2);
        PinOverlayBlock paragraph = Assert.Single(abstractBlocks, block => block.Text.StartsWith("dominant sequence", StringComparison.Ordinal));
        Assert.Contains("convolutional neural networks", paragraph.Text, StringComparison.Ordinal);
        Assert.Contains("mechanism. We propose", paragraph.Text, StringComparison.Ordinal);
        Assert.True(paragraph.Lines.Count >= 5);

        OcrResult subtitleOcr = ReadRecognized(Path.Combine(replay, "ui-en-subtitle-ocr.json"));
        var source = new BitmapImage();
        source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(Path.Combine(corpus, "ui-en-subtitle.png"));
        source.EndInit(); source.Freeze();
        PinOverlayBlock subtitle = Assert.Single(PinTranslationOverlay.BuildBlocks(subtitleOcr, source));
        Assert.Equal(2, subtitle.Lines.Count);
        Assert.Equal(TextAlignment.Center, PinTranslationOverlay.InferAlignment(subtitle.Lines));

        foreach (string name in new[] { "web-en-wiki-screenshot", "var-small60-en-wiki" })
        {
            OcrResult recognized = ReadRecognized(Path.Combine(replay, name + "-ocr.json"));
            var wiki = new BitmapImage();
            wiki.BeginInit(); wiki.CacheOption = BitmapCacheOption.OnLoad;
            wiki.UriSource = new Uri(Path.Combine(corpus, name + ".png"));
            wiki.EndInit(); wiki.Freeze();
            PinOverlayBlock history = Assert.Single(PinTranslationOverlay.BuildBlocks(recognized, wiki),
                block => block.Text.Contains("screenshots were created", StringComparison.Ordinal));
            Assert.Equal(7, history.Lines.Count);
            Assert.Contains("entire screencasts", history.Text, StringComparison.Ordinal);
        }
    }

    [PinOverlayReplayFact]
    public async Task RecordedW9TitleAndInverseLabelsStaySeparateFromBody()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        OcrResult recognized = ReadRecognized(Path.Combine(replay, "pdf-en-irs-w9-p1-ocr.json"));
        IReadOnlyList<(string, BitmapSource)> pages = await PinTranslationOverlayCorpusTests.RenderPdfAsync(
            Path.Combine(corpus, "pdf-en-irs-w9.pdf"));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(recognized, pages[0].Item2);
        PinOverlayBlock title = Assert.Single(blocks, block => block.Text.StartsWith("Request for Taxpayer", StringComparison.Ordinal));
        Assert.Contains("Certification", title.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("www.irs.gov", title.Text, StringComparison.Ordinal);
        Assert.Contains(blocks, block => block.Text == "Part I");
        Assert.Contains(blocks, block => block.Text == "Part II");
        Assert.Contains(blocks, block => block.Text == "Certification");
        Assert.DoesNotContain(blocks, block => block.Text.Contains("Taxpayer", StringComparison.Ordinal)
            && block.Text.Contains("Enter your TIN", StringComparison.Ordinal));
    }

    [PinOverlayReplayFact]
    public void RecordedKoreanWikipediaKeepsBodyLineAndSeparatesThreeListItems()
    {
        string replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        OcrResult recognized = ReadRecognized(Path.Combine(replay, "web-ko-wiki-screenshot-ocr.json"));
        var source = new BitmapImage();
        source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri(Path.Combine(corpus, "web-ko-wiki-screenshot.png"));
        source.EndInit(); source.Freeze();
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(recognized, source);
        PinOverlayBlock body = Assert.Single(blocks, block => block.Lines.SelectMany(line => line.Words)
            .Any(word => word.Text == "신이" && Math.Abs(word.Bounds.Y - 838) < 2));
        Assert.Contains("모습을", body.Text, StringComparison.Ordinal);
        PinOverlayBlock[] bullets = [.. blocks.Where(block => block.ProtectedRegions
            .Any(rect => rect.X is >= 270 and <= 272 && rect.Y is >= 590 and <= 730))];
        Assert.Equal(3, bullets.Length);
        Assert.DoesNotContain(bullets, block => block.Text.Contains('•'));
        Assert.Contains(bullets, block => block.Text.StartsWith("스크린 갭처:", StringComparison.Ordinal));
    }

    private static OcrResult ReadRecognized(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = json.RootElement;
        return OcrResult.Success(root.GetProperty("Text").GetString()!, "ko",
            root.GetProperty("Lines").Deserialize<OcrLine[]>()!, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlyphStemAndUnderlineEndDoNotSplitKoreanWords(bool underline)
    {
        OcrLine line = Words(new("파일 탐색기", new(20, 20, 100, 16)), new("바로 가기", new(128, 20, 90, 16)));
        BitmapSource source = Picture(260, 70, (x, y) =>
            (x == 124 && y >= 20 && y < 36) || (underline && y == 35 && x >= 20 && x <= 124)
                ? Colors.Black : Colors.White);
        PinOverlayBlock block = Assert.Single(PinTranslationOverlay.BuildBlocks(Recognition(line), source));
        Assert.Equal("파일 탐색기 바로 가기", block.Text);
        Assert.Empty(block.ProtectedRegions);
    }

    [Fact]
    public void NarrowRuleExtendingBeyondTheInkSplitsTheSameSmallGap()
    {
        OcrLine line = Words(new("파일 탐색기", new(20, 20, 100, 16)), new("바로 가기", new(128, 20, 90, 16)));
        BitmapSource source = Picture(260, 70, (x, y) => x == 124 && y >= 10 && y < 46 ? Colors.Gray : Colors.White);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(line), source);
        Assert.Equal(["파일 탐색기", "바로 가기"], blocks.Select(block => block.Text));
        Assert.All(blocks, block => Assert.Contains(block.ProtectedRegions, rect => rect.X == 124 && rect.Width == 1));
    }

    [Fact]
    public void BoldHeadingAndRegularParagraphBecomeSeparateBlocks()
    {
        OcrLine heading = Words(new OcrWord("A full width heading for this section", new(20, 20, 200, 16)));
        OcrLine first = Words(new OcrWord("The first regular paragraph line continues", new(20, 44, 200, 16)));
        OcrLine second = Words(new OcrWord("The second regular paragraph line follows", new(20, 68, 200, 16)));
        BitmapSource source = Picture(260, 110, (x, y) =>
            x >= 20 && x < 220 && ((y >= 20 && y < 36 && (x - 20) % 8 < 3)
                || (y >= 44 && y < 60 && (x - 20) % 8 < 1)
                || (y >= 68 && y < 84 && (x - 20) % 8 < 1)) ? Colors.Black : Colors.White);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(heading, first, second), source);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(heading.Text, blocks[0].Text);
        Assert.Equal(2, blocks[1].Lines.Count);
    }

    [Fact]
    public void LargerTitleDoesNotMergeWithInstructionsBelow()
    {
        OcrLine title = Words(new OcrWord("Request for identification and certification", new(20, 20, 240, 28)));
        OcrLine instructions = Words(new OcrWord("Go to the form instructions for more details", new(20, 60, 240, 18)));
        BitmapSource source = Picture(290, 100, (x, y) => x >= 20 && x < 260 && (x - 20) % 9 < 2
            && ((y >= 20 && y < 48) || (y >= 60 && y < 78)) ? Colors.Black : Colors.White);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(title, instructions), source);
        Assert.Equal([title.Text, instructions.Text], blocks.Select(block => block.Text));
    }

    [Fact]
    public void WhiteOnBlackLabelStaysSeparateFromHeadingAndBody()
    {
        OcrLine heading = Words(new("Part", new(10, 20, 36, 16)), new("II", new(52, 20, 14, 16)),
            new("Certification", new(104, 20, 110, 16)));
        OcrLine body = Words(new OcrWord("The regular body continues below the heading", new(10, 44, 240, 16)));
        BitmapSource source = Picture(280, 95, (x, y) =>
        {
            bool label = x < 82 && y >= 7 && y < 40;
            bool text = (y >= 20 && y < 36 && ((x >= 10 && x < 66) || (x >= 104 && x < 214))
                    && x % 8 < 3)
                || (y >= 44 && y < 60 && x >= 10 && x < 250 && x % 8 < 1);
            return label ? text ? Colors.White : Colors.Black : text ? Colors.Black : Colors.White;
        });
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(heading, body), source);
        Assert.Equal(["Part II", "Certification", body.Text], blocks.Select(block => block.Text));
    }

    [Theory]
    [InlineData("•")]
    [InlineData("1.")]
    public void ThreeListItemsStayThreeBlocksAndKeepTheirMarkerPixels(string marker) => StaTestHost.Run(() =>
    {
        OcrLine[] lines = Enumerable.Range(0, 3).Select(index => Words(
            new(marker, new(10, 25 + index * 24, 8, 6)),
            new("Item with explanatory text", new(30, 20 + index * 24, 180, 16)))).ToArray();
        BitmapSource source = Picture(280, 110, (x, y) => x >= 10 && x < 18
            && Enumerable.Range(0, 3).Any(index => y >= 25 + index * 24 && y < 31 + index * 24)
                ? Colors.Black : Colors.White);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(lines), source);
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, block =>
        {
            Assert.Equal("Item with explanatory text", block.Text);
            Assert.Single(block.Lines);
            Assert.Equal(30, block.Bounds.X);
            Assert.Single(block.ProtectedRegions);
        });
        PinOverlayComposition composed = PinTranslationOverlay.Compose(source,
            blocks.Select(block => new PinOverlayTranslation(block, "항목 설명")).ToArray());
        Assert.Equal(3, composed.RenderedBlocks);
        for (int index = 0; index < 3; index++)
        {
            var region = new Int32Rect(8, 23 + index * 24, 12, 10);
            Assert.Equal(Pixels(source, region), Pixels(composed.Image, region));
        }
    });

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
