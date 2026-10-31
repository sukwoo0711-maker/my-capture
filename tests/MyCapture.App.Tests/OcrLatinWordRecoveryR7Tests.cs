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
    public async Task TranslationFastRequestUsesSecondReadingOnlyForSuspectTokens()
    {
        Assert.Equal(OcrQuality.Fast, new OcrSettings().Quality);
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        var windows = new FixedEngine(before);
        int calls = 0;
        var service = Service(windows, (request, _) =>
        {
            calls++;
            Assert.False(request.UseNeuralModel);
            return Task.FromResult(Result(Replace(before.Lines[0], "하", "of")));
        });
        OcrResult after = await service.RecognizeAsync(OcrRequest.FromBytes([1], searchRotatedOrientations: false).WithLatinRecovery());
        Assert.Equal("only one of the following seven boxes", after.Text);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OrdinaryFastTextOrMissingModelDoesNotInvokeNeural(bool suspect, bool installed)
    {
        OcrResult before = Result(Line(suspect ? "only one 하 the following seven boxes" : "only one of the following seven boxes"));
        int calls = 0;
        var service = Service(new(before), (_, _) => { calls++; throw new InvalidOperationException(); }, installed);
        Assert.Same(before, await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PlainRecognitionKeepsWindowsResultEvenWithSuspectTokens()
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        int calls = 0;
        var service = Service(new(before), (_, _) => { calls++; throw new InvalidOperationException(); });
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
        var service = Service(new(before), (_, _) => throws
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
        var service = Service(new(before), (_, _) => { cancel.Cancel(); return Task.FromResult(before); });
        Assert.Equal(OcrStatus.Cancelled, (await service.RecognizeAsync(OcrRequest.FromBytes([1]).WithLatinRecovery(), cancel.Token)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedLineGetsOnlyOneBoundedStripRetry(bool succeeds) => StaTestHost.Run(() =>
    {
        OcrResult before = Result(Line("only one 하 the following seven boxes"));
        BitmapSource bitmap = BitmapSource.Create(800, 80, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 80 * 4], 800 * 4);
        bitmap.Freeze();
        OcrRequest original = OcrRequest.FromBitmap(bitmap, searchRotatedOrientations: false);
        OcrRect region = Assert.Single(OcrLatinWordRecovery.RetryRegions(before));
        OcrRequest crop = Assert.IsType<OcrRequest>(CaptureOcrService.CreateLatinRetryRequest(original, region, out int x, out int y));
        Assert.NotNull(crop.Bitmap);
        Assert.True(crop.Bitmap.IsFrozen);
        Assert.True(crop.Bitmap.PixelWidth < bitmap.PixelWidth * 2);
        Assert.True(crop.Bitmap.PixelHeight < bitmap.PixelHeight);
        int calls = 0;
        var service = Service(new(before), (request, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(before);
            Assert.Equal(2, calls);
            Assert.True(request.UseNeuralModel);
            if (!succeeds) return Task.FromResult(OcrResult.NoText("ko", TimeSpan.Zero));
            OcrWord[] words = Replace(before.Lines[0], "하", "of").Words
                .Where(word => word.Bounds.X >= region.X && word.Bounds.Right <= region.Right)
                .Select(word => word with { Bounds = new((word.Bounds.X - x) * 2, (word.Bounds.Y - y) * 2,
                    word.Bounds.Width * 2, word.Bounds.Height * 2) }).ToArray();
            return Task.FromResult(Result(new OcrLine(string.Join(' ', words.Select(word => word.Text)),
                words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), words)));
        });
        OcrResult after = service.RecognizeAsync(original.WithLatinRecovery()).GetAwaiter().GetResult();
        Assert.Equal(2, calls);
        Assert.Equal(succeeds ? "only one of the following seven boxes" : before.Text, after.Text);
        Assert.Equal(before.Lines[0].Bounds, after.Lines[0].Bounds);
        Assert.True(after.Elapsed >= before.Elapsed);
    });

    private static CaptureOcrService Service(FixedEngine windows,
        Func<OcrRequest, CancellationToken, Task<OcrResult>> neural, bool installed = true) =>
        new(windows, neural, () => installed, () => false, NullLogger<CaptureOcrService>.Instance);

    private static OcrLine Replace(OcrLine line, string before, string after) => line with
    {
        Text = line.Text.Replace(before, after, StringComparison.Ordinal),
        Words = line.Words.Select(word => word.Text == before ? word with { Text = after } : word).ToArray(),
    };

    private static OcrLine Line(string text)
    {
        double x = 10;
        OcrWord[] words = text.Split(' ').Select(token =>
        {
            var word = new OcrWord(token, new(x, 10, Math.Max(15, token.Length * 8), 20));
            x = word.Bounds.Right + 6;
            return word;
        }).ToArray();
        return new(text, new(10, 10, x - 16, 20), words);
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
