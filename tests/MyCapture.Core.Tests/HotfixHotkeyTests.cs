using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class HotfixHotkeyTests
{
    [Theory]
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
        var draft = new SettingsDraft(new AppSettings()) { ScrollingCaptureHotkey = text };
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
