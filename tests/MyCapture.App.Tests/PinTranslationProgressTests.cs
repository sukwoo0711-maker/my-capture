using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

// No user clipboard, model, network or profile: pins open off screen and every translator is a fake.
public sealed class PinTranslationProgressTests : KoreanCaptionTest
{
    private const string ThreeSentences =
        "Save the document before closing the window. Open the report after the meeting. Send the summary to the team.";

    [Fact]
    public void TimeLeftWaitsForTwoSentencesThenSmoothsRoundsAndLeavesAtTheEnd()
    {
        var estimate = new PinTranslationTimeEstimate();
        estimate.Observe(0, 10, Seconds(0));
        Assert.Null(estimate.Remaining);
        // One sentence gives no pace yet: its time includes the model's start.
        estimate.Observe(1, 10, Seconds(1));
        Assert.Null(estimate.Remaining);
        Assert.Equal("번역 중 · 1/10", estimate.Describe("번역 중 · 1/10"));

        estimate.Observe(2, 10, Seconds(2));
        Assert.Equal(8, estimate.Remaining!.Value.TotalSeconds, 6);
        Assert.Equal("번역 중 · 2/10 · 약 8초 남음", estimate.Describe("번역 중 · 2/10"));
        // A slower sentence moves the estimate only part of the way, and one second more is not shown.
        estimate.Observe(3, 10, Seconds(6.5));
        Assert.Equal(9.0125, estimate.Remaining!.Value.TotalSeconds, 6);
        Assert.Equal("번역 중 · 3/10 · 약 8초 남음", estimate.Describe("번역 중 · 3/10"));
        estimate.Observe(4, 10, Seconds(8));
        Assert.Equal(9.783125, estimate.Remaining!.Value.TotalSeconds, 6);
        Assert.Equal("번역 중 · 4/10 · 약 10초 남음", estimate.Describe("번역 중 · 4/10"));

        estimate.Observe(10, 10, Seconds(9));
        Assert.Null(estimate.Remaining);
        Assert.Equal("번역 중 · 10/10", estimate.Describe("번역 중 · 10/10"));
    }

    [Theory]
    [InlineData(3, "번역 중")]
    [InlineData(9, "번역 중 · 약 7초 남음")]
    [InlineData(25, "번역 중 · 약 25초 남음")]
    [InlineData(30, "번역 중 · 약 30초 남음")]
    [InlineData(100, "번역 중 · 약 2분 남음")]
    public void TimeLeftUnderTwoSecondsIsHiddenAndLongerTimesRoundToFiveSecondsOrMinutes(int total, string expected)
    {
        var estimate = new PinTranslationTimeEstimate();
        estimate.Observe(1, total, Seconds(0));
        // One sentence a second leaves total - 2 seconds: 1, 7, 23, 28 and 98.
        estimate.Observe(2, total, Seconds(1));
        Assert.Equal(expected, estimate.Describe("번역 중"));
    }

    [Fact]
    public void TimeLeftReadsNaturallyInEnglish()
    {
        using var language = UiText.UseLanguage("en-US");
        var seconds = new PinTranslationTimeEstimate();
        seconds.Observe(1, 10, Seconds(0));
        seconds.Observe(2, 10, Seconds(1));
        Assert.Equal("Translating · 2/10 · about 8 s left", seconds.Describe(UiText.Format("Translate_Progress", 2, 10)));
        var minutes = new PinTranslationTimeEstimate();
        minutes.Observe(1, 100, Seconds(0));
        minutes.Observe(2, 100, Seconds(1));
        Assert.Equal("Translating · 2/100 · about 2 min left", minutes.Describe(UiText.Format("Translate_Progress", 2, 100)));
    }

