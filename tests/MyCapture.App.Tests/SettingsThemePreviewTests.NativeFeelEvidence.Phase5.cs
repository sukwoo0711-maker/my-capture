using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [NativeFeelSceneGroup(95)]
    [NativeFeelScene("phase5-notice-ko", "welcome-window")]
    [NativeFeelScene("phase5-first-pin-ready-ko", "welcome-window")]
    [NativeFeelScene("phase5-first-pin-result-ko", "welcome-window")]
    [NativeFeelScene("phase5-pin-left-ko", "pin-window")]
    [NativeFeelScene("phase5-practice-ko", "welcome-window")]
    [NativeFeelScene("phase5-notice-en", "welcome-window")]
    [NativeFeelScene("phase5-first-pin-ready-en", "welcome-window")]
    [NativeFeelScene("phase5-first-pin-result-en", "welcome-window")]
    [NativeFeelScene("phase5-pin-left-en", "pin-window")]
    [NativeFeelScene("phase5-practice-en", "welcome-window")]
    private static void NativeFeelPhase5Welcome(NativeFeelSceneContext context)
    {
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var culture = new Phase4EvidenceLanguage(language);
            CapturePhase5Moments(context.Desktop.Monitor, context.Desktop.Scale,
                window =>
                {
                    context.Desktop.Place(window);
                    var area = context.Desktop.Monitor.WorkArea;
                    window.Left = window is PinWindow
                        ? area.Right / context.Desktop.Scale - window.Width - 32
                        : area.Left / context.Desktop.Scale + 32;
                },
                (window, scene) => context.Desktop.Still($"phase5-{scene}-{language[..2]}", "phase5-first-run", window,
                    "Actual product windows; sample text only. PinWindow is shown directly with explicit coordinates, then an injected result carries its actual IDs. No native F3, clipboard pipeline or cursor is exercised."));
        }
    }

    [NativeFeelEvidenceFact]
    public Task Phase5Welcome_CaptureDesktopEvidence() => RunIsolated(nameof(Phase5Welcome_CaptureDesktopEvidence),
        CapturePhase5Desktop, hungAfter: TimeSpan.FromMinutes(5));

    private static void CapturePhase5Desktop()
    {
        string requested = Environment.GetEnvironmentVariable(NativeFeelEvidenceFactAttribute.OutputVariable)
            ?? throw new InvalidOperationException("Native phase 5 evidence was not requested.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        string profile = OwnedTestDirectory.Create("mc-phase5-native-");
        var application = CreateLibraryApplication();
        var cases = new List<NativeFeelCase>();
        string? failure = null;
        double? requestedScale = NativeFeelCoverage.RequestedScale(Environment.GetEnvironmentVariable(NativeFeelCoverage.ScaleVariable));
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            {
                ThemeService.Apply(theme);
                string name = theme == AppTheme.Glass ? "dark" : "light";
                using var desktop = new NativeFeelDesktopCapture(output, name, requestedScale);
                try { NativeFeelPhase5Welcome(new NativeFeelSceneContext(desktop, profile, name)); }
                finally { cases.AddRange(desktop.Cases); }
            }
            Assert.Equal(20, cases.Count);
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(output, "manifest.json", new
            {
                capture = "Native desktop pixels using existing covered-desktop safety checks",
                requestedScale, expectedCases = 20, cases, failure,
                firstPin = "Injected sample and hotkey notification, actual PinWindow, result with actual shown IDs; no native F3 or PinManager pipeline",
            });
            foreach (Window window in application.Windows.Cast<Window>().ToArray()) window.Close();
            application.Shutdown();
        }
    }

    [Phase5EvidenceFact]
    public Task Phase5Welcome_RenderComparisonMatrix() => RunIsolated(nameof(Phase5Welcome_RenderComparisonMatrix),
        RenderPhase5WelcomeMatrix, hungAfter: TimeSpan.FromMinutes(5));

    private static void RenderPhase5WelcomeMatrix()
    {
        string requested = Environment.GetEnvironmentVariable(Phase5EvidenceFactAttribute.OutputVariable)
            ?? throw new InvalidOperationException("Phase 5 evidence was not requested.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        var application = CreateLibraryApplication();
        var images = new List<object>();
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        string? failure = null;
        try
        {
            ThemeService.SystemHighContrastOverrideForTest = false;
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in new[] { "ko-KR", "en-US" })
            foreach (double scale in new[] { 1d, 1.5d })
            {
                using var culture = new Phase4EvidenceLanguage(language);
                ThemeService.Apply(theme);
                string palette = theme == AppTheme.Glass ? "dark" : "light";
                string suffix = $"{palette}-{language[..2]}-scale{scale * 100:0}";
                var bounds = new RectD(-32000, -32000, 1920 * scale, 1080 * scale);
                var monitor = new MonitorInfo("phase5-offscreen", bounds, bounds, (uint)(96 * scale), true);
                CapturePhase5Moments(monitor, scale, window => Phase4PlaceOffscreen(window, monitor), Save, syntheticDpi: true);

                void Save(Window window, string scene)
                {
                    window.UpdateLayout();
                    NativeFeelDesktopCapture.Pump(100);
                    string name = $"{scene}-{suffix}.png";
                    BitmapSource bitmap = window is PinWindow ? Phase5RenderPin(window, scale) : Phase4RenderClient(window, scale);
                    TestDiagnosticArtifacts.SavePng(output, name, bitmap);
                    images.Add(new { scene, file = name, language, theme = palette, scale,
                        width = window.ActualWidth, height = window.ActualHeight,
                        bitmap.PixelWidth, bitmap.PixelHeight,
                        title = window is WelcomeWindow guide ? guide.TitleText : window.Title,
                        actualPinVisible = window is PinWindow pin && pin.IsVisible });
                }
            }
            Assert.Equal(40, images.Count);
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(output, "manifest.json", new
            {
                capture = "Real WPF Window RenderTargetBitmap; native title bar and desktop shadow excluded",
                nativeDesktop = false, dpi = "SetRootDpi; no OS display changes", expectedImages = 40,
                clipboard = "Injected clipboard and sequence, sample text only; no real clipboard, profile, cursor or native input",
                pin = "Actual PinWindow shown directly; injected result uses its actual IDs after visibility. This verifies real window lifetime, not OS F3 or the PinManager clipboard pipeline.", images, failure,
            });
            foreach (Window window in application.Windows.Cast<Window>().ToArray()) window.Close();
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            application.Shutdown();
        }
    }

    private static void CapturePhase5Moments(MonitorInfo monitor, double scale,
        Action<Window> place, Action<Window, string> save, bool syntheticDpi = false)
    {
        Func<HotkeySettings>? previousHotkeys = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => NewInstallDefaults.Create().Hotkeys;
        var clipboard = new OnboardingGuideTests.FakeClipboard();
        var guide = new WelcomeWindow(NewInstallDefaults.Create().Hotkeys, AfterCaptureAction.QuickAccess,
            options: new WelcomeOptions
            {
                CoreShortcutsNotice = true, FirstPinExperience = true, Clipboard = clipboard,
                ClipboardSequenceProvider = () => 42, ReducedMotion = true, MonitorProvider = () => monitor,
            }) { ShowActivated = false, ShowInTaskbar = false };
        PinWindow? pin = null;
        try
        {
            Show(guide);
            save(guide, "notice");
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.True(guide.IsFirstPinExperience);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            save(guide, "first-pin-ready");
            PracticeJourneyDriver.Click(guide.SampleTextButton);
            string sample = Assert.Single(clipboard.Writes);
            Assert.Equal(UiText.Get("FirstRun.SampleText"), sample);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(sample, false, scale), sample, false);
            pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, .1),
                monitor.WorkArea.Left / scale + 40, monitor.WorkArea.Top / scale + 40,
                () => new PinSettings(), new SyntheticPinDragInput())
            {
                ShowActivated = false, ShowInTaskbar = false, ReadWorkArea = _ => monitor.WorkArea,
            };
            Show(pin);
            Assert.True(pin.IsVisible);
            Assert.True(guide.Notify(new(PracticeEventKind.PinOpened, guide.Session.Id,
                LogicalPinId: pin.LogicalPracticeId, WindowPinId: pin.PracticeId, ClipboardSequence: 42)));
            Assert.Equal(FirstPinState.Pinned, guide.FirstPin.State);
            save(guide, "first-pin-result");
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(WelcomeOutcome.FirstPinCompleted, guide.Outcome);
            Assert.False(guide.IsVisible);
            Assert.True(pin.IsVisible);
            save(pin, "pin-left");
        }
        finally
        {
            guide.Close();
            pin?.Close();
            ShortcutHelpWindow.ReadHotkeys = previousHotkeys;
        }
        var practice = new WelcomeWindow(NewInstallDefaults.Create().Hotkeys, AfterCaptureAction.QuickAccess,
            options: new WelcomeOptions { Clipboard = clipboard, ReducedMotion = true, MonitorProvider = () => monitor })
            { ShowActivated = false, ShowInTaskbar = false };
        try { Show(practice); save(practice, "practice"); }
        finally { practice.Close(); }

        void Show(Window window)
        {
            place(window);
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            if (syntheticDpi) VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            NativeFeelDesktopCapture.Pump(250);
            window.UpdateLayout();
        }
    }

    private static BitmapSource Phase5RenderPin(Window window, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        VisualTreeHelper.SetRootDpi(background, new DpiScale(scale, scale));
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle((Brush)window.FindResource("Surface.Canvas"), null,
                new Rect(0, 0, bitmap.PixelWidth / scale, bitmap.PixelHeight / scale));
        bitmap.Render(background);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class Phase5EvidenceFactAttribute : FactAttribute
{
    internal const string OutputVariable = "MYCAPTURE_PHASE5_EVIDENCE";

    public Phase5EvidenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputVariable)))
            Skip = "Set MYCAPTURE_PHASE5_EVIDENCE to a new absolute directory for opt-in WPF client renders.";
    }
}
