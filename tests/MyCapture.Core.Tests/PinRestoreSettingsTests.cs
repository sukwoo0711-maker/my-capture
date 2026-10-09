using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Settings surface of closed-pin restore: the optional <c>RestoreClosedPin</c> hotkey and
/// the <c>ClosedWindowRestoreLimit</c> count, including older settings files without them.
/// </summary>
public sealed class PinRestoreSettingsTests
{
    private static readonly Hotkey CtrlAltF3 = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3);

    [Fact]
    public void Defaults_HotkeyUnassignedAndLimitTwenty()
    {
        var settings = new AppSettings();
        Assert.Equal(Hotkey.None, settings.Hotkeys.RestoreClosedPin);
        Assert.False(settings.Hotkeys.RestoreClosedPin.IsAssigned);
        Assert.Equal(20, settings.Pin.ClosedWindowRestoreLimit);

        var draft = new SettingsDraft(settings);
        Assert.Equal(string.Empty, draft.RestoreClosedPinHotkey);
        Assert.Equal("20", draft.ClosedWindowRestoreLimit);
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void OlderSettingsFile_WithoutNewFields_LoadsDefaults()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        string file = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "settings.json", ".json");
        File.WriteAllText(file, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","pasteToScreen":"F3","hideAllPins":"Shift+F3"},
             "pin":{"closeOnDoubleClick":true,"initialOpacity":1.0,"zoomStep":0.1}}
            """);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        Assert.Equal(Hotkey.None, loaded.Hotkeys.RestoreClosedPin);
        Assert.Equal(20, loaded.Pin.ClosedWindowRestoreLimit);
        Assert.Equal("F3", loaded.Hotkeys.PasteToScreen.ToString());
        Assert.False(store.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void StoreRoundTrip_PreservesHotkeyAndLimit()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        settings.Hotkeys.RestoreClosedPin = CtrlAltF3;
        settings.Pin.ClosedWindowRestoreLimit = 5;
        store.Save(settings);

        AppSettings loaded = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();

        Assert.Equal(CtrlAltF3, loaded.Hotkeys.RestoreClosedPin);
        Assert.Equal("Ctrl+Alt+F3", loaded.Hotkeys.RestoreClosedPin.ToString());
        Assert.Equal(5, loaded.Pin.ClosedWindowRestoreLimit);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(250, 100)]
    public void Store_ClampsOutOfRangeLimitWithWarning(int stored, int expected)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        settings.Pin.ClosedWindowRestoreLimit = stored;
        store.Save(settings);

        var reload = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal(expected, reload.Load().Pin.ClosedWindowRestoreLimit);
        Assert.Contains(reload.LastLoadWarnings, warning => warning.Contains(nameof(PinSettings.ClosedWindowRestoreLimit), StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeStoredHotkey_IsMigratedToUnassigned()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        string file = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "settings.json", ".json");
        File.WriteAllText(file, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","restoreClosedPin":"Shift+A"}}
            """);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        Assert.Equal(Hotkey.None, loaded.Hotkeys.RestoreClosedPin);
        Assert.True(store.LastLoadMigratedUnsafeHotkeys);
    }

    [Fact]
    public void DeepClone_CopiesRestoreHotkey()
    {
        var source = new AppSettings();
        source.Hotkeys.RestoreClosedPin = CtrlAltF3;

        AppSettings clone = source.DeepClone();
        source.Hotkeys.RestoreClosedPin = Hotkey.None;

        Assert.Equal(CtrlAltF3, clone.Hotkeys.RestoreClosedPin);
    }

    [Fact]
    public void Draft_MapsHotkeyAndLimitToSettingsAndHotkeySet()
    {
        var draft = new SettingsDraft(new AppSettings())
        {
            RestoreClosedPinHotkey = "Ctrl+Alt+F3",
            ClosedWindowRestoreLimit = "0",
        };

        Assert.False(draft.HasErrors);
        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(CtrlAltF3, mapped.Hotkeys.RestoreClosedPin);
        Assert.Equal(0, mapped.Pin.ClosedWindowRestoreLimit);
        Assert.Equal(CtrlAltF3, draft.ToHotkeySettings().RestoreClosedPin);

        var reloaded = new SettingsDraft(mapped);
        Assert.Equal("Ctrl+Alt+F3", reloaded.RestoreClosedPinHotkey);
        Assert.Equal("0", reloaded.ClosedWindowRestoreLimit);
    }

    [Fact]
    public void Draft_RejectsChordAlreadyUsedByPasteToScreen()
    {
        var draft = new SettingsDraft(new AppSettings()) { RestoreClosedPinHotkey = "F3" };

        Assert.True(draft.HasErrors);
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.RestoreClosedPinHotkey)).Cast<string>());
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.PasteToScreenHotkey)).Cast<string>());

        draft.RestoreClosedPinHotkey = string.Empty;
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void Draft_RejectsUnsafeGlobalChord()
    {
        var draft = new SettingsDraft(new AppSettings()) { RestoreClosedPinHotkey = "A" };
        Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.RestoreClosedPinHotkey)).Cast<string>());
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("100", true)]
    [InlineData("101", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Draft_ValidatesLimitAgainstSettingsRange(string value, bool valid)
    {
        var draft = new SettingsDraft(new AppSettings()) { ClosedWindowRestoreLimit = value };
        Assert.Equal(valid, !draft.GetErrors(nameof(SettingsDraft.ClosedWindowRestoreLimit)).Cast<string>().Any());
    }
}
