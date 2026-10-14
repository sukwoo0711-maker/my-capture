using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class ColorCopyPreparationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationCompletesOnTheShowTurnWithoutWaitingForMetadata(bool colorCopyAvailable) => StaTestHost.Run(() =>
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        int completedThread = 0;
        int reads = 0;
        int saved = 0;
        var metadata = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new WindowCandidateService(NullLogger<WindowCandidateService>.Instance);
        output.WriteLine("Native candidates in the existing coordinator fixture's 32 x 20 bounds: {0}",
            candidates.GetCandidates(new RectD(0, 0, 32, 20)).Count);
        CaptureOverlayWindow? overlay = null;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            candidates,
            NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => WindowDetectOverlayTests.Frame(),
            () => new RectD(0, 0, 640, 360));
        coordinator.ReadWindowSources = null;
        coordinator.ReadPageUrls = _ => { reads++; return metadata.Task; };
        coordinator.CopyColorText = colorCopyAvailable ? _ => Task.FromResult(true) : null;
        coordinator.ColorCopied = _ => Assert.Fail("Opening the selector must not copy a color.");
        coordinator.ApplyCaptureExclusion = window =>
        {
            Assert.Equal(1, reads);
            overlay = (CaptureOverlayWindow)window;
            return true;
        };
        coordinator.OpenEditorAfterCapture = () => false;
        coordinator.SelectionPersistRequested = _ => { saved++; return Task.CompletedTask; };
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            coordinator.Start(false, false, false);
            Task completion = coordinator.LastPreparationForTest.ContinueWith(_ =>
                completedThread = Environment.CurrentManagedThreadId,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            WindowDetectOverlayTests.PumpUntil(() => overlay?.IsVisible == true);
            Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Equal(ownerThread, completedThread);
            Assert.False(metadata.Task.IsCompleted);
            Assert.False(coordinator.LastSourceDetailsForTest.IsCompleted);

            overlay!.CompleteSelection(new RectD(4, 4, 16, 10));
            WindowDetectOverlayTests.PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.Equal(1, saved);
            Assert.False(coordinator.IsActive);
            Assert.False(metadata.Task.IsCompleted);
        }
        finally
        {
            metadata.TrySetResult(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });
}
