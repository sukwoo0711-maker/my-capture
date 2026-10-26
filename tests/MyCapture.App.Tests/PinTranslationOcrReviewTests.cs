using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOcrReviewTests : KoreanCaptionTest
{
    private const string English = "Save the document before closing the window.";
    private const string Korean = "창을 닫기 전에 문서를 저장합니다.";

    [Theory]
    [InlineData("Install version 24.1 before continuing.", "ko", "ko", true)]
    [InlineData(English, "KO-KR", "ko-KR", true)]
    [InlineData(English, "en-US", "ko", false)]
    [InlineData(Korean, "en-US", "en", true)]
    [InlineData(Korean, "ko-KR", "en-US", false)]
    [InlineData(Korean, "ko", "ko", false)]
    [InlineData("문서를 저장합니다. Save the document.", "ko", "ko", false)]
    [InlineData("문서를 저장합니다. Save the document.", "en", "en", false)]
    [InlineData("https://example.com/guide 24.1", "ko", "ko", false)]
    [InlineData(English, "", "ko", false)]
    [InlineData(English, "fr", "ko", false)]
    [InlineData(English, "ko", "fr", false)]
    public void LanguageReviewRequiresActualFallbackAndUnambiguousSourceProse(
        string text, string actualLanguage, string target, bool expected)
    {
        OcrResult result = OcrResult.Success(text, actualLanguage, [], TimeSpan.Zero);
        Assert.Equal(expected, PinTranslationOcr.NeedsLanguageReview(result, target));
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void NoTextAndUnavailableRecognitionDoNotInventALanguageMismatch()
    {
        Assert.False(PinTranslationOcr.NeedsLanguageReview(OcrResult.NoText("ko", TimeSpan.Zero), "ko"));
        Assert.False(PinTranslationOcr.NeedsLanguageReview(OcrResult.Unavailable(), "ko"));
    }

    [Theory]
    [InlineData("ko", false, true)]
    [InlineData("en-US", false, false)]
    [InlineData("en-US", true, true)]
    [InlineData("ko", true, true)]
    public Task ImageTranslationCombinesOcrAndModelReviewWithoutChangingSource(
        string actualLanguage, bool modelNeedsReview, bool expectedReview) => RunAsync(async () =>
    {
        var translator = new FakeTranslator(modelNeedsReview);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { }, ActivatePinForTest = static _ => { },
        };
        string target = "ko";
        manager.ConfigureTranslation(_ => translator, () => target,
            (_, _, _) => Task.FromResult(OcrResult.Success(English, actualLanguage, [], TimeSpan.Zero)));
        try
        {
            BitmapSource image = BitmapSource.Create(600, 240, 96, 96, PixelFormats.Bgra32, null,
                new byte[600 * 240 * 4], 600 * 4);
            image.Freeze();
            PinWindow pin = manager.PinImage(image);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.Equal(expectedReview ? FeedbackKind.Warning : FeedbackKind.Confirmation, pin.FeedbackForTest.Kind);
            bool ocrMismatch = actualLanguage == "ko";
            string message = UiText.Get(ocrMismatch ? "Pin_TranslationOcrLanguageReview"
                : modelNeedsReview ? "Pin_TranslationReview" : "Pin_Translated");
            Assert.Equal(message, pin.FeedbackForTest.Text);
            if (ocrMismatch) Assert.Equal(message, TranslationAction(pin).ToolTip);
            Assert.Equal(English, pin.OriginalCopyText);
            Assert.Same(image, pin.TranslationSourceContent.Image);
            pin.InvokeTranslationAction();
            Assert.Same(image, pin.CurrentDisplayContent.Image);
            Assert.Equal(UiText.Get("Pin_Translate"), TranslationAction(pin).ToolTip);
            if (ocrMismatch) Assert.False(pin.IsFeedbackVisible);
            pin.InvokeTranslationAction();
            if (ocrMismatch) Assert.Equal(message, TranslationAction(pin).ToolTip);
            target = "en";
            manager.RefreshTranslationAvailability();
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(UiText.Get("Pin_Translate"), TranslationAction(pin).ToolTip);
            if (ocrMismatch) Assert.False(pin.IsFeedbackVisible);
        }
        finally { manager.CloseAll(); }
    });

    private static MenuItem TranslationAction(PinWindow pin) => Assert.Single(
        pin.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header,
            UiText.Get(pin.IsShowingTranslation ? "Pin_TranslationOriginal" : "Pin_Translate")));

    private sealed class FakeTranslator(bool needsReview) : ITextTranslator
    {
        public long ModelDownloadBytes => 1;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No download is allowed in this test.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslatedText>>(sources.Select(source =>
                new TranslatedText(source, Korean, needsReview ? ["5000"] : [], [])).ToArray());
    }

    private static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
