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
            Assert.False(settings.General.AlwaysShowShortcutHints);
            Assert.Equal(1, settings.General.ShortcutHintsRevision);
        }
        Assert.Equal(json, File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CurrentChoices_SurviveCloneDraftSaveLoadAndExportImport(bool show, bool alwaysShow)
    {
        AppPaths paths = CreateTestPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var source = new AppSettings { General = { ShowShortcutHints = show, AlwaysShowShortcutHints = alwaysShow } };
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
            Assert.Equal(alwaysShow, settings.General.AlwaysShowShortcutHints);
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

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void MissingAlwaysChoice_DefaultsOffRegardlessOfLegacyChoiceAndRevision(bool legacyShow, int revision)
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        string marker = revision == 0 ? string.Empty : ",\"shortcutHintsRevision\":1";
        string json = "{\"schemaVersion\":4,\"general\":{\"showShortcutHints\":"
            + legacyShow.ToString().ToLowerInvariant() + marker + "}}";
        File.WriteAllText(paths.SettingsFile, json);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

        foreach (AppSettings settings in new[] { store.Load(), store.ImportFrom(paths.SettingsFile) })
        {
            Assert.False(settings.General.AlwaysShowShortcutHints);
            Assert.Equal(revision == 0 || legacyShow, settings.General.ShowShortcutHints);
        }
        Assert.Equal(json, File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitAlwaysChoice_SurvivesLegacyMigrationWithoutRevision(bool alwaysShow)
    {
        AppPaths paths = CreateTestPaths();
        paths.EnsureCreated();
        string json = "{\"general\":{\"showShortcutHints\":false,\"alwaysShowShortcutHints\":"
            + alwaysShow.ToString().ToLowerInvariant() + "}}";
        File.WriteAllText(paths.SettingsFile, json);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();
        Assert.True(loaded.General.ShowShortcutHints);
        Assert.Equal(alwaysShow, loaded.General.AlwaysShowShortcutHints);
        Assert.Equal(alwaysShow, store.ImportFrom(paths.SettingsFile).General.AlwaysShowShortcutHints);
        store.Save(loaded);
        Assert.Equal(alwaysShow, store.Load().General.AlwaysShowShortcutHints);
    }

    [Fact]
    public void AlwaysChoice_DraftTracksEditsReloadAndResetWithoutChangingLegacyChoice()
    {
        var source = new AppSettings { General = { ShowShortcutHints = true } };
        var draft = new SettingsDraft(source);
        Assert.False(draft.AlwaysShowShortcutHints);
        Assert.False(draft.HasUnappliedChanges);
        var notifications = new List<string?>();
        draft.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        draft.AlwaysShowShortcutHints = true;
        Assert.Contains(nameof(SettingsDraft.AlwaysShowShortcutHints), notifications);
        Assert.True(draft.HasUnappliedChanges);
        Assert.True(draft.ToAppSettings().General.AlwaysShowShortcutHints);
        Assert.True(draft.ToAppSettings().General.ShowShortcutHints);
        Assert.False(source.General.AlwaysShowShortcutHints);

        draft.AlwaysShowShortcutHints = false;
        Assert.False(draft.HasUnappliedChanges);
        source.General.AlwaysShowShortcutHints = true;
        draft.LoadFrom(source);
        Assert.True(draft.AlwaysShowShortcutHints);
        Assert.False(draft.HasUnappliedChanges);
        draft.ResetToDefaults();
        Assert.False(draft.AlwaysShowShortcutHints);
        Assert.False(draft.ToAppSettings().General.AlwaysShowShortcutHints);
        Assert.True(draft.HasUnappliedChanges);
    }

    [Fact]
    public void AlwaysChoice_IsOffForNewInstallPropertyDefaultsAndReset()
    {
        var settings = new AppSettings();
        Assert.False(settings.General.AlwaysShowShortcutHints);
        Assert.False(NewInstallDefaults.Create().General.AlwaysShowShortcutHints);
        Assert.False(new SettingsStore(CreateTestPaths(), NullLogger<SettingsStore>.Instance).Load().General.AlwaysShowShortcutHints);
        settings.General.AlwaysShowShortcutHints = true;
        NewInstallDefaults.Apply(settings);
        Assert.False(settings.General.AlwaysShowShortcutHints);
    }

    private static AppPaths CreateTestPaths() => AppPaths.CreateForRoot(Path.Combine(
        AppContext.BaseDirectory, "shortcut-hint-preference-fixtures", Guid.NewGuid().ToString("N")));
}
