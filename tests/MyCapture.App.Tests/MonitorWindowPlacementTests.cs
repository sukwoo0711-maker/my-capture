using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Windows opened after a capture stay whole on the monitor the capture came from.</summary>
public sealed class MonitorWindowPlacementTests
{
    // The reported desktop: an ultrawide primary beside a portrait virtual display on its right.
    private static readonly MonitorInfo Ultrawide = new(@"\\.\DISPLAY1", new RectD(0, 0, 3440, 1440),
        new RectD(0, 0, 3440, 1392), 96, true);
    private static readonly MonitorInfo Portrait = new(@"\\.\DISPLAY2", new RectD(3440, 0, 768, 1366),
        new RectD(3440, 0, 768, 1318), 96, false);
    private static readonly Size EditorMinimum = AnnotationEditorWindow.MinimumSize;

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void EditorOnAPortraitSecondaryStaysInsideItsWorkArea(uint dpi)
    {
        RectD work = Portrait.WorkArea;
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(work, dpi / 96.0, new Size(980, 620),
            EditorMinimum, AnnotationEditorWindow.WorkAreaMargin);

        AssertInside(work, fit.Bounds);
        Assert.Equal(fit.Bounds.Width, Math.Round(fit.Size.Width * dpi / 96.0), 3);
        Assert.True(fit.Minimum.Width <= work.Width / (dpi / 96.0));
        Assert.True(fit.Size.Width >= fit.Minimum.Width && fit.Size.Height >= fit.Minimum.Height);
        // Centred: equal free space either side, to the pixel.
        Assert.InRange(Math.Abs((fit.Bounds.Left - work.Left) - (work.Right - fit.Bounds.Right)), 0, 1);
    }

    [Fact]
    public void ReportedSecondaryCaseNoLongerSpillsOntoThePrimary()
    {
        // Before: sized from the primary work area, 980 DIPs wide at (3334,349)-(4314,969).
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(Portrait.WorkArea, 1, new Size(980, 620),
            EditorMinimum, AnnotationEditorWindow.WorkAreaMargin);
        Assert.Equal(new RectD(3472, 349, 704, 620), fit.Bounds);
        Assert.Equal(new Size(704, 620), fit.Size);
    }

    [Fact]
    public void PrimaryLandscapeKeepsTheDesiredSizeAndMargins()
    {
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(Ultrawide.WorkArea, 1, new Size(2200, 1500),
            EditorMinimum, AnnotationEditorWindow.WorkAreaMargin);
        Assert.Equal(new Size(2200, 1328), fit.Size); // Height capped at work area - 64.
        AssertInside(Ultrawide.WorkArea, fit.Bounds);
        Assert.Equal(new RectD(620, 32, 2200, 1328), fit.Bounds);
    }

    [Fact]
    public void AMinimumLargerThanTheMonitorShrinksSoTheWindowIsAlwaysWhole()
    {
        var tiny = new RectD(-600, 200, 600, 400);
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(tiny, 1.5, new Size(980, 620), EditorMinimum, 64);
        AssertInside(tiny, fit.Bounds);
        Assert.Equal(400, fit.Minimum.Width, 3);
        Assert.Equal(new RectD(-600, 200, 600, 400), fit.Bounds);
    }

    [Fact]
    public void CaptureMonitorIsTheOneShowingMostOfTheSelection()
    {
        Assert.Same(Portrait, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(3500, 100, 300, 200)));
        Assert.Same(Ultrawide, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(100, 100, 300, 200)));
        // Spanning both: 240 px on DISPLAY1 against 160 on DISPLAY2, then 40 against 360.
        Assert.Same(Ultrawide, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(3200, 100, 400, 200)));
        Assert.Same(Portrait, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(3400, 100, 400, 200)));
        // Off every monitor (a stale frame after a display change): the nearest one.
        Assert.Same(Portrait, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(3600, 1500, 50, 50)));
        Assert.Same(Ultrawide, MonitorWindowPlacement.MonitorFor([Ultrawide, Portrait], new RectD(-500, 300, 50, 50)));
        Assert.Null(MonitorWindowPlacement.MonitorFor([], new RectD(0, 0, 10, 10)));
    }

    [Fact]
    public void EditorPlacedOnThePortraitMonitorGetsItsSizeAndPosition() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = BitmapSource.Create(600, 900, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 900 * 4], 600 * 4);
        bitmap.Freeze();
        var region = new RectD(3500, 100, 600, 900);
        var editor = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), new RectD(0, 0, 600, 900), bitmap);
        try
        {
            editor.PlaceOnMonitor(Portrait);
            Assert.Equal(WindowStartupLocation.Manual, editor.WindowStartupLocation);
            // Desired 980 x 1056 DIPs: width capped at 768 - 64, height kept.
            Assert.Equal(704, editor.Width, 3);
            Assert.Equal(1056, editor.Height, 3);
            Assert.Equal(3472, editor.Left, 3);
            Assert.Equal(131, editor.Top, 3);
            Assert.True(editor.MinWidth <= editor.Width);
        }
        finally { editor.Close(); }
    });

    private static void AssertInside(RectD outer, RectD inner)
    {
        Assert.True(inner.Left >= outer.Left && inner.Top >= outer.Top, $"{inner} starts outside {outer}");
        Assert.True(inner.Right <= outer.Right && inner.Bottom <= outer.Bottom, $"{inner} ends outside {outer}");
    }
}
