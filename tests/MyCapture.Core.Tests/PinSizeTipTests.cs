using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSizeTipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAndExistingUsersSeeSizeOnNextPinAndHideAllOnlyLater(bool hideAllAlreadyShown)
    {
        var settings = new GeneralSettings { FirstPinTipShown = hideAllAlreadyShown };
        var shown = new List<PinFirstUseTip>();
        bool Present(PinFirstUseTip tip) { shown.Add(tip); return true; }
        Assert.True(FirstUseTips.ShowNextPinTip(settings, Present));
        Assert.Equal([PinFirstUseTip.Size], shown);
        Assert.True(settings.PinSizeTipShown);
        Assert.Equal(hideAllAlreadyShown, settings.FirstPinTipShown);
        Assert.Equal(!hideAllAlreadyShown, FirstUseTips.ShowNextPinTip(settings, Present));
        Assert.Equal(hideAllAlreadyShown ? new[] { PinFirstUseTip.Size }
            : new[] { PinFirstUseTip.Size, PinFirstUseTip.HideAll }, shown);
        Assert.True(settings.FirstPinTipShown);
        Assert.False(FirstUseTips.ShowNextPinTip(settings, Present));
    }

    [Fact]
    public void RefusedOrFailedSizeTipDoesNotConsumeEitherTipOrShowHideAll()
    {
        var settings = new GeneralSettings();
        var requested = new List<PinFirstUseTip>();
        Assert.False(FirstUseTips.ShowNextPinTip(settings, tip => { requested.Add(tip); return false; }));
        Assert.Equal([PinFirstUseTip.Size], requested);
        Assert.False(settings.PinSizeTipShown);
        Assert.False(settings.FirstPinTipShown);
        Assert.Throws<InvalidOperationException>(() => FirstUseTips.ShowNextPinTip(settings, _ => throw new InvalidOperationException()));
        Assert.False(settings.PinSizeTipShown);
        Assert.False(settings.FirstPinTipShown);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BothPinFlagsSurviveJsonCloneAndDraft(bool sizeShown, bool hideAllShown)
    {
        var source = new AppSettings { General = { PinSizeTipShown = sizeShown, FirstPinTipShown = hideAllShown } };
        var json = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source, JsonDefaults.Readable), JsonDefaults.Readable)!;
        AppSettings clone = json.DeepClone();
        var draft = new SettingsDraft(clone);
        foreach (AppSettings item in new[] { json, clone, draft.ToAppSettings() })
        {
            Assert.Equal(sizeShown, item.General.PinSizeTipShown);
            Assert.Equal(hideAllShown, item.General.FirstPinTipShown);
        }
        clone.General.PinSizeTipShown = !sizeShown;
        Assert.Equal(sizeShown, json.General.PinSizeTipShown);
    }

    [Fact]
    public void ExistingSavedSettingsWithoutNewFlagStillGetSizeTip()
    {
        AppSettings existing = JsonSerializer.Deserialize<AppSettings>(
            """{"general":{"firstPinTipShown":true}}""", JsonDefaults.Readable)!;
        Assert.True(existing.General.FirstPinTipShown);
        Assert.False(existing.General.PinSizeTipShown);
        Assert.False(NewInstallDefaults.Create().General.PinSizeTipShown);
        Assert.False(new GeneralSettings().PinSizeTipShown);
        Assert.True(FirstUseTips.ShowNextPinTip(existing.General, tip => tip == PinFirstUseTip.Size));
    }
}
