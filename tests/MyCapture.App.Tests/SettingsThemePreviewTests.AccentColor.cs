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
    public Task AccentColorSelectionSavesImmediatelyImportsResetsAndRetriesFailure() =>
        RunIsolated(nameof(AccentColorSelectionSavesImmediatelyImportsResetsAndRetriesFailure), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            Application application = CreateThemedApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.SystemHighContrastOverrideForTest = false;
            string root = TestRecycleBin.CreateTempSubdirectory("mc-accent-preview-").FullName;
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            AppSettings saved = NewInstallDefaults.Create();
            saved.General.AccentColor = AppAccentNames.Violet; // Existing explicit choice must survive the new default.
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
                var selector = Assert.IsType<ComboBox>(window.FindName("AccentColorSelector"));
                Assert.Equal("포인트 색", System.Windows.Automation.AutomationProperties.GetName(selector));
                Assert.Equal(AppAccentNames.Violet, selector.SelectedValue);
                Assert.False(Assert.IsType<SettingsDraft>(window.DataContext).HasUnappliedChanges);
                Assert.Null(window.FindName("ApplyButton"));
                SaveEvidence(window, "accent-violet.png");
                var oldBrush = Assert.IsType<SolidColorBrush>(survivor.Background);

                selector.SelectedValue = AppAccentNames.Red;
                Drain(window);
                AssertAccent(AppAccent.Red);
                Assert.False(Assert.IsType<SettingsDraft>(window.DataContext).HasUnappliedChanges);
                Assert.Equal(AppAccentNames.Red, saved.General.AccentColor);
                Assert.Equal(AppAccentNames.Red, store.Load().General.AccentColor);
                Assert.Equal(1, saves);
                Assert.True(oldBrush.IsFrozen);
                Assert.Equal(ToAccentColor(ThemeCatalog.ColorsFor(AppTheme.Glass)["Accent.Default"]), oldBrush.Color);
                Assert.NotSame(oldBrush, survivor.Background);
                Assert.Equal(ToAccentColor(ThemeCatalog.ColorsFor(AppTheme.Glass, AppAccent.Red)["Accent.Default"]),
                    Assert.IsType<SolidColorBrush>(survivor.Background).Color);
                SaveEvidence(window, "accent-red.png");

                var themeSelector = Assert.IsType<ComboBox>(window.FindName("ThemeSelector"));
                foreach (AppTheme theme in Enum.GetValues<AppTheme>())
                {
                    themeSelector.SelectedValue = AppThemeNames.ToSetting(theme);
                    Drain(window);
                    Assert.Equal(AppAccentNames.Red, selector.SelectedValue);
                    // The guide deliberately uses system colours for the explicit contrast theme.
                    if (theme != AppTheme.HighContrast) AssertAccent(AppAccent.Red);
                    else Assert.Equal(AppAccent.Red, ThemeService.CurrentAccent);
                }
                themeSelector.SelectedValue = AppThemeNames.Glass;
                Drain(window);

                ThemeService.ScheduleSystemThemeRefresh(window.Dispatcher, application.Resources, () => true);
                Drain(window);
                Assert.Equal(AppAccent.Red, ThemeService.CurrentAccent);
                ThemeService.ScheduleSystemThemeRefresh(window.Dispatcher, application.Resources, () => false);
                Drain(window);
                AssertAccent(AppAccent.Red);

                int beforeClose = saves;
                SettingsWindow.CancelCommand.Execute(null, window);
                AssertAccent(AppAccent.Red);
                SettingsDraft closedDraft = Assert.IsType<SettingsDraft>(window.DataContext);
                Assert.Equal(AppAccentNames.Red, closedDraft.AccentColor);
                Assert.False(closedDraft.HasUnappliedChanges);
                Assert.Equal(AppAccentNames.Red, saved.General.AccentColor);
                Assert.Equal(beforeClose, saves);
                window.ShowSettings();
                Drain(window);
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(beforeClose, saves);
                Assert.True(window.IsVisible);
                Assert.Equal(AppAccentNames.Red, store.Load().General.AccentColor);

                window.ShowSettings();
                SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
                draft.ResetToDefaults();
                Drain(window);
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppAccentNames.Red, saved.General.AccentColor);
                string exported = Path.Combine(root, "red-export.json");
                store.ExportTo(saved, exported);
                window.ImportSettingsFrom(exported);
                Drain(window);
                AssertAccent(AppAccent.Red);
                Assert.Equal(AppAccentNames.Red, selector.SelectedValue);

                selector.SelectedValue = AppAccentNames.Violet;
                failSave = true;
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(1, failures);
                Assert.True(window.IsVisible);
                AssertAccent(AppAccent.Violet);
                Assert.Equal(AppAccentNames.Red, saved.General.AccentColor);
                window.Close();
                Assert.True(window.IsVisible);
                Assert.Equal(AppAccentNames.Red, store.Load().General.AccentColor);
                failSave = false;
                window.Close();
                Assert.False(window.IsVisible);
                AssertAccent(AppAccent.Violet);
                Assert.Equal(AppAccentNames.Violet, store.Load().General.AccentColor);
                draft.AccentColor = AppAccentNames.Red;
                AssertAccent(AppAccent.Violet);
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
                var colors = ThemeCatalog.ColorsFor(ThemeService.Current, accent);
                AssertBrush((Brush)application.Resources["Accent.Default"], colors["Accent.Default"]);
                AssertBrush((Brush)application.Resources["CaptureGuide.SelectionBorder"], colors["Overlay.SelectionBorder"]);
            }
        });

    private static Color ToAccentColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
