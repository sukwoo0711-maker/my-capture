using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task FixedAccentAndFourThemeChoicesPersistImportResetAndRollBack() =>
        RunIsolated(nameof(FixedAccentAndFourThemeChoicesPersistImportResetAndRollBack), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            Application application = CreateThemedApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.SystemHighContrastOverrideForTest = false;
            string root = TestRecycleBin.CreateTempSubdirectory("mc-accent-preview-").FullName;
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            AppSettings saved = NewInstallDefaults.Create();
            saved.General.Theme = AppThemeNames.Glass;
            saved.General.AccentColor = AppAccentNames.Violet; // Legacy files normalize to the one supported accent.
            bool failSave = false;
            int saves = 0;
            int failures = 0;
            ThemeService.ApplyFromSettings(saved.General);
            var window = new SettingsWindow(() => saved, candidate =>
            {
                if (failSave) return SettingsApplyResult.NotSaved(["Synthetic save failure"]);
                saves++;
                store.Save(candidate);
                saved = candidate;
                ThemeService.ApplyFromSettings(saved.General);
                return new SettingsApplyResult(true, true, true, false, []);
            }, NullLogger.Instance, settingsStore: () => store, showSaveFailureMessage: _ => failures++)
            {
                Left = -10000, Top = -10000, Width = 940, Height = 700,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
            };
            var survivor = new Border();
            survivor.SetResourceReference(Border.BackgroundProperty, "Accent.Default");
            var otherWindow = new Window
            {
                Content = survivor, Width = 120, Height = 100, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            WorkspaceTheme.Attach(otherWindow, WorkspaceRole.Gallery);
            try
            {
                window.Show();
                otherWindow.Show();
                Drain(window);
                Assert.Null(window.FindName("AccentColorSelector"));
                var themeSelector = Assert.IsType<ComboBox>(window.FindName("ThemeSelector"));
                Assert.Equal(new[] { "system", "glass-light", "glass", "high-contrast" },
                    themeSelector.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).ToArray());
                Assert.Equal("테마", System.Windows.Automation.AutomationProperties.GetName(themeSelector));
                SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
                Assert.Equal(AppAccentNames.Red, draft.AccentColor);
                Assert.False(draft.HasUnappliedChanges);
                Assert.False(Assert.IsType<Button>(window.FindName("ApplyButton")).IsEnabled);
                AssertAccent(AppAccent.Red);
                var oldBrush = Assert.IsType<SolidColorBrush>(survivor.Background);

                themeSelector.SelectedValue = AppThemeNames.GlassLight;
                Drain(window);
                AssertAccent(AppAccent.Red);
                Assert.True(draft.HasUnappliedChanges);
                Assert.True(Assert.IsType<Button>(window.FindName("ApplyButton")).IsEnabled);
                Assert.Equal(AppAccentNames.Violet, saved.General.AccentColor);
                Assert.Equal(0, saves);
                Assert.True(oldBrush.IsFrozen);
                Assert.NotSame(oldBrush, survivor.Background);
                Assert.Equal(ToAccentColor(ThemeCatalog.ColorsFor(AppTheme.GlassLight)["Accent.Default"]),
                    Assert.IsType<SolidColorBrush>(survivor.Background).Color);
                foreach (string theme in new[] { "system", "glass-light", "glass", "high-contrast" })
                {
                    themeSelector.SelectedValue = theme;
                    Drain(window);
                    AssertAccent(AppAccent.Red);
                }
                themeSelector.SelectedValue = AppThemeNames.GlassLight;
                Drain(window);
                ThemeService.ScheduleSystemThemeRefresh(window.Dispatcher, application.Resources, () => true);
                Drain(window);
                Assert.Equal(AppAccent.Red, ThemeService.CurrentAccent);
                ThemeService.ScheduleSystemThemeRefresh(window.Dispatcher, application.Resources, () => false);
                Drain(window);
                AssertAccent(AppAccent.Red);

                SettingsWindow.CancelCommand.Execute(null, window);
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppAccentNames.Red, Assert.IsType<SettingsDraft>(window.DataContext).AccentColor);
                Assert.Equal(AppAccentNames.Violet, saved.General.AccentColor);
                Assert.Equal(0, saves);
                window.ShowSettings();
                themeSelector.SelectedValue = AppThemeNames.GlassLight;
                Drain(window);
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(1, saves);
                Assert.Equal(AppAccentNames.Red, store.Load().General.AccentColor);
                Assert.Equal(AppThemeNames.GlassLight, store.Load().General.Theme);

                window.ShowSettings();
                draft = Assert.IsType<SettingsDraft>(window.DataContext);
                draft.ResetToDefaults();
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppThemeNames.System, draft.Theme);
                string exported = Path.Combine(root, "red-export.json");
                store.ExportTo(saved, exported);
                window.ImportSettingsFrom(exported);
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppAccentNames.Red, draft.AccentColor);

                themeSelector.SelectedValue = AppThemeNames.Glass;
                failSave = true;
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(1, failures);
                Assert.True(window.IsVisible);
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppThemeNames.GlassLight, saved.General.Theme);
                window.ConfirmUnappliedChanges = () => SettingsCloseChoice.Discard;
                window.Close();
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppTheme.GlassLight, ThemeService.Current);
                draft.AccentColor = AppAccentNames.Violet;
                AssertAccent(AppAccent.Red);
            }
            finally
            {
                window.CloseForExit();
                otherWindow.Close();
                ThemeService.ApplyFromSettings(AppThemeNames.Glass);
                ThemeService.SystemHighContrastOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }

            void AssertAccent(AppAccent accent)
            {
                Assert.Equal(accent, ThemeService.CurrentAccent);
                var colors = ThemeCatalog.ColorsFor(ThemeService.EffectiveTheme, accent);
                AssertBrush((Brush)application.Resources["Accent.Default"], colors["Accent.Default"]);
                if (ThemeService.Current == AppTheme.HighContrast)
                    Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)application.Resources["CaptureGuide.SelectionBorder"]).Color);
                else AssertBrush((Brush)application.Resources["CaptureGuide.SelectionBorder"], colors["Overlay.SelectionBorder"]);
            }
        });

    private static Color ToAccentColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
