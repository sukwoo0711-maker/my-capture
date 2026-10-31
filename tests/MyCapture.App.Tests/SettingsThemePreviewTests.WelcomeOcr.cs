using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task WelcomeOcr_LayoutAndEvidenceInBothLanguages() =>
        RunIsolated(nameof(WelcomeOcr_LayoutAndEvidenceInBothLanguages), () =>
        {
            Application application = CreateThemedApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    UiText.Configure(locale[..2]);
                    ThemeService.Apply(AppTheme.Glass);
                    var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
                        ocrEnvironment: new WelcomeOcrTestEnvironment())
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                    };
                    try
                    {
                        // The choices are on the guide's last step.
                        OnboardingGuideTests.AdvanceTo(welcome, WelcomeStep.Finish);
                        welcome.Show();
                        Drain(welcome);
                        Assert.Equal(WelcomeWindow.DefaultWidth, welcome.Width);
                        Assert.True(welcome.UseAccurateOcr);
                        TextBlock label = Descendants(welcome.AccurateOcrCheck).OfType<TextBlock>()
                            .Single(text => KoreanWordWrap.Strip(text.Text) == UiText.Get("Welcome_AccurateOcr"));
                        Assert.True(label.ActualHeight <= label.FontSize * 1.8, "The checkbox label stays on one line.");
                        Assert.True(label.ActualWidth >= label.DesiredSize.Width, "The label is not clipped.");
                        Rect help = welcome.AccurateOcrHelp.TransformToAncestor(welcome)
                            .TransformBounds(new Rect(welcome.AccurateOcrHelp.RenderSize));
                        Assert.True(help.Right <= welcome.ActualWidth - 20);
                        Assert.True(help.Bottom <= welcome.ActualHeight - 16);
                        Assert.Equal(1, VisualTreeHelper.GetDpi(welcome).DpiScaleX);
                        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_WELCOME_OCR_EVIDENCE");
                        if (!string.IsNullOrWhiteSpace(root))
                        {
                            Directory.CreateDirectory(root);
                            string phase = Environment.GetEnvironmentVariable("MYCAPTURE_WELCOME_OCR_PHASE") ?? "after";
                            SaveBitmap(RenderWindow(welcome), Path.Combine(root, $"welcome-{locale}-{phase}.png"));
                        }
                    }
                    finally { welcome.Close(); }
                }
            }
            finally { application.Shutdown(); }
        });
}
