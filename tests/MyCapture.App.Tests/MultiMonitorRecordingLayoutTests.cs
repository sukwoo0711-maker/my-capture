using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MultiMonitorRecordingLayoutTests
{
    [Fact]
    public void FrozenFrame_TranslatesNegativeVirtualOriginWithoutClippingCrossMonitorRegion()
    {
        // Use a small real bitmap with a negative virtual origin; only coordinate translation is
        // under test, so allocating a full multi-monitor-sized backing plane would add no proof.
        BitmapSource bitmap = BitmapSource.Create(
            64,
            36,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[64 * 36 * 4],
            64 * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(
            bitmap,
            new RectD(-2560, -1440, 64, 36),
            null,
            0);

        RectD translated = frame.ToBitmapSpace(new RectD(-2550, -1430, 40, 20));

        Assert.Equal(new RectD(10, 10, 40, 20), translated);
    }

    [Fact]
    public void CrossMonitorMixedDpiLayout_KeepsPhysicalRegionFrameExact()
    {
        var region = new RectD(-100, 100, 200, 200);
        var desktop = new RectD(-1920, 0, 5760, 2160);

        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
            region,
            desktop,
            dpiScale: 2,
            borderDip: 2,
            stripHeightDip: 56,
            minimumStripWidthDip: 460);

        Assert.Equal(region.Inflate(4), layout.FrameBounds);
        Assert.True(layout.WindowBounds.Left <= layout.FrameBounds.Left);
        Assert.True(layout.WindowBounds.Right >= layout.FrameBounds.Right);
        Assert.True(layout.WindowBounds.Top <= layout.FrameBounds.Top);
        Assert.True(layout.WindowBounds.Bottom >= layout.FrameBounds.Bottom);
    }

    [Fact]
    public void BottomEdge_FlipsPaletteAboveWithoutMovingSelectedRegion()
    {
        var region = new RectD(100, 900, 640, 180);
        var desktop = new RectD(0, 0, 1920, 1080);

        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
            region,
            desktop,
            dpiScale: 1,
            borderDip: 2,
            stripHeightDip: 56,
            minimumStripWidthDip: 460);

        Assert.Equal(region.Inflate(2), layout.FrameBounds);
        Assert.Equal(layout.FrameBounds.Top, layout.PaletteBounds.Bottom, precision: 6);
        Assert.False(layout.PaletteOverlapsRegion);
    }

    [Fact]
    public void NarrowLeftEdge_ClampsPaletteButNotThePhysicalFrame()
    {
        var region = new RectD(0, 120, 100, 80);
        var desktop = new RectD(0, 0, 1920, 1080);

        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
            region,
            desktop,
            dpiScale: 1.5,
            borderDip: 2,
            stripHeightDip: 56,
            minimumStripWidthDip: 460);

        Assert.Equal(-3, layout.FrameBounds.Left);
        Assert.Equal(0, layout.PaletteBounds.Left);
        Assert.Equal(region.Width + 6, layout.FrameBounds.Width);
    }

    [Fact]
    public void FullVirtualDesktop_ReportsPaletteOverlapForCaptureExclusionFallback()
    {
        var desktop = new RectD(-1920, -200, 5760, 2360);

        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
            desktop,
            desktop,
            dpiScale: 1.25,
            borderDip: 2,
            stripHeightDip: 56,
            minimumStripWidthDip: 460);

        Assert.True(layout.PaletteOverlapsRegion);
        Assert.Equal(desktop.Inflate(2.5), layout.FrameBounds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FullScreen_ControlsStayInsideActualMonitorWorkArea(double scale)
    {
        var region = new RectD(-2560, -1440, 2560, 1440);
        var work = new RectD(-2560, -1440, 2560, 1392);
        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(region, work, scale, 6, 56, 460);
        Assert.Equal(region.Inflate(6 * scale), layout.FrameBounds);
        Assert.Equal(460 * scale, layout.PaletteBounds.Width);
        Assert.True(work.Contains(layout.PaletteBounds.TopLeft));
        Assert.True(work.Contains(layout.PaletteBounds.BottomRight));
        Assert.True(layout.PaletteOverlapsRegion);
    }

    [Fact]
    public void StaggeredDisplays_PaletteAvoidsVirtualDesktopGapAndTaskbar()
    {
        var region = new RectD(-1500, 500, 1800, 1400);
        // The left monitor ends at y=1040; another display extends the desktop to y=2160.
        var leftWorkArea = new RectD(-1920, 0, 1920, 1040);
        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(region, leftWorkArea, 1.5, 6, 56, 460);
        Assert.True(leftWorkArea.Contains(layout.PaletteBounds.TopLeft));
        Assert.True(leftWorkArea.Contains(layout.PaletteBounds.BottomRight));
        Assert.Equal(region.Inflate(9), layout.FrameBounds);
    }

    [Fact]
    public void VeryNarrowHighDpiWorkArea_PaletteFitsInsteadOfExtendingOffscreen()
    {
        var work = new RectD(400, 100, 640, 800);
        RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(work, work, 2, 6, 56, 460);
        Assert.Equal(work.Width, layout.PaletteBounds.Width);
        Assert.True(work.Contains(layout.PaletteBounds.TopLeft));
        Assert.True(work.Contains(layout.PaletteBounds.BottomRight));
    }
}
