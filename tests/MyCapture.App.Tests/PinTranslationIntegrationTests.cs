using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Core.Localization;
using MyCapture.Ocr.Translation;
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
    public Task KoreanPinInAKoreanAppIsDisabledWithAReasonAndNeverTranslates() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            PinWindow korean = manager.PinRenderedText(Image(), Korean, false);
            Assert.Equal(PinTranslationState.Disabled, korean.TranslationState);
            Assert.Equal("이미 한국어입니다.", korean.TranslationDisabledHelp);
            korean.InvokeTranslationAction();
            await korean.TranslationWork;
            Assert.Equal(PinTranslationState.Disabled, korean.TranslationState);
            Assert.Equal(0, translator.InstallChecks);
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task JapanesePinIsDisabledWithTheUnsupportedLanguageReason() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), "今日は天気がいいので、公園を散歩します。", false);
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            Assert.Equal(UiText.Get("Pin_TranslationUnsupportedLanguage"), pin.TranslationDisabledHelp);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task TablePinTranslatesCellTextAndKeepsEveryTab() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            const string source = "Name\tValue\t12\r\nFirst\tSecond\t2026-10-27";
            PinWindow table = manager.PinRenderedText(Image(), source, true);
            Assert.Equal(PinTranslationState.Available, table.TranslationState);
            table.InvokeTranslationAction();
            await table.TranslationWork;
            Assert.True(table.IsShowingTranslation);
            string[] rows = table.DisplayedText!.Split("\r\n");
            Assert.Equal([Korean, Korean, "12"], rows[0].Split('\t'));
            Assert.Equal([Korean, Korean, "2026-10-27"], rows[1].Split('\t'));
            Assert.Equal(source, table.OriginalText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task EnglishAppTranslatesAKoreanPinToEnglishAndDisablesAnEnglishPin() => RunAsync(async () =>
    {
        using var language = UiText.UseLanguage("en-US");
        var translator = new FakeTranslator { Result = English };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Korean, false);
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(English, pin.DisplayedText);
            Assert.Equal(Korean, pin.OriginalText);
            Assert.Equal([Korean], Assert.Single(translator.Sources));
            pin.InvokeTranslationAction();
            Assert.Equal(Korean, pin.DisplayedText);
            PinWindow english = manager.PinRenderedText(Image(), English, false);
            Assert.Equal(PinTranslationState.Disabled, english.TranslationState);
            Assert.Equal("Already in English.", english.TranslationDisabledHelp);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ADirectionWithoutAModelIsDisabledUntilItIsPrepared() => RunAsync(async () =>
    {
        using var language = UiText.UseLanguage("en-US");
        var translator = new FakeTranslator();
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        manager.ConfigureTranslation(direction => direction == PinTranslationDirection.EnglishToKorean ? translator : null);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Korean, false);
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            Assert.Equal("Translation model is being prepared", pin.TranslationDisabledHelp);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(0, translator.InstallChecks);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task PicturePinIsReadByTheRecognizerTranslatedAndReturnsToThePicture() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        int reads = 0;
        manager.ConfigureImageTranslation((_, _) =>
        {
            reads++;
            return Task.FromResult<string?>("Save the document before closing.\nOpen the settings window.");
        });
        try
        {
            PinWindow pin = manager.PinImage(Image());
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            var frame = (pin.Width, pin.Height);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(1, reads);
            Assert.True(pin.IsShowingTranslation);
            Assert.NotNull(pin.DisplayedText);
            Assert.Equal(PinContentKind.Image, pin.PinnedContent.Kind);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingTranslation);
            Assert.Null(pin.DisplayedText);
            Assert.Equal(frame, (pin.Width, pin.Height));
            pin.InvokeTranslationAction();
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(1, reads);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task PicturePinThatIsAlreadyInTheTargetLanguageNeverLoadsAModel() => RunAsync(async () =>
    {
        var translator = new FakeTranslator { Installed = false };
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult<string?>(Korean));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            Assert.Equal(0, translator.InstallChecks);
            Assert.False(pin.IsShowingTranslation);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task CopyUsesTheVisibleTextWhileTheSourceTextStaysTheOriginal() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            Assert.Equal(English, pin.VisibleText);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.Equal(Korean, pin.VisibleText);
            Assert.Equal(English, pin.OriginalText);
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

    private sealed class FakeTranslator : ITextTranslator
    {
        internal bool Installed = true;
        internal bool Fail;
        internal string Result = Korean;
        internal List<IReadOnlyList<string>> Sources = [];
        internal int Downloads;
        internal int Translations;
        internal int InstallChecks;
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
            Sources.Add(sources);
            Started.TrySetResult(true);
            if (Delayed is { } wait) await wait.Task;
            if (Fail) throw new InvalidOperationException("Synthetic inference failure.");
            return sources.Select(source => new TranslatedText(source, Result, [], [])).ToArray();
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
