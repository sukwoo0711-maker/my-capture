using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Core.Localization;
using MyCapture.Ocr.Translation;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

// No user clipboard, visible windows, model, network, or filesystem operations.
public sealed class PinTranslationIntegrationTests : KoreanCaptionTest
{
    private const string English = "Save the document before closing the window.";
    private const string Korean = "창을 닫기 전에 문서를 저장합니다.";

    private static PinManager Manager(ITextTranslator translator)
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        manager.ConfigureTranslation(translator);
        return manager;
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(600, 240, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 240 * 4], 600 * 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public Task F3WaitsForManualTranslationAndTogglesWithoutLosingSourceOrMovingFrame() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            PinContent content = PinContent.FromText(Image(), English, false);
            Assert.Equal(PasteResult.Pinned, await manager.PasteFromClipboardAsync(() => Task.FromResult(
                ClipboardImageReader.PinReadAttempt.Success(content))));
            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            var frame = (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity);
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            Assert.Equal(0, translator.InstallChecks);
            Assert.Equal(0, translator.Translations);
            Assert.Equal(0, translator.Downloads);
            Assert.Equal(English, pin.DisplayedText);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.Equal(English, pin.OriginalText);
            Assert.Equal(English, pin.PinnedContent.OriginalText);
            pin.InvokeTranslationAction();
            Assert.Equal(English, pin.DisplayedText);
            pin.InvokeTranslationAction();
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
            Assert.Equal(1, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task MissingModelRequiresInstallationActionThenTranslates() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Installed = false };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            int prompts = 0;
            pin.ConfirmTranslationModelDownload = bytes =>
            {
                prompts++;
                Assert.Equal(translator.ModelDownloadBytes, bytes);
                return false;
            };
            Assert.Equal(0, translator.InstallChecks);
            Assert.Equal(0, prompts);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.ModelNeeded, pin.TranslationState);
            Assert.Equal(0, translator.Downloads);
            Assert.Equal(0, translator.Translations);
            Assert.Equal(1, prompts);
            Assert.Equal(English, pin.DisplayedText);
            pin.ConfirmTranslationModelDownload = _ => { prompts++; return true; };
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, translator.Downloads);
            Assert.Equal(2, prompts);
            Assert.Equal(Korean, pin.DisplayedText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task FailedTranslationPreservesOriginalAndRetryWorks() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Fail = true };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal(English, pin.DisplayedText);
            translator.Fail = false;
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(Korean, pin.DisplayedText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ClosedPinIgnoresLateResultEvenIfTranslatorIgnoresCancellation() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        PinManager manager = Manager(translator);
        PinWindow pin = manager.PinRenderedText(Image(), English, false);
        pin.InvokeTranslationAction();
        await translator.Started.Task;
        pin.Close();
        translator.Delayed.SetResult(true);
        await pin.TranslationWork;
        Assert.Equal(English, pin.DisplayedText);
        Assert.False(pin.IsShowingTranslation);
        Assert.Equal(0, manager.Count);
        manager.CloseAll();
    });

    [Fact]
    public Task SupersededTranslationCannotReplaceNewerResult() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Delayed = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.InvokeTranslationAction();
            await translator.Started.Task;
            Task first = pin.TranslationWork;
            TaskCompletionSource<bool> pending = translator.Delayed;
            translator.Delayed = null;
            manager.StartPinTranslation(pin, false);
            await pin.TranslationWork;
            pending.SetResult(true);
            await first;
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ClosingDuringModelConsentDoesNotStartDownload() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Installed = false };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.ConfirmTranslationModelDownload = _ => { pin.Close(); return true; };
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(0, translator.Downloads);
            Assert.Equal(0, translator.Translations);
            Assert.Equal(0, manager.Count);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task TargetLanguageIsDisabledAndEnglishTableOffersManualTranslation() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            PinWindow korean = manager.PinRenderedText(Image(), Korean, false);
            PinWindow table = manager.PinRenderedText(Image(), "Name\tValue\r\nFirst\tSecond", true);
            Assert.Equal(PinTranslationState.Disabled, korean.TranslationState);
            Assert.Equal(PinTranslationState.Available, table.TranslationState);
            korean.InvokeTranslationAction();
            await korean.TranslationWork;
            Assert.Equal(0, translator.InstallChecks);
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public void TranslationWithoutAConfiguredEngineIsHidden() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { }, ActivatePinForTest = static _ => { },
        };
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            Assert.Equal(PinTranslationState.Unavailable, pin.TranslationState);
            Assert.DoesNotContain(pin.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>(),
                item => Equals(item.Header, UiText.Get("Pin_Translate")));
        }
        finally { manager.CloseAll(); }
    });

    [Theory]
    [InlineData("ko-KR", "글은 PC 밖으로 나가지 않습니다.", "설치하시겠습니까?")]
    [InlineData("en-US", "Your text stays on this PC.", "Install the model?")]
    public void ModelConsentIncludesSizePrivacyAndExplicitQuestion(string language, string privacy, string question)
    {
        using var scope = UiText.UseLanguage(language);
        string text = PinWindow.TranslationModelConsentText(246_256_561);
        Assert.Contains("247 MB", text);
        Assert.Contains(privacy, text);
        Assert.Contains(question, text);
    }

    [Fact]
    public Task ImageRecognitionWaitsForActionAndTranslationTogglesBackToExactImage() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        int recognitionCalls = 0;
        manager.ConfigureTranslation(_ => translator, () => "ko", (_, target, _) =>
        {
            recognitionCalls++;
            Assert.Equal("ko", target);
            return Task.FromResult(OcrResult.Success(English, "en-US", [], TimeSpan.Zero));
        });
        try
        {
            var original = Image();
            PinWindow pin = manager.PinImage(original);
            Assert.Equal(0, recognitionCalls);
            Assert.Same(original, pin.CurrentDisplayContent.Image);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, recognitionCalls);
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.Equal(English, pin.OriginalCopyText);
            Assert.Same(original, pin.TranslationSourceContent.Image);
            string? copied = null;
            pin.OriginalTextCopyRequested += (_, text) => copied = text;
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal(English, copied);
            pin.CopyTranslationText();
            Assert.Equal(Korean, copied);
            pin.InvokeTranslationAction();
            Assert.Same(original, pin.CurrentDisplayContent.Image);
            pin.InvokeTranslationAction();
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.Equal(1, recognitionCalls);
            Assert.Equal(1, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task TargetLanguageChangeClearsCachedTranslationAndSelectsOppositeDirectionOnlyOnAction() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Output = English };
        PinManager manager = Manager(translator);
        string target = "en";
        var factoryTargets = new List<string>();
        manager.ConfigureTranslation(language => { factoryTargets.Add(language); return translator; }, () => target);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Korean, false);
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            Assert.Empty(factoryTargets);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(English, pin.DisplayedText);
            Assert.Equal(["en"], factoryTargets);
            target = "ko";
            manager.RefreshTranslationAvailability();
            Assert.Equal(Korean, pin.DisplayedText);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            pin.InvokeTranslationAction();
            Assert.Single(factoryTargets);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task UnavailableOcrAndUnsupportedLanguageNeverLoadTranslationModel() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        int factoryCalls = 0;
        manager.ConfigureTranslation(_ => { factoryCalls++; return translator; }, () => "ko",
            (_, _, _) => Task.FromResult(OcrResult.Unavailable()));
        try
        {
            PinWindow japanese = manager.PinRenderedText(Image(), "これは日本語の文書です。", false);
            Assert.Equal(PinTranslationState.Disabled, japanese.TranslationState);
            japanese.InvokeTranslationAction();
            PinWindow image = manager.PinImage(Image());
            image.InvokeTranslationAction();
            await image.TranslationWork;
            Assert.Equal(PinTranslationState.Disabled, image.TranslationState);
            Assert.Equal(0, factoryCalls);
            Assert.Equal(0, translator.InstallChecks);
        }
        finally { manager.CloseAll(); }
    });

    private sealed class FakeTranslator : ITextTranslator
    {
        internal bool Installed = true;
        internal bool Fail;
        internal int Downloads;
        internal int Translations;
        internal int InstallChecks;
        internal string Output = Korean;
        internal TaskCompletionSource<bool>? Delayed;
        internal TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long ModelDownloadBytes => 246_256_561;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken)
        {
            InstallChecks++;
            return Task.FromResult(Installed);
        }
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            Installed = true;
            return Task.CompletedTask;
        }
        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Translations++;
            Started.TrySetResult(true);
            if (Delayed is { } wait) await wait.Task;
            if (Fail) throw new InvalidOperationException("Synthetic inference failure.");
            return sources.Select(source => new TranslatedText(source, Output, [], [])).ToArray();
        }
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
