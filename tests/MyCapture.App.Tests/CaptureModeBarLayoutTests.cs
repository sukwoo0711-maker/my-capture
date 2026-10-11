using System.Windows;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Pure placement of the selector's mode bar and start guide on one of several monitors.</summary>
public sealed class CaptureModeBarLayoutTests
{
    // A 150% laptop on the left at a negative origin, a 100% primary in the middle, a portrait 125% on the right.
    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY2", new RectD(-1920, 180, 1920, 1080),
        new RectD(-1920, 180, 1920, 1032), 144, IsPrimary: false);
    private static readonly MonitorInfo Primary = new(@"\\.\DISPLAY1", new RectD(0, 0, 2560, 1440),
        new RectD(0, 48, 2560, 1392), 96, IsPrimary: true);
    private static readonly MonitorInfo Right = new(@"\\.\DISPLAY3", new RectD(2560, 0, 1200, 1920),
        new RectD(2560, 0, 1200, 1872), 120, IsPrimary: false);
    private static readonly IReadOnlyList<MonitorInfo> Monitors = [Left, Primary, Right];
    private static readonly RectD Desktop = new(-1920, 0, 1920 + 2560 + 1200, 1920);

    [Theory]
    [InlineData(-100, 600, 0, 180, 1920, 1080, 1.5)]
    [InlineData(1000, 700, 1920, 0, 2560, 1440, 1.0)]
    [InlineData(3000, 1800, 4480, 0, 1200, 1920, 1.25)]
    public void PointerChoosesItsMonitorInOverlayPixels(double x, double y, double left, double top, double width,
        double height, double scale)
    {
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(Monitors, new PointD(x, y), Desktop);
        Assert.Equal(new RectD(left, top, width, height), area.Bounds);
        Assert.Equal(scale, area.Scale, 6);
        Assert.True(area.Bounds.Contains(area.WorkArea.Center));
    }

    [Fact]
    public void WorkAreaKeepsTheBarOffATopTaskbar()
    {
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(Monitors, new PointD(10, 10), Desktop);
        Assert.Equal(new RectD(1920, 48, 2560, 1392), area.WorkArea);
    }

    [Fact]
    public void PointerOffEveryMonitorFallsBackToThePrimaryThenTheOverlay()
    {
        // In the gap below the left monitor's top edge: no display holds the point.
        CaptureGuideArea gap = CaptureModeBarLayout.ChooseArea(Monitors, new PointD(-500, 50), Desktop);
        Assert.Equal(new RectD(1920, 0, 2560, 1440), gap.Bounds);

        // A display the overlay does not cover is never chosen.
        var elsewhere = new MonitorInfo(@"\\.\DISPLAY9", new RectD(9000, 0, 800, 600), new RectD(9000, 0, 800, 600), 96, IsPrimary: true);
        CaptureGuideArea onlySecondary = CaptureModeBarLayout.ChooseArea([elsewhere, Right], new PointD(9100, 100), Desktop);
        Assert.Equal(new RectD(4480, 0, 1200, 1920), onlySecondary.Bounds);

        CaptureGuideArea none = CaptureModeBarLayout.ChooseArea([], new PointD(0, 0), new RectD(0, 0, 640, 360));
        Assert.Equal(new RectD(0, 0, 640, 360), none.Bounds);
        Assert.Equal(none.Bounds, none.WorkArea);
        Assert.Equal(0, none.Scale);
    }

    [Theory]
    [InlineData(1.5, 1.0, 1.5)]
    [InlineData(1.0, 1.0 / 1.5, 2.0 / 3.0)]
    [InlineData(1.5, 1.0 / 1.5, 1.0)]
    [InlineData(0, 1.0, 1.0)]
    [InlineData(4.0, 1.0, 3.0)]
    [InlineData(1.0, 0.1, 0.5)]
    public void ContentScaleKeepsTheMonitorsPhysicalSize(double monitorScale, double dipPerPixel, double expected) =>
        Assert.Equal(expected, CaptureModeBarLayout.ContentScale(monitorScale, dipPerPixel), 6);

    [Fact]
    public void BarIsCentredAtTheTopOfItsAreaAndNeverLeavesIt()
    {
        var viewport = new Size(5680, 1920);
        var area = new Rect(1920, 48, 2560, 1392);
        Rect bar = CaptureModeBarLayout.PlaceBar(area, new Size(600, 46), viewport);
        Assert.Equal(new Rect(1920 + 980, 48 + CaptureModeBarLayout.TopMargin, 600, 46), bar);

        // Scaled with a 150% monitor: the margin scales too.
        Rect scaled = CaptureModeBarLayout.PlaceBar(area, new Size(900, 69), viewport, scale: 1.5);
        Assert.Equal(48 + (CaptureModeBarLayout.TopMargin * 1.5), scaled.Top);
        Assert.Equal(area.Left + (area.Width / 2), scaled.Left + (scaled.Width / 2));

        // Wider than a narrow monitor: starts at that monitor's left edge instead of spilling left.
        Rect narrow = CaptureModeBarLayout.PlaceBar(new Rect(4480, 0, 500, 1920), new Size(600, 46), viewport);
        Assert.Equal(4480, narrow.Left);

        // Snapped to physical pixels at 150%.
        double pixel = 1 / 1.5;
        Rect snapped = CaptureModeBarLayout.PlaceBar(new Rect(0, 0, 1001, 700), new Size(301, 40), new Size(1001, 700), 1, pixel, pixel);
        Assert.Equal(Math.Round(snapped.Left / pixel), snapped.Left / pixel, 6);
        Assert.Equal(Math.Round(snapped.Top / pixel), snapped.Top / pixel, 6);
    }

    [Fact]
    public void GuideCardIsCentredAndStaysOnScreen()
    {
        Rect card = CaptureModeBarLayout.PlaceCard(new Rect(0, 0, 1280, 672), new Size(440, 160), new Size(1280, 720));
        Assert.Equal(new Rect(420, 256, 440, 160), card);
        Rect clamped = CaptureModeBarLayout.PlaceCard(new Rect(-200, -50, 300, 100), new Size(440, 160), new Size(1280, 720));
        Assert.Equal(new Point(0, 0), clamped.TopLeft);

        // A monitor this short would centre the card over the bar: it sits just below the bar instead,
        // with the margin scaled like the bar.
        var bar = new Rect(340, 12, 600, 46);
        Rect below = CaptureModeBarLayout.PlaceCard(new Rect(0, 0, 1280, 300), new Size(366, 240), new Size(1280, 720), bar: bar);
        Assert.Equal(new Rect(457, bar.Bottom + CaptureModeBarLayout.EdgeMargin, 366, 240), below);
        Rect scaled = CaptureModeBarLayout.PlaceCard(new Rect(0, 0, 1280, 300), new Size(366, 240), new Size(1280, 720),
            bar: bar, scale: 1.5);
        Assert.Equal(bar.Bottom + (CaptureModeBarLayout.EdgeMargin * 1.5), scaled.Top);

        // With room to spare, or beside the bar rather than under it, the card keeps the centre.
        Assert.Equal(card, CaptureModeBarLayout.PlaceCard(new Rect(0, 0, 1280, 672), new Size(440, 160), new Size(1280, 720), bar: bar));
        Rect beside = CaptureModeBarLayout.PlaceCard(new Rect(1000, 0, 280, 300), new Size(200, 240), new Size(1280, 720), bar: bar);
        Assert.Equal(30, beside.Top);
    }
}
