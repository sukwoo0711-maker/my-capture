using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PracticeRevisionSettingsTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    public void RevisionSurvivesJsonCloneDraftAndReset(int revision, int expected)
    {
        var settings = new AppSettings { General = { PracticeRevisionOffered = revision } };
        AppSettings saved = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonDefaults.Readable), JsonDefaults.Readable)!;
        AppSettings clone = saved.DeepClone();
        var draft = new SettingsDraft(clone);
        Assert.Equal(expected, draft.ToAppSettings().General.PracticeRevisionOffered);
        draft.ResetToDefaults();
        Assert.Equal(expected, draft.ToAppSettings().General.PracticeRevisionOffered);
        clone.General.PracticeRevisionOffered = expected + 1;
        Assert.Equal(expected, saved.General.PracticeRevisionOffered);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"general\":{}}")]
    public void LegacySettingsStartWithNoOffer(string json) =>
        Assert.Equal(0, JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!.General.PracticeRevisionOffered);
}
