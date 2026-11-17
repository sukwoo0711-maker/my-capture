using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Windows 11 rounds the selector's chip windows and paints their one-pixel edge; elsewhere each
/// chip draws the same edge itself, square. The read-out sits just outside the selection frame.
/// </summary>
public sealed class NativeSelectorDwmFrameTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.5)]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.5)]
    public void DwmRoundingDecidesWhichSideDrawsEachChipEdge(bool rounded, double scale) => StaTestHost.Run(() =>
    {
        bool? previous = NativeSelectorChrome.DwmRoundingOverrideForTest;
        NativeSelectorChrome.DwmRoundingOverrideForTest = rounded;
        try
        {
            MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, (uint)Math.Round(96 * scale), true)];
            using var owner = new NativeCaptureOverlay(Bounds, false, true)
            {
                ReadCursor = () => new PointD(320, 180), ReadMonitors = () => monitors, PlaceCursor = _ => false,
            };
            owner.AttachFrame(Frame());
            using var chrome = new NativeSelectorChrome(owner, true, false);
            // Bar, read-out, hint and magnifier: the read-out is the small chip, the rest are panels.
            Assert.Equal(DwmCornerPreference.Round, chrome.CornersForTest(chrome.Windows[0]));
            Assert.Equal(DwmCornerPreference.RoundSmall, chrome.CornersForTest(chrome.Windows[1]));
            Assert.Equal(DwmCornerPreference.Round, chrome.CornersForTest(chrome.Windows[2]));
            Assert.Equal(DwmCornerPreference.Round, chrome.CornersForTest(chrome.Windows[3]));
            foreach (Window window in chrome.Windows.Take(4)) Assert.Equal(rounded, chrome.IsRoundedForTest(window));

            CaptureModeBar bar = owner.ModeBar;
            Assert.Equal(rounded ? CaptureModeBarFrame.Window : CaptureModeBarFrame.Square, bar.Frame);
            Assert.Equal(rounded ? 0 : 1 / scale, bar.BorderThickness.Left, 8);
            Assert.Equal(0, bar.CornerRadius.TopLeft);
            Assert.Equal(!rounded, chrome.ReadoutPanelForTest.Outlined);
            Assert.Equal(0, chrome.ReadoutPanelForTest.CornerRadius.TopLeft);
            Assert.Equal(!rounded, chrome.HintForTest.Outlined);
            Assert.Equal(rounded ? 0 : 1 / scale, chrome.HintForTest.BorderThickness.Left, 8);
            Assert.Equal(rounded, Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest).WindowRounded);

            // Every frame keeps the bar's outer size: the edge comes out of the padding.
            Size framed = Measure(bar);
            foreach (CaptureModeBarFrame frame in Enum.GetValues<CaptureModeBarFrame>())
            {
                bar.UseFrame(frame, 1 / scale);
                Assert.Equal(framed, Measure(bar));
            }
            bar.UseFrame(rounded ? CaptureModeBarFrame.Window : CaptureModeBarFrame.Square, 1 / scale);
        }
        finally { NativeSelectorChrome.DwmRoundingOverrideForTest = previous; }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ReadoutSitsAboveTheFrameMovesInsideNearTheTopAndStepsClearOfTheStartChip(double scale)
    {
        var work = new RectD(0, 0, 1920, 1040);
        var area = new CaptureGuideArea(new RectD(0, 0, 1920, 1080), work, scale);
        var chip = new Size(90, NativeSelectorChrome.ReadoutHeight);
        double outset = CaptureFrameStyle.Outset, gap = NativeSelectorChrome.ReadoutGap * scale;
        double width = chip.Width * scale, height = chip.Height * scale, inset = 8 * scale, edge = 8 * scale;

        // Room above: its left edge on the frame's outer edge, the gap above the frame.
        var selection = new Rect(400, 300, 500, 300);
        Rect placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        Assert.Equal(new Rect(selection.Left - outset, selection.Top - outset - gap - height, width, height), placed);

        // Near the top, inside the selection's corner when it holds the chip.
        selection = new Rect(400, 10, 500, 300);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        Assert.Equal(new Rect(selection.Left + inset, selection.Top + inset, width, height), placed);

        // Near the top with a selection too small to hold it: below the frame.
        selection = new Rect(400, 10, 40, 20);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        Assert.Equal(new Rect(selection.Left - outset, selection.Bottom + outset + gap, width, height), placed);

        // Over the mode bar and hint at the top of the screen: inside the selection, below them, or
        // below the frame when the selection's corner is under them too.
        var topChrome = new Rect(300 * scale, 12 * scale, 700 * scale, 80 * scale);
        selection = new Rect(400, 100 * scale, 500, 300);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, topChrome);
        Assert.Equal(new Rect(selection.Left + inset, selection.Top + inset, width, height), placed);
        Assert.False(placed.IntersectsWith(topChrome));
        selection = new Rect(400, 60 * scale, 500, 300);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, topChrome);
        Assert.Equal(new Rect(selection.Left - outset, selection.Bottom + outset + gap, width, height), placed);
        selection = new Rect(400, 300, 500, 300);
        Assert.Equal(NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty),
            NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, topChrome));

        // At the left edge of the work area it stays on screen.
        selection = new Rect(0, 300, 500, 300);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        Assert.Equal(work.Left + edge, placed.Left, 8);

        // Over the start-point chip: beside it on its row, else below it.
        selection = new Rect(400, 300, 500, 300);
        Rect free = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        var anchor = new Rect(free.Left + 10, free.Top, 60 * scale, free.Height);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, anchor, Rect.Empty);
        Assert.False(placed.IntersectsWith(anchor));
        Assert.Equal(anchor.Left - gap - width, placed.Left, 8);
        Assert.Equal(free.Top, placed.Top, 8);
        selection = new Rect(edge / scale + 2, 300, 500, 300);
        free = NativeSelectorChrome.PlaceReadout(selection, chip, area, Rect.Empty, Rect.Empty);
        anchor = new Rect(free.Left + 10, free.Top, 60 * scale, free.Height);
        placed = NativeSelectorChrome.PlaceReadout(selection, chip, area, anchor, Rect.Empty);
        Assert.False(placed.IntersectsWith(anchor));
        Assert.Equal(anchor.Bottom + gap, placed.Top, 8);
    }

    private static Size Measure(FrameworkElement element)
    {
        element.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize;
    }

    private static FrozenFrame Frame()
    {
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 90;
            pixels[index + 1] = 110;
            pixels[index + 2] = 130;
            pixels[index + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 0);
    }
}
