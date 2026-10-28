using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureKeyPanelSettingsTests
{
    [Fact]
    public void StartThreeThenPanelTenThenOnDemand()
    {
        var capture = new CaptureSettings();
        for (int selector = 1; selector <= 16; selector++)
        {
            bool guide = CaptureStartGuidePolicy.TryClaim(capture);
            bool panel = CaptureKeyPanelPolicy.TryClaim(capture, guide);
            Assert.Equal(selector <= 3, guide);
            Assert.Equal(selector is >= 4 and <= 13, panel);
            Assert.False(guide && panel);
            Assert.Equal(Math.Clamp(selector - 3, 0, 10), capture.KeyPanelShownCount);
        }
    }

    [Theory]
    [InlineData(-9, 1)]
    [InlineData(9, 10)]
    [InlineData(999, 10)]
    public void CountIsBoundedAndPreferenceSurvivesJsonAndClone(int count, int expected)
    {
        var settings = new AppSettings { Capture = { StartGuideShownCount = 3, KeyPanelShownCount = count, AlwaysShowKeyPanel = true } };
        Assert.True(CaptureKeyPanelPolicy.TryClaim(settings.Capture, false));
        Assert.Equal(expected, settings.Capture.KeyPanelShownCount);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonDefaults.Readable), JsonDefaults.Readable)!;
        Assert.Equal(expected, loaded.Capture.KeyPanelShownCount);
        Assert.True(loaded.Capture.AlwaysShowKeyPanel);
        AppSettings clone = loaded.DeepClone();
        clone.Capture.KeyPanelShownCount = 0;
        Assert.Equal(expected, loaded.Capture.KeyPanelShownCount);
        Assert.True(clone.Capture.AlwaysShowKeyPanel);
    }

    [Fact]
    public void AlwaysShowDuringGuideDoesNotConsumeTheTenAndOldSettingsDefaultOff()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Readable)!;
        Assert.False(settings.Capture.AlwaysShowKeyPanel);
        settings.Capture.AlwaysShowKeyPanel = true;
        Assert.True(CaptureKeyPanelPolicy.TryClaim(settings.Capture, true));
        Assert.Equal(0, settings.Capture.KeyPanelShownCount);
        settings.Capture.StartGuideShownCount = 3;
        settings.Capture.KeyPanelShownCount = 10;
        Assert.True(CaptureKeyPanelPolicy.TryClaim(settings.Capture, false));
        Assert.Equal(10, settings.Capture.KeyPanelShownCount);
    }

    [Fact]
    public void PreferenceAloneMakesDraftDirtyAndRevertingRestoresCleanState()
    {
        var draft = new SettingsDraft(new AppSettings());
        Assert.False(draft.HasUnappliedChanges);
        draft.AlwaysShowKeyPanel = true;
        Assert.True(draft.HasUnappliedChanges);
        Assert.True(draft.ToAppSettings().Capture.AlwaysShowKeyPanel);
        draft.AlwaysShowKeyPanel = false;
        Assert.False(draft.HasUnappliedChanges);
    }
}
