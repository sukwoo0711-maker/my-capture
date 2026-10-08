using System.Text.Json;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CropSelectionTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void LegacyReceiptFlagAlwaysStartsInEditorAndIsNeverWritten(bool legacy)
    {
        string json = "{\"general\":{\"showQuickAccess\":" + legacy.ToString().ToLowerInvariant() + "}}";
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Compact)!;
        Assert.Equal(AfterCaptureAction.Editor, settings.General.AfterCapture);
        Assert.True(settings.General.ShowShortcutHints);
        foreach (AfterCaptureAction choice in Enum.GetValues<AfterCaptureAction>())
        {
            var draft = new SettingsDraft(settings) { AfterCapture = choice, ShowShortcutHints = false };
            string saved = JsonSerializer.Serialize(draft.ToAppSettings().DeepClone(), JsonDefaults.Compact);
            Assert.DoesNotContain("showQuickAccess", saved);
            var reloaded = JsonSerializer.Deserialize<AppSettings>(saved, JsonDefaults.Compact)!;
            Assert.Equal(choice, reloaded.General.AfterCapture);
            Assert.False(reloaded.General.ShowShortcutHints);
        }
    }

    [Fact]
    public void FrameStartsFullAndCanResizeMoveAndCreateOutside()
    {
        var crop = new CropSelection(640, 360);
        Assert.Equal(new RectD(0, 0, 640, 360), crop.Region);
        crop.Begin(new PointD(640, 360), 5);
        Assert.Equal(CropGrip.BottomRight, crop.Grip);
        crop.Drag(new PointD(320, 180), false); crop.End();
        Assert.Equal(new RectD(0, 0, 320, 180), crop.Region);
        crop.Begin(new PointD(100, 90), 5); crop.Drag(new PointD(150, 120), false); crop.End();
        Assert.Equal(new RectD(50, 30, 320, 180), crop.Region);
        crop.Begin(new PointD(500, 300), 5); crop.Drag(new PointD(400, 240), false); crop.End();
        Assert.Equal(new RectD(400, 240, 100, 60), crop.Region);
        crop.Nudge(1000, 1000);
        Assert.Equal(new RectD(540, 300, 100, 60), crop.Region);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(1.3333333333333333)] [InlineData(1.7777777777777777)] [InlineData(0.5625)]
    public void RatioRemainsExactForNumericEditsAndAllHandlesAtBounds(double ratio)
    {
        foreach (var point in new[] { new PointD(0, 0), new PointD(320, 0), new PointD(640, 0), new PointD(640, 180),
            new PointD(640, 360), new PointD(320, 360), new PointD(0, 360), new PointD(0, 180) })
        {
            var crop = new CropSelection(640, 360);
            crop.Begin(point, 1); crop.Drag(new PointD(280, 160), true); crop.End();
            Assert.Equal(640.0 / 360, crop.Region.Width / crop.Region.Height, 5);
        }
        var sized = new CropSelection(640, 360); sized.SetRatio(ratio); sized.SetWidth(900);
        Assert.Equal(ratio, sized.Region.Width / sized.Region.Height, 5);
        sized.SetHeight(500);
        Assert.Equal(ratio, sized.Region.Width / sized.Region.Height, 5);
        Assert.True(sized.Region.Left >= 0 && sized.Region.Top >= 0 && sized.Region.Right <= 640 && sized.Region.Bottom <= 360);
        sized.SetRatio(null); double width = sized.Region.Width; sized.SetHeight(30);
        Assert.Equal(width, sized.Region.Width); Assert.Equal(30, sized.Region.Height);
    }

    [Fact]
    public void InvalidNumericEditsKeepThePendingRegion()
    {
        var crop = new CropSelection(100, 80); var before = crop.Region;
        crop.SetWidth(double.NaN); crop.SetHeight(double.PositiveInfinity); crop.SetWidth(-2); crop.SetHeight(0);
        Assert.Equal(before, crop.Region);
    }
}
