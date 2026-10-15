using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task AccentBrandSettingsLibraryAndEditorEvidence() =>
        RunIsolated(nameof(AccentBrandSettingsLibraryAndEditorEvidence), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            Application application = CreateThemedApplication();
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.SystemHighContrastOverrideForTest = false;
            string phase = Environment.GetEnvironmentVariable("MYCAPTURE_ACCENT_BRAND_PHASE") ?? "after";
            try
            {
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight })
                foreach (AppAccent accent in new[] { AppAccent.Violet, AppAccent.Red })
                {
                    var settings = NewInstallDefaults.Create();
                    settings.General.Theme = AppThemeNames.ToSetting(theme);
                    if (accent == AppAccent.Violet) settings.General.AccentColor = AppAccentNames.Violet;
                    else Assert.Equal(AppAccentNames.Red, settings.General.AccentColor);
                    ThemeService.ApplyFromSettings(settings.General);
                    string suffix = $"{AppThemeNames.ToSetting(theme)}-{AppAccentNames.ToSetting(accent)}-{phase}.png";
                    var window = CreateOffscreenSettingsWindow(settings);
                    window.Width = 1000;
                    window.Height = 820;
                    try
                    {
                        window.Show();
                        Drain(window);
                        Assert.Equal(settings.General.AccentColor, Assert.IsType<ComboBox>(window.FindName("AccentColorSelector")).SelectedValue);
                        Assert.Equal(AppAccentNames.Red,
                            Assert.IsType<ComboBoxItem>(Assert.IsType<ComboBox>(window.FindName("AccentColorSelector")).Items[0]).Tag);
                        SaveEvidence(window, "settings-" + suffix);
                    }
                    finally { window.CloseForExit(); }

                    LibraryUxFixture library = CreateLibraryUxFixture("mc-accent-brand-");
                    library.Window.Width = 1100;
                    library.Window.Height = 760;
                    try
                    {
                        library.Window.Show();
                        var rows = (ListBox)library.Window.FindName("RowsList");
                        PumpLibraryUntil(library.Window, () => FolderCardButtons(rows).Length == 2,
                            "accent library cards");
                        SaveEvidence(library.Window, "library-" + suffix);
                    }
                    finally { library.Window.CloseForExit(); }

                    var editor = EditorFlowWindow(1000, 640);
                    try
                    {
                        editor.Show();
                        Drain(editor);
                        SaveEvidence(editor, "editor-" + suffix);
                    }
                    finally { editor.Close(); }
                }
            }
            finally
            {
                ThemeService.SystemHighContrastOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }
        });
}
