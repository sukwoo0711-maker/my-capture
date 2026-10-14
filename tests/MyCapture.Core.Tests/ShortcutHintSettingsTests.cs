using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ShortcutHintSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"general":{}}""")]
    [InlineData("""{"schemaVersion":4,"general":{"showShortcutHints":false}}""")]
    [InlineData("""{"schemaVersion":4,"general":{"showShortcutHints":true}}""")]
    public void LegacySettings_ShowHintsOnLoadAndImportWithoutRewritingTheFile(string json)
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, json);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

        foreach (AppSettings settings in new[] { store.Load(), store.ImportFrom(paths.SettingsFile) })
        {
            Assert.True(settings.General.ShowShortcutHints);
            Assert.Equal(1, settings.General.ShortcutHintsRevision);
        }
        Assert.Equal(json, File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentChoices_SurviveCloneDraftSaveLoadAndExportImport(bool show)
    {
        AppPaths paths = CreateTestPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var source = new AppSettings { General = { ShowShortcutHints = show } };
        AppSettings clone = source.DeepClone();
        var draft = new SettingsDraft(clone) { NotifyOnQuickSave = false };
        AppSettings applied = draft.ToAppSettings();

        store.Save(applied);
        AppSettings loaded = store.Load();
        string exportedPath = Path.Combine(paths.DataRoot, "hint-preferences.json");
        store.ExportTo(loaded, exportedPath);
        AppSettings imported = store.ImportFrom(exportedPath);

        foreach (AppSettings settings in new[] { source, clone, applied, loaded, imported })
        {
            Assert.Equal(show, settings.General.ShowShortcutHints);
            Assert.Equal(1, settings.General.ShortcutHintsRevision);
        }
    }

    [Fact]
    public void TurningHintsOffAfterMigration_RemainsOffAfterReload()
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """{"general":{"showShortcutHints":false}}""");
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings migrated = store.Load();
        Assert.True(migrated.General.ShowShortcutHints);

        var draft = new SettingsDraft(migrated) { ShowShortcutHints = false };
        store.Save(draft.ToAppSettings());

        Assert.False(store.Load().General.ShowShortcutHints);
    }

    [Fact]
    public void JsonAndClone_PreserveTheDifferenceBetweenLegacyAndCurrentChoices()
    {
        AppSettings legacy = JsonSerializer.Deserialize<AppSettings>(
            """{"general":{"showShortcutHints":false}}""", JsonDefaults.Readable)!;
        Assert.Equal(0, legacy.General.ShortcutHintsRevision);
        Assert.Equal(0, legacy.DeepClone().General.ShortcutHintsRevision);

        var current = new AppSettings { General = { ShowShortcutHints = false } };
        string json = JsonSerializer.Serialize(current, JsonDefaults.Readable);
        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        Assert.Equal(1, reloaded.General.ShortcutHintsRevision);
        Assert.False(reloaded.General.ShowShortcutHints);
    }

    private static AppPaths CreateTestPaths() => AppPaths.CreateForRoot(Path.Combine(
        AppContext.BaseDirectory, "shortcut-hint-preference-fixtures", Guid.NewGuid().ToString("N")));
}
