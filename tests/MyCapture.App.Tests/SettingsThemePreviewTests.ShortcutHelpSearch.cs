using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The shortcut help's search states with the real theme: typed text starts where the hint
/// does, result rows are at least 36 high with 12 at each side, and nothing in the result list
/// is clipped. With <c>MYCAPTURE_SETTINGS_UX_EVIDENCE</c> set, the tabbed view, a search across
/// tabs and an empty search are written as PNG at 96 and 144 dpi in both languages.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task ShortcutHelpSearch_ThemedStatesKeepTheirMetrics() =>
        RunIsolated(nameof(ShortcutHelpSearch_ThemedStatesKeepTheirMetrics), VerifyShortcutHelpSearchThemed);

    private static void VerifyShortcutHelpSearchThemed()
    {
        Application application = CreateThemedApplication();
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_PHASE") is { Length: > 0 } label ? label : "after";
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight, AppTheme.HighContrast })
                {
                    ThemeService.Apply(theme);
                    var help = new ShortcutHelpWindow
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                    };
                    FluidMotion.SetWindowEntrance(help, false);
                    try
                    {
                        help.Show();
                        Drain(help);
                        ShortcutHelpSearchTests.HelpView view = ShortcutHelpSearchTests.View(help);
                        string where = $"{locale} {AppThemeNames.ToSetting(theme)}";
                        string name = $"shortcut-help-{locale}-{AppThemeNames.ToSetting(theme)}";
                        view.Tabs.SelectedIndex = 1;
                        Drain(help);
                        SaveHelpEvidence(help, $"{name}-tabs-{phase}");

                        // The shared template applies Padding twice to a TextBox; the help search
                        // undoes that so typed text and the hint line up, at the 40 it asks for.
                        TextBlock hint = Descendants(help).OfType<TextBlock>().First(text => text.Text == UiText.Get("Help.Search"));
                        view.Search.Text = "Ctrl+C";
                        Drain(help);
                        FrameworkElement typed = Descendants(view.Search).OfType<FrameworkElement>()
                            .First(element => element.GetType().Name == "TextBoxView");
                        Assert.Equal(BoundsIn(hint, help).Left, BoundsIn(typed, help).Left, 1.5);
                        Assert.InRange(view.Search.ActualHeight, 40, 41);

                        Border[] rows = view.VisibleResults();
                        Assert.Equal(5, rows.Length);
                        Rect list = BoundsIn(view.ResultsView, help);
                        foreach (Border row in rows)
                        {
                            Rect bounds = BoundsIn(row, help);
                            Assert.True(bounds.Height >= 36, $"{where}: a result row is {bounds.Height:0.#} high.");
                            Assert.True(bounds.Left >= list.Left - 0.5 && bounds.Right <= list.Right + 0.5, $"{where}: {bounds} leaves {list}.");
                            TextBlock action = Descendants(row).OfType<TextBlock>().First();
                            Assert.Equal(12, BoundsIn(action, help).Left - bounds.Left, 0.5);
                            foreach (TextBlock text in Descendants(row).OfType<TextBlock>())
                                Assert.True(text.DesiredSize.Width <= text.ActualWidth + 1, $"{where}: \"{text.Text}\" is clipped.");
                        }
                        Assert.True(BoundsIn(view.Count, help).Bottom <= list.Top + 0.5);
                        SaveHelpEvidence(help, $"{name}-search-across-tabs-{phase}");

                        view.Search.Text = "F2";
                        Drain(help);
                        SaveHelpEvidence(help, $"{name}-search-other-tab-{phase}");

                        view.Search.Text = "zzz";
                        Drain(help);
                        Rect empty = BoundsIn(view.Empty, help);
                        Assert.True(empty.Top >= BoundsIn(view.Search, help).Bottom && empty.Bottom <= help.ActualHeight);
                        SaveHelpEvidence(help, $"{name}-search-empty-{phase}");
                    }
                    finally
                    {
                        help.Close();
                    }
                }
            }
        }
        finally
        {
            ShortcutHelpWindow.ReadHotkeys = previous;
            application.Shutdown();
        }
    }

    private static void SaveHelpEvidence(Window window, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_EVIDENCE"))) return;
        foreach (int dpi in new[] { 96, 144 })
            SaveSettingsUxEvidence(RenderAtDpi(window, dpi), $"{name}-{dpi}dpi.png");
    }
}