    [Fact]
    public async Task ServicePassesOnlyGrowingCountsOfItsOwnCallAndEndsWithTheTotal()
    {
        PinContent content = await TextContent(ThreeSentences);
        var reports = new List<TranslationProgress>();
        var progress = new TextTranslationServiceTests.SyncProgress<TranslationProgress>(reports.Add);
        // A repeated count, a count of another request, one going back and one past the end.
        var noisy = new ReportingTranslator(sink =>
        {
            foreach ((int completed, int total) in new[] { (0, 3), (1, 7), (2, 3), (1, 3), (2, 3), (5, 3) })
                sink?.Report(new(completed, total));
        });

        Assert.NotNull(await new PinTranslationService(noisy) { Progress = progress }.TranslateAsync(content, CancellationToken.None));
        Assert.Equal(1, noisy.Calls);
        Assert.Equal(new TranslationProgress[] { new(0, 3), new(2, 3), new(3, 3) }, reports);

        // A translator that reports nothing still gives the pin the count and the end.
        reports.Clear();
        var silent = new ReportingTranslator(static _ => { });
        Assert.NotNull(await new PinTranslationService(silent) { Progress = progress }.TranslateAsync(content, CancellationToken.None));
        Assert.Equal(new TranslationProgress[] { new(0, 3), new(3, 3) }, reports);
    }

