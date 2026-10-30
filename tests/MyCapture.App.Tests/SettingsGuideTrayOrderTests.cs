using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideTrayOrderTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void AllConditionalStatesKeepGroupsRecoveryAndHelpOrder(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        BitmapSource bitmap = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 24 * 4], 32 * 4);
        bitmap.Freeze();
        var pin = new PinWindow(PinContent.FromImage(bitmap), new PinViewState(32, 24, 1, 1, .1), -10000, -10000) { ShowActivated = false };
        Action noop = () => { };
        try
        {
            // Restore, scrolling, clipboard, pins, hidden, click-through, update, busy, first-use, support.
            for (int mask = 0; mask < 1024; mask++)
            {
                bool Flag(int bit) => (mask & (1 << bit)) != 0;
                var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop,
                    PinClipboard: noop, HideOrShowAllPins: noop, ClearClickThrough: noop, ActivateOpenPin: _ => { },
                    InstallUpdate: noop, FirstUseGuide: Flag(8) ? noop : null, ContactSupport: Flag(9) ? noop : null);
                var state = new TrayMenuState(Flag(0), Flag(1), Flag(2), Flag(3) ? 1 : 0, Flag(4), Flag(5) ? 1 : 0,
                    Flag(3) ? [pin] : [], Flag(6), null, Flag(7));
                var menu = TrayMenuLayout.Build(commands, new HotkeySettings(), state);
                var expected = new List<string> { UiText.Get("Text_2C20ADDA2018"), UiText.Get("Text_4F5AF20FAA6E"), UiText.Get("Onboard_TrayCopyText"), UiText.Get("Onboard_TrayMoreCaptures"), "", UiText.Get("Tray_PinClipboard") };
                if (Flag(5)) expected.Add(UiText.Get("Tray_ClearClickThrough"));
                if (Flag(0)) expected.Add(UiText.Get("Onboard_TrayReopenPin"));
                if (Flag(3)) { expected.Add(UiText.Get("Tray_OpenPins")); expected.Add(UiText.Get(Flag(4) ? "Tray_ShowAllPins" : "Tray_HideAllPins")); }
                expected.AddRange(["", UiText.Get("Text_8739F6684453"), UiText.Get("Text_00480581AAB5")]);
                if (Flag(6)) expected.Add(UiText.Get("Update_TrayInstall"));
                expected.AddRange(["", UiText.Get("Tray_Help"), "", UiText.Get("Text_72B6DBA8AAC5")]);
                Assert.Equal(expected, menu.Select(entry => entry.Label));
                var help = menu.Single(entry => entry.Label == UiText.Get("Tray_Help"));
                var helpLabels = new List<string> { UiText.Get("Onboard_ShowShortcuts") };
                if (Flag(8)) helpLabels.Add(UiText.Get("Onboarding.Replay"));
                if (Flag(9)) helpLabels.Add(UiText.Get("Support_Title"));
                Assert.Null(help.Action); Assert.Equal(helpLabels, help.Children!.Select(entry => entry.Label));
                Assert.Equal("F1", help.Children![0].Gesture);
                Assert.Same(commands.Shortcuts, help.Children![0].Action);
                if (Flag(8)) Assert.Same(commands.FirstUseGuide, help.Children!.Single(entry => entry.Label == UiText.Get("Onboarding.Replay")).Action);
                if (Flag(9)) Assert.Same(commands.ContactSupport, help.Children!.Single(entry => entry.Label == UiText.Get("Support_Title")).Action);
                Assert.Equal(Flag(2), menu[5].IsEnabled);
                Assert.Equal(UiText.Get(Flag(1) ? "Text_E8B876F98CD8" : "Text_4DBA30197C0A"), menu[3].Children![2].Label);
                if (Flag(6)) Assert.Equal(!Flag(7), menu.Single(entry => entry.Label == UiText.Get("Update_TrayInstall")).IsEnabled);
                Assert.False(menu[0].IsSeparator); Assert.False(menu[^1].IsSeparator);
                Assert.DoesNotContain(Enumerable.Range(1, menu.Count - 1), i => menu[i].IsSeparator && menu[i - 1].IsSeparator);
            }
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void MissingOptionalCallbacksDoNotLeaveAnEmptyGroup()
    {
        Action noop = () => { };
        var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop);
        var menu = TrayMenuLayout.Build(commands, new HotkeySettings(), new TrayMenuState(PinCount: 3, ClickThroughPinCount: 2, UpdateAvailable: true));
        Assert.Equal(3, menu.Count(entry => entry.IsSeparator));
        Assert.Equal(4, menu.ToList().FindIndex(entry => entry.IsSeparator));
        Assert.Single(menu.Single(entry => entry.Label == UiText.Get("Tray_Help")).Children!);
    }
}
