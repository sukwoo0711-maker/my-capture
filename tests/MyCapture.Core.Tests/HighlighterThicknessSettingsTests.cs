using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class HighlighterThicknessSettingsTests
{
    [Fact]
    public void HighlighterWidthIsKeptApartFromTheSharedStrokeWidthAndStartsAtTwelve()
    {
        var defaults = new AnnotationDefaults();

        Assert.Equal(12, defaults.HighlighterThickness);
        Assert.Equal(3, defaults.StrokeThickness);
    }

    [Theory]
    [InlineData("{}", 12)]
    [InlineData("""{"annotation":{"strokeThickness":20}}""", 12)]
    [InlineData("""{"annotation":{"highlighterThickness":24}}""", 24)]
    public void SavedSettingsWithoutTheFieldKeepTheDefaultAndSavedOnesLoadAsWritten(string json, double expected)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;

        Assert.Equal(expected, settings.Annotation.HighlighterThickness);
    }

    [Fact]
    public void WidthsRoundTripThroughJsonAndDeepCloneWithoutMixingTheTwo()
    {
        var settings = new AppSettings { Annotation = { StrokeThickness = 3, HighlighterThickness = 20 } };

        string json = JsonSerializer.Serialize(settings, JsonDefaults.Readable);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        AppSettings clone = settings.DeepClone();

        Assert.Contains("highlighterThickness", json, StringComparison.OrdinalIgnoreCase);
        foreach (AnnotationDefaults annotation in new[] { loaded.Annotation, clone.Annotation })
        {
            Assert.Equal(3, annotation.StrokeThickness);
            Assert.Equal(20, annotation.HighlighterThickness);
        }
    }

    [Fact]
    public void SettingsDraftKeepsTheHighlighterWidthWhenAnotherSettingChangesAndResetsItWithTheRest()
    {
        var source = new AppSettings { Annotation = { HighlighterThickness = 28 } };
        var draft = new SettingsDraft(source);
        draft.NotifyOnQuickSave = !draft.NotifyOnQuickSave;

        Assert.Equal(28, draft.ToAppSettings().Annotation.HighlighterThickness);

        draft.ResetToDefaults();

        Assert.Equal(12, draft.ToAppSettings().Annotation.HighlighterThickness);
        Assert.Equal(28, source.Annotation.HighlighterThickness);
    }
}
