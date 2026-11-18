using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinRestoreDefaultsTests
{
    [Fact]
    public void NewInstallAndResetAssignRestoreWhileLegacyDefaultsStayUnassigned()
    {
        Assert.Equal("Ctrl+Shift+F3", NewInstallDefaults.Create().Hotkeys.RestoreClosedPin.ToString());
        var legacy = new AppSettings();
        Assert.Equal(Hotkey.None, legacy.Hotkeys.RestoreClosedPin);
        var draft = new SettingsDraft(legacy);
        Assert.Equal(Hotkey.None, draft.ToAppSettings().Hotkeys.RestoreClosedPin);
        draft.ResetToDefaults();
        Assert.Equal("Ctrl+Shift+F3", draft.ToAppSettings().Hotkeys.RestoreClosedPin.ToString());
        Assert.Equal(Hotkey.None, legacy.Hotkeys.RestoreClosedPin);
    }
}
