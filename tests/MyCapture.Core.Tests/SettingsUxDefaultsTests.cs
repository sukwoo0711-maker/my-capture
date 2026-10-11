using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// New installations register only region capture and recording and hide key captions; files
/// written by earlier versions keep every chord and caption choice they already had.
/// </summary>
public sealed class SettingsUxDefaultsTests
{
    private static SettingsStore Store(TempWorkspace workspace) => new(workspace.Paths, NullLogger<SettingsStore>.Instance);

    private static Hotkey[] OptionalHotkeys(HotkeySettings keys) =>
    [
        keys.OpenLibrary, keys.PasteToScreen, keys.HideAllPins, keys.ToggleClickThrough, keys.RepeatLastRegion,
        keys.CaptureWindow, keys.CaptureFullScreen, keys.UploadGitHubImage, keys.ScrollingCapture, keys.DelayedCapture,
        keys.RestoreClosedPin, keys.CaptureText, keys.PauseRecording,
    ];

    [Fact]
    public void NewInstall_RegistersOnlyRegionCaptureAndRecording_AndHidesKeyCaptions()
    {
        using var workspace = new TempWorkspace();

        AppSettings fresh = Store(workspace).Load();

        Assert.True(fresh.General.IsFirstRun);
        Assert.Equal("Ctrl+Shift+C", fresh.Hotkeys.Capture.ToString());
        Assert.Equal("Ctrl+Shift+X", fresh.Hotkeys.RecordRegion.ToString());
        Assert.All(OptionalHotkeys(fresh.Hotkeys), key => Assert.Equal(Hotkey.None, key));
        Assert.False(fresh.General.ShowShortcutHints);
        Assert.False(File.Exists(workspace.Paths.SettingsFile));
    }

    [Fact]
    public void NewInstall_KeepsItsDefaultsAfterTheWelcomeWritesTheFirstFile()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = Store(workspace);
        AppSettings fresh = store.Load();

        store.CompleteFirstRun(fresh);
        AppSettings reloaded = Store(workspace).Load();

