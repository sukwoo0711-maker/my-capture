using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrLatinWordRecoveryR7Tests
{
    [Theory]
    [InlineData("only one 하 the following seven boxes", "하", "of")]
    [InlineData("or 터 for the tax classification", "터", "P)")]
    [InlineData("훼1 integral part of the system", "훼1", "an")]
    [InlineData("101빙 short-term memory used by the system", "101빙", "long")]
    [InlineData("a variety (교 tasks in this paper", "(교", "of")]
    [InlineData("shooting the screen 예 the operating system", "예", "by")]
    [InlineData("employer identification number (티비. If you do not", "(티비.", "(EIN).")]
    [InlineData("the WMT 2이4 English to German task", "2이4", "2014")]
    public void IndependentWordReadingRepairsOnlyTheSuspectToken(string text, string suspect, string correction)
    {
        OcrResult before = Result(Line(text));
        OcrResult neural = Result(Replace(before.Lines[0], suspect, correction));

        OcrResult after = OcrLatinWordRecovery.Recover(before, neural, TimeSpan.FromMilliseconds(25));

        Assert.Equal(text.Replace(suspect, correction, StringComparison.Ordinal), after.Text);
        Assert.Equal(0, OcrLatinWordRecovery.CountSuspects(after));
        Assert.Equal(before.Lines[0].Bounds, after.Lines[0].Bounds);
        Assert.Equal(before.Lines[0].Words.Select(word => word.Bounds), after.Lines[0].Words.Select(word => word.Bounds));
        Assert.Equal(text, before.Text);
    }

    [Theory]
    [InlineData("한글 본문과 English words를 함께 읽습니다")]
    [InlineData("Microsoft 365 고객 지원 Office")]
    [InlineData("choose 한국어 settings")]
    public void KoreanContentDoesNotTriggerTheLatinRecovery(string text)
    {
        OcrResult before = Result(Line(text));
        Assert.False(OcrLatinWordRecovery.HasSuspects(before));
        Assert.Same(before, OcrLatinWordRecovery.Recover(before, Result(Line("unrelated English reading")), TimeSpan.Zero));
    }

    [Fact]
    public void LatinAlternativeCannotReplaceAnActualTwoSyllableKoreanWord()
    {
        OcrResult before = Result(Line("read the word 한글 in this example"));
        Assert.False(OcrLatinWordRecovery.HasSuspects(before));
        Assert.Same(before, OcrLatinWordRecovery.Recover(before,
            Result(Replace(before.Lines[0], "한글", "hangul")), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("\"가\"")]
    [InlineData("'가'")]
    [InlineData("`가`")]
    public void QuotedKoreanIsPreservedEvenWhenTheAlternativeLooksLatin(string token)
    {
        OcrResult before = Result(Line("read the word " + token + " in this example"));
        Assert.False(OcrLatinWordRecovery.HasSuspects(before));
        Assert.Same(before, OcrLatinWordRecovery.Recover(before,
            Result(Replace(before.Lines[0], token, "a")), TimeSpan.Zero));
    }

    [Fact]
    public void ReadingThatAlsoConsumesTheNextWordIsRejected()
    {
        OcrResult before = Result(Line("presence or absence of prompts (22츠 and input"));
        OcrLine neural = Replace(before.Lines[0], "(22츠", "(>>>and");
        OcrWord[] words = neural.Words.ToArray();
        int index = Array.FindIndex(words, word => word.Text == "(>>>and");
        words[index] = words[index] with { Bounds = OcrRect.Union(words[index].Bounds, words[index + 1].Bounds) };
        neural = neural with { Words = words };
        Assert.Same(before, OcrLatinWordRecovery.Recover(before, Result(neural), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(500, 0)]
    public void ReadingInAnotherRowOrColumnDoesNotRepair(double x, double y)
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        OcrLine correct = Replace(before.Lines[0], "하", "of");
        OcrLine unrelated = correct with
        {
            Bounds = correct.Bounds with { X = correct.Bounds.X + x, Y = correct.Bounds.Y + y },
            Words = correct.Words.Select(word => word with
            { Bounds = word.Bounds with { X = word.Bounds.X + x, Y = word.Bounds.Y + y } }).ToArray(),
        };
        Assert.Same(before, OcrLatinWordRecovery.Recover(before, Result(unrelated), TimeSpan.Zero));
    }

    [Fact]
    public void AWordGuessWithoutTwoLatinAnchorsKeepsSource()
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        OcrWord suspect = before.Lines[0].Words.Single(word => word.Text == "하");
        OcrWord guess = suspect with { Text = "of" };
        OcrResult sparse = Result(new OcrLine("of", guess.Bounds, [guess]));
        Assert.Same(before, OcrLatinWordRecovery.Recover(before, sparse, TimeSpan.Zero));
    }

    [Fact]
    public void SuccessfulRepairPreservesUnrelatedLinesAndExactSeparators()
    {
        OcrLine first = Line("only one 하 the following seven boxes");
        first = first with { Text = "only  one 하\tthe following seven boxes" };
        OcrLine other = Line("actual 한국어 문장");
        OcrResult before = Result(first, other);
        OcrResult after = OcrLatinWordRecovery.Recover(before,
            Result(Replace(first, "하", "of")), TimeSpan.Zero);
        Assert.Equal("only  one of\tthe following seven boxes", after.Lines[0].Text);
        Assert.Same(other, after.Lines[1]);
    }

    [Fact]
    public async Task TranslationFastRequestReadsOnlyTheSuspectRowWithTheRecognizer()
    {
        Assert.Equal(OcrQuality.Fast, new OcrSettings().Quality);
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        int calls = 0;
        OcrResult? first = null;
        var service = Service(new(before), (_, rows, _) =>
        {
            calls++;
            Assert.Equal(OcrLatinWordRecovery.SuspectRows(before).Select(row => (row.Text, row.Bounds)),
                rows.Select(row => (row.Text, row.Bounds)));
            return Task.FromResult(Result(Replace(before.Lines[0], "하", "of")));
        });
        OcrResult after = await service.RecognizeAsync(OcrRequest.FromBytes([1], searchRotatedOrientations: false)
            .WithLatinRecovery(reading =>
            {
                // The Windows reading arrives before its suspect row is read again.
                Assert.Equal(0, calls);
                first = reading;
            }));
        Assert.Equal("only one of the following seven boxes", after.Text);
        Assert.Equal(1, calls);
        Assert.Same(before, first);
    }

    [Fact]
    public async Task EachSuspectRowIsReadOnceWithinItsLatinAnchors()
    {
        OcrLine suspect = Line("we first open the settings and then read only one 하 the following seven boxes on screen");
        OcrLine clean = Line("nothing here needs a second reading at all", 60);
        OcrResult before = Result(suspect, clean);
        IReadOnlyList<OcrLine>? asked = null;
        var service = Service(new(before), (_, rows, _) =>
        {
            Assert.Null(asked);
            asked = rows;
            return Task.FromResult(OcrResult.NoText("ko", TimeSpan.Zero));
        });
        OcrResult after = await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery());
        OcrLine row = Assert.Single(asked!);
        int index = suspect.Words.ToList().FindIndex(word => word.Text == "하");
        Assert.Equal(suspect.Words.Skip(index - 3).Take(7), row.Words);
        Assert.Equal((suspect.Words[index - 3].Bounds.X, suspect.Words[index + 3].Bounds.Right), (row.Bounds.X, row.Bounds.Right));
        Assert.Equal((suspect.Bounds.Y, suspect.Bounds.Height), (row.Bounds.Y, row.Bounds.Height));
        Assert.Equal(before.Text, after.Text);
        Assert.True(after.Elapsed >= before.Elapsed);
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(10, 21)]
    public void SuspectRowKeepsToItsOwnVisualRow(double captionX, double captionY)
    {
        // Windows joined a caption that sits lower into the line.
        OcrWord[] words =
        [
            new("Screenshot", new(captionX, captionY, 60, 10)), new("tools", new(captionX + 64, captionY, 40, 10)),
            new("running", new(170, 10, 50, 16)), new("예", new(224, 12, 14, 12)), new("the", new(242, 10, 24, 16)),
            new("device", new(270, 10, 48, 16)), new("powering", new(322, 10, 64, 16)), new("the", new(390, 10, 24, 16)),
            new("display", new(418, 10, 56, 16)),
        ];
        var line = new OcrLine(string.Join(' ', words.Select(word => word.Text)),
            words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);

        OcrLine row = Assert.Single(OcrLatinWordRecovery.SuspectRows(Result(line)));

        Assert.Equal(["running", "예", "the", "device", "powering"], row.Words.Select(word => word.Text));
        Assert.Equal(new OcrRect(170, 10, 216, 16), row.Bounds);
    }

    [Fact]
    public void MenuWordsFarApartOnOneRowStayAnchors()
    {
        OcrWord[] words =
        [
            new("Office", new(10, 10, 40, 16)), new("Products", new(110, 10, 60, 16)), new("0일.대ces", new(230, 10, 52, 16)),
            new("Account", new(340, 10, 56, 16)), new("billing", new(450, 10, 48, 16)),
        ];
        var line = new OcrLine(string.Join(' ', words.Select(word => word.Text)),
            words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);
        Assert.Equal(words, Assert.Single(OcrLatinWordRecovery.SuspectRows(Result(line))).Words);
    }

    [Fact]
    public void ALaterSuspectsWiderRowKeepsItsLeftAnchors()
    {
        // SameRow is not transitive across boxes of different heights: "(교" sits too low for "open", while
        // "하" shares a row with both, so the merged row starts where the row of "하" starts.
        OcrWord[] words =
        [
            new("please", new(10, 10, 48, 16)), new("we", new(62, 10, 18, 16)), new("open", new(84, 10, 34, 16)),
            new("(교", new(122, 20, 20, 16)), new("하", new(146, 14, 16, 16)), new("the", new(166, 10, 24, 16)),
        ];
        var line = new OcrLine(string.Join(' ', words.Select(word => word.Text)),
            words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);

        OcrLine row = Assert.Single(OcrLatinWordRecovery.SuspectRows(Result(line)));

        Assert.Equal("we open (교 하 the", row.Text);
    }

    [Fact]
    public void SuspectsThatShareAnchorsAreReadInOneRow()
    {
        OcrResult before = Result(Line("we open 하 the settings and 예 read only one of the following seven boxes"));
        OcrLine row = Assert.Single(OcrLatinWordRecovery.SuspectRows(before));
        Assert.Equal("we open 하 the settings and 예 read only one", row.Text);
    }

    [Fact]
    public async Task RotatedReadingRecoversThroughTheWholeImagePass()
    {
        // Row crops are horizontal source strips, so a rotated reading keeps the whole-image
        // reading, whose source-axis boxes Recover maps through the reading orientation.
        var orientation = new OcrReadingOrientation(90, 80, 800);
        OcrLine line = Original(Line("only one 하 the following seven boxes"), orientation);
        OcrResult before = OcrResult.Success(line.Text, "ko", [line], TimeSpan.Zero, orientation);
        int rowCalls = 0, wholeCalls = 0;
        var service = new CaptureOcrService(new FixedEngine(before), (_, _) =>
            {
                wholeCalls++;
                return Task.FromResult(Result(Replace(line, "하", "of")));
            }, () => true, () => false, NullLogger<CaptureOcrService>.Instance,
            readNeuralRows: (_, _, _) => { rowCalls++; throw new InvalidOperationException(); });

        OcrResult after = await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery());

        Assert.Equal("only one of the following seven boxes", after.Text);
        Assert.Equal(orientation, after.ReadingOrientation);
        Assert.Equal((0, 1), (rowCalls, wholeCalls));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OrdinaryFastTextOrMissingModelDoesNotInvokeNeural(bool suspect, bool installed)
    {
        OcrResult before = Result(Line(suspect ? "only one 하 the following seven boxes" : "only one of the following seven boxes"));
        int calls = 0;
        var service = Service(new(before), (_, _, _) => { calls++; throw new InvalidOperationException(); }, installed);
        Assert.Same(before, await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery(_ => calls++)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PlainRecognitionKeepsWindowsResultEvenWithSuspectTokens()
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        int calls = 0;
        var service = Service(new(before), (_, _, _) => { calls++; throw new InvalidOperationException(); });
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        Assert.Same(before, await service.RecognizeAsync(OcrRequestFactory.FromBitmap(image, new OcrSettings())));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void LatinRecoveryCopyKeepsEveryOtherRequestOption()
    {
        OcrRequest original = OcrRequest.FromBytes([1, 2], 3, ["en-US"], searchRotatedOrientations: false,
            enhanceContrast: true, useNeuralModel: true, localCorrection: true, useSuperResolution: true);
        OcrRequest copy = original.WithLatinRecovery();
        Assert.False(original.RecoverLatinIntrusions);
        Assert.True(copy.RecoverLatinIntrusions);
        Assert.Null(copy.BeforeLatinRecovery);
        Action<OcrResult> first = _ => { };
        Assert.Same(first, original.WithLatinRecovery(first).BeforeLatinRecovery);
        Assert.Same(original.EncodedImage, copy.EncodedImage);
        Assert.Equal((3.0, false, true, true, true, true), (copy.UpscaleFactor, copy.SearchRotatedOrientations,
            copy.EnhanceContrast, copy.UseNeuralModel, copy.LocalCorrection, copy.UseSuperResolution));
        Assert.Equal(["en-US"], copy.PreferredLanguages);
    }

    [Fact]
    public async Task ImageTranslationAsksForLatinRecovery()
    {
        var recorder = new RecordingOcr();
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        await PinTranslationEngines.RecognizeResultAsync(recorder, image, new OcrSettings(), CancellationToken.None);
        Assert.True(Assert.IsType<OcrRequest>(recorder.Request).RecoverLatinIntrusions);
    }

    private sealed class RecordingOcr : IOcrService
    {
        public OcrRequest? Request { get; private set; }
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(OcrResult.NoText("ko", TimeSpan.Zero));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryFailureKeepsTheOriginalWindowsResult(bool throws)
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        var service = Service(new(before), (_, _, _) => throws
            ? throw new InvalidOperationException("recognizer failed")
            : Task.FromResult(OcrResult.Failed("recognizer failed")));
        OcrResult after = await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery());
        Assert.Equal(before.Text, after.Text);
        Assert.Same(before.Lines, after.Lines);
        Assert.True(after.Elapsed >= before.Elapsed);
    }

    [Fact]
    public async Task CancellationDuringSecondReadingReturnsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        var service = Service(new(before), (_, _, _) => { cancel.Cancel(); return Task.FromResult(before); });
        Assert.Equal(OcrStatus.Cancelled, (await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery(), cancel.Token)).Status);
    }

    [Fact]
    public void RecognizedRowGetsTheWordBoxesOfTheDetectionPipeline()
    {
        string[] chars = ["o", "n", "l", "y", " ", "o", "f", " ", "t", "h", "e"];
        int[] cols = [2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32];
        OcrLine line = Assert.IsType<OcrLine>(NeuralOcrEngine.RowLine(chars, [.. chars.Select(_ => .9f)], cols, 40,
            new System.Windows.Int32Rect(100, 50, 400, 30), []));
        Assert.Equal("only of the", line.Text);
        Assert.Equal(new OcrRect(100, 50, 400, 30), line.Bounds);
        // Ten pixels a column and three columns a character: each cell is 30 px wide around its column.
        Assert.Equal([("only", new OcrRect(110, 50, 120, 30)), ("of", new OcrRect(260, 50, 60, 30)), ("the", new OcrRect(350, 50, 90, 30))],
            line.Words.Select(word => (word.Text, word.Bounds)));
    }

    [Fact]
    public void RowReadingThatLostItsSpacesSplitsAtTheWindowsWords()
    {
        string[] chars = [.. "ofgeneratingvector".Select(character => character.ToString())];
        int[] cols = [.. chars.Select((_, index) => 2 + 3 * index)];
        float[] scores = [.. chars.Select(_ => .9f)];
        var crop = new System.Windows.Int32Rect(100, 50, 400, 30);
        // Five pixels a column: the characters sit at 112.5, 127.5, ... 367.5.
        OcrRect[] guides = [new(105, 52, 30, 20), new(137, 52, 148, 20), new(287, 52, 90, 20)];

        OcrLine joined = Assert.IsType<OcrLine>(NeuralOcrEngine.RowLine(chars, scores, cols, 80, crop, []));
        OcrLine split = Assert.IsType<OcrLine>(NeuralOcrEngine.RowLine(chars, scores, cols, 80, crop, guides));

        Assert.Equal(["ofgeneratingvector"], joined.Words.Select(word => word.Text));
        Assert.Equal([("of", new OcrRect(105, 50, 30, 30)), ("generating", new OcrRect(135, 50, 150, 30)),
            ("vector", new OcrRect(285, 50, 90, 30))], split.Words.Select(word => (word.Text, word.Bounds)));
    }

    [Fact]
    public void UnsureOrEmptyRowReadingIsDropped()
    {
        string[] chars = ["o", "f"];
        var crop = new System.Windows.Int32Rect(0, 0, 100, 20);
        Assert.Null(NeuralOcrEngine.RowLine(chars, [.4f, .5f], [3, 6], 12, crop, []));
        Assert.Null(NeuralOcrEngine.RowLine([" "], [.9f], [3], 12, crop, []));
        Assert.Null(NeuralOcrEngine.RowLine(null, null, null, 0, crop, []));
    }

    [Fact]
    public void CjkInARowSplitsEveryCharacterIntoItsOwnBox()
    {
        OcrLine line = Assert.IsType<OcrLine>(NeuralOcrEngine.RowLine(["漢", "字", "a", "b"], [.9f, .9f, .9f, .9f],
            [2, 6, 10, 13], 20, new System.Windows.Int32Rect(0, 0, 200, 24), []));
        Assert.Equal(["漢", "字", "a", "b"], line.Words.Select(word => word.Text));
        Assert.True(line.Words.Zip(line.Words.Skip(1)).All(pair => pair.First.Bounds.Right <= pair.Second.Bounds.X));
    }

    [Fact]
    public void RowCropKeepsAMarginInsideTheImage()
    {
        Assert.Equal(new System.Windows.Int32Rect(95, 35, 210, 30), NeuralOcrEngine.RowCrop(new OcrRect(100, 40, 200, 20), 1000, 500));
        Assert.Equal(new System.Windows.Int32Rect(0, 0, 53, 11), NeuralOcrEngine.RowCrop(new OcrRect(1, 1, 50, 8), 1000, 500));
        Assert.Equal(new System.Windows.Int32Rect(990, 495, 10, 5), NeuralOcrEngine.RowCrop(new OcrRect(992, 497, 40, 8), 1000, 500));
        Assert.True(NeuralOcrEngine.RowCrop(new OcrRect(0, 0, 0, 0), 1000, 500).IsEmpty);
    }

    private static CaptureOcrService Service(FixedEngine windows,
        Func<OcrRequest, IReadOnlyList<OcrLine>, CancellationToken, Task<OcrResult>> rows, bool installed = true) =>
        new(windows, (_, _) => throw new InvalidOperationException("The whole-image recognizer must not run for Fast text."),
            () => installed, () => false, NullLogger<CaptureOcrService>.Instance, readNeuralRows: rows);

    private static OcrLine Replace(OcrLine line, string before, string after) => line with
    {
        Text = line.Text.Replace(before, after, StringComparison.Ordinal),
        Words = line.Words.Select(word => word.Text == before ? word with { Text = after } : word).ToArray(),
    };

    private static OcrLine Original(OcrLine reading, OcrReadingOrientation orientation) => reading with
    {
        Bounds = orientation.ToOriginal(reading.Bounds),
        Words = reading.Words.Select(word => word with { Bounds = orientation.ToOriginal(word.Bounds) }).ToArray(),
    };

    private static OcrLine Line(string text, double y = 10)
    {
        double x = 10;
        OcrWord[] words = text.Split(' ').Select(token =>
        {
            var word = new OcrWord(token, new(x, y, Math.Max(15, token.Length * 8), 20));
            x = word.Bounds.Right + 6;
            return word;
        }).ToArray();
        return new(text, new(10, y, x - 16, 20), words);
    }

    private static OcrResult Result(params OcrLine[] lines) => OcrResult.Success(
        string.Join('\n', lines.Select(line => line.Text)), "ko", lines, TimeSpan.Zero);

    private sealed class FixedEngine(OcrResult result) : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
