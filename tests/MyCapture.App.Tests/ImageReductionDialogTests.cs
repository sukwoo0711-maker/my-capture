using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Moving the size target keeps the last preview and sizes on screen, marked as the previous
/// setting's result, and calculates once, 250 ms after the target stops moving. An older
/// calculation that finishes late never replaces a newer result.
/// </summary>
public sealed class ImageReductionDialogTests
{
    [Fact]
    public async Task StylingAndUrlChangesInvalidateAndDebounceThePixelPreview() => await StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        const string url = "https://example.com/export";
        var dialog = new ImageReductionExportDialog(CreateImage(1600, 800), "capture.png", url);
        var scheduler = new ManualScheduler();
        dialog.ScheduleRecalculation = scheduler.Schedule;
        try
        {
            Wait(dialog.UpdatePreviewAsync());
            Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(dialog.PreparedExportBytesForTest!));
            dialog.OptionsForTest.Apply(new() { Format = ImageExportFormat.Png, LongEdge = 800, Margin = 24, CornerRadius = 12, IncludeSourceUrl = true });
            Assert.False(dialog.CanExport);
            Assert.True(dialog.ShowsPreviousResultForTest);
            ManualScheduler.Request request = Assert.Single(scheduler.Requests, item => !item.Cancelled);
            Assert.Equal(TimeSpan.FromMilliseconds(250), request.Delay);
            request.Action();
            PumpUntil(() => dialog.CanExport);
            Assert.Contains("848", dialog.ResultTextForTest);
            Assert.Contains("448", dialog.ResultTextForTest);
            Assert.Equal(url, PngSourceUrlMetadata.TryReadSourceUrl(dialog.PreparedExportBytesForTest!));
        }
        finally { dialog.Close(); }
        return true;
    });

    [Fact]
    public async Task TargetChangesKeepThePreviousResultAndRecalculateOnceAfterTheyStop() => await StaThreadTask.RunAsync(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var dialog = new ImageReductionExportDialog(CreateImage(96, 64), "capture.png");
        var scheduler = new ManualScheduler();
        dialog.ScheduleRecalculation = scheduler.Schedule;
        try
        {
            Wait(dialog.UpdatePreviewAsync());
            BitmapSource? first = dialog.PreviewForTest;
            string firstResult = dialog.ResultTextForTest;
            Assert.NotNull(first);
            Assert.True(dialog.CanExport);
            Assert.Equal(0, dialog.PreparedTargetForTest);
            Assert.Equal(string.Empty, dialog.CalculationStateForTest);

            // A drag across three values: the result on screen stays, marked as the previous one.
            dialog.TargetReductionPercent = 30;
            dialog.TargetReductionPercent = 40;
            dialog.TargetReductionPercent = 50;
            Assert.Same(first, dialog.PreviewForTest);
            // The previous sizes stay; the note that it is ready to save does not.
            Assert.StartsWith(dialog.ResultTextForTest, firstResult, StringComparison.Ordinal);
            Assert.Contains("→", dialog.ResultTextForTest, StringComparison.Ordinal);
            Assert.DoesNotContain(UiText.Get("ExportReduction_Ready"), dialog.ResultTextForTest, StringComparison.Ordinal);
            Assert.True(dialog.ShowsPreviousResultForTest);
            Assert.Equal("새 목표로 계산 중 · 아래는 이전 설정 결과", dialog.CalculationStateForTest);
            // The previous result can no longer be saved: it is not the current target's.
            Assert.False(dialog.CanExport);
            Assert.Null(dialog.PreparedTargetForTest);

            // Each change restarts the 250 ms wait; only the last request is still pending.
            Assert.Equal(3, scheduler.Requests.Count);
            Assert.All(scheduler.Requests, request => Assert.Equal(TimeSpan.FromMilliseconds(250), request.Delay));
            ManualScheduler.Request pending = Assert.Single(scheduler.Requests, request => !request.Cancelled);
            Assert.Same(scheduler.Requests[^1], pending);
            Assert.True(dialog.IsRecalculationPendingForTest);

            pending.Action();
            Assert.False(dialog.IsRecalculationPendingForTest);
            PumpUntil(() => dialog.CanExport);
            Assert.NotSame(first, dialog.PreviewForTest);
            Assert.Equal(50, dialog.PreparedTargetForTest);
            Assert.False(dialog.ShowsPreviousResultForTest);
            Assert.Equal(string.Empty, dialog.CalculationStateForTest);
        }
        finally
        {
            dialog.Close();
        }

        return true;
    });

    [Fact]
    public async Task AnOlderCalculationThatFinishesLateNeverReplacesTheNewerResult() => await StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var dialog = new ImageReductionExportDialog(CreateImage(96, 64), "capture.png");
        var scheduler = new ManualScheduler();
        dialog.ScheduleRecalculation = scheduler.Schedule;
        using var slowGate = new ManualResetEventSlim();
        // The 20 % encode ignores cancellation and finishes only when released, like an
        // encoder caught between its cancellation checks.
        dialog.Encoder = (image, original, target, _) =>
        {
            if (target == 20) slowGate.Wait(TimeSpan.FromSeconds(10));
            return ImageExportEncoder.Encode(image, original, target, CancellationToken.None);
        };
        try
        {
            dialog.TargetReductionPercent = 20;
            Task slow = dialog.UpdatePreviewAsync();
            dialog.TargetReductionPercent = 60;
            Task fast = dialog.UpdatePreviewAsync();
            Wait(fast);
            Assert.Equal(60, dialog.PreparedTargetForTest);
            BitmapSource? newer = dialog.PreviewForTest;
            Assert.NotNull(newer);

            slowGate.Set();
            Wait(slow);
            Assert.Equal(60, dialog.PreparedTargetForTest);
            Assert.Same(newer, dialog.PreviewForTest);
            Assert.True(dialog.CanExport);
        }
        finally
        {
            slowGate.Set();
            dialog.Close();
        }

        return true;
    });

    [Fact]
    public async Task ClosingDropsAPendingRecalculation() => await StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var dialog = new ImageReductionExportDialog(CreateImage(32, 24), "capture.png");
        var scheduler = new ManualScheduler();
        dialog.ScheduleRecalculation = scheduler.Schedule;
        dialog.TargetReductionPercent = 30;
        Assert.True(dialog.IsRecalculationPendingForTest);
        dialog.Close();
        Assert.All(scheduler.Requests, request => Assert.True(request.Cancelled));
        return true;
    });

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        Assert.True(condition(), "timed out");
    }

    private static BitmapSource CreateImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        new Random(714).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Records each scheduled calculation and runs it only when the test says so.</summary>
    private sealed class ManualScheduler
    {
        internal List<Request> Requests { get; } = [];

        internal IDisposable Schedule(TimeSpan delay, Action action)
        {
            var request = new Request(delay, action);
            Requests.Add(request);
            return request;
        }

        internal sealed class Request(TimeSpan delay, Action action) : IDisposable
        {
            internal TimeSpan Delay { get; } = delay;

            internal Action Action { get; } = action;

            internal bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }
}
