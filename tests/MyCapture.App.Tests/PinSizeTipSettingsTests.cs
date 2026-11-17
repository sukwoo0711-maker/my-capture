using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSizeTipSettingsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PinTipFlagsRoundTripSettingsStoreAndImport(bool sizeShown, bool hideAllShown)
    {
        string root = OwnedTestDirectory.Create("pin-size-settings-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            store.Save(new AppSettings { General = { PinSizeTipShown = sizeShown, FirstPinTipShown = hideAllShown } });
            foreach (AppSettings settings in new[] { store.Load(), store.ImportFrom(paths.SettingsFile) })
            {
                Assert.Equal(sizeShown, settings.General.PinSizeTipShown);
                Assert.Equal(hideAllShown, settings.General.FirstPinTipShown);
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData("ko-KR", "드래그: 이동 · 휠·테두리: 크기 · 오른쪽 클릭: 메뉴", "휠 또는 테두리 끌기: 크기 · Ctrl+휠: 불투명도 · Esc: 닫기")]
    [InlineData("en-US", "Drag: move · Wheel or edge: size · Right-click: menu", "Wheel or drag the edge: size · Ctrl+wheel: opacity · Esc: close")]
    public void BothLanguagesDescribeTheNewGestureAndClosing(string language, string tooltip, string sizeTip)
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(tooltip, UiText.Get("Onboard_PinTip"));
        Assert.Equal(sizeTip, UiText.Get("Onboard_PinSizeTip"));
    }

    [Theory]
    [InlineData("ko-KR", "F2: 글자 선택")]
    [InlineData("en-US", "F2: select text")]
    public void TheFirstPinTipAddsTheTextSelectionKeyFromTheShortcutCatalogOnce(string language, string selectLine)
    {
        using var locale = UiText.UseLanguage(language);
        var hideAll = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
        // The key is the one the shortcut window and the pin menu show for text selection.
        ShortcutDefinition selectText = ShortcutCatalog.Get(ShortcutScope.Pin, "SelectText");
        Assert.Equal(UiText.Format("Pin_SelectTextTip", selectText.Hint), App.PinTipText(PinFirstUseTip.Size, hideAll).Detail);
        Assert.Equal((UiText.Get("Onboard_PinSizeTip"), selectLine), App.PinTipText(PinFirstUseTip.Size, hideAll));
        // The second pin's tip is unchanged and has no second line.
        Assert.Equal((UiText.Format("Onboard_FirstPinTip", hideAll), (string?)null), App.PinTipText(PinFirstUseTip.HideAll, hideAll));
        Assert.Equal((UiText.Get("Onboard_FirstPinTipUnassigned"), (string?)null), App.PinTipText(PinFirstUseTip.HideAll, Hotkey.None));
    }
}
