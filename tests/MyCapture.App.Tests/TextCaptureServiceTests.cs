using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TextCaptureServiceTests
{
    private const string SensitiveText = "계좌 110-123-456789\r\nSecret token";

    [Fact]
    public async Task RecognizedTextIsCopiedWithCharacterCountAndNeverLogged()
    {
        var ocr = new FakeOcr(OcrResult.Success(SensitiveText, "ko-KR", [], TimeSpan.FromMilliseconds(12)));
        var log = new RecordingLogger();
        string? copied = null;
        var service = new TextCaptureService(ocr, () => new OcrSettings(), text => { copied = text; return Task.FromResult(true); }, log);

        TextCaptureOutcome outcome = await service.RecognizeAndCopyAsync(Image(), CancellationToken.None);

        Assert.Equal(TextCaptureStatus.Copied, outcome.Status);
        Assert.Equal(SensitiveText, copied);
        Assert.Equal(TextCaptureService.CountCharacters(SensitiveText), outcome.CharacterCount);
        Assert.NotNull(outcome.Result);
        Assert.Equal(ocr.Result.Status, outcome.Result.Status);
        Assert.Equal(ocr.Result.Text, outcome.Result.Text);
        Assert.Equal(ocr.Result.LanguageTag, outcome.Result.LanguageTag);
        Assert.Same(ocr.Result.Lines, outcome.Result.Lines);
        Assert.Equal(ocr.Result.Elapsed, outcome.Result.Elapsed);
        Assert.Equal(ocr.Result.Message, outcome.Result.Message);
        Assert.Equal(OcrQuality.Fast, outcome.Result.Quality);
        Assert.NotEmpty(log.Messages);
        Assert.All(log.Messages, message =>
        {
            Assert.DoesNotContain("110-123", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret", message, StringComparison.Ordinal);
            Assert.DoesNotContain("계좌", message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(OcrQuality.Fast)]
    [InlineData(OcrQuality.Accurate)]
    [InlineData(OcrQuality.Enhanced)]
    public async Task RecognitionHonoursTheCurrentOcrQualityStage(OcrQuality quality)
    {
        var settings = new OcrSettings { Quality = quality, PreferredLanguages = ["ko-KR"] };
        OcrQualityProfile.Synchronize(settings);
        var ocr = new FakeOcr(OcrResult.NoText("ko-KR", TimeSpan.Zero));
        var service = new TextCaptureService(ocr, () => settings, _ => Task.FromResult(true), new RecordingLogger());
        BitmapSource image = Image();

        await service.RecognizeAndCopyAsync(image, CancellationToken.None);

        OcrRequest request = Assert.IsType<OcrRequest>(ocr.Request);
        Assert.Same(image, request.Bitmap);
        Assert.Equal(OcrQualityProfile.UseNeuralModel(quality), request.UseNeuralModel);
        Assert.Equal(OcrQualityProfile.UpscaleFactor(quality), request.UpscaleFactor);
        Assert.Equal(OcrQualityProfile.EnhanceContrast(quality), request.EnhanceContrast);
        Assert.Equal(new[] { "ko-KR" }, request.PreferredLanguages);
    }

    [Theory]
    [InlineData("noText", "NoText")]
    [InlineData("whitespace", "NoText")]
    [InlineData("unavailable", "Unavailable")]
    [InlineData("failed", "Failed")]
    [InlineData("cancelled", "Cancelled")]
    public async Task EmptyFailedAndCancelledResultsNeverTouchTheClipboard(string kind, string expectedStatus)
    {
        TextCaptureStatus expected = Enum.Parse<TextCaptureStatus>(expectedStatus);
        OcrResult result = kind switch
        {
            "noText" => OcrResult.NoText("ko-KR", TimeSpan.Zero),
            "whitespace" => OcrResult.Success(" \r\n\t ", "ko-KR", [], TimeSpan.Zero),
            "unavailable" => OcrResult.Unavailable("no engine"),
            "failed" => OcrResult.Failed("decode"),
            _ => OcrResult.Cancelled(),
        };
        int copies = 0;
        var service = new TextCaptureService(new FakeOcr(result), () => new OcrSettings(),
            _ => { copies++; return Task.FromResult(true); }, new RecordingLogger());

        TextCaptureOutcome outcome = await service.RecognizeAndCopyAsync(Image(), CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(expected, TextCaptureService.Classify(result));
        Assert.Equal(0, copies);
        Assert.Equal(0, outcome.CharacterCount);
    }

    [Fact]
    public async Task CancellationBeforeOrDuringRecognitionLeavesTheClipboardAlone()
    {
        int copies = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ocr = new FakeOcr(OcrResult.Success("text", "en", [], TimeSpan.Zero));
        var service = new TextCaptureService(ocr, () => new OcrSettings(), _ => { copies++; return Task.FromResult(true); },
            new RecordingLogger());
        Assert.Equal(TextCaptureStatus.Cancelled, (await service.RecognizeAndCopyAsync(Image(), cancelled.Token)).Status);
        Assert.Null(ocr.Request);

        using var during = new CancellationTokenSource();
        var cancelling = new FakeOcr(OcrResult.Success("text", "en", [], TimeSpan.Zero)) { BeforeReturn = during.Cancel };
        var second = new TextCaptureService(cancelling, () => new OcrSettings(), _ => { copies++; return Task.FromResult(true); },
            new RecordingLogger());
        Assert.Equal(TextCaptureStatus.Cancelled, (await second.RecognizeAndCopyAsync(Image(), during.Token)).Status);

        var throwing = new FakeOcr(OcrResult.Success("text", "en", [], TimeSpan.Zero)) { Throw = new OperationCanceledException() };
        var third = new TextCaptureService(throwing, () => new OcrSettings(), _ => { copies++; return Task.FromResult(true); },
            new RecordingLogger());
        Assert.Equal(TextCaptureStatus.Cancelled, (await third.RecognizeAndCopyAsync(Image(), CancellationToken.None)).Status);
        Assert.Equal(0, copies);
    }

    [Fact]
    public async Task ClipboardRejectionOrEngineFaultIsReportedWithoutThrowing()
    {
        var rejected = new TextCaptureService(new FakeOcr(OcrResult.Success("abc", "en", [], TimeSpan.Zero)), () => new OcrSettings(),
            _ => Task.FromResult(false), new RecordingLogger());
        TextCaptureOutcome busy = await rejected.RecognizeAndCopyAsync(Image(), CancellationToken.None);
        Assert.Equal(TextCaptureStatus.ClipboardFailed, busy.Status);
        Assert.True(busy.Result!.HasText);

        var log = new RecordingLogger();
        var throwingClipboard = new TextCaptureService(new FakeOcr(OcrResult.Success(SensitiveText, "en", [], TimeSpan.Zero)),
            () => new OcrSettings(), _ => throw new InvalidOperationException(SensitiveText), log);
        Assert.Equal(TextCaptureStatus.ClipboardFailed,
            (await throwingClipboard.RecognizeAndCopyAsync(Image(), CancellationToken.None)).Status);
        Assert.All(log.Messages, message => Assert.DoesNotContain("Secret", message, StringComparison.Ordinal));
        Assert.All(log.Exceptions, exception => Assert.Null(exception));

        var fault = new FakeOcr(OcrResult.Success("abc", "en", [], TimeSpan.Zero)) { Throw = new InvalidOperationException("engine") };
        var faulted = new TextCaptureService(fault, () => new OcrSettings(), _ => Task.FromResult(true), new RecordingLogger());
        TextCaptureOutcome failure = await faulted.RecognizeAndCopyAsync(Image(), CancellationToken.None);
        Assert.Equal(TextCaptureStatus.Failed, failure.Status);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("a b\r\nc", 3)]
    [InlineData("한글 텍스트", 5)]
    [InlineData("é", 1)]
    [InlineData("😀x", 2)]
    public void CharacterCountIgnoresWhitespaceAndCountsGraphemes(string text, int expected) =>
        Assert.Equal(expected, TextCaptureService.CountCharacters(text));

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        image.Freeze();
        return image;
    }

    private sealed class FakeOcr(OcrResult result) : IOcrService
    {
        internal OcrResult Result { get; } = result;
        internal OcrRequest? Request { get; private set; }
        internal Action? BeforeReturn { get; init; }
        internal Exception? Throw { get; init; }
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko-KR", "en"];

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Throw is not null) throw Throw;
            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        internal List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
