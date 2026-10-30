using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingWatermarkSettingsTests
{
    [Fact]
    public void OldSettingsRemainOffWithRequestedDefaults()
    {
        var settings = JsonSerializer.Deserialize<RecordingSettings>("{}")!;
        Assert.False(settings.Watermark.Enabled);
        Assert.Equal(RecordingWatermarkSource.Image, settings.Watermark.Source);
        Assert.Equal(StampAnchor.BottomRight, settings.Watermark.Anchor);
        Assert.Equal(0.15, settings.Watermark.WidthFraction);
        Assert.Equal(16, settings.Watermark.Margin);
        Assert.Equal(0.7, settings.Watermark.Opacity);
    }

    [Fact]
    public void CloneDraftAndDiskRoundTripEveryFieldWithoutChangingEditorStamp()
    {
        var settings = new AppSettings();
        var stamp = settings.Annotation.Stamp.Clone();
        var watermark = new RecordingWatermarkSettings { Enabled = true, Source = RecordingWatermarkSource.Text,
            Text = "Example", TextColor = "#00AAFF", Anchor = StampAnchor.TopCenter, WidthFraction = 0.32, Margin = 24, Opacity = 0.85 };
        settings.Recording.Watermark = watermark;
        AppSettings clone = settings.DeepClone();
        Assert.Equal(watermark, clone.Recording.Watermark);
        Assert.NotSame(watermark, clone.Recording.Watermark);
        var draft = new SettingsDraft(clone);
        Assert.Equal(watermark, draft.RecordingWatermark);
        draft.RecordingWatermarkEnabled = false;
        Assert.True(settings.Recording.Watermark.Enabled);
        Assert.False(draft.ToAppSettings().Recording.Watermark.Enabled);
        draft.RecordingWatermarkEnabled = true;
        AppSettings applied = draft.ToAppSettings();
        Assert.Equal(watermark, applied.Recording.Watermark);
        Assert.Equal(JsonSerializer.Serialize(stamp), JsonSerializer.Serialize(applied.Annotation.Stamp));
        string folder = Path.Combine(Path.GetTempPath(), "mc-watermark-settings-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.CreateForRoot(folder);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        store.Save(applied);
        Assert.Equal(watermark, store.Load().Recording.Watermark);
        Assert.True(File.Exists(paths.SettingsFile));
        // Preserve the isolated generated settings file; this test never touches the real profile.
    }

    [Theory]
    [InlineData(StampAnchor.TopLeft, 16, 16)]
    [InlineData(StampAnchor.TopCenter, 425, 16)]
    [InlineData(StampAnchor.TopRight, 834, 16)]
    [InlineData(StampAnchor.MiddleLeft, 16, 262.5)]
    [InlineData(StampAnchor.Center, 425, 262.5)]
    [InlineData(StampAnchor.MiddleRight, 834, 262.5)]
    [InlineData(StampAnchor.BottomLeft, 16, 509)]
    [InlineData(StampAnchor.BottomCenter, 425, 509)]
    [InlineData(StampAnchor.BottomRight, 834, 509)]
    public void EveryAnchorUsesPhysicalRegionPixels(StampAnchor anchor, double left, double top)
    {
        RectD result = new RecordingWatermarkSettings { Anchor = anchor }.Place(1000, 600, 500, 250);
        Assert.Equal(left, result.Left, 6); Assert.Equal(top, result.Top, 6);
        Assert.Equal(150, result.Width, 6); Assert.Equal(75, result.Height, 6);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 4)]
    [InlineData(80, 400)]
    public void EveryAnchorClampsInsideSmallRegionAndPreservesAspect(double width, double height)
    {
        foreach (StampAnchor anchor in Enum.GetValues<StampAnchor>())
        {
            RectD result = new RecordingWatermarkSettings { Anchor = anchor, WidthFraction = 1, Margin = 4096 }.Place(width, height, 500, 250);
            Assert.True(result.Left >= 0 && result.Top >= 0 && result.Right <= width && result.Bottom <= height);
            Assert.Equal(2, result.Width / result.Height, 6);
        }
    }

    [Fact]
    public void SmallImageIsNotEnlargedAndTextDoesScaleWithRegionWidth()
    {
        var image = new RecordingWatermarkSettings();
        Assert.Equal(80, image.Place(3840, 2160, 80, 40).Width);
        Assert.Equal(576, (image with { Source = RecordingWatermarkSource.Text }).Place(3840, 2160, 80, 40).Width);
    }

    [Fact]
    public void InvalidStoredNumbersAndTextNormalizeWithoutNaNGeometry()
    {
        var settings = new RecordingWatermarkSettings { WidthFraction = double.NaN, Opacity = 4, Margin = -9,
            Text = new string('x', 90) + "\r\n", TextColor = "bad", Anchor = (StampAnchor)900 }.Normalize();
        Assert.Equal(80, settings.Text.Length); Assert.Equal(0.15, settings.WidthFraction);
        Assert.Equal(1, settings.Opacity); Assert.Equal(0, settings.Margin);
        Assert.Equal("#FFFFFF", settings.TextColor); Assert.Equal(StampAnchor.BottomRight, settings.Anchor);
    }
}
