using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeSelectorRoutingTests
{
    [Theory]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    [InlineData(false, "saved")]
    [InlineData(true, "saved")]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    public void ReadySelectionAndCancellationRetainThePracticeRequest(bool native, string outcome) => StaTestHost.Run(() =>
    {
        var target = new PracticeCaptureTarget(Guid.NewGuid(), Guid.NewGuid());
        var signals = new List<PracticeEvent>();
        var ready = new TaskCompletionSource();
        var saved = new TaskCompletionSource();
        using var coordinator = Coordinator(native);
        bool readyAtNativeInput = false;
        void Signal(PracticeEvent signal)
        {
            signals.Add(signal);
            if (signal.Kind == PracticeEventKind.SelectorReady)
            {
                readyAtNativeInput = coordinator.NativeOverlayForProbe?.IsReady == true;
                ready.TrySetResult();
            }
        }
        PracticeSignals.Result += Signal;
        CaptureSelectionCompletedEventArgs? selection = null;
        coordinator.SelectionPersistRequested = value => { selection = value; return saved.Task; };
        try
        {
            coordinator.Start(false, false, false, practiceTarget: target);
            Assert.True(coordinator.OwnsPracticeCapture(target));
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest);
            PinHistoryTestSupport.Await(ready.Task);
            var selector = new CoordinatorSelector(coordinator, native);
            Assert.True(selector.IsVisible);
            Assert.Equal(native, readyAtNativeInput);
            Drain();
            Assert.Single(signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
            Assert.True(coordinator.OwnsPracticeCapture(target));
            if (outcome == "close") selector.Close();
            else
            {
                selector.CompleteSelection(new RectD(4, 5, 30, 20));
                Assert.NotNull(selection);
                Assert.Equal(target, selection.PracticeTarget);
                Assert.False(selector.IsVisible);
                Assert.True(coordinator.OwnsPracticeCapture(target));
                Assert.DoesNotContain(signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
                if (outcome == "failed") saved.SetException(new InvalidOperationException("Synthetic practice persistence failure"));
                else saved.SetResult();
                PinHistoryTestSupport.Await(coordinator.LastTransitionForTest);
            }
            Drain();
            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.OwnsPracticeCapture(target));
            Assert.Equal(outcome == "saved" ? 0 : 1, signals.Count(signal => signal.Kind == PracticeEventKind.CaptureCancelled));
            Assert.All(signals, signal =>
            {
                Assert.Equal(target.SessionId, signal.SessionId);
                Assert.Equal(target.RequestId, signal.RequestId);
            });
        }
        finally { saved.TrySetResult(); PracticeSignals.Result -= Signal; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingBeforePresentationCancelsWithoutALateReadySignal(bool native) => StaTestHost.Run(() =>
    {
        var target = new PracticeCaptureTarget(Guid.NewGuid(), Guid.NewGuid());
        var signals = new List<PracticeEvent>();
        using var coordinator = Coordinator(native);
        PracticeSignals.Result += signals.Add;
        try
        {
            coordinator.Start(false, false, false, practiceTarget: target);
            new CoordinatorSelector(coordinator, native).Close();
            coordinator.Cancel();
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest);
            Drain();
            Assert.DoesNotContain(signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled
                && signal.SessionId == target.SessionId && signal.RequestId == target.RequestId);
            Assert.False(coordinator.OwnsPracticeCapture(target));
            Assert.False(coordinator.IsActive);
        }
        finally { PracticeSignals.Result -= signals.Add; }
    });

    [Fact]
    public void NativeInputReadyFailurePreservesThePracticeRequestThroughFallback() => StaTestHost.Run(() =>
    {
        var target = new PracticeCaptureTarget(Guid.NewGuid(), Guid.NewGuid());
        var signals = new List<PracticeEvent>();
        var failures = new List<Exception>();
        var closed = new TaskCompletionSource();
        using var coordinator = Coordinator(native: true);
        coordinator.TransitionFailed += failures.Add;
        PracticeSignals.Result += signals.Add;
        try
        {
            coordinator.Start(false, false, false, practiceTarget: target);
            NativeCaptureOverlay selector = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
            selector.Closed += (_, _) => closed.TrySetResult();
            selector.InputReady += (_, _) => throw new InvalidOperationException("Synthetic native readiness failure");
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest);
            PinHistoryTestSupport.Await(closed.Task);
            Drain();
            var fallback = new CoordinatorSelector(coordinator, native: false);
            Assert.True(fallback.IsVisible);
            Assert.Empty(failures);
            Assert.Single(signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
            Assert.DoesNotContain(signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
            Assert.True(coordinator.OwnsPracticeCapture(target));
            fallback.Close();
            Assert.Single(signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
            Assert.All(signals, signal =>
            {
                Assert.Equal(target.SessionId, signal.SessionId);
                Assert.Equal(target.RequestId, signal.RequestId);
            });
            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.OwnsPracticeCapture(target));
        }
        finally { PracticeSignals.Result -= signals.Add; }
    });

    private static CaptureOverlayCoordinator Coordinator(bool native)
    {
        var bounds = new RectD(0, 0, 80, 60);
        var frame = new FrozenFrame(EditorKeysTests.Solid(80, 60, 80), bounds, null, 0);
        return new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => frame, () => bounds, _ => [])
        {
            UseNativeDibSelector = native,
            OpenEditorAfterCapture = () => false,
            ReadWindowSources = null,
            ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
        };
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
