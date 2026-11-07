using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Core-shortcut consent and its persistence through every settings representation.</summary>
public sealed class CoreShortcutSettingsTests
{
    [Fact]
    public void Chords_AreTheFourExactCoreAssignmentsAndCannotBeChanged()
    {
        Assert.Equal(new[] { "Ctrl+Shift+C", "Ctrl+Shift+X", "Ctrl+Shift+Z", "F3" },
            CoreShortcuts.Chords.Select(hotkey => hotkey.ToString()));
        Assert.All(CoreShortcuts.Chords, hotkey => Assert.True(CoreShortcuts.IsCoreChord(hotkey)));
        Assert.False(CoreShortcuts.IsCoreChord(Hotkey.None));
        Assert.False(CoreShortcuts.IsCoreChord(new(HotkeyModifiers.Shift, Hotkey.VkF3)));
        Assert.False(CoreShortcuts.IsCoreChord(new(HotkeyModifiers.Control, Hotkey.VkC)));
        Assert.Throws<NotSupportedException>(() => ((IList<Hotkey>)CoreShortcuts.Chords)[0] = Hotkey.None);
    }

    [Fact]
    public void Accept_AssignsCoreCommandsClearsEveryOtherConflictAndRecordsConsent()
    {
        var settings = new HotkeySettings();
        PropertyInfo[] chords = typeof(HotkeySettings).GetProperties()
            .Where(property => property.PropertyType == typeof(Hotkey)).ToArray();
        for (int index = 0; index < chords.Length; index++)
            chords[index].SetValue(settings, CoreShortcuts.Chords[index % CoreShortcuts.Chords.Count]);

        CoreShortcuts.Accept(settings);

        Assert.Equal(CoreShortcuts.Capture, settings.Capture);
        Assert.Equal(CoreShortcuts.RecordRegion, settings.RecordRegion);
        Assert.Equal(CoreShortcuts.OpenLibrary, settings.OpenLibrary);
        Assert.Equal(CoreShortcuts.PasteToScreen, settings.PasteToScreen);
        string[] coreCommands = [nameof(HotkeySettings.Capture), nameof(HotkeySettings.RecordRegion),
            nameof(HotkeySettings.OpenLibrary), nameof(HotkeySettings.PasteToScreen)];
        Assert.All(chords.Where(property => !coreCommands.Contains(property.Name)),
            property => Assert.Equal(Hotkey.None, property.GetValue(settings)));
        Assert.True(settings.ForceCoreShortcuts);
        Assert.True(settings.CoreShortcutsAsked);
    }

    [Fact]
    public void Accept_PreservesOtherChords()
    {
        var settings = new HotkeySettings();
        Hotkey hideAllPins = settings.HideAllPins;
        Hotkey uploadGitHubImage = settings.UploadGitHubImage;

        CoreShortcuts.Accept(settings);

        Assert.Equal(hideAllPins, settings.HideAllPins);
        Assert.Equal(uploadGitHubImage, settings.UploadGitHubImage);
    }

    [Fact]
    public void LegacySettings_DefaultBothFlagsToFalse()
    {
        var settings = new AppSettings();

        Assert.False(settings.Hotkeys.ForceCoreShortcuts);
        Assert.False(settings.Hotkeys.CoreShortcutsAsked);
    }

