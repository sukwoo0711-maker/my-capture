using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class EditorTextPreferencesTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"annotation":{"fontFamily":"Malgun Gothic","fontSize":18}}""")]
    public void OlderSettings_KeepTextDefaultsWhenNewFieldsAreAbsent(string json)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;

        Assert.False(settings.Annotation.TextBold);
        Assert.False(settings.Annotation.TextBackground);
        Assert.True(settings.Annotation.TextOutline);
        Assert.Empty(settings.Annotation.RecentFontFamilies);
        Assert.Equal("Malgun Gothic", settings.Annotation.FontFamily);
        Assert.Equal(18, settings.Annotation.FontSize);
        Assert.Equal(4, settings.SchemaVersion);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void TextDefaults_SurviveSaveReloadAndExportImport(bool bold, bool background, bool outline)
    {
        AppPaths paths = CreateTestPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings
        {
            Annotation =
            {
                FontFamily = "Maple Mono",
                FontSize = 29,
                TextBold = bold,
                TextBackground = background,
                TextOutline = outline,
                RecentFontFamilies = ["Maple Mono", "Malgun Gothic"],
            },
        };

        store.Save(settings);
        AppSettings loaded = store.Load();
        string exportPath = Path.Combine(paths.DataRoot, "text-preferences.json");
        store.ExportTo(loaded, exportPath);
        AppSettings imported = store.ImportFrom(exportPath);

        foreach (AnnotationDefaults annotation in new[] { loaded.Annotation, imported.Annotation })
        {
            Assert.Equal("Maple Mono", annotation.FontFamily);
            Assert.Equal(29, annotation.FontSize);
            Assert.Equal(bold, annotation.TextBold);
            Assert.Equal(background, annotation.TextBackground);
            Assert.Equal(outline, annotation.TextOutline);
            Assert.Equal(["Maple Mono", "Malgun Gothic"], annotation.RecentFontFamilies);
        }
    }

    [Fact]
    public void DeepClone_CopiesTextDefaultsAndOwnsItsRecentFonts()
    {
        AppSettings source = CustomizedSettings();

        AnnotationDefaults clone = source.DeepClone().Annotation;

        Assert.True(clone.TextBold);
        Assert.True(clone.TextBackground);
        Assert.False(clone.TextOutline);
        Assert.Equal(source.Annotation.RecentFontFamilies, clone.RecentFontFamilies);
        Assert.NotSame(source.Annotation.RecentFontFamilies, clone.RecentFontFamilies);
        clone.RecentFontFamilies.Clear();
        Assert.Equal(["Maple Mono", "Consolas"], source.Annotation.RecentFontFamilies);
    }

    [Fact]
    public void SettingsDraft_PreservesEditorDefaultsWhenAnotherSettingChanges()
    {
        AppSettings source = CustomizedSettings();
        var draft = new SettingsDraft(source);
        draft.NotifyOnQuickSave = !draft.NotifyOnQuickSave;
        source.Annotation.RecentFontFamilies.Clear();

        AppSettings applied = draft.ToAppSettings();

        Assert.True(applied.Annotation.TextBold);
        Assert.True(applied.Annotation.TextBackground);
        Assert.False(applied.Annotation.TextOutline);
        Assert.Equal("Maple Mono", applied.Annotation.FontFamily);
        Assert.Equal(29, applied.Annotation.FontSize);
        Assert.Equal(["Maple Mono", "Consolas"], applied.Annotation.RecentFontFamilies);
        applied.Annotation.RecentFontFamilies.Clear();
        Assert.Equal(["Maple Mono", "Consolas"], draft.ToAppSettings().Annotation.RecentFontFamilies);
    }

    [Fact]
    public void SettingsDraft_ResetRestoresTextDefaultsWithoutMutatingLiveSettings()
    {
        AppSettings source = CustomizedSettings();
        var draft = new SettingsDraft(source);

        draft.ResetToDefaults();

        Assert.True(draft.HasUnappliedChanges);
        AnnotationDefaults reset = draft.ToAppSettings().Annotation;
        Assert.False(reset.TextBold);
        Assert.False(reset.TextBackground);
        Assert.True(reset.TextOutline);
        Assert.Empty(reset.RecentFontFamilies);
        Assert.True(source.Annotation.TextBold);
        Assert.True(source.Annotation.TextBackground);
        Assert.False(source.Annotation.TextOutline);
        Assert.Equal(["Maple Mono", "Consolas"], source.Annotation.RecentFontFamilies);
    }

    [Fact]
    public void RecentFonts_NullListLoadsAsEmpty()
    {
        const string json = """{"annotation":{"recentFontFamilies":null}}""";

        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;

        Assert.Empty(settings.Annotation.RecentFontFamilies);
        Assert.Empty(settings.DeepClone().Annotation.RecentFontFamilies);
    }

    [Fact]
    public void RecentFonts_LoadKeepsFiveDistinctNonEmptyNamesInOrder()
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """
            {
              "annotation": {
                "recentFontFamilies": [" Maple Mono ", "maple mono", null, "", " ", "Consolas",
                  "Malgun Gothic", "Segoe UI", "Arial", "Courier New"]
              }
            }
            """);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

        AppSettings loaded = store.Load();

        Assert.Equal(["Maple Mono", "Consolas", "Malgun Gothic", "Segoe UI", "Arial"],
            loaded.Annotation.RecentFontFamilies);
    }

    private static AppSettings CustomizedSettings() => new()
    {
        Annotation =
        {
            FontFamily = "Maple Mono",
            FontSize = 29,
            TextBold = true,
            TextBackground = true,
            TextOutline = false,
            RecentFontFamilies = ["Maple Mono", "Consolas"],
        },
    };

    private static AppPaths CreateTestPaths() => AppPaths.CreateForRoot(Path.Combine(
        AppContext.BaseDirectory, "editor-text-preference-fixtures", Guid.NewGuid().ToString("N")));
}
