using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Settings surface of the optional <c>PauseRecording</c> hotkey: unassigned by default,
/// absent from older settings files, and carried through every settings copy path like the
/// other optional commands.
/// </summary>
public sealed class RecordingPauseHotkeySettingsTests
{
    private const uint VkP = 0x50;
    private static readonly Hotkey CtrlAltP = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VkP);

    [Fact]
    public void Defaults_PauseHotkeyIsUnassigned()
    {
        var settings = new AppSettings();
        Assert.Equal(Hotkey.None, settings.Hotkeys.PauseRecording);
        Assert.False(settings.Hotkeys.PauseRecording.IsAssigned);

        var draft = new SettingsDraft(settings);
        Assert.Equal(string.Empty, draft.PauseRecordingHotkey);
        Assert.False(draft.HasErrors);
        Assert.Equal(Hotkey.None, draft.ToHotkeySettings().PauseRecording);
    }

    [Fact]
    public void OlderSettingsFile_WithoutPauseHotkey_LoadsUnassignedWithoutMigration()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        string file = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "settings.json", ".json");
        File.WriteAllText(file, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","recordRegion":"Ctrl+Shift+X","restoreClosedPin":"Ctrl+Alt+F3"}}
            """);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        Assert.Equal(Hotkey.None, loaded.Hotkeys.PauseRecording);
        Assert.Equal("Ctrl+Shift+X", loaded.Hotkeys.RecordRegion.ToString());
        Assert.Equal("Ctrl+Alt+F3", loaded.Hotkeys.RestoreClosedPin.ToString());
        Assert.False(store.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void StoreRoundTrip_PreservesPauseHotkey()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        settings.Hotkeys.PauseRecording = CtrlAltP;
        store.Save(settings);

        string json = File.ReadAllText(ValidatedFilePaths.FileWithinRoot(workspace.Paths.DataRoot, "settings.json", ".json"));
        using (var document = System.Text.Json.JsonDocument.Parse(json))
        {
            Assert.Equal(
                "Ctrl+Alt+P",
                document.RootElement.GetProperty("hotkeys").GetProperty("pauseRecording").GetString());
        }

        var reload = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = reload.Load();
        Assert.Equal(CtrlAltP, loaded.Hotkeys.PauseRecording);
        Assert.Equal("Ctrl+Alt+P", loaded.Hotkeys.PauseRecording.ToString());
        Assert.False(reload.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void ExplicitlyUnassignedPauseHotkey_StaysUnassigned()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        string file = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "settings.json", ".json");
        File.WriteAllText(file, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","pauseRecording":""}}
            """);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

        Assert.Equal(Hotkey.None, store.Load().Hotkeys.PauseRecording);
        Assert.False(store.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void UnsafeStoredPauseHotkey_IsMigratedToUnassigned()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        string file = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "settings.json", ".json");
        File.WriteAllText(file, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","pauseRecording":"Shift+P"}}
            """);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        Assert.Equal(Hotkey.None, loaded.Hotkeys.PauseRecording);
        Assert.True(store.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void DeepClone_CopiesPauseHotkey()
    {
        var source = new AppSettings();
        source.Hotkeys.PauseRecording = CtrlAltP;

        AppSettings clone = source.DeepClone();
        source.Hotkeys.PauseRecording = Hotkey.None;

        Assert.Equal(CtrlAltP, clone.Hotkeys.PauseRecording);
    }

    [Fact]
    public void Draft_MapsPauseHotkeyToSettingsAndHotkeySet()
    {
        var draft = new SettingsDraft(new AppSettings()) { PauseRecordingHotkey = "Ctrl+Alt+P" };

        Assert.False(draft.HasErrors);
        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(CtrlAltP, mapped.Hotkeys.PauseRecording);
        Assert.Equal(CtrlAltP, draft.ToHotkeySettings().PauseRecording);

        var reloaded = new SettingsDraft(mapped);
        Assert.Equal("Ctrl+Alt+P", reloaded.PauseRecordingHotkey);

        reloaded.PauseRecordingHotkey = string.Empty;
        Assert.False(reloaded.HasErrors);
        Assert.Equal(Hotkey.None, reloaded.ToAppSettings().Hotkeys.PauseRecording);
    }

    [Fact]
    public void Draft_RejectsChordAlreadyUsedByRecordRegion()
    {
        var draft = new SettingsDraft(new AppSettings()) { PauseRecordingHotkey = "Ctrl+Shift+X" };

        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.PauseRecordingHotkey)).Cast<string>());
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.RecordRegionHotkey)).Cast<string>());

        draft.PauseRecordingHotkey = "Ctrl+Alt+P";
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void Draft_RejectsUnsafeGlobalChord()
    {
        var draft = new SettingsDraft(new AppSettings()) { PauseRecordingHotkey = "P" };

        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.PauseRecordingHotkey)).Cast<string>());
    }
}
