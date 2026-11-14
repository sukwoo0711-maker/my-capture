using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The pixel-evidence repairs of the picture-translation reading, on text drawn with WPF: the reading under test
/// carries the drawn word boxes with the text Windows OCR is known to return for them.
/// </summary>
public sealed class OcrLatinGlyphRepairTests
{
    // Times New Roman joins more letters at these sizes, so its words seldom split one glyph per letter; it is
    // covered by the cases that must stay unchanged.
    public static TheoryData<string, int> RepairFonts() => new()
    {
        { "Segoe UI", 12 }, { "Segoe UI", 16 }, { "Arial", 12 }, { "Arial", 16 }, { "Consolas", 12 }, { "Consolas", 16 },
    };

    public static TheoryData<string, int> Fonts() => new()
    {
        { "Segoe UI", 12 }, { "Segoe UI", 16 }, { "Arial", 12 }, { "Arial", 16 },
        { "Times New Roman", 16 }, { "Consolas", 12 }, { "Consolas", 16 },
    };

    [Theory]
    [MemberData(nameof(RepairFonts))]
    public void ShapeIdenticalCapitalsAtXHeightBecomeLowerCase(string font, int size) => StaTestHost.Run(() =>
        Assert.Equal("We have also seen boxes of the window so",
            Repair("We have also seen boxes of the window so",
                "We have alSO seen bOXes Of the WindOW SO", font, size)));

    [Theory]
    [MemberData(nameof(RepairFonts))]
    public void DigitsInsideWordsBecomeTheLettersTheirInkShows(string font, int size) => StaTestHost.Run(() =>
        Assert.Equal("Copilot reads the tile and more so",
            Repair("Copilot reads the tile and more so", "Copi10t reads the ti1e and m0re 50", font, size)));

    [Theory]
    [InlineData("Segoe UI", 16)]
    [InlineData("Arial", 16)]
    [InlineData("Consolas", 16)]
    public void DottedStemsAreIAndTallNIsH(string font, int size) => StaTestHost.Run(() =>
        Assert.Equal("the pin with icon for hours",
            Repair("the pin with icon for hours", "tne pln wlth lcon for nours", font, size)));

    [Theory]
    [MemberData(nameof(Fonts))]
    public void DrawnCaseAndIdentifiersAreKept(string font, int size) => StaTestHost.Run(() =>
    {
        const string text = "Of these, maxWidth and getURL run on iOS, eBay, McDonald OK USB3 x0 Windows OS 50 and all will tell";
        Assert.Equal(text, Repair(text, text, font, size));
    });

    [Theory]
    [InlineData("Segoe UI", 16)]
    [InlineData("Segoe UI", 24)]
    [InlineData("Arial", 24)]
    [InlineData("Consolas", 16)]
    [InlineData("Consolas", 24)]
    public void InteriorCrossbarAndXHeightRoundGlyphRepairTheNumericReading(string font, int size) => StaTestHost.Run(() =>
    {
        const string drawn = "We have to read the page";
        Assert.Equal(drawn, Repair(drawn, "We have 10 read the page", font, size));
    });

