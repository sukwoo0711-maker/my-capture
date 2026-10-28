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
    [InlineData("ko-KR", "드래그: 이동 · 휠·테두리: 크기 · 오른쪽 클릭: 메뉴", "휠 또는 테두리 끌기: 크기 · Ctrl+휠: 투명도 · Esc: 닫기")]
    [InlineData("en-US", "Drag: move · Wheel or edge: size · Right-click: menu", "Wheel or drag the edge: size · Ctrl+wheel: opacity · Esc: close")]
    public void BothLanguagesDescribeTheNewGestureAndClosing(string language, string tooltip, string sizeTip)
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(tooltip, UiText.Get("Onboard_PinTip"));
        Assert.Equal(sizeTip, UiText.Get("Onboard_PinSizeTip"));
    }
}