    [Fact]
    public void ChipShowsTheCountAndTimeLeftInPlaceAndTheBarEasesToEachShare() => RunWithMotion(true, () =>
    {
        PinWindow pin = NewTextPin();
        var announced = new List<string>();
        pin.AnnounceFeedback = (_, text, _, _) => announced.Add(text);
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(("번역 중", FeedbackKind.Progress, false), pin.FeedbackForTest);
            // Without a count a highlight slides along the bar.
            Assert.Equal(("번역 중", (double?)null, true, true), pin.TranslationProgressForTest);

            long start = Stopwatch.GetTimestamp();
            pin.ShowTranslationProgress(0, 12, start);
            Assert.Equal("번역 중 · 0/12", pin.FeedbackForTest.Text);
            Assert.Equal(("번역 중 · 0/12", (double?)null, true, true), pin.TranslationProgressForTest);

            // Within a tenth of a second of the last line a count waits for its turn.
            pin.ShowTranslationProgress(3, 12, start + Ticks(1));
            Assert.True(pin.IsTranslationProgressQueuedForTest);
            Assert.True(pin.IsTranslationProgressTimerRunningForTest);
            Assert.Equal("번역 중 · 0/12", pin.FeedbackForTest.Text);
            pin.FlushTranslationProgressForTest();
            Assert.Equal("번역 중 · 3/12", pin.FeedbackForTest.Text);
            PinProgressStrip bar = pin.TranslationStripForTest;
            Assert.Equal(0.25, bar.Value);
            // The fill eases from where it was; the animation's first frame starts at the old share.
            Assert.True(bar.IsEasing);
            // A glint passes over the filled part while the next sentence decodes.
            Assert.True(bar.IsMoving);

            pin.ShowTranslationProgress(4, 12, start + Ticks(2));
            pin.FlushTranslationProgressForTest();
            Assert.Equal(("번역 중 · 4/12 · 약 8초 남음", FeedbackKind.Progress, false), pin.FeedbackForTest);
            Assert.Equal(4 / 12d, bar.Value!.Value, 6);
            // Counts change the message in place: it was announced once, and it still has no countdown.
            Assert.Equal(["번역 중"], announced);
            Assert.False(pin.IsFeedbackTimerRunning);

            pin.SetTranslationState(PinTranslationState.Failed);
            Assert.Equal(FeedbackKind.ActionRequired, pin.FeedbackForTest.Kind);
            Assert.Equal(((string?)null, (double?)null, false, false), pin.TranslationProgressForTest);
            Assert.False(bar.HasRunningAnimations);
            Assert.False(pin.IsTranslationProgressTimerRunningForTest);
            // A late count of the stopped translation changes nothing.
            pin.ShowTranslationProgress(5, 12, start + Ticks(3));
            Assert.Equal(FeedbackKind.ActionRequired, pin.FeedbackForTest.Kind);
            Assert.Null(pin.TranslationProgressForTest.Line);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void WithoutAnimationsNothingMovesAndOnlyACountShowsABar() => RunWithMotion(false, () =>
    {
        PinWindow pin = NewTextPin();
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(("번역 중", (double?)null, false, false), pin.TranslationProgressForTest);

            pin.ShowTranslationProgress(3, 12, Stopwatch.GetTimestamp());
            PinProgressStrip bar = pin.TranslationStripForTest;
            Assert.Equal(("번역 중 · 3/12", (double?)0.25, true, false), pin.TranslationProgressForTest);
            Assert.Equal(0.25, bar.ShownValue);
            Assert.False(bar.HasRunningAnimations);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void AWaitingCountAppearsWithoutAnotherReportAndReplacesTheOnesBeforeIt() => RunWithMotion(false, () =>
    {
        PinWindow pin = NewTextPin();
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            long start = Stopwatch.GetTimestamp();
            pin.ShowTranslationProgress(1, 12, start);
            pin.ShowTranslationProgress(2, 12, start);
            pin.ShowTranslationProgress(3, 12, start);
            Assert.Equal("번역 중 · 1/12", pin.FeedbackForTest.Text);

            PumpUntil(() => !pin.IsTranslationProgressQueuedForTest);
            Assert.Equal("번역 중 · 3/12", pin.FeedbackForTest.Text);
            Assert.False(pin.IsTranslationProgressTimerRunningForTest);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void AStepWithoutAMessageTakesTheProgressAwayAndAnOutcomeReplacesItInPlace() => RunWithMotion(false, () =>
    {
        PinWindow pin = NewTextPin();
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.True(pin.IsFeedbackVisible);
            // The install question has no message of its own; "translating" leaves at the next tick.
            pin.SetTranslationState(PinTranslationState.ModelNeeded, 1_000_000);
            Assert.Equal(((string?)null, (double?)null, false, false), pin.TranslationProgressForTest);
            Assert.Equal(TimeSpan.Zero, pin.FeedbackRemainingForTest);
            pin.AdvanceFeedbackForTest(FeedbackCountdown.TickInterval);
            Assert.False(pin.IsFeedbackVisible);

            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            pin.SetTranslationState(PinTranslationState.Available);
            pin.ReportTranslationNote(UiText.Get("Pin_TranslationNoText"));
            Assert.Equal((UiText.Get("Pin_TranslationNoText"), FeedbackKind.Result, false), pin.FeedbackForTest);
            Assert.Equal(FeedbackTiming.Result, pin.FeedbackRemainingForTest);
            pin.AdvanceFeedbackForTest(FeedbackCountdown.TickInterval);
            Assert.True(pin.IsFeedbackVisible);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ModelInstallationSlidesWithoutACountAndTranslationTakesItsPlace() => RunWithMotion(true, () =>
    {
        PinWindow pin = NewTextPin();
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Downloading, 1_000_000);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(("번역 모델 설치 중", (double?)null, true, true), pin.TranslationProgressForTest);
            // A count is a translation's: the installation shows none.
            pin.ShowTranslationProgress(1, 3, Stopwatch.GetTimestamp());
            Assert.Equal("번역 모델 설치 중", pin.FeedbackForTest.Text);

            pin.SetTranslationState(PinTranslationState.Translating);
            Assert.Equal("번역 중", pin.FeedbackForTest.Text);
            Assert.Equal(("번역 중", (double?)null, true, true), pin.TranslationProgressForTest);
            pin.ShowTranslationProgress(1, 3, Stopwatch.GetTimestamp());
            Assert.Equal("번역 중 · 1/3", pin.FeedbackForTest.Text);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ReportsReachThePinOnItsDispatcherOnlyWhileTheirTranslationRuns() => RunWithMotion(false, () =>
    {
        PinWindow pin = NewTextPin();
        using var replaced = new CancellationTokenSource();
        using var current = new CancellationTokenSource();
        try
        {
            pin.Show();
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            var stale = new PinTranslationProgress(pin, replaced.Token);
            var live = new PinTranslationProgress(pin, current.Token);
            replaced.Cancel();
            stale.Report(new(3, 3));
            // From the translator's thread; the second report replaces the first before the pin gets to it.
            Task.Run(() =>
            {
                live.Report(new(1, 3));
                live.Report(new(2, 3));
            }).Wait();
            Assert.Equal("번역 중", pin.FeedbackForTest.Text);
            Drain();
            Assert.Equal("번역 중 · 2/3", pin.FeedbackForTest.Text);

            current.Cancel();
            live.Report(new(3, 3));
            Drain();
            Assert.Equal("번역 중 · 2/3", pin.FeedbackForTest.Text);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ClosingDuringATranslationStopsTheBarAndLetsThePinGo() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        try
        {
            WeakReference[] references = CloseDuringTranslation();
            for (int attempt = 0; attempt < 5 && references.Any(reference => reference.IsAlive); attempt++)
            {
                Drain();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.All(references, reference => Assert.False(reference.IsAlive, "A closed pin is still rooted."));
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [Fact]
    public Task TextPinShowsTheTranslatorsCountUntilTheResultReplacesIt() => RunAsync(async () =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var translator = new SteppingTranslator();
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        manager.ConfigureTranslation(translator);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), ThreeSentences, false);
            pin.InvokeTranslationAction();
            IProgress<TranslationProgress> progress = await translator.Started.Task;
            await Settle();
            pin.FlushTranslationProgressForTest();
            Assert.Equal("번역 중 · 0/3", pin.TranslationProgressForTest.Line);

            progress.Report(new(1, 3));
            await Settle();
            pin.FlushTranslationProgressForTest();
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(("번역 중 · 1/3", FeedbackKind.Progress, false), pin.FeedbackForTest);
            Assert.Equal(1 / 3d, pin.TranslationProgressForTest.Value!.Value, 6);

            translator.Release.SetResult();
            await pin.TranslationWork;
            await Settle();
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            // The outcome replaced the progress message.
            Assert.NotEqual(FeedbackKind.Progress, pin.FeedbackForTest.Kind);
            Assert.Equal(((string?)null, (double?)null, false, false), pin.TranslationProgressForTest);
            Assert.False(pin.TranslationStripForTest.HasRunningAnimations);
        }
        finally
        {
            manager.CloseAll();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    // Keep the pin out of the collecting frame so the JIT's local lifetimes cannot hide a root.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CloseDuringTranslation()
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinWindow pin = NewTextPin();
        pin.Show();
        pin.SetTranslationState(PinTranslationState.Translating);
        pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
        // Only the bar animates from here, so whatever runs when the pin closes is its own.
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        long start = Stopwatch.GetTimestamp();
        pin.ShowTranslationProgress(3, 12, start);
        pin.ShowTranslationProgress(4, 12, start);
        PinProgressStrip bar = pin.TranslationStripForTest;
        Assert.True(bar.IsMoving);
        Assert.True(bar.IsEasing);
        Assert.True(pin.IsTranslationProgressTimerRunningForTest);

        pin.Close();
        Assert.False(bar.IsMoving);
        Assert.False(bar.HasRunningAnimations);
        Assert.False(pin.IsTranslationProgressTimerRunningForTest);
        Assert.False(pin.IsTranslationProgressQueuedForTest);
        return [new(pin), new(bar)];
    }

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static PinWindow NewTextPin()
    {
        var image = BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 180 * 4], 480 * 4);
        image.Freeze();
        return new PinWindow(PinContent.FromText(image, ThreeSentences, false),
            new PinViewState(480, 180, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
            { ShowActivated = false };
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(600, 240, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 240 * 4], 600 * 4);
        image.Freeze();
        return image;
    }

    private static Task<PinContent> TextContent(string text) => StaThreadTask.RunAsync(() =>
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        return PinContent.FromText(image, text, false);
    });

    private static void RunWithMotion(bool animations, Action body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
        try { body(); }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    /// <summary>Runs what the dispatcher has queued, including the pin's background-priority progress.</summary>
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            var resume = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
                (sender, _) => { ((DispatcherTimer)sender!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            resume.Stop();
        }
        Assert.True(done(), "The waiting count did not appear.");
    }

    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

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

    /// <summary>Reports what the test chooses, then translates every sentence.</summary>
    private sealed class ReportingTranslator(Action<IProgress<TranslationProgress>?> report) : ITextTranslator
    {
        internal int Calls { get; private set; }
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The service must not download a model.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            report(progress);
            return Task.FromResult<IReadOnlyList<TranslatedText>>(
                sources.Select(source => new TranslatedText(source, "창을 닫기 전에 문서를 저장합니다.", [], [])).ToArray());
        }
    }

    /// <summary>Hands the test its progress and translates once the test releases it.</summary>
    private sealed class SteppingTranslator : ITextTranslator
    {
        internal TaskCompletionSource<IProgress<TranslationProgress>> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long ModelDownloadBytes => 1;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The model is installed.");
        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult(progress ?? throw new InvalidOperationException("The pin asked for no progress."));
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return [.. sources.Select(source => new TranslatedText(source, "창을 닫기 전에 문서를 저장합니다.", [], []))];
        }
    }
}
