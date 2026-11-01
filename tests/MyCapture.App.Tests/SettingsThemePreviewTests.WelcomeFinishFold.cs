using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task WelcomeOcr_OriginalLayoutOn1366Laptop() =>
        WelcomeOcr_OriginalLayoutOnMonitor(nameof(WelcomeOcr_OriginalLayoutOn1366Laptop), 1366, 728, 96);

    [Fact]
    public Task WelcomeOcr_OriginalLayoutOnScaledLaptop() =>
        WelcomeOcr_OriginalLayoutOnMonitor(nameof(WelcomeOcr_OriginalLayoutOnScaledLaptop), 1920, 1032, 120);

    [Fact]
    public Task WelcomeOcr_OriginalLayoutOn1024Laptop() =>
        WelcomeOcr_OriginalLayoutOnMonitor(nameof(WelcomeOcr_OriginalLayoutOn1024Laptop), 1024, 728, 96);

    private Task WelcomeOcr_OriginalLayoutOnMonitor(string testName, int width, int height, uint dpi) =>
        RunIsolated(testName, () =>
        {
            var monitor = new MonitorInfo("test", new RectD(0, 0, width, height + 40),
                new RectD(0, 0, width, height), dpi, IsPrimary: true);
            // The original test has no options argument. Apply the simulated work-area limit
            // at Loaded, before its assertions, without changing that test or the pointer.
            // Class handlers live for the process: each wrapper therefore runs in isolation.
            int loaded = 0;
            EventManager.RegisterClassHandler(typeof(WelcomeWindow), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    ((WelcomeWindow)sender).LimitToWorkArea(monitor);
                    loaded++;
                }));
            WelcomeOcr_LayoutAndEvidenceInBothLanguages().GetAwaiter().GetResult();
            Assert.Equal(2, loaded);
        });

    [Fact]
    public Task WelcomeOcr_FinishPreferencesStayAboveFoldOnLaptopMonitors() =>
        RunIsolated(nameof(WelcomeOcr_FinishPreferencesStayAboveFoldOnLaptopMonitors), () =>
        {
            Application application = CreateThemedApplication();
            bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var failures = new List<string>();
            var measurements = new StringBuilder();
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_WELCOME_FINISH_EVIDENCE");
            string? root = string.IsNullOrWhiteSpace(evidence) ? null : DiagnosticOutputPaths.Create(evidence);
            string phase = Environment.GetEnvironmentVariable("MYCAPTURE_WELCOME_OCR_PHASE") ?? "after";
            try
            {
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    UiText.Configure(locale[..2]);
                    ThemeService.Apply(AppTheme.Glass);
                    foreach ((int width, int height, uint dpi) in new (int, int, uint)[]
                        { (1366, 728, 96), (1920, 1032, 120), (1024, 728, 96), (1920, 1040, 96) })
                    {
                        var monitor = new MonitorInfo("test", new RectD(0, 0, width, height + 40),
                            new RectD(0, 0, width, height), dpi, IsPrimary: true);
                        WelcomeWindow welcome = OnboardingGuideTests.Create(new(), new WelcomeOptions
                        {
                            MonitorProvider = () => monitor, TextScale = 1, ReducedMotion = true, UseAccurateOcr = true,
                        });
                        string scene = $"{locale[..2]}-{width}x{height}-{dpi}dpi";
                        // The simulated monitor may be taller than the runner's real desktop (about
                        // 1024×768 on CI), which would clamp the roomy 800 DIP window to 788.
                        welcome.LimitToWorkArea(monitor);
                        using var tracking = new WindowTrackSizeScope(welcome, new Size(width, height + 40));
                        try
                        {
                            OnboardingGuideTests.AdvanceTo(welcome, WelcomeStep.Finish);
                            welcome.Show();
                            Drain(welcome);
                            Assert.Equal(WelcomeStep.Finish, welcome.CurrentStep);
                            Assert.True(welcome.PreferencesShown);
                            Assert.Equal(640, welcome.ActualWidth);
                            Assert.Equal(height / monitor.ScaleFactor >= 860 ? 800 : 720, welcome.ActualHeight);
                            Assert.Equal(0, welcome.Scroller.VerticalOffset);

                            var preferences = Assert.IsType<Grid>(LogicalTreeHelper.GetParent(welcome.StartAtSignInCheck));
                            foreach ((string name, FrameworkElement element) in new (string, FrameworkElement)[]
                            {
                                ("start-at-sign-in", welcome.StartAtSignInCheck),
                                ("accurate-OCR", welcome.AccurateOcrCheck),
                                ("OCR-help", welcome.AccurateOcrHelp),
                                ("preferences", preferences),
                            })
                            {
                                Assert.True(element.IsVisible);
                                Rect windowBounds = element.TransformToAncestor(welcome).TransformBounds(new Rect(element.RenderSize));
                                Rect viewportBounds = element.TransformToAncestor(welcome.Scroller).TransformBounds(new Rect(element.RenderSize));
                                Check(windowBounds.Top >= 0 && windowBounds.Bottom <= welcome.ActualHeight - 16,
                                    $"{name} outside window: {windowBounds}, limit {welcome.ActualHeight - 16}");
                                Check(viewportBounds.Top >= 0 && viewportBounds.Bottom <= welcome.Scroller.ViewportHeight,
                                    $"{name} below fold: {viewportBounds}, viewport {welcome.Scroller.ViewportHeight}");
                                Check(viewportBounds.Left >= 0 && viewportBounds.Right <= welcome.Scroller.ViewportWidth,
                                    $"{name} outside viewport width: {viewportBounds}");
                                measurements.AppendLine(string.Create(CultureInfo.InvariantCulture,
                                    $"{scene} {name}: window {welcome.ActualWidth}x{welcome.ActualHeight}, bottom {windowBounds.Bottom}, viewport-bottom {viewportBounds.Bottom}, viewport {welcome.Scroller.ViewportHeight}, extent {welcome.Scroller.ExtentHeight}, offset {welcome.Scroller.VerticalOffset}"));
                            }

                            // Retain the original OCR layout checks as well as checking the entire block.
                            TextBlock label = Descendants(welcome.AccurateOcrCheck).OfType<TextBlock>()
                                .Single(text => text.Text == UiText.Get("Welcome_AccurateOcr"));
                            Check(label.ActualHeight <= label.FontSize * 1.8, "OCR checkbox label wraps");
                            Check(label.ActualWidth >= label.DesiredSize.Width, "OCR checkbox label is clipped");
                            Rect help = welcome.AccurateOcrHelp.TransformToAncestor(welcome)
                                .TransformBounds(new Rect(welcome.AccurateOcrHelp.RenderSize));
                            Check(help.Right <= welcome.ActualWidth - 20, "OCR help exceeds the right margin");
                            Check(welcome.StatusText == UiText.Get("Journey.C2.Success"), "Results still announce a next step");

                            if (root is not null)
                            {
                                double scale = monitor.ScaleFactor;
                                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(welcome.ActualWidth * scale),
                                    (int)Math.Ceiling(welcome.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                                bitmap.Render(welcome);
                                SaveBitmap(bitmap, DiagnosticOutputPaths.Child(root, $"finish-{scene}-{phase}.png"));
                            }
                        }
                        finally { welcome.Close(); }

                        void Check(bool condition, string detail)
                        {
                            if (!condition) failures.Add($"{scene}: {detail}");
                        }
                    }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = motion;
                application.Shutdown();
                if (root is not null)
                    File.WriteAllText(DiagnosticOutputPaths.Child(root, $"layout-{phase}.txt"), measurements.ToString(), new UTF8Encoding(false));
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
}
