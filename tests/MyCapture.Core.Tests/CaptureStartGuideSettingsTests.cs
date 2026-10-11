using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>The capture selector's start guide: shown for the first three selectors, then only on request.</summary>
public sealed class CaptureStartGuideSettingsTests
{
    [Fact]
    public void GuideShowsForTheFirstThreeSelectorsThenStops()
    {
        var capture = new CaptureSettings();
        Assert.Equal(0, capture.StartGuideShownCount);
        for (int shown = 1; shown <= CaptureStartGuidePolicy.ShowCount; shown++)
        {
            Assert.True(CaptureStartGuidePolicy.ShouldShow(capture.StartGuideShownCount));
            Assert.True(CaptureStartGuidePolicy.TryClaim(capture));
            Assert.Equal(shown, capture.StartGuideShownCount);
        }

        Assert.Equal(3, CaptureStartGuidePolicy.ShowCount);
        Assert.False(CaptureStartGuidePolicy.ShouldShow(capture.StartGuideShownCount));
        Assert.False(CaptureStartGuidePolicy.TryClaim(capture));
        Assert.Equal(3, capture.StartGuideShownCount);
    }

    [Theory]
    [InlineData(-5, true, 1)]
    [InlineData(2, true, 3)]
    [InlineData(3, false, 3)]
    [InlineData(int.MaxValue, false, 3)]
    public void HandEditedCountsAreRepairedInsteadOfTrusted(int stored, bool shows, int after)
    {
        var capture = new CaptureSettings { StartGuideShownCount = stored };
        Assert.Equal(shows, CaptureStartGuidePolicy.TryClaim(capture));
        Assert.Equal(after, capture.StartGuideShownCount);
    }

    [Fact]
    public void OlderSettingsFilesStartWithTheGuideAndKeepTheirOtherCaptureValues()
    {
        AppSettings older = JsonSerializer.Deserialize<AppSettings>(
            """{"capture":{"autoDetectWindows":false,"delaySeconds":5}}""", JsonDefaults.Readable)!;
        Assert.Equal(0, older.Capture.StartGuideShownCount);
        Assert.False(older.Capture.AutoDetectWindows);
        Assert.Equal(5, older.Capture.DelaySeconds);
        Assert.True(CaptureStartGuidePolicy.ShouldShow(older.Capture.StartGuideShownCount));
    }

    [Fact]
    public void CountRoundTripsThroughDiskAndCloneAndOutOfRangeValuesAreClampedOnLoad()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings { Capture = { StartGuideShownCount = 2 } };
        store.Save(settings);
        Assert.Contains("\"startGuideShownCount\": 2", File.ReadAllText(workspace.Paths.SettingsFile), StringComparison.Ordinal);
        Assert.Equal(2, store.Load().Capture.StartGuideShownCount);

        AppSettings clone = settings.DeepClone();
        Assert.Equal(2, clone.Capture.StartGuideShownCount);
        clone.Capture.StartGuideShownCount = 3;
        Assert.Equal(2, settings.Capture.StartGuideShownCount);

        File.WriteAllText(workspace.Paths.SettingsFile, """{"schemaVersion":4,"capture":{"startGuideShownCount":-7}}""");
        Assert.Equal(0, store.Load().Capture.StartGuideShownCount);
        File.WriteAllText(workspace.Paths.SettingsFile, """{"schemaVersion":4,"capture":{"startGuideShownCount":99}}""");
        AppSettings clamped = store.Load();
        Assert.Equal(3, clamped.Capture.StartGuideShownCount);
        Assert.Empty(store.LastLoadWarnings);
    }

    [Fact]
    public void TheSettingsDraftDoesNotEditTheCount()
    {
        // The window has no control for it; the apply service carries the live value over.
        var settings = new AppSettings { Capture = { StartGuideShownCount = 2 } };
        Assert.Equal(0, new SettingsDraft(settings).ToAppSettings().Capture.StartGuideShownCount);
    }
}
