using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinRecognitionSharingTests
{
    [Fact]
    public async Task CancellationReachesSynchronousEnginePreparation()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr(token =>
        {
            started.SetResult(true);
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Cancellation did not reach synchronous OCR preparation.");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(PinImageTextSelectionTests.Layout());
        });
        BitmapSource image = Image();
        using var cancellation = new CancellationTokenSource();
        Task<OcrResult> recognition = Task.Run(() => PinTextCopyService.RecognizeAsync(image, ocr,
            new OcrSettings(), cancellation.Token));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recognition.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ocr.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellingSelectionDoesNotCancelConcurrentCopy()
    {
        BitmapSource image = Image();
        var completion = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new FakeOcr(_ => completion.Task);
        using var cancellation = new CancellationTokenSource();
        Task<OcrResult> selection = PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings(), cancellation.Token);
        string? copied = null;
        Task<bool> copy = PinTextCopyService.CopyAsync(image, ocr,
            text => { copied = text; return Task.FromResult(true); }, new OcrSettings());
        Assert.Equal(1, ocr.Count);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.False(ocr.Token.IsCancellationRequested);
        completion.SetResult(PinImageTextSelectionTests.Layout());
        Assert.True(await copy.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PinImageTextSelectionTests.Layout().Text, copied);
        OcrResult cached = await PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings());
        Assert.Equal(3, cached.Lines.Count);
        Assert.Equal(1, ocr.Count);
    }

    [Fact]
    public async Task AbandonedRecognitionCannotReplaceNewRequestEvenWhenEngineIgnoresCancellation()
    {
        BitmapSource image = Image();
        var old = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var ocr = new FakeOcr(_ => ++calls == 1 ? old.Task : Task.FromResult(PinImageTextSelectionTests.Layout()));
        using var cancellation = new CancellationTokenSource();
        Task<OcrResult> first = PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        OcrResult fresh = await PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings());
        old.SetResult(OcrResult.Success("obsolete", "en", [], TimeSpan.Zero));
        Assert.Same(fresh, await PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings()));
        Assert.Equal(2, ocr.Count);
    }

    [Fact]
    public async Task TranslationRecoveryIsAnEffectiveOptionAndNeverPoisonsPlainCopy()
    {
        BitmapSource image = Image();
        var ocr = new FakeOcr(_ => Task.FromResult(PinImageTextSelectionTests.Layout()));
        var settings = new OcrSettings { Quality = OcrQuality.Fast, UpscaleFactor = 1 };
        OcrResult plain = await PinTextCopyService.RecognizeAsync(image, ocr, settings);
        OcrResult recovered = await PinTranslationEngines.RecognizeResultAsync(ocr, image, settings, CancellationToken.None);
        Assert.Equal(2, ocr.Count);
        Assert.Same(recovered, await PinTranslationEngines.RecognizeResultAsync(ocr, image, settings, CancellationToken.None));
        Assert.Equal(2, ocr.Count);
        Assert.Equal(plain.Text, recovered.Text);
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return image;
    }

    private sealed class FakeOcr(Func<CancellationToken, Task<OcrResult>> recognize) : IOcrService
    {
        internal int Count;
        internal CancellationToken Token;
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko", "en"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Count++;
            Token = cancellationToken;
            return recognize(cancellationToken);
        }
    }
}
