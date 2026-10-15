using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

public sealed class AccentColorTests(ITestOutputHelper output)
{
    [Fact]
    public void NewSettingsAndResetUseRedAndExplicitVioletRemainsViolet()
    {
        Assert.Equal(AppAccentNames.Red, new GeneralSettings().AccentColor);
        Assert.Equal(AppAccentNames.Red, NewInstallDefaults.Create().General.AccentColor);
        var draft = new SettingsDraft(new AppSettings { General = { AccentColor = AppAccentNames.Violet } });
        draft.ResetToDefaults();
        Assert.Equal(AppAccentNames.Red, draft.AccentColor);
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(AppAccent.Red, AppAccentNames.Parse(null));
        Assert.Equal(AppAccent.Red, AppAccentNames.Parse("unknown"));
        Assert.Equal(AppAccent.Violet, AppAccentNames.Parse("Violet"));
        Assert.Equal(AppAccentNames.Red, AppAccentNames.ToSetting((AppAccent)99));
    }

    [Fact]
    public void PinFillAndEveryNonTextRoleMeetContrastOnActualThemeSurfaces()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>().Where(theme => theme != AppTheme.HighContrast))
        {
            var colors = ThemeCatalog.ColorsFor(theme, AppAccent.Red);
            output.WriteLine($"{theme}: foreground {colors["Accent.Cool"]}, indicator {colors["Accent.Indicator"]}");
            Assert.Equal(ThemeColor.Rgb(0xE0, 0x45, 0x3C), colors["Accent.Default"]);
            Assert.Equal(ThemeColor.Rgb(0xF3, 0x5B, 0x4D), colors["Accent.Hover"]);
            Check(colors["Overlay.HandleStroke"], colors["Overlay.HandleFill"], 3, $"{theme} handle");
            foreach (string surface in new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay",
                         "Surface.Floating", "Surface.Sunken", "Surface.Hover", "Surface.Pressed", "Accent.Subtle" })
            foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
            foreach (string role in new[] { "Accent.Indicator", "Border.Focus", "Border.Accent", "Overlay.SelectionBorder" })
                Check(colors[role], ThemeContrast.Composite(colors[surface], backdrop), 3, $"{theme}/{role}/{surface}");
        }
    }

    [Theory]
    [InlineData(null, "red")]
    [InlineData("unknown", "red")]
    [InlineData("Violet", "violet")]
    [InlineData("Red", "red")]
    public void LoadPreservesExplicitChoiceAndUsesRedForAbsentOrUnknown(string? accent, string expected)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-accent-load-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        string field = accent is null ? string.Empty : $",\"accentColor\":\"{accent}\"";
        File.WriteAllText(paths.SettingsFile, "{\"general\":{\"isFirstRun\":false,\"themeRevision\":1" + field + "}}");
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal(expected, store.Load().General.AccentColor);
        Assert.Equal(expected, store.Load().General.AccentColor);
    }

    [Theory]
    [InlineData("ko-KR", "포인트 색", "바이올렛", "레드")]
    [InlineData("en-US", "Accent color", "Violet", "Red")]
    public void AccentChoiceHasLocalizedCopy(string language, string label, string violet, string red)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(label, UiText.Get("Settings.AccentColor"));
        Assert.Equal(violet, UiText.Get("Settings.AccentViolet"));
        Assert.Equal(red, UiText.Get("Settings.AccentRed"));
    }

    [Fact]
    public void EveryThemeAndAccentKeepsFillTextAndFocusReadableAndRedForegroundMeetsAa()
    {
        ThemeColor[] backdrops = [ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255)];
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
        {
            var colors = ThemeCatalog.ColorsFor(theme, accent);
            foreach (string fill in new[] { "Accent.Default", "Accent.Hover", "Accent.Pressed", "Accent.Gradient" })
                Check(colors["Text.OnAccent"], colors[fill], 4.5, $"{theme}/{accent} text on {fill}");
            foreach (string surface in new[] { "Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Overlay", "Accent.Subtle" })
            foreach (ThemeColor backdrop in backdrops)
            {
                ThemeColor composite = ThemeContrast.Composite(colors[surface], backdrop);
                if (accent == AppAccent.Red)
                    Check(colors["Accent.Cool"], composite, 4.5, $"{theme}/{accent} foreground on {surface}");
                else
                {
                    double legacy = ThemeContrast.RatioOverBackdrop(ThemeCatalog.ColorsFor(theme)["Accent.Cool"],
                        ThemeCatalog.ColorsFor(theme)[surface], backdrop);
                    Assert.Equal(legacy, ThemeContrast.Ratio(colors["Accent.Cool"], composite));
                    output.WriteLine($"{theme}/Violet foreground on {surface}: {legacy:F3}:1 (unchanged)");
                }
                Check(colors["Border.Focus"], composite, 3, $"{theme}/{accent} focus on {surface}");
            }
            // Red also covers state surfaces and floating chrome. Violet's shipped colours
            // are deliberately unchanged; its regression oracle below locks every old token.
            if (accent != AppAccent.Red || theme == AppTheme.HighContrast) continue;
            foreach (string surface in new[] { "Surface.Hover", "Surface.Pressed", "Surface.Sunken", "Surface.Floating" })
            foreach (ThemeColor backdrop in backdrops)
            {
                ThemeColor composite = ThemeContrast.Composite(colors[surface], backdrop);
                Check(colors["Accent.Cool"], composite, 4.5, $"{theme}/{accent} foreground on {surface}");
                Check(colors["Border.Focus"], composite, 3, $"{theme}/{accent} focus on {surface}");
            }
        }
    }

    [Fact]
    public void VioletReturnsEveryOriginalTokenAndHighContrastIgnoresAccent()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            Assert.Same(ThemeCatalog.ColorsFor(theme), ThemeCatalog.ColorsFor(theme, AppAccent.Violet));
            Assert.Equal(CaptureGuideTokens.ColorsFor(theme), CaptureGuideTokens.ColorsFor(theme, accentColor: AppAccent.Violet));
        }
        Assert.Same(ThemeCatalog.ColorsFor(AppTheme.HighContrast), ThemeCatalog.ColorsFor(AppTheme.HighContrast, AppAccent.Red));
    }

    [Fact]
    public void GuideTracksAccentUnlessCustomColorIsSelected()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        foreach (AppAccent accent in Enum.GetValues<AppAccent>())
        {
            var guide = CaptureGuideTokens.ColorsFor(theme, accentColor: accent);
            Assert.Equal(ThemeCatalog.ColorsFor(theme, accent)["Overlay.SelectionBorder"], guide["CaptureGuide.SelectionBorder"]);
            Check(guide["CaptureGuide.AccentText"], guide["CaptureGuide.SelectionBorder"], 4.5, $"{theme}/{accent} guide label");
            Assert.Equal(ThemeColor.Rgb(0x12, 0x34, 0x56),
                CaptureGuideTokens.ColorsFor(theme, "#123456", accent)["CaptureGuide.SelectionBorder"]);
        }
    }

    [Theory]
    [InlineData(null, "red")]
    [InlineData("unknown", "red")]
    [InlineData("RED", "red")]
    [InlineData("violet", "violet")]
    public void ImportNormalizesMissingOrUnknownAccentWithoutChangingTheme(string? accent, string expected)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-accent-import-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        string import = Path.Combine(root, "import.json");
        string field = accent is null ? string.Empty : $",\"accentColor\":\"{accent}\"";
        File.WriteAllText(import, "{\"general\":{\"theme\":\"daylight\",\"themeRevision\":1" + field + "}}");
        AppSettings settings = store.ImportFrom(import);
        Assert.Equal(expected, settings.General.AccentColor);
        Assert.Equal(AppThemeNames.Daylight, settings.General.Theme);
        store.Save(settings);
        Assert.Equal(expected, store.Load().General.AccentColor);
        // Retain the isolated fixture; no real profile data or permanent cleanup.
    }

    [Fact]
    public void AccentSurvivesCloneDraftSaveExportImportAndResetsToRed()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-accent-settings-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings { General = { AccentColor = AppAccentNames.Red } };
        var draft = new SettingsDraft(settings.DeepClone());
        Assert.Equal(AppAccentNames.Red, draft.AccentColor);
        Assert.False(draft.HasUnappliedChanges);
        draft.AccentColor = AppAccentNames.Violet;
        Assert.True(draft.HasUnappliedChanges);
        draft.AccentColor = AppAccentNames.Red;
        Assert.False(draft.HasUnappliedChanges);
        store.Save(draft.ToAppSettings());
        Assert.Equal(AppAccentNames.Red, store.Load().General.AccentColor);
        string export = Path.Combine(root, "export.json");
        store.ExportTo(draft.ToAppSettings(), export);
        Assert.Equal(AppAccentNames.Red, store.ImportFrom(export).General.AccentColor);
        draft.ResetToDefaults();
        Assert.Equal(AppAccentNames.Red, draft.AccentColor);
        Assert.Equal(AppAccentNames.Red, draft.ToAppSettings().General.AccentColor);
        Assert.True(draft.HasUnappliedChanges);
    }

    [Theory]
    [InlineData(AppAccentNames.Violet, AppAccentNames.Red)]
    [InlineData(AppAccentNames.Red, AppAccentNames.Violet)]
    public void AccentChangesRemainUnappliedUntilReloadedAndRestoreTheSavedBaseline(string savedAccent, string editedAccent)
    {
        var saved = new AppSettings { General = { AccentColor = savedAccent } };
        var draft = new SettingsDraft(saved);
        Assert.False(draft.HasUnappliedChanges);

        draft.AccentColor = editedAccent;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(savedAccent, saved.General.AccentColor);

        draft.AccentColor = savedAccent;
        Assert.False(draft.HasUnappliedChanges);

        draft.ReplaceValues(new AppSettings { General = { AccentColor = editedAccent } });
        Assert.True(draft.HasUnappliedChanges);
        draft.LoadFrom(saved);
        Assert.Equal(savedAccent, draft.AccentColor);
        Assert.False(draft.HasUnappliedChanges);

        draft.AccentColor = editedAccent;
        draft.LoadFrom(draft.ToAppSettings());
        Assert.False(draft.HasUnappliedChanges);
        draft.AccentColor = savedAccent;
        Assert.True(draft.HasUnappliedChanges);
    }

    private void Check(ThemeColor foreground, ThemeColor background, double minimum, string pair)
    {
        double ratio = ThemeContrast.Ratio(foreground, background);
        output.WriteLine($"{pair}: {ratio:F3}:1");
        Assert.True(ratio >= minimum, $"{pair}: {ratio:F3}:1 < {minimum}:1");
    }
}
