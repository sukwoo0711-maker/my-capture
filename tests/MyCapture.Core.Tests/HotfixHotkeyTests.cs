using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class HotfixHotkeyTests
{
    [Theory]
    [InlineData("Shift+A", false)]
    [InlineData("Shift+1", false)]
    [InlineData("Shift+Space", false)]
    [InlineData("Shift+Enter", false)]
    [InlineData("Shift+Left", false)]
    [InlineData("Shift+Right", false)]
    [InlineData("Shift+Up", false)]
    [InlineData("Shift+Down", false)]
    [InlineData("Shift+F3", true)]
    [InlineData("Shift+F24", true)]
    [InlineData("Shift+PrintScreen", true)]
    [InlineData("Alt+A", true)]
    [InlineData("Win+1", true)]
    [InlineData("A", false)]
    [InlineData("1", false)]
    [InlineData("Backspace", false)]
    [InlineData("Delete", false)]
    [InlineData("Tab", false)]
    [InlineData("Enter", false)]
    [InlineData("Space", false)]
    [InlineData("F8", true)]
    [InlineData("F24", true)]
    [InlineData("PrintScreen", true)]
    [InlineData("Ctrl+Shift+A", true)]
    [InlineData("Ctrl+VK_0xBF", true)]
    [InlineData("VK_0xBF", false)]
    public void GlobalChordPolicy_IsAppliedBySettingsDraft(string text, bool allowed)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey key));
        Assert.Equal(allowed, key.IsSafeGlobalShortcut);
        var draft = new SettingsDraft(new AppSettings { Hotkeys = { HideAllPins = Hotkey.None } }) { ScrollingCaptureHotkey = text };
        Assert.Equal(!allowed, draft.GetErrors(nameof(draft.ScrollingCaptureHotkey)).Cast<string>().Any());
    }

    [Theory]
    [InlineData(0xBA)] [InlineData(0xBB)] [InlineData(0xBC)] [InlineData(0xBD)]
    [InlineData(0xBE)] [InlineData(0xBF)] [InlineData(0xC0)] [InlineData(0xDB)]
    [InlineData(0xDC)] [InlineData(0xDD)] [InlineData(0xDE)] [InlineData(0xE2)]
    public void OemChord_RoundTripsWithoutLosingItsPhysicalKey(uint vk)
    {
        var key = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, vk);
        Assert.True(Hotkey.TryParse(key.ToString(), out Hotkey parsed));
        Assert.Equal(key, parsed);
        Assert.True(parsed.IsSafeGlobalShortcut);
    }

    [Fact]
    public void MultiplePrimaryKeysAndImeSentinels_AreRejected()
    {
        Assert.False(Hotkey.TryParse("Ctrl+A+B", out _));
        Assert.False(new Hotkey(HotkeyModifiers.Control, 0xE5).IsSafeGlobalShortcut);
        Assert.True(Hotkey.None.IsSafeGlobalShortcut);
    }

    [Fact]
    public void Version400UnsafeSettingsArePersistedOnceAndDoNotReturnOnRestart()
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "MyCapture-hotkey-migration-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """
            {"schemaVersion":4,"general":{"launchAtLogin":false},"hotkeys":{
              "capture":"Shift+C","openLibrary":"A","pasteToScreen":"Shift+F3",
              "hideAllPins":"F8","toggleClickThrough":"Space","repeatLastRegion":"Shift+Left",
              "captureWindow":"Shift+1","captureFullScreen":"Enter","recordRegion":"Ctrl+Shift+X",
              "uploadGitHubImage":"Shift+Space","scrollingCapture":"Win+S","delayedCapture":"Alt+D"}}
            """);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings repaired = store.Load();
        Assert.True(store.LastLoadMigratedUnsafeHotkeys);
        Assert.NotEmpty(store.LastLoadWarnings);
        Assert.Equal("Ctrl+Shift+C", repaired.Hotkeys.Capture.ToString());
        Assert.Equal("Shift+F3", repaired.Hotkeys.PasteToScreen.ToString());
        Assert.Equal("F8", repaired.Hotkeys.HideAllPins.ToString());
        Assert.Equal("Ctrl+Shift+X", repaired.Hotkeys.RecordRegion.ToString());
        Assert.Equal("Win+S", repaired.Hotkeys.ScrollingCapture.ToString());
        Assert.Equal("Alt+D", repaired.Hotkeys.DelayedCapture.ToString());
        Assert.All(new[] { repaired.Hotkeys.OpenLibrary, repaired.Hotkeys.ToggleClickThrough,
            repaired.Hotkeys.RepeatLastRegion, repaired.Hotkeys.CaptureWindow,
            repaired.Hotkeys.CaptureFullScreen, repaired.Hotkeys.UploadGitHubImage }, key => Assert.Equal(Hotkey.None, key));
        string persisted = File.ReadAllText(paths.SettingsFile);
        Assert.DoesNotContain("Shift+Space", persisted);
        for (int restart = 0; restart < 2; restart++)
        {
            var next = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            AppSettings loaded = next.Load();
            Assert.False(next.LastLoadMigratedUnsafeHotkeys);
            Assert.Equal(Hotkey.None, loaded.Hotkeys.UploadGitHubImage);
            Assert.Equal(Hotkey.None, loaded.Hotkeys.OpenLibrary);
            Assert.Equal("Ctrl+Shift+C", loaded.Hotkeys.Capture.ToString());
            Assert.Equal(persisted, File.ReadAllText(paths.SettingsFile));
        }
    }

    [Theory]
    [InlineData("Shift+C")]
    [InlineData("")]
    public void RestoredCaptureClearsOptionalConflictsAndPersistsOnce(string capture)
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "MyCapture-hotkey-conflict-" + Guid.NewGuid().ToString("N")));
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Hotkey.TryParse(capture, out Hotkey previous);
        var source = new AppSettings();
        source.Hotkeys.Capture = previous;
        Hotkey duplicate = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC);
        var optional = typeof(HotkeySettings).GetProperties().Where(property => property.Name != nameof(HotkeySettings.Capture)).ToArray();
        foreach (var property in optional) property.SetValue(source.Hotkeys, duplicate);
        store.Save(source);
        AppSettings repaired = store.Load();
        Assert.Equal(duplicate, repaired.Hotkeys.Capture);
        Assert.All(optional, property => Assert.Equal(Hotkey.None, property.GetValue(repaired.Hotkeys)));
        Assert.True(store.LastLoadMigratedUnsafeHotkeys);
        Assert.Contains(MyCapture.Core.Localization.UiText.Get("Hotkey.Migrated"), store.LastLoadWarnings);
        var next = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings reloaded = next.Load();
        Assert.Equal(duplicate, reloaded.Hotkeys.Capture);
        Assert.All(optional, property => Assert.Equal(Hotkey.None, property.GetValue(reloaded.Hotkeys)));
        Assert.False(next.LastLoadMigratedUnsafeHotkeys);
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void CombinedRegistrationAndMigrationNoticeFitsNativeBalloon(string language)
    {
        using var scope = MyCapture.Core.Localization.UiText.UseLanguage(language);
        var longestChord = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0xFE);
        string suffix = MyCapture.Core.Localization.UiText.Format("Text_646F2DBF1852", int.MaxValue);
        string message = MyCapture.Core.Localization.UiText.Format("Text_114E1105C3EE", longestChord, suffix)
            + "\n" + MyCapture.Core.Localization.UiText.Get("Hotkey.Migrated");
        Assert.InRange(message.Length, 1, 255);
        Assert.DoesNotContain("주세요", message);
    }

    [Fact]
    public void WelcomeCompletion_SurvivesTwoFreshSettingsLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), "MyCapture-first-run-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.CreateForRoot(root);
        var firstStore = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings first = firstStore.Load();
        Assert.True(first.General.IsFirstRun);
        firstStore.CompleteFirstRun(first);
        Assert.False(first.General.IsFirstRun);
        for (int launch = 0; launch < 2; launch++)
            Assert.False(new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load().General.IsFirstRun);
    }
}
