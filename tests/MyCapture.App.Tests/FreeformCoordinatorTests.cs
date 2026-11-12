using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class FreeformCoordinatorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpeningForTextDisablesFreeformOnBothProductionSelectorRoutes(bool native, bool text) => StaTestHost.Run(() =>
    {
        using var release = new ManualResetEventSlim();
        FrozenFrame frame = Frame();
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => { release.Wait(); return frame; }, () => frame.ScreenBounds, _ => [])
        {
            UseNativeDibSelector = native,
            AutoDetectWindows = () => false,
            ApplyCaptureExclusion = _ => true,
            ApplyNativeCaptureExclusion = _ => true,
            ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
            ReadWindowSources = null,
            TextCaptureRequested = _ => { },
            OpenEditorAfterCapture = () => false,
        };
        try
        {
            coordinator.Start(false, false, false, textCapture: text);
            if (native)
            {
                NativeCaptureOverlay selector = Assert.IsType<NativeCaptureOverlay>(coordinator.NativeOverlayForProbe);
                Assert.Equal(!text, selector.FreeformAvailable);
                Assert.Equal(text ? UiText.Get("CaptureFreeform_Text") : null, selector.FreeformUnavailableReason);
                selector.SelectMode(CaptureSelectorMode.Freeform);
                Assert.Equal(text ? CaptureSelectorMode.Text : CaptureSelectorMode.Freeform, selector.Mode);
                Assert.False(selector.IsVisible);
            }
            else
            {
                var field = typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var window = Assert.IsType<CaptureOverlayWindow>(field.GetValue(coordinator));
                var selector = Assert.IsType<CaptureOverlayView>(window.Content);
                Assert.Equal(!text, selector.FreeformAvailable);
                Assert.Equal(text ? UiText.Get("CaptureFreeform_Text") : null, window.FreeformUnavailableReason);
                Assert.Equal(!text, selector.ModeBarForTest!.FreeformButton.IsEnabled);
                selector.SelectMode(CaptureSelectorMode.Freeform);
                Assert.Equal(text ? CaptureSelectorMode.Text : CaptureSelectorMode.Freeform, selector.Mode);
                Assert.False(window.IsVisible);
            }
        }
        finally
        {
            coordinator.Cancel();
            release.Set();
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        }
    });
}
