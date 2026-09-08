using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingWallClockWindowTests : KoreanCaptionTest
{
    [Fact]
    public void Lifecycle_InitiallyDisabledWithoutTimer() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.Null(window.Timer);
            Assert.False(window.IsTimerRunning);
            Assert.Null(window.TimerInterval);
            Assert.False(window.IsClosing);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(60, 30, 33.333333333333336)]
    [InlineData(30, 30, 33.333333333333336)]
    [InlineData(10, 10, 100.0)]
    [InlineData(1, 1, 1000.0)]
    [InlineData(-5, 1, 1000.0)]
    [InlineData(120, 30, 33.333333333333336)]
    public void Lifecycle_StartTimer_EnablesTimerWithBoundedInterval(int requestedFps, int expectedFps, double expectedIntervalMs) => StaTestHost.Run(() =>
    {
        TimeSpan staticInterval = RecordingWallClockWindow.CalculateTimerInterval(requestedFps);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedIntervalMs), staticInterval);

        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: requestedFps);
        try
        {
            Assert.Equal(expectedFps, window.TargetFps);

            window.StartTimer();

            Assert.True(window.IsTimerRunning);
            Assert.NotNull(window.Timer);
            Assert.Equal(staticInterval, window.TimerInterval);

            // Double start is idempotent
            window.StartTimer();
            Assert.True(window.IsTimerRunning);

            window.StopTimer();
            Assert.False(window.IsTimerRunning);
            Assert.Null(window.Timer);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Lifecycle_CloseClock_DisposesTimerAndMarksClosing() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            window.StartTimer();
            Assert.True(window.IsTimerRunning);

            window.CloseClock();

            Assert.Null(window.Timer);
            Assert.False(window.IsTimerRunning);
            Assert.True(window.IsClosing);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Lifecycle_WindowClose_DisposesTimerViaOnClosed() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);

        window.StartTimer();
        Assert.True(window.IsTimerRunning);

        window.Close();

        Assert.Null(window.Timer);
        Assert.False(window.IsTimerRunning);
        Assert.True(window.IsClosing);
    });

    [Fact]
    public void DateFormat_MatchesSpecificationAndFormatsPrecision() => StaTestHost.Run(() =>
    {
        Assert.Equal("yyyy-MM-dd HH:mm:ss.fff", RecordingWallClockWindow.DateFormat);

        var testTime = new DateTime(2026, 9, 8, 14, 30, 45, 123);
        string formatted = RecordingWallClockWindow.FormatTime(testTime);
        Assert.Equal("2026-09-08 14:30:45.123", formatted);

        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.True(
                DateTime.TryParseExact(
                    window.CurrentText,
                    RecordingWallClockWindow.DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _),
                $"CurrentText '{window.CurrentText}' must match format '{RecordingWallClockWindow.DateFormat}'.");

            window.UpdateTime(testTime);
            Assert.Equal("2026-09-08 14:30:45.123", window.CurrentText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Bounds_NarrowRegion_DoesNotSpillOutsideAndScalesContent() => StaTestHost.Run(() =>
    {
        // Region width of 80 is narrower than natural clock width (~200)
        var region = new RectD(100, 100, 80, 50);
        var window = new RecordingWallClockWindow(
            region,
            dpiScale: 1.0,
            targetFps: 30,
            initialFontSize: RecordingWallClockWindow.DefaultFontSize);
        try
        {
            RectD bounds = window.PhysicalBounds;

            Assert.True(bounds.Left >= region.Left, $"Left {bounds.Left} must be >= region Left {region.Left}");
            Assert.True(bounds.Right <= region.Right, $"Right {bounds.Right} must be <= region Right {region.Right}");
            Assert.True(bounds.Top >= region.Top, $"Top {bounds.Top} must be >= region Top {region.Top}");
            Assert.True(bounds.Bottom <= region.Bottom, $"Bottom {bounds.Bottom} must be <= region Bottom {region.Bottom}");
            Assert.True(window.PhysicalWidth <= region.Width);

            // Requested font size retains user intent
            Assert.Equal(RecordingWallClockWindow.DefaultFontSize, window.CurrentFontSize);

            // Viewbox scales content to fit without clipping
            Assert.Equal(Stretch.Uniform, window.Viewbox.Stretch);
            Assert.Equal(StretchDirection.DownOnly, window.Viewbox.StretchDirection);
            Assert.Equal(TextWrapping.NoWrap, window.TextBlock.TextWrapping);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Bounds_NegativeOriginRegion_StrictlyClampedWithinRegion() => StaTestHost.Run(() =>
    {
        var region = new RectD(-1920, -1080, 500, 300);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            RectD bounds = window.PhysicalBounds;

            Assert.True(bounds.Left >= region.Left, $"Left {bounds.Left} must be >= {region.Left}");
            Assert.True(bounds.Right <= region.Right, $"Right {bounds.Right} must be <= {region.Right}");
            Assert.True(bounds.Top >= region.Top, $"Top {bounds.Top} must be >= {region.Top}");
            Assert.True(bounds.Bottom <= region.Bottom, $"Bottom {bounds.Bottom} must be <= {region.Bottom}");

            Assert.Equal(window.PhysicalLeft, window.Left);
            Assert.Equal(window.PhysicalTop, window.Top);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Bounds_SmallNegativeOriginRegion_DoesNotSpillOutside() => StaTestHost.Run(() =>
    {
        var region = new RectD(-600, -400, 75, 22);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            RectD bounds = window.PhysicalBounds;

            Assert.True(bounds.Left >= region.Left);
            Assert.True(bounds.Right <= region.Right);
            Assert.True(bounds.Top >= region.Top);
            Assert.True(bounds.Bottom <= region.Bottom);

            Assert.True(window.PhysicalWidth <= region.Width);
            Assert.True(window.PhysicalHeight <= region.Height);
            Assert.Equal(RecordingWallClockWindow.DefaultFontSize, window.CurrentFontSize);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Bounds_UpdateRecordingRegion_ReclampingMaintainsRequestedFontIntent() => StaTestHost.Run(() =>
    {
        var narrowRegion = new RectD(0, 0, 90, 30);
        var window = new RecordingWallClockWindow(narrowRegion, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.True(window.PhysicalWidth <= 90.0);
            Assert.Equal(RecordingWallClockWindow.DefaultFontSize, window.CurrentFontSize);

            var wideRegion = new RectD(0, 0, 1920, 1080);
            window.UpdateRecordingRegion(wideRegion, dpiScale: 1.0);

            // Expands in wide region while maintaining font size intent
            Assert.True(window.PhysicalWidth > 90.0);
            Assert.Equal(RecordingWallClockWindow.DefaultFontSize, window.CurrentFontSize);
            Assert.True(window.PhysicalBounds.Right <= wideRegion.Right);
            Assert.True(window.PhysicalBounds.Bottom <= wideRegion.Bottom);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void FontSizeAdjustment_KeepsBoundsWithinRegionWhenEnlargedInNarrowRegion() => StaTestHost.Run(() =>
    {
        var region = new RectD(200, 200, 100, 40);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.Equal(RecordingWallClockWindow.DefaultFontSize, window.CurrentFontSize);

            // Zoom in font size by 10 notches
            window.AdjustFontSize(10);

            Assert.Equal(23.0, window.CurrentFontSize);
            Assert.True(window.PhysicalWidth <= region.Width);
            Assert.True(window.PhysicalBounds.Right <= region.Right);
            Assert.True(window.PhysicalBounds.Bottom <= region.Bottom);

            // Clamps at MaxFontSize
            window.AdjustFontSize(20);
            Assert.Equal(RecordingWallClockWindow.MaxFontSize, window.CurrentFontSize);
            Assert.True(window.PhysicalWidth <= region.Width);

            // Clamps at MinFontSize
            window.AdjustFontSize(-50);
            Assert.Equal(RecordingWallClockWindow.MinFontSize, window.CurrentFontSize);
            Assert.True(window.PhysicalWidth <= region.Width);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void DpiScale_MixedDpiLayout_MaintainsPhysicalBoundsWithinRegion() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 300, 150);
        var window = new RecordingWallClockWindow(region, dpiScale: 2.0, targetFps: 30);
        try
        {
            Assert.Equal(2.0, window.DpiScale);
            Assert.True(window.PhysicalBounds.Left >= region.Left);
            Assert.True(window.PhysicalBounds.Right <= region.Right);
            Assert.True(window.PhysicalBounds.Top >= region.Top);
            Assert.True(window.PhysicalBounds.Bottom <= region.Bottom);

            window.ApplyDpiScale(1.5);
            Assert.Equal(1.5, window.DpiScale);
            Assert.True(window.PhysicalBounds.Left >= region.Left);
            Assert.True(window.PhysicalBounds.Right <= region.Right);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Accessibility_ContainerExposesCorrectNameAndHelpText() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 640, 360);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.Equal("녹화 시계", AutomationProperties.GetName(window.Container));
            Assert.Contains(RecordingWallClockWindow.DateFormat, AutomationProperties.GetHelpText(window.Container));
        }
        finally
        {
            window.Close();
        }
    });
}