        Assert.False(reloaded.General.IsFirstRun);
        Assert.Equal("Ctrl+Shift+C", reloaded.Hotkeys.Capture.ToString());
        Assert.Equal("Ctrl+Shift+X", reloaded.Hotkeys.RecordRegion.ToString());
        Assert.All(OptionalHotkeys(reloaded.Hotkeys), key => Assert.Equal(Hotkey.None, key));
        Assert.False(reloaded.General.ShowShortcutHints);
    }

    [Fact]
    public void ExistingFile_WithTheEarlierDefaults_IsReadBackUnchangedAndNotRewritten()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,
             "hotkeys":{"capture":"Ctrl+Shift+C","openLibrary":"Ctrl+Shift+Z","pasteToScreen":"F3","hideAllPins":"Shift+F3",
                        "recordRegion":"Ctrl+Shift+X","uploadGitHubImage":"F9"},
             "general":{"showShortcutHints":true,"themeRevision":1},
             "storeRating":{"firstSeenUtc":"2026-01-01T00:00:00+00:00"}}
            """);
        string before = File.ReadAllText(workspace.Paths.SettingsFile);

        AppSettings loaded = Store(workspace).Load();

        Assert.False(loaded.General.IsFirstRun);
        Assert.Equal("Ctrl+Shift+Z", loaded.Hotkeys.OpenLibrary.ToString());
        Assert.Equal("F3", loaded.Hotkeys.PasteToScreen.ToString());
        Assert.Equal("Shift+F3", loaded.Hotkeys.HideAllPins.ToString());
        Assert.Equal("F9", loaded.Hotkeys.UploadGitHubImage.ToString());
        Assert.True(loaded.General.ShowShortcutHints);
        Assert.Equal(before, File.ReadAllText(workspace.Paths.SettingsFile));
    }

    [Fact]
    public void ExistingFile_WithoutTheProperties_KeepsTheEarlierDefaults()
    {
        // A file from a version that predates a property never stored it; that user has been
        // using the earlier default, so it must not disappear on upgrade.
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """{"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C"}}""");

        AppSettings loaded = Store(workspace).Load();

        Assert.Equal("Ctrl+Shift+Z", loaded.Hotkeys.OpenLibrary.ToString());
        Assert.Equal("F3", loaded.Hotkeys.PasteToScreen.ToString());
        Assert.Equal("Shift+F3", loaded.Hotkeys.HideAllPins.ToString());
        Assert.Equal("F9", loaded.Hotkeys.UploadGitHubImage.ToString());
        Assert.Equal("Ctrl+Shift+X", loaded.Hotkeys.RecordRegion.ToString());
        Assert.True(loaded.General.ShowShortcutHints);
    }

    [Fact]
    public void ExistingFile_KeepsUserChoices_InBothDirections()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = Store(workspace);
        AppSettings chosen = NewInstallDefaults.Create();
        chosen.Hotkeys.PasteToScreen = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3);
        chosen.General.ShowShortcutHints = true;
        store.Save(chosen);
        AppSettings unassigned = new() { Hotkeys = { OpenLibrary = Hotkey.None }, General = { ShowShortcutHints = false } };

        AppSettings loaded = Store(workspace).Load();
        store.Save(unassigned);
        AppSettings legacyOff = Store(workspace).Load();

        Assert.Equal("Ctrl+Alt+F3", loaded.Hotkeys.PasteToScreen.ToString());
        Assert.Equal(Hotkey.None, loaded.Hotkeys.OpenLibrary);
        Assert.True(loaded.General.ShowShortcutHints);
        Assert.Equal(Hotkey.None, legacyOff.Hotkeys.OpenLibrary);
        Assert.Equal("F3", legacyOff.Hotkeys.PasteToScreen.ToString());
        Assert.False(legacyOff.General.ShowShortcutHints);
    }

    [Fact]
    public void UnreadableExistingFile_BelongsToAnExistingUser_AndKeepsTheEarlierDefaults()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, "{ not json");

        AppSettings loaded = Store(workspace).Load();

        Assert.False(loaded.General.IsFirstRun);
        Assert.Equal("F3", loaded.Hotkeys.PasteToScreen.ToString());
        Assert.True(loaded.General.ShowShortcutHints);
    }

    [Fact]
    public void ResetToDefaults_UsesTheNewInstallDefaults()
    {
        var draft = new SettingsDraft(new AppSettings());
        Assert.Equal("F3", draft.PasteToScreenHotkey);
        Assert.True(draft.ShowShortcutHints);

        draft.ResetToDefaults();

        Assert.Equal("Ctrl+Shift+C", draft.CaptureHotkey);
        Assert.Equal("Ctrl+Shift+X", draft.RecordRegionHotkey);
        Assert.Equal(string.Empty, draft.OpenLibraryHotkey);
        Assert.Equal(string.Empty, draft.PasteToScreenHotkey);
        Assert.Equal(string.Empty, draft.HideAllPinsHotkey);
        Assert.Equal(string.Empty, draft.UploadGitHubImageHotkey);
        Assert.False(draft.ShowShortcutHints);
        Assert.False(draft.HasErrors);
        AppSettings mapped = draft.ToAppSettings();
        Assert.All(OptionalHotkeys(mapped.Hotkeys), key => Assert.Equal(Hotkey.None, key));
    }

    [Fact]
    public void UnappliedChanges_FollowValuesNotKeystrokes()
    {
        var draft = new SettingsDraft(new AppSettings());
        Assert.False(draft.HasUnappliedChanges);

        draft.MaxItems = "301";
        Assert.True(draft.HasUnappliedChanges);
        draft.MaxItems = "300";
        Assert.False(draft.HasUnappliedChanges);

        draft.GitHubToken = "ghp_example";
        Assert.True(draft.HasUnappliedChanges);
        draft.GitHubToken = string.Empty;
        Assert.False(draft.HasUnappliedChanges);

        draft.ResetToDefaults();
        Assert.True(draft.HasUnappliedChanges); // The reset is pending until Apply.

        draft.LoadFrom(new AppSettings());
        Assert.False(draft.HasUnappliedChanges); // Loading saved settings is the new baseline.

        draft.ReplaceValues(new AppSettings());
        Assert.False(draft.HasUnappliedChanges);
        draft.ReplaceValues(new AppSettings { Queue = { MaxItems = 321 } });
        Assert.True(draft.HasUnappliedChanges); // An import stays unapplied.
        Assert.Equal("321", draft.MaxItems);
    }

    [Fact]
    public void UnappliedChanges_CoverEveryEditableDraftProperty()
    {
        // A custom capture guide colour makes its hex field part of the saved value.
        var source = new AppSettings { General = { CaptureGuideColor = "#112233" }, Ocr = { PreferredLanguages = ["ko-KR"] } };
        PropertyInfo[] editable = typeof(SettingsDraft).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanWrite && property.SetMethod!.IsPublic)
            .ToArray();
        Assert.True(editable.Length >= 60, $"Only {editable.Length} editable properties were found.");

        foreach (PropertyInfo property in editable)
        {
            var draft = new SettingsDraft(source);
            object? original = property.GetValue(draft);
            object changed = Candidates(property.PropertyType, original)
                .FirstOrDefault(candidate => { property.SetValue(draft, candidate); return !Equals(property.GetValue(draft), original); })
                ?? throw new InvalidOperationException($"No different value could be assigned to {property.Name}.");

            Assert.True(draft.HasUnappliedChanges, $"{property.Name} = {changed} was not detected as a change.");
            property.SetValue(draft, original);
            Assert.False(draft.HasUnappliedChanges, $"{property.Name} restored to {original} still reads as changed.");
        }

        static IEnumerable<object> Candidates(Type type, object? original)
        {
            if (type == typeof(bool)) { yield return !(bool)original!; yield break; }
            if (type.IsEnum)
            {
                foreach (object value in Enum.GetValues(type)) yield return value;
                yield break;
            }
            string text = original as string ?? string.Empty;
            foreach (string candidate in new[] { text + "1", "7", "custom", "theme", "gdi", "accurate", "daylight",
                         "ko-KR", "Ctrl+Alt+F7", "#445566", "en-US" })
                yield return candidate;
        }
    }
}