    [Fact]
    public void CrossbarRepairDeclinesTouchingGlyphsAndMissingReferences() => StaTestHost.Run(() =>
    {
        const string read = "We have 10 read the page";
        (BitmapSource bitmap, OcrResult reading) = Scene("We have to read the page", read, "Arial", 16);
        OcrGrayImage image = Assert.IsType<OcrGrayImage>(OcrGrayImage.From(bitmap));
        OcrLine line = reading.Lines[0];
        OcrLatinGlyphRepair.Glyph[]?[] glyphs = [.. line.Words.Select(word => OcrLatinGlyphRepair.Segment(image, word, line.Bounds))];
        Assert.Equal("10", line.Words[2].Text);
        Assert.Single(Assert.IsType<OcrLatinGlyphRepair.Glyph[]>(glyphs[2]));
        MethodInfo reference = typeof(OcrLatinGlyphRepair).GetMethod("Reference", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("OcrLatinGlyphRepair.Reference");
        Assert.Null(reference.Invoke(null, [line, glyphs]));
        OcrResult repaired = OcrLatinGlyphRepair.Repair(reading, bitmap);
        Assert.Same(reading, repaired);
        Assert.Equal(read, repaired.Text);
    });

    [Theory]
    [MemberData(nameof(Fonts))]
    [InlineData("Segoe UI", 24)]
    [InlineData("Arial", 24)]
    [InlineData("Consolas", 24)]
    public void DrawnDigitsAndNumericIdentifiersKeepTheirReading(string font, int size) => StaTestHost.Run(() =>
    {
        const string text = "We read 10 0x10 v10 10% 100 101 more";
        Assert.Equal(text, Repair(text, text, font, size));
        // Independent counterexamples: the round glyph is tall, or the stem has a digit's flag rather than a crossbar.
        Assert.Equal("We have 10 more", Repair("We have t0 more", "We have 10 more", font, size));
        Assert.Equal("We have 10 more", Repair("We have 1o more", "We have 10 more", font, size));
    });

    [Theory]
    [InlineData("Segoe UI", 16)]
    [InlineData("Arial", 24)]
    public void CrossbarRepairRequiresLineReferencesAndAnUnadornedNumericAtom(string font, int size) => StaTestHost.Run(() =>
    {
        Assert.Equal("10", Repair("to", "10", font, size));
        Assert.Equal("MMMM 10", Repair("MMMM to", "MMMM 10", font, size));
        Assert.Equal("We have 10% more", Repair("We have to% more", "We have 10% more", font, size));
        Assert.Equal("We have v10 more", Repair("We have vto more", "We have v10 more", font, size));
        Assert.Equal("We have 0x10 more", Repair("We have 0xto more", "We have 0x10 more", font, size));
        // Too few pixel rows to distinguish a flag from a crossbar must keep the original reading.
        Assert.Equal("We have 10 more", Repair("We have to more", "We have 10 more", font, 6));
    });

    [Fact]
    public void CrossbarRequiresAnUndottedAscenderAndAnAdjacentRoundGlyphAtXHeight()
    {
        OcrLatinGlyphRepair.Glyph crossbar = new(9, false, true), round = new(7, false), tall = new(10, false);
        Assert.Equal("to", OcrLatinGlyphRepair.RepairWord("10", [crossbar, round], 7, 10));
        // The crossbar stem must rise strictly above the x-height margin, including its exact boundary.
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(7.99, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(8, false, true), round], 7, 10));
        Assert.Equal("to", OcrLatinGlyphRepair.RepairWord("10", [new(8.01, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(9, false), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [crossbar, tall], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [crossbar, new(3, false)], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(9, true, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(7, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(15, false, true), round], 7, 10));
        Assert.Equal("10%", OcrLatinGlyphRepair.RepairWord("10%", [crossbar, round, tall], 7, 10));
        Assert.Equal("v10", OcrLatinGlyphRepair.RepairWord("v10", [round, crossbar, round], 7, 10));
        Assert.Equal("0x10", OcrLatinGlyphRepair.RepairWord("0x10", [tall, round, crossbar, round], 7, 10));
    }

    [Fact]
    public void RepairWordFollowsTheGlyphHeights()
    {
        // A dotted glyph's height is its body without the dot.
        OcrLatinGlyphRepair.Glyph x = new(7, false), tall = new(10, false), dotted = new(7, true);
        Assert.Equal("also", OcrLatinGlyphRepair.RepairWord("alSO", [x, tall, x, x], 7, 10));
        Assert.Equal("aSO", OcrLatinGlyphRepair.RepairWord("aSO", [x, tall, tall], 7, 10));
        Assert.Equal("Of", OcrLatinGlyphRepair.RepairWord("Of", [tall, tall], 7, 10));
        Assert.Equal("of", OcrLatinGlyphRepair.RepairWord("Of", [x, tall], 7, 10));
        Assert.Equal("with", OcrLatinGlyphRepair.RepairWord("wlth", [x, dotted, tall, tall], 7, 10));
        Assert.Equal("Image", OcrLatinGlyphRepair.RepairWord("Image", [tall, x, x, x, x], 7, 10));
        Assert.Equal("Clipboard", OcrLatinGlyphRepair.RepairWord("C11pboard", [tall, tall, dotted, x, tall, x, x, x, tall], 7, 10));
        Assert.Equal("a11", OcrLatinGlyphRepair.RepairWord("a11", [x, tall, tall], 7, 10));
        Assert.Equal("x0", OcrLatinGlyphRepair.RepairWord("x0", [x, tall], 7, 10));
        Assert.Equal("of", OcrLatinGlyphRepair.RepairWord("0f", [x, tall], 7, 10));
        Assert.Equal("Win10Pro", OcrLatinGlyphRepair.RepairWord("Win10Pro", [tall, dotted, x, tall, tall, tall, x, x], 7, 10));
        Assert.Equal("maxWidth", OcrLatinGlyphRepair.RepairWord("maxWidth", [x, x, x, tall, dotted, tall, tall, tall], 7, 10));
        Assert.Equal("so", OcrLatinGlyphRepair.RepairWord("SO", [x, x], 7, 10));
        Assert.Equal("SO", OcrLatinGlyphRepair.RepairWord("SO", [tall, tall], 7, 10));
        Assert.Equal("SO", OcrLatinGlyphRepair.RepairWord("SO", [x, tall], 7, 10));
        Assert.Equal("so", OcrLatinGlyphRepair.RepairWord("50", [x, x], 7, 10));
        Assert.Equal("50", OcrLatinGlyphRepair.RepairWord("50", [tall, tall], 7, 10));
        Assert.Equal("is", OcrLatinGlyphRepair.RepairWord("15", [dotted, x], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [tall, tall], 7, 10));
        Assert.Equal("OK", OcrLatinGlyphRepair.RepairWord("OK", [x, x], 7, 10));
        // A glyph count that does not match the text decides nothing.
        Assert.Equal("alSO", OcrLatinGlyphRepair.RepairWord("alSO", [x, tall, x], 7, 10));
        // A tall stem with ink above it (another line's descender) is an l, not an i.
        OcrLatinGlyphRepair.Glyph tallUnderInk = new(10, true);
        Assert.Equal("will", OcrLatinGlyphRepair.RepairWord("will", [x, dotted, tallUnderInk, tallUnderInk], 7, 10));
        Assert.Equal("Cllpboard", OcrLatinGlyphRepair.RepairWord("C11pboard", [tall, tall, tallUnderInk, x, tall, x, x, x, tall], 7, 10));
        // A word of another size than the line's references is judged by its own letters: a heading Syntax keeps its
        // n on a line of smaller text, and a body-text Steps keeps its capital on a line measured from a heading.
        Assert.Equal("Syntax", OcrLatinGlyphRepair.RepairWord("Syntax",
            [new(24, false), new(24, false), new(17, false), new(21, false), new(17, false), new(17, false)], 10, 14));
        Assert.Equal("Steps", OcrLatinGlyphRepair.RepairWord("Steps",
            [new(10, false), new(9, false), new(7, false), new(9, false), new(7, false)], 10, 14));
        Assert.Equal("steps", OcrLatinGlyphRepair.RepairWord("Steps",
            [new(7, false), new(9, false), new(7, false), new(9, false), new(7, false)], 10, 14));
    }

    [Theory]
    [InlineData("alSO", true)]
    [InlineData("Copi10t", true)]
    [InlineData("wlth", true)]
    [InlineData("maxWidth", true)]
    [InlineData("OK", false)]
    [InlineData("SO", true)]
    [InlineData("OS", true)]
    [InlineData("50", true)]
    [InlineData("15", true)]
    [InlineData("2025", false)]
    [InlineData("12", false)]
    [InlineData("USB3", false)]
    [InlineData("P100", false)]
    [InlineData("x64", false)]
    [InlineData("get_URL", false)]
    [InlineData("file.exe", false)]
    [InlineData("한글O", false)]
    [InlineData("eBay", false)]
    public void CandidatesAreLatinAtomsWithAShapeQuestion(string word, bool expected) =>
        Assert.Equal(expected, OcrLatinGlyphRepair.IsCandidate(word));

    [Fact]
    public void KoreanLinesAreNotMeasured()
    {
        OcrResult reading = OcrResult.Success("한글 문장과 alSO 단어", "ko", [new OcrLine("한글 문장과 alSO 단어", new OcrRect(0, 0, 200, 20),
            [new OcrWord("한글", new OcrRect(0, 0, 40, 20)), new OcrWord("문장과", new OcrRect(45, 0, 50, 20)),
                new OcrWord("alSO", new OcrRect(100, 0, 40, 20)), new OcrWord("단어", new OcrRect(150, 0, 40, 20))])], TimeSpan.Zero);
        Assert.False(OcrLatinGlyphRepair.HasCandidates(reading));
        Assert.Same(reading, OcrLatinGlyphRepair.Repair(reading, new OcrGrayImage(200, 20, new byte[200 * 20])));
    }

    [Fact]
    public void JoinedLineIsRecognisedOnlyWhenLongWideAndLatin()
    {
        Assert.True(OcrLatinLineSplit.IsJoined(new OcrWord("Thelibraryopensashortmessagewhenacapture", new OcrRect(0, 0, 900, 24))));
        Assert.False(OcrLatinLineSplit.IsJoined(new OcrWord("internationalization", new OcrRect(0, 0, 500, 24))));
        Assert.False(OcrLatinLineSplit.IsJoined(new OcrWord("Thelibraryopensashortmessagewhenacapture", new OcrRect(0, 0, 200, 24))));
        Assert.False(OcrLatinLineSplit.IsJoined(new OcrWord("한글한글한글한글한글한글한글한글한글한글한글한글한글", new OcrRect(0, 0, 900, 24))));
        Assert.False(OcrLatinLineSplit.IsJoined(new OcrWord("https://example.com/a/very/long/path/name", new OcrRect(0, 0, 900, 24))));
    }

    [Fact]
    public void JoinedLineIsReplacedByPiecesThatSpellIt() => StaTestHost.Run(() =>
    {
        const string drawn = "The library opens a short message when a capture is saved so you can copy it or pin it and the "
            + "keyboard shortcuts work even when the app window is closed while the tray icon stays in the corner of the screen";
        (BitmapSource bitmap, OcrResult reading) = Joined(drawn);
        string[] words = drawn.Split(' ');
        OcrResult split = OcrLatinLineSplit.SplitAsync(reading, OcrGrayImage.From(bitmap)!, (canvas, _) =>
            Task.FromResult(PieceReading(canvas, words)), CancellationToken.None).GetAwaiter().GetResult();
        OcrLine line = Assert.Single(split.Lines);
        Assert.Equal(drawn, line.Text);
        Assert.Equal(words, line.Words.Select(word => word.Text));
        Assert.Equal(reading.Lines[0].Bounds, line.Bounds);
        OcrRect joined = reading.Lines[0].Bounds;
        Assert.All(line.Words, word => Assert.InRange(word.Bounds.X, joined.X - joined.Height, joined.Right + joined.Height));
    });

    [Fact]
    public void JoinedLineIsKeptWhenThePiecesSpellSomethingElse() => StaTestHost.Run(() =>
    {
        const string drawn = "The library opens a short message when a capture is saved so you can copy it or pin it and the "
            + "keyboard shortcuts work even when the app window is closed while the tray icon stays in the corner of the screen";
        (BitmapSource bitmap, OcrResult reading) = Joined(drawn);
        OcrResult split = OcrLatinLineSplit.SplitAsync(reading, OcrGrayImage.From(bitmap)!, (canvas, _) =>
            Task.FromResult(PieceReading(canvas, ["An", "unrelated", "reading", "of", "another", "line"])), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.Same(reading, split);
    });

    [Fact]
    public void OnlyImageTranslationRequestsAreRepaired() => StaTestHost.Run(() =>
    {
        const string drawn = "We have also seen boxes of the window";
        (BitmapSource bitmap, OcrResult reading) = Scene(drawn, "We have alSO seen bOXes Of the WindOW", "Segoe UI", 16);
        var service = new CaptureOcrService(new FixedOcr(reading), (_, _) => Task.FromResult(OcrResult.Unavailable()),
            () => false, () => false, NullLogger<CaptureOcrService>.Instance);
        OcrRequest plain = OcrRequest.FromBitmap(bitmap, searchRotatedOrientations: false);
        Assert.Same(reading, service.RecognizeAsync(plain).GetAwaiter().GetResult());
        Assert.Equal(drawn, service.RecognizeAsync(plain.WithLatinRecovery()).GetAwaiter().GetResult().Text);
        Assert.Same(reading, service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery()).GetAwaiter().GetResult());
    });

    private static string Repair(string drawn, string read, string font, int size)
    {
        (BitmapSource bitmap, OcrResult reading) = Scene(drawn, read, font, size);
        OcrResult repaired = OcrLatinGlyphRepair.Repair(reading, bitmap);
        Assert.Equal(reading.Lines[0].Words.Select(word => word.Bounds), repaired.Lines[0].Words.Select(word => word.Bounds));
        return repaired.Text;
    }

    /// <summary>Draws <paramref name="drawn"/> and returns a one-line reading of <paramref name="read"/> with the drawn word boxes.</summary>
    internal static (BitmapSource, OcrResult) Scene(string drawn, string read, string font, int size)
    {
        string[] drawnWords = drawn.Split(' '), readWords = read.Split(' ');
        Assert.Equal(drawnWords.Length, readWords.Length);
        var text = new FormattedText(drawn, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, new Typeface(font), size,
            Brushes.Black, null, TextFormattingMode.Display, 1.0);
        var origin = new Point(20, 20);
        BitmapSource bitmap = Draw(text, origin, (int)Math.Ceiling(text.Width) + 40, (int)Math.Ceiling(text.Height) + 40);
        var words = new List<OcrWord>();
        int start = 0;
        for (int index = 0; index < drawnWords.Length; index++)
        {
            Rect box = text.BuildHighlightGeometry(origin, start, drawnWords[index].Length).Bounds;
            words.Add(new OcrWord(readWords[index], new OcrRect(Math.Floor(box.X), Math.Floor(box.Y), Math.Ceiling(box.Width), Math.Ceiling(box.Height))));
            start += drawnWords[index].Length + 1;
        }
        OcrRect bounds = words.Select(word => word.Bounds).Aggregate(OcrRect.Union);
        return (bitmap, OcrResult.Success(read, "ko", [new OcrLine(read, bounds, words)], TimeSpan.Zero));
    }

    private static (BitmapSource, OcrResult) Joined(string drawn)
    {
        var text = new FormattedText(drawn, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18,
            Brushes.Black, null, TextFormattingMode.Display, 1.0);
        var origin = new Point(30, 30);
        BitmapSource bitmap = Draw(text, origin, (int)Math.Ceiling(text.Width) + 60, (int)Math.Ceiling(text.Height) + 60);
        Rect box = text.BuildHighlightGeometry(origin).Bounds;
        var bounds = new OcrRect(Math.Floor(box.X), Math.Floor(box.Y), Math.Ceiling(box.Width), Math.Ceiling(box.Height));
        string joined = drawn.Replace(" ", string.Empty, StringComparison.Ordinal);
        return (bitmap, OcrResult.Success(joined, "ko", [new OcrLine(joined, bounds, [new OcrWord(joined, bounds)])], TimeSpan.Zero));
    }

    // A stand-in reader: the given words, spread along the first stacked piece of the canvas.
    private static OcrResult PieceReading(BitmapSource canvas, IReadOnlyList<string> words)
    {
        // The first inked row of the canvas lies inside the first piece.
        int stride = canvas.PixelWidth;
        byte[] pixels = new byte[stride * canvas.PixelHeight];
        canvas.CopyPixels(pixels, stride, 0);
        int y = Array.FindIndex(pixels, value => value < 128) / stride;
        double width = (canvas.PixelWidth - 40.0) / words.Count;
        OcrWord[] found = [.. words.Select((word, index) => new OcrWord(word, new OcrRect(20 + index * width, y, width - 4, 10)))];
        return OcrResult.Success(string.Join(' ', words), "ko",
            [new OcrLine(string.Join(' ', words), found.Select(word => word.Bounds).Aggregate(OcrRect.Union), found)], TimeSpan.Zero);
    }

    private static BitmapSource Draw(FormattedText text, Point origin, int width, int height)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawText(text, origin);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FixedOcr(OcrResult result) : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
