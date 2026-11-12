using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

// The waiting states of a pin translation: the model download's amount, cancelling, why a download failed, the picture
// reading stage and a translation with nothing to show. No user clipboard, model, network or profile: every
// translator and recognizer is a fake and the pins never show.
public sealed class PinTranslationWaitingTests : KoreanCaptionTest
{
    private const string English = "Save the document before closing the window.";
    private const string Korean = "창을 닫기 전에 문서를 저장합니다.";
    private const long ModelBytes = 246_256_561;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("ko-KR", "번역 모델 설치 중 · 91 / 247 MB", "번역 취소")]
    [InlineData("en-US", "Installing translation model · 91 / 247 MB", "Cancel translation")]
    public Task DownloadShowsTheAmountAndADeterminateBarThenTranslates(string language, string line, string cancel) => RunAsync(async () =>
    {
        using var culture = UiText.UseLanguage(language);
        var translator = new WaitingTranslator { Installed = false, Result = language == "ko-KR" ? Korean : English };
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        translator.Download = async (progress, token) =>
        {
            Assert.NotNull(progress);
            progress!.Report(new(0, ModelBytes));
            progress.Report(new(91_000_000, ModelBytes));
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            translator.Installed = true;
        };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), language == "ko-KR" ? English : Korean, false);
            pin.ConfirmTranslationModelDownload = _ => true;
            pin.InvokeTranslationAction();
            await reached.Task.WaitAsync(Limit);
            await Settle();
            pin.FlushTranslationDownloadForTest();
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(PinTranslationState.Downloading, pin.TranslationState);
            (string? shown, double? share, bool bar, bool moving) = pin.TranslationProgressForTest;
            Assert.Equal(line, shown);
            Assert.Equal(line, pin.FeedbackForTest.Text);
            Assert.Equal(91_000_000d / ModelBytes, share!.Value, 6);
            Assert.True(bar);
            Assert.False(moving);
            Assert.Equal((cancel, cancel, true), pin.TranslationActionForTest);

            release.SetResult();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.Null(pin.TranslationProgressForTest.Line);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public void DownloadAmountsReachThePinAtMostTenTimesASecond() => StaTestHost.Run(() =>
    {
        var image = BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 180 * 4], 480 * 4);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromText(image, English, false), new MyCapture.Core.Pin.PinViewState(480, 180, 1, 1, 0.1),
            -10000, -10000, () => new PinSettings()) { ShowActivated = false };
        try
        {
            pin.SetTranslationState(PinTranslationState.Downloading, ModelBytes);
            pin.ShowTranslationDownloadProgress(0, ModelBytes);
            Assert.Equal("번역 모델 설치 중 · 0 / 247 MB", pin.TranslationProgressForTest.Line);
            // Within the interval a newer amount waits for its turn and replaces an older one.
            pin.ShowTranslationDownloadProgress(10_000_000, ModelBytes);
            pin.ShowTranslationDownloadProgress(20_500_000, ModelBytes);
            Assert.Equal("번역 모델 설치 중 · 0 / 247 MB", pin.TranslationProgressForTest.Line);
            pin.FlushTranslationDownloadForTest();
            Assert.Equal("번역 모델 설치 중 · 20 / 247 MB", pin.TranslationProgressForTest.Line);
            // An amount is a download's: a translation shows none of it.
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.ShowTranslationDownloadProgress(30_000_000, ModelBytes);
            Assert.Equal("번역 중", pin.TranslationProgressForTest.Line);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(nameof(PinTranslationState.Checking))]
    [InlineData(nameof(PinTranslationState.Downloading))]
    [InlineData(nameof(PinTranslationState.Translating))]
    public Task CancelReturnsToWhatThePinOfferedAndLeavesNothingRunning(string state) => RunAsync(async () =>
    {
        PinTranslationState running = Enum.Parse<PinTranslationState>(state);
        var translator = new WaitingTranslator { Installed = running != PinTranslationState.Downloading };
        var blocked = new BlockedCall();
        if (running == PinTranslationState.Checking) translator.InstallCheck = blocked.Run;
        else if (running == PinTranslationState.Downloading) translator.Download = (_, token) => blocked.Run(token);
        else translator.Translate = async (_, _, token) =>
        {
            await blocked.Run(token);
            return [];
        };
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.ConfirmTranslationModelDownload = _ => true;
            pin.InvokeTranslationAction();
            await blocked.Started.Task.WaitAsync(Limit);
            await Settle();
            Assert.Equal(running, pin.TranslationState);
            Assert.Equal(("번역 취소", "번역 취소", true), pin.TranslationActionForTest);

            pin.InvokeTranslationAction();
            PinTranslationState expected = running == PinTranslationState.Downloading
                ? PinTranslationState.ModelNeeded : PinTranslationState.Available;
            Assert.Equal(expected, pin.TranslationState);
            Assert.Equal("번역을 취소했습니다.", pin.FeedbackForTest.Text);
            Assert.Null(pin.TranslationProgressForTest.Line);
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.True(blocked.Token.IsCancellationRequested);
            Assert.True(blocked.Call!.IsCompleted);
            await Settle();
            Assert.Equal(expected, pin.TranslationState);
            Assert.Equal("번역을 취소했습니다.", pin.FeedbackForTest.Text);
            Assert.Equal(English, pin.DisplayedText);

            // The command offers the translation again, and it runs to the end.
            translator.InstallCheck = null;
            translator.Download = null;
            translator.Translate = null;
            pin.InvokeTranslationAction();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.Equal(Korean, pin.DisplayedText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task CancellingAPictureReadingStopsWaitingAtOnceAndTheReadingStopsBeforeTranslating() => RunAsync(async () =>
    {
        var translator = new WaitingTranslator();
        PinManager manager = Manager(translator);
        // A recognizer call that, like a single OCR call, does not observe the token.
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfigureImageTranslation((_, token) =>
        {
            started.TrySetResult(token);
            return reading.Task;
        });
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            CancellationToken token = await started.Task.WaitAsync(Limit);
            await Settle();
            Assert.Equal("글자 읽는 중", pin.TranslationProgressForTest.Line);
            Assert.Equal(("번역 취소", "번역 취소", true), pin.TranslationActionForTest);

            pin.InvokeTranslationAction();
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
            Assert.Equal("번역을 취소했습니다.", pin.FeedbackForTest.Text);
            Assert.True(token.IsCancellationRequested);
            // The pin's work ends although the recognizer has not returned.
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.False(reading.Task.IsCompleted);

            reading.SetResult(English);
            await Task.Delay(200);
            await Settle();
            // The reading stops between its stages: no installation check, warm-up or translation follows.
            Assert.Equal(0, translator.InstallChecks);
            Assert.Equal(0, translator.WarmUps);
            Assert.Equal(0, translator.Translations);
            Assert.Equal(PinTranslationState.Available, pin.TranslationState);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ClosingAPinDuringItsReadingCancelsTheRecognizerAndEndsTheWork() => RunAsync(async () =>
    {
        var translator = new WaitingTranslator();
        PinManager manager = Manager(translator);
        var blocked = new BlockedCall();
        manager.ConfigureImageTranslation(async (_, token) =>
        {
            await blocked.Run(token);
            return English;
        });
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await blocked.Started.Task.WaitAsync(Limit);
            pin.Close();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.True(blocked.Token.IsCancellationRequested);
            Assert.True(blocked.Call!.IsCompleted);
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task PicturePinSaysItReadsTheTextBeforeTheCountAppears() => RunAsync(async () =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var translator = new WaitingTranslator();
        var counted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        translator.Translate = async (sources, progress, token) =>
        {
            progress?.Report(new(0, sources.Count));
            counted.TrySetResult();
            await release.Task.WaitAsync(token);
            return [.. sources.Select(source => new TranslatedText(source, Korean, [], []))];
        };
        PinManager manager = Manager(translator);
        var reading = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ConfigureImageTranslation((_, _) => reading.Task);
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await Settle();
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(PinTranslationState.Translating, pin.TranslationState);
            Assert.Equal("글자 읽는 중", pin.TranslationProgressForTest.Line);
            Assert.Equal("글자 읽는 중", pin.FeedbackForTest.Text);

            reading.SetResult("Save the document before closing.\nOpen the settings window.");
            await counted.Task.WaitAsync(Limit);
            await Settle();
            pin.FlushTranslationProgressForTest();
            Assert.StartsWith("번역 중 · 0/", pin.TranslationProgressForTest.Line);
            Assert.Equal(pin.TranslationProgressForTest.Line, pin.FeedbackForTest.Text);

            release.SetResult();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
        }
        finally
        {
            manager.CloseAll();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    public static TheoryData<string, string> Failures => new()
    {
        { "network", "번역 모델을 내려받지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도하십시오." },
        { "stalled", "번역 모델 내려받기가 멈췄습니다. 네트워크 상태를 확인한 뒤 다시 시도하십시오." },
        { "disk-full", "디스크 공간이 부족합니다. 247 MB 이상 확보한 뒤 다시 시도하십시오." },
        { "disk", "번역 모델을 저장할 수 없습니다. 저장 공간과 권한을 확인하십시오." },
        { "corrupt", "내려받은 파일이 손상되었습니다. 다시 시도하십시오." },
        { "verification", "내려받은 파일이 손상되었습니다. 다시 시도하십시오." },
        { "inference", "번역하지 못했습니다. 원문이 유지됩니다." },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public Task EachFailureSaysWhatHappenedAndTheCommandRetries(string failure, string message) => RunAsync(async () =>
    {
        var translator = new WaitingTranslator { Installed = failure == "inference" };
        translator.Download = failure switch
        {
            "network" => (_, _) => throw new TranslationModelException(TranslationModelError.Network, "Synthetic."),
            "stalled" => (_, _) => throw new TranslationModelException(TranslationModelError.Stalled, "Synthetic."),
            "disk-full" => (_, _) => throw new TranslationModelException(TranslationModelError.DiskFull, "Synthetic."),
            "disk" => (_, _) => throw new TranslationModelException(TranslationModelError.Disk, "Synthetic."),
            "corrupt" => (_, _) => throw new TranslationModelException(TranslationModelError.Corrupt, "Synthetic."),
            // The download ends, yet the model is still not complete.
            _ => (_, _) => Task.CompletedTask,
        };
        if (failure == "inference") translator.Translate = (_, _, _) => throw new InvalidOperationException("Synthetic inference failure.");
        PinManager manager = Manager(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), English, false);
            pin.ConfirmTranslationModelDownload = _ => true;
            pin.InvokeTranslationAction();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.Equal((message, FeedbackKind.ActionRequired), (pin.FeedbackForTest.Text, pin.FeedbackForTest.Kind));
            Assert.Equal(("번역", "번역 재시도", true), pin.TranslationActionForTest);
            Assert.Equal(English, pin.DisplayedText);
        }
        finally { manager.CloseAll(); }
    });

    [Theory]
    [InlineData("network", "Can't download the translation model. Check your internet connection and try again.")]
    [InlineData("stalled", "The translation model download stopped. Check your network and try again.")]
    public void FailureMessagesReadInEnglish(string failure, string message)
    {
        using var culture = UiText.UseLanguage("en-US");
        var error = new TranslationModelException(failure == "network" ? TranslationModelError.Network : TranslationModelError.Stalled, "Synthetic.");
        Assert.Equal(message, PinManager.TranslationFailureMessage(error, ModelBytes));
    }

    [Fact]
    public Task PictureWithOnlyCodeSaysThereIsNothingToTranslateNotThatItIsAlreadyKorean() => RunAsync(async () =>
    {
        // Code without comments has no human text in either language.
        const string code = "if (document.IsSaved) {\n    window.Close();\n}\nreturn result;";
        Assert.Equal(PinTranslationAvailability.NothingToTranslate, PinTranslationService.Evaluate(code, targetIsKorean: true).Availability);
        var translator = new WaitingTranslator();
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult<string?>(code));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork.WaitAsync(Limit);
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            Assert.Equal("번역할 내용이 없습니다.", pin.TranslationDisabledHelp);
            Assert.Equal(("번역할 내용이 없습니다.", FeedbackKind.Result), (pin.FeedbackForTest.Text, pin.FeedbackForTest.Kind));
            Assert.Equal(0, translator.Translations);
        }
        finally { manager.CloseAll(); }
    });

    [Theory]
    [InlineData(true, "번역할 내용이 없습니다.")]
    [InlineData(false, "이미 한국어입니다.")]
    public Task AnEmptyResultTurnsTheCommandOffWithANoteInsteadOfEndingSilently(bool picture, string note) => RunAsync(async () =>
    {
        // The service returns nothing when every block of a picture is code or protected, or (text) the plan is empty.
        PinManager manager = Manager(new WaitingTranslator());
        manager.ConfigureImageTranslation((_, _) => Task.FromResult<string?>(English));
        try
        {
            PinWindow pin = picture ? manager.PinImage(Image()) : manager.PinRenderedText(Image(), English, false);
            PinManager.ReportNothingTranslated(pin, pin.TranslationSourceContent, korean: true);
            await Settle();
            Assert.Equal(PinTranslationState.Disabled, pin.TranslationState);
            Assert.Equal(note, pin.TranslationDisabledHelp);
            Assert.Equal((note, FeedbackKind.Result), (pin.FeedbackForTest.Text, pin.FeedbackForTest.Kind));
        }
        finally { manager.CloseAll(); }
    });

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

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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

    /// <summary>A call that waits until its token is cancelled; the test sees its token and its task.</summary>
    private sealed class BlockedCall
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal Task? Call { get; private set; }

        internal Task Run(CancellationToken token)
        {
            Token = token;
            Call = Task.Delay(Timeout.Infinite, token);
            Started.TrySetResult();
            return Call;
        }
    }

    private sealed class WaitingTranslator : ITextTranslator
    {
        internal bool Installed = true;
        internal string Result = Korean;
        internal int InstallChecks;
        internal int WarmUps;
        internal int Translations;
        internal Func<CancellationToken, Task>? InstallCheck;
        internal Func<IProgress<TranslationDownloadProgress>?, CancellationToken, Task>? Download;
        internal Func<IReadOnlyList<string>, IProgress<TranslationProgress>?, CancellationToken, Task<IReadOnlyList<TranslatedText>>>? Translate;

        public long ModelDownloadBytes => ModelBytes;

        public async Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InstallChecks);
            if (InstallCheck is { } check) await check(cancellationToken);
            return Installed;
        }

        public Task WarmUpAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WarmUps);
            return Task.CompletedTask;
        }

        public async Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (Download is { } download) await download(progress, cancellationToken);
            else Installed = true;
        }

        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Translations);
            if (Translate is { } translate) return await translate(sources, progress, cancellationToken);
            return [.. sources.Select(source => new TranslatedText(source, Result, [], []))];
        }
    }
}
