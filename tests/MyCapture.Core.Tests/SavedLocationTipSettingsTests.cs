using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SavedLocationTipSettingsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(100, 5)]
    public void CountIsBoundedAndSurvivesJsonCloneAndDraft(int value, int expected)
    {
        var settings = new AppSettings { General = { SavedLocationTipCount = value } };
        AppSettings fromJson = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonDefaults.Readable), JsonDefaults.Readable)!;
        AppSettings clone = fromJson.DeepClone();
        AppSettings drafted = new SettingsDraft(clone) { NotifyOnQuickSave = false }.ToAppSettings();

        foreach (AppSettings current in new[] { settings, fromJson, clone, drafted })
            Assert.Equal(expected, current.General.SavedLocationTipCount);

        clone.General.SavedLocationTipCount = expected == 0 ? 1 : 0;
        Assert.Equal(expected, fromJson.General.SavedLocationTipCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"general\":{}}")]
    public void ExistingSettingsWithoutCountStartAtZero(string json)
    {
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        Assert.Equal(0, loaded.General.SavedLocationTipCount);
        Assert.Equal(0, NewInstallDefaults.Create().General.SavedLocationTipCount);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(23, 5)]
    public void InvalidPersistedCountIsClamped(int value, int expected)
    {
        string json = JsonSerializer.Serialize(new { general = new { savedLocationTipCount = value } });
        Assert.Equal(expected, JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!.General.SavedLocationTipCount);
    }

    [Fact]
    public void OnlySuccessfulPresentationsConsumeTheSharedFiveCount()
    {
        var settings = new GeneralSettings();
        int presentations = 0;
        for (int index = 0; index < FirstUseTips.SavedLocationLimit; index++)
        {
            Assert.False(FirstUseTips.ShowSavedLocation(settings, () => false));
            Assert.Equal(index, settings.SavedLocationTipCount);
            Assert.True(FirstUseTips.ShowSavedLocation(settings, () => { presentations++; return true; }));
            Assert.Equal(index + 1, settings.SavedLocationTipCount);
        }
        Assert.False(FirstUseTips.ShowSavedLocation(settings, () => throw new InvalidOperationException("Must not present a sixth tip.")));
        Assert.Equal(5, presentations);
        Assert.False(settings.FirstCaptureTipShown);
        Assert.False(settings.FirstPinTipShown);
    }

    [Fact]
    public void ThrowingPresentationDoesNotConsumeTheTip()
    {
        var settings = new GeneralSettings { SavedLocationTipCount = 2 };
        Assert.Throws<InvalidOperationException>(() => FirstUseTips.ShowSavedLocation(settings,
            () => throw new InvalidOperationException("Synthetic presenter failure")));
        Assert.Equal(2, settings.SavedLocationTipCount);
        Assert.True(FirstUseTips.ShowSavedLocation(settings, () => true));
        Assert.Equal(3, settings.SavedLocationTipCount);
    }
}