    [Fact]
    public void NewInstall_AssignsAcceptedCoreShortcutsAndClearsConflicts()
    {
        var settings = new AppSettings();
        PropertyInfo[] chords = typeof(HotkeySettings).GetProperties()
            .Where(property => property.PropertyType == typeof(Hotkey)).ToArray();
        for (int index = 0; index < chords.Length; index++)
            chords[index].SetValue(settings.Hotkeys, CoreShortcuts.Chords[index % CoreShortcuts.Chords.Count]);

        NewInstallDefaults.Apply(settings);

        var accepted = new HotkeySettings();
        CoreShortcuts.Accept(accepted);
        Assert.Equal(accepted.Capture, settings.Hotkeys.Capture);
        Assert.Equal(accepted.RecordRegion, settings.Hotkeys.RecordRegion);
        Assert.Equal(accepted.OpenLibrary, settings.Hotkeys.OpenLibrary);
        Assert.Equal(accepted.PasteToScreen, settings.Hotkeys.PasteToScreen);
        Assert.Equal(accepted.ForceCoreShortcuts, settings.Hotkeys.ForceCoreShortcuts);
        Assert.Equal(accepted.CoreShortcutsAsked, settings.Hotkeys.CoreShortcutsAsked);
        string[] coreCommands = [nameof(HotkeySettings.Capture), nameof(HotkeySettings.RecordRegion),
            nameof(HotkeySettings.OpenLibrary), nameof(HotkeySettings.PasteToScreen)];
        Assert.All(chords.Where(property => !coreCommands.Contains(property.Name)),
            property => Assert.False(CoreShortcuts.IsCoreChord((Hotkey)property.GetValue(settings.Hotkeys)!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decline_RecordsTheAnswerAndTurnsPriorityOffWithoutChangingAKey(bool force)
    {
        HotkeySettings settings = NewInstallDefaults.Create().Hotkeys;
        settings.ForceCoreShortcuts = force;
        settings.CoreShortcutsAsked = false;
        settings.CaptureText = CoreShortcuts.PasteToScreen;
        var before = typeof(HotkeySettings).GetProperties()
            .Where(property => property.Name is not (nameof(HotkeySettings.CoreShortcutsAsked)
                or nameof(HotkeySettings.ForceCoreShortcuts)))
            .ToDictionary(property => property, property => property.GetValue(settings));

        CoreShortcuts.Decline(settings);

        Assert.True(settings.CoreShortcutsAsked);
        // "Practise with current keys" leaves other programs their keys, whatever priority was before.
        Assert.False(settings.ForceCoreShortcuts);
        Assert.All(before, pair => Assert.Equal(pair.Value, pair.Key.GetValue(settings)));
    }

    [Fact]
    public void Store_OlderFileDefaultsBothFlagsToFalseAndRoundTripsConsent()
    {
        using var workspace = new TempWorkspace();
        AppPaths paths = workspace.Paths;
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """
            {"schemaVersion":4,"hotkeys":{"capture":"Ctrl+Shift+C","recordRegion":"Ctrl+Shift+X"}}
            """);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        Assert.False(settings.Hotkeys.ForceCoreShortcuts);
        Assert.False(settings.Hotkeys.CoreShortcutsAsked);

        CoreShortcuts.Accept(settings.Hotkeys);
        store.Save(settings);
        AppSettings reloaded = new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load();

        Assert.True(reloaded.Hotkeys.ForceCoreShortcuts);
        Assert.True(reloaded.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(CoreShortcuts.OpenLibrary, reloaded.Hotkeys.OpenLibrary);
        Assert.Equal(CoreShortcuts.PasteToScreen, reloaded.Hotkeys.PasteToScreen);
    }

    [Fact]
    public void Clone_CopiesBothFlagsIndependently()
    {
        var source = new AppSettings { Hotkeys = { ForceCoreShortcuts = true, CoreShortcutsAsked = true } };

        AppSettings clone = source.DeepClone();
        source.Hotkeys.ForceCoreShortcuts = false;
        source.Hotkeys.CoreShortcutsAsked = false;

        Assert.True(clone.Hotkeys.ForceCoreShortcuts);
        Assert.True(clone.Hotkeys.CoreShortcutsAsked);
    }

    [Fact]
    public void Draft_PreservesTheAnswerAndTracksForceEditsWithoutAssigningKeys()
    {
        AppSettings source = NewInstallDefaults.Create();
        source.Hotkeys.ForceCoreShortcuts = false;
        source.Hotkeys.OpenLibrary = Hotkey.None;
        source.Hotkeys.CoreShortcutsAsked = true;
        var draft = new SettingsDraft(source);
        var notifications = new List<string?>();
        draft.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        Assert.False(draft.HasUnappliedChanges);

        draft.ForceCoreShortcuts = true;

        Assert.Contains(nameof(SettingsDraft.ForceCoreShortcuts), notifications);
        Assert.True(draft.HasUnappliedChanges);
        AppSettings mapped = draft.ToAppSettings();
        Assert.True(mapped.Hotkeys.ForceCoreShortcuts);
        Assert.True(mapped.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(Hotkey.None, mapped.Hotkeys.OpenLibrary);
        // Toggling force never assigns keys: the new-install default (F3 since 4.5.0) stays as it was.
        Assert.Equal(NewInstallDefaults.Create().Hotkeys.PasteToScreen, mapped.Hotkeys.PasteToScreen);
        Assert.True(draft.ToHotkeySettings().ForceCoreShortcuts);
        Assert.True(draft.ToHotkeySettings().CoreShortcutsAsked);
        draft.LoadFrom(mapped);
        Assert.True(draft.ForceCoreShortcuts);
        Assert.False(draft.HasUnappliedChanges);
        draft.ForceCoreShortcuts = false;
        Assert.True(draft.HasUnappliedChanges);
        Assert.False(draft.ToAppSettings().Hotkeys.ForceCoreShortcuts);
        Assert.True(draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Draft_ResetToDefaultsPreservesTheAnswer(bool asked, bool force)
    {
        var source = new AppSettings
        {
            Hotkeys = { ForceCoreShortcuts = force, CoreShortcutsAsked = asked },
        };
        var draft = new SettingsDraft(source);

        draft.ResetToDefaults();

        Assert.Equal(force, draft.ForceCoreShortcuts);
        Assert.Equal(force, draft.ToAppSettings().Hotkeys.ForceCoreShortcuts);
        Assert.Equal(force, draft.ToHotkeySettings().ForceCoreShortcuts);
        Assert.Equal(asked, draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
        Assert.Equal(asked, draft.ToHotkeySettings().CoreShortcutsAsked);
        Assert.Equal(CoreShortcuts.OpenLibrary, draft.ToHotkeySettings().OpenLibrary);
        Assert.Equal(CoreShortcuts.Capture, draft.ToHotkeySettings().Capture);
        Assert.Equal(CoreShortcuts.RecordRegion, draft.ToHotkeySettings().RecordRegion);
        Assert.Equal(NewInstallDefaults.Create().Hotkeys.PasteToScreen, draft.ToHotkeySettings().PasteToScreen);
    }

    [Fact]
    public void Draft_ResetKeepsPriorityOffForAUserWhoKeptTheirKeys()
    {
        // An upgrade answered "practise with current keys". Resetting must not start taking the core
        // keys from another program that holds one, such as F3.
        var declined = new AppSettings();
        CoreShortcuts.Decline(declined.Hotkeys);
        var draft = new SettingsDraft(declined);

        draft.ResetToDefaults();

        Assert.False(draft.ForceCoreShortcuts);
        AppSettings reset = draft.ToAppSettings();
        Assert.False(reset.Hotkeys.ForceCoreShortcuts);
        Assert.True(reset.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(CoreShortcuts.PasteToScreen, reset.Hotkeys.PasteToScreen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Draft_ResetBeforeAnUpgradeAnswersLeavesPriorityToTheQuestion(bool accepted)
    {
        // Settings opens from the tray while the upgrade's question is still waiting for an answer.
        var unanswered = new AppSettings();
        var draft = new SettingsDraft(unanswered);

        draft.ResetToDefaults();
        AppSettings reset = draft.ToAppSettings();
        Assert.False(reset.Hotkeys.CoreShortcutsAsked);
        Assert.False(reset.Hotkeys.ForceCoreShortcuts);
        if (accepted) CoreShortcuts.Accept(reset.Hotkeys);
        else CoreShortcuts.Decline(reset.Hotkeys);

        Assert.True(reset.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(accepted, reset.Hotkeys.ForceCoreShortcuts);
    }

    [Fact]
    public void Draft_ResetKeepsPriorityOnForANewInstallation()
    {
        using var workspace = new TempWorkspace();
        AppSettings installed = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();
        Assert.True(installed.General.IsFirstRun);
        installed.Hotkeys.Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkC);
        var draft = new SettingsDraft(installed);

        draft.ResetToDefaults();

        Assert.True(draft.ForceCoreShortcuts);
        AppSettings reset = draft.ToAppSettings();
        Assert.True(reset.Hotkeys.ForceCoreShortcuts);
        Assert.True(reset.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(CoreShortcuts.Capture, reset.Hotkeys.Capture);
    }

    [Fact]
    public void Draft_ReplacementIncludesTheAnswerInChangeTracking()
    {
        var source = new AppSettings();
        var draft = new SettingsDraft(source);
        source.Hotkeys.CoreShortcutsAsked = true;

        draft.ReplaceValues(source);

        Assert.True(draft.HasUnappliedChanges);
        Assert.True(draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
    }
}
