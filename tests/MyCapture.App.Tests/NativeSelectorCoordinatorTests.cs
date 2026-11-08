using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptInNativeSelectorRoutesImageAndDirectTextResults(bool text) => StaTestHost.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        FrozenFrame frame = Frame();
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => { release.Wait(); return frame; }, () => frame.ScreenBounds, _ => []);
        coordinator.UseNativeDibSelector = true;
        coordinator.AutoDetectWindows = () => false;
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = null;
        coordinator.OpenEditorAfterCapture = () => false;
        int images = 0, texts = 0;
        coordinator.SelectionPersistRequested = _ => { images++; return Task.CompletedTask; };
        coordinator.TextCaptureRequested = selection => { Assert.True(selection.TextCapture); texts++; };
        try
        {
            coordinator.Start(false, false, false, textCapture: text);
            NativeCaptureOverlay overlay = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
            Assert.Equal(text, overlay.TextCaptureMode);
            Assert.True(overlay.TextCaptureAvailable);
            Assert.False(overlay.IsVisible);
            overlay.AttachFrame(frame);
            overlay.BeginSelection(new PointD(10, 10));
            overlay.EndSelection(new PointD(100, 80));
            PumpUntil(() => text ? texts == 1 : images == 1);
            Assert.Equal(text ? 0 : 1, images);
            Assert.Equal(text ? 1 : 0, texts);
            Assert.Equal(0, overlay.LiveOwnedNativeHandles);
            Assert.Null(coordinator.NativeOverlayForProbe);
        }
        finally
        {
            coordinator.Cancel();
            release.Set();
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        }
    });

    [Fact]
    public void OrdinaryCoordinatorStillDefaultsToWpfUntilParityIsComplete() => StaTestHost.Run(() =>
    {
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance);
        Assert.False(coordinator.UseNativeDibSelector);
    });
}
