using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in, offscreen pictures of the native selector's modes: the bar with each mode chosen, the
/// highlight and its tab, and the magnifier footer. Nothing is shown, read from the screen or moved.
/// </summary>
public sealed class SelectorModeEvidenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void RenderSelectorModes(string language) => StaTestHost.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SELMODE_EVIDENCE") != "1") return;
        string directory = OwnedTestDirectory.Create("selector-modes-");
        output.WriteLine("Selector mode evidence: " + directory);
        using var culture = UiText.UseLanguage(language);
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        var window = new WindowCandidate(new IntPtr(88), new RectD(120, 140, 520, 360), "Editor");
        PointD point = new(380, 300);
        MonitorInfo[] monitors = [new("selector-modes", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        using var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, true)
        {
            DetectWindows = true, SourceWindows = [window], ReadCursor = () => point, PlaceCursor = _ => false,
            ReadMonitors = () => monitors, OffersOtherActions = true, TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        owner.PreparePointerFeedback(); owner.PrepareVisualFeedback(); owner.UpdatePointer(false, false);

        void Shot(string name)
        {
            owner.RefreshHover();
            chrome.Update(point, owner.DisplayedRegionForTest);
            chrome.FlushHoverTabForTest();
            BitmapSource scene = NativeSelectorTestRenderer.Render(owner, chrome);
            TestDiagnosticArtifacts.SavePng(directory, $"{language}-{name}.png", scene);
            output.WriteLine($"{name}: tab={chrome.TargetLabelForTest}");
        }

        Shot("01-window");
        owner.SelectMode(CaptureSelectorMode.Text);
        Shot("02-text");
        owner.SelectMode(CaptureSelectorMode.Color);
        Shot("03-color");
        output.WriteLine("color readout: " + chrome.MagnifierForTest?.ColorReadoutForTest);
        owner.SelectMode(CaptureSelectorMode.Capture);
        Shot("04-region");
        owner.SelectMode(CaptureSelectorMode.Scroll);
        Shot("05-scroll");
        owner.SelectMode(CaptureSelectorMode.Capture);
        owner.ArmedDelaySeconds = 5;
        Shot("06-delay-armed");
    });
}
