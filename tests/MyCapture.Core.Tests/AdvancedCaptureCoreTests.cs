using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Pure, deterministic coverage of the advanced-capture Core algorithms: the bounded
/// last-region history and the scroll stitcher. No WPF, no dispatcher, no platform types —
/// every input is a hand-built value so the maths is verified byte-exact.
/// </summary>
public sealed class AdvancedCaptureCoreTests
{
    // ----- LastRegionStore -----

    [Fact]
    public void LastRegionStore_StartsEmpty()
    {
        var store = new LastRegionStore(() => 20);
        Assert.Null(store.Last);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void LastRegionStore_RecordsMostRecentFirst()
    {
        var store = new LastRegionStore(() => 20);
        store.Record(new RectD(0, 0, 100, 100));
        store.Record(new RectD(10, 20, 200, 150));

        Assert.Equal(2, store.Count);
        RectD? last = store.Last;
        Assert.NotNull(last);
        Assert.Equal(10, last!.Value.Left);
        Assert.Equal(200, last.Value.Width);
    }

    [Fact]
    public void LastRegionStore_NormalizesNegativeDrag()
    {
        var store = new LastRegionStore(() => 20);
        // A drag up-and-left produces negative width/height.
        store.Record(new RectD(300, 400, -100, -50));

        RectD? last = store.Last;
        Assert.NotNull(last);
        Assert.Equal(200, last!.Value.Left);
        Assert.Equal(350, last.Value.Top);
        Assert.Equal(100, last.Value.Width);
        Assert.Equal(50, last.Value.Height);
    }

    [Fact]
    public void LastRegionStore_IgnoresEmptyRegion()
    {
        var store = new LastRegionStore(() => 20);
        store.Record(new RectD(10, 10, 0, 500));
        store.Record(new RectD(10, 10, 500, 0));

        Assert.Equal(0, store.Count);
        Assert.Null(store.Last);
    }

    [Fact]
    public void LastRegionStore_CollapsesConsecutiveDuplicates()
    {
        var store = new LastRegionStore(() => 20);
        store.Record(new RectD(0, 0, 100, 100));
        store.Record(new RectD(0, 0, 100, 100));
        // Sub-pixel jitter that still rounds outward to the same 100x100 pixel rect.
        store.Record(new RectD(0.0, 0.0, 100.0, 100.0));

        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void LastRegionStore_EvictsOldestBeyondLimit()
    {
        var store = new LastRegionStore(() => 3);
        for (int i = 0; i < 6; i++)
        {
            store.Record(new RectD(i * 10, 0, 50, 50));
        }

        Assert.Equal(3, store.Count);
        IReadOnlyList<RectD> snapshot = store.Snapshot();
        // Most recent first: last recorded was i=5 at Left=50.
        Assert.Equal(50, snapshot[0].Left);
        Assert.Equal(40, snapshot[1].Left);
        Assert.Equal(30, snapshot[2].Left);
    }

    [Fact]
    public void LastRegionStore_LimitChangeTakesEffectOnNextRecord()
    {
        int limit = 5;
        var store = new LastRegionStore(() => limit);
        for (int i = 0; i < 5; i++)
        {
            store.Record(new RectD(i * 10, 0, 50, 50));
        }

        Assert.Equal(5, store.Count);

        limit = 2;
        store.Record(new RectD(999, 0, 50, 50));
        Assert.Equal(2, store.Count);
    }

    // ----- ScrollStitcher -----

    /// <summary>
    /// Builds a frame whose rows carry a unique, deterministic value so overlap detection is
    /// unambiguous. Row <c>r</c> is filled with the byte <c>(baseValue + r) % 256</c> across
    /// all channels, giving each logical scroll position a distinct signature.
    /// </summary>
    private static ScrollFrame RowCodedFrame(int width, int height, int baseValue)
    {
        byte[] pixels = new byte[width * height * ScrollFrame.BytesPerPixel];
        for (int row = 0; row < height; row++)
        {
            byte value = (byte)((baseValue + row) & 0xFF);
            int offset = row * width * ScrollFrame.BytesPerPixel;
            for (int b = 0; b < width * ScrollFrame.BytesPerPixel; b++)
            {
                pixels[offset + b] = value;
            }
        }

        return new ScrollFrame(width, height, pixels);
    }

    private static byte RowValue(ScrollFrame image, int row) =>
        image.Pixels[row * image.Width * ScrollFrame.BytesPerPixel];

    [Fact]
    public void ScrollStitcher_SeedsFromFirstFrame()
    {
        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);
        ScrollAppendResult result = stitcher.Append(RowCodedFrame(4, 20, baseValue: 0));

        Assert.Equal(ScrollAppendKind.Seeded, result.Kind);
        Assert.Equal(20, stitcher.Height);
        Assert.True(stitcher.HasContent);
    }

    [Fact]
    public void ScrollStitcher_AppendsOnlyNewRowsPastOverlap()
    {
        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);

        // Frame A: rows coded 0..19. Frame B: scrolled down 5 -> rows coded 5..24.
        stitcher.Append(RowCodedFrame(4, 20, baseValue: 0));
        ScrollAppendResult second = stitcher.Append(RowCodedFrame(4, 20, baseValue: 5));

        Assert.Equal(ScrollAppendKind.Appended, second.Kind);
        Assert.Equal(15, second.OverlapRows);
        Assert.Equal(5, second.AppendedRows);
        Assert.Equal(25, stitcher.Height); // 20 + 5 new rows

        // The stitched image must read 0..24 top to bottom with no duplication.
        ScrollFrame image = stitcher.ToImage();
        for (int row = 0; row < 25; row++)
        {
            Assert.Equal((byte)row, RowValue(image, row));
        }
    }

    [Fact]
    public void ScrollStitcher_StopsWhenFrameAddsNoNewContent()
    {
        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);
        stitcher.Append(RowCodedFrame(4, 20, baseValue: 0));

        // Identical frame: full overlap, nothing new -> the end-of-scroll signal.
        ScrollAppendResult duplicate = stitcher.Append(RowCodedFrame(4, 20, baseValue: 0));

        Assert.Equal(ScrollAppendKind.NoNewContent, duplicate.Kind);
        Assert.Equal(20, duplicate.OverlapRows + stitcher.EffectiveHeaderHeight);
        Assert.Equal(0, duplicate.AppendedRows);
        Assert.Equal(20, stitcher.Height);
    }

    [Fact]
    public void ScrollStitcher_ReportsNoOverlapWhenContentJumps()
    {
        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);
        stitcher.Append(RowCodedFrame(4, 20, baseValue: 0));

        // Jump far past a one-frame scroll: rows 100..119 share nothing with 0..19.
        ScrollAppendResult jumped = stitcher.Append(RowCodedFrame(4, 20, baseValue: 100));

        Assert.Equal(ScrollAppendKind.NoOverlap, jumped.Kind);
        Assert.Equal(20, stitcher.Height); // unchanged: the frame was rejected
    }

    [Fact]
    public void ScrollStitcher_ExcludesFixedHeaderFromOutputAndSearch()
    {
        // A 4-row sticky header (coded 200..203) sits atop scrolling content.
        const int width = 4;
        const int header = 4;
        var options = new ScrollStitchOptions(FixedHeaderHeight: header);
        var stitcher = new ScrollStitcher(width, options);

        ScrollFrame Frame(int contentBase)
        {
            byte[] pixels = new byte[width * 20 * ScrollFrame.BytesPerPixel];
            for (int row = 0; row < 20; row++)
            {
                byte value = row < header
                    ? (byte)(200 + row)              // fixed header, identical every frame
                    : (byte)((contentBase + (row - header)) & 0xFF);
                int offset = row * width * ScrollFrame.BytesPerPixel;
                for (int b = 0; b < width * ScrollFrame.BytesPerPixel; b++)
                {
                    pixels[offset + b] = value;
                }
            }

            return new ScrollFrame(width, 20, pixels);
        }

        stitcher.Append(Frame(contentBase: 0));      // header + content 0..15
        ScrollAppendResult second = stitcher.Append(Frame(contentBase: 6)); // content scrolled 6

        Assert.Equal(ScrollAppendKind.Appended, second.Kind);
        Assert.Equal(6, second.AppendedRows);

        ScrollFrame image = stitcher.ToImage();

        // Header appears exactly once at the top.
        for (int row = 0; row < header; row++)
        {
            Assert.Equal((byte)(200 + row), RowValue(image, row));
        }

        // Content follows the header with no duplicated header rows in the middle.
        Assert.Equal((byte)0, RowValue(image, header));
        Assert.Equal(header + 16 + 6, image.Height); // header + first content + 6 new rows
    }

    [Fact]
    public void ScrollFrame_RejectsUndersizedBuffer()
    {
        Assert.Throws<ArgumentException>(() => new ScrollFrame(4, 4, new byte[10]));
    }

    [Fact]
    public void RegionHistoryEntry_RemapsOriginAndDpiThenClamps()
    {
        var entry = new RegionHistoryEntry(
            new RectD(100, 50, 200, 100),
            "DISPLAY-A",
            new RectD(0, 0, 1920, 1080),
            96);

        RectD? mapped = entry.ResolveForMonitor(new RectD(-2560, 100, 2560, 1440), 192);

        Assert.Equal(new RectD(-2360, 200, 400, 200), mapped);
    }

    [Fact]
    public void RegionHistoryEntry_ClampsMappedSelectionIntoCurrentMonitor()
    {
        var entry = new RegionHistoryEntry(
            new RectD(1300, 700, 200, 200),
            "DISPLAY-A",
            new RectD(0, 0, 1920, 1080),
            96);

        RectD? mapped = entry.ResolveForMonitor(new RectD(500, 0, 1366, 768), 96);

        Assert.Equal(new RectD(1666, 568, 200, 200), mapped);
    }

    [Fact]
    public void LastRegionStore_PreservesMonitorMetadata()
    {
        var store = new LastRegionStore(() => 5);
        store.Record(new RegionHistoryEntry(
            new RectD(10, 20, 30, 40),
            "DISPLAY-X",
            new RectD(-100, 0, 100, 100),
            144));

        RegionHistoryEntry entry = Assert.Single(store.SnapshotEntries());
        Assert.Equal("DISPLAY-X", entry.MonitorDeviceName);
        Assert.Equal(144u, entry.MonitorDpi);
        Assert.Equal(new RectD(-100, 0, 100, 100), entry.MonitorBounds);
    }

    [Fact]
    public void ScrollStitcher_AutoDetectsFixedHeaderAndWritesItOnce()
    {
        const int width = 4;
        const int header = 4;
        ScrollFrame Frame(int contentBase)
        {
            byte[] pixels = new byte[width * 20 * ScrollFrame.BytesPerPixel];
            for (int row = 0; row < 20; row++)
            {
                byte value = row < header ? (byte)(220 + row) : (byte)(contentBase + row - header);
                Array.Fill(pixels, value, row * width * 4, width * 4);
            }
            return new ScrollFrame(width, 20, pixels);
        }

        var stitcher = new ScrollStitcher(width, ScrollStitchOptions.Default);
        stitcher.Append(Frame(0));
        ScrollAppendResult result = stitcher.Append(Frame(6));

        Assert.Equal(header, stitcher.EffectiveHeaderHeight);
        Assert.Equal(ScrollAppendKind.Appended, result.Kind);
        Assert.Equal(26, stitcher.Height);
        ScrollFrame image = stitcher.ToImage();
        for (int row = 0; row < header; row++)
        {
            Assert.Equal((byte)(220 + row), RowValue(image, row));
        }
    }

    [Fact]
    public void ScrollStitcher_NoStaticPrefixKeepsHeaderAtZero()
    {
        var stitcher = new ScrollStitcher(4, ScrollStitchOptions.Default);
        stitcher.Append(RowCodedFrame(4, 20, 0));
        ScrollAppendResult result = stitcher.Append(RowCodedFrame(4, 20, 5));

        Assert.Equal(0, stitcher.EffectiveHeaderHeight);
        Assert.Equal(ScrollAppendKind.Appended, result.Kind);
    }

    [Fact]
    public void ScrollStitcher_RejectsGrowthAtLimitBeforeMutation()
    {
        var options = ScrollStitchOptions.Default with
        {
            MaxOutputBytes = 4L * 4 * 24,
            MaxOutputHeight = 24,
        };
        var stitcher = new ScrollStitcher(4, options);
        stitcher.Append(RowCodedFrame(4, 20, 0));

        ScrollAppendResult result = stitcher.Append(RowCodedFrame(4, 20, 5));

        Assert.Equal(ScrollAppendKind.LimitReached, result.Kind);
        Assert.Equal(20, stitcher.Height);
        Assert.Equal(20, stitcher.ToImage().Height);
    }

    // ----- FixedRegionPlanner -----

    [Fact]
    public void FixedRegion_CentresOnCursorWhenFullyInside()
    {
        // 200x100 box centred on (500,400) inside a 1920x1080 monitor at origin.
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            200, 100, new PointD(500, 400), new RectD(0, 0, 1920, 1080));

        Assert.NotNull(region);
        Assert.Equal(400, region!.Value.Left);   // 500 - 200/2
        Assert.Equal(350, region.Value.Top);      // 400 - 100/2
        Assert.Equal(200, region.Value.Width);
        Assert.Equal(100, region.Value.Height);
    }

    [Fact]
    public void FixedRegion_SlidesInsideAtTopLeftEdgeKeepingFullSize()
    {
        // Cursor in the very corner: the centred box would spill off the top-left, so it is
        // slid wholly inside while keeping its full requested size.
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            200, 100, new PointD(0, 0), new RectD(0, 0, 1920, 1080));

        Assert.NotNull(region);
        Assert.Equal(0, region!.Value.Left);
        Assert.Equal(0, region.Value.Top);
        Assert.Equal(200, region.Value.Width);
        Assert.Equal(100, region.Value.Height);
    }

    [Fact]
    public void FixedRegion_SlidesInsideAtBottomRightEdgeKeepingFullSize()
    {
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            200, 100, new PointD(1920, 1080), new RectD(0, 0, 1920, 1080));

        Assert.NotNull(region);
        Assert.Equal(1720, region!.Value.Left);   // 1920 - 200
        Assert.Equal(980, region.Value.Top);        // 1080 - 100
        Assert.Equal(200, region.Value.Width);
        Assert.Equal(100, region.Value.Height);
    }

    [Fact]
    public void FixedRegion_HonoursNonZeroMonitorOrigin()
    {
        // A second monitor to the right of the primary: bounds start at x=1920.
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            300, 200, new PointD(1920, 0), new RectD(1920, 0, 1366, 768));

        Assert.NotNull(region);
        // Centred box spills off the monitor's left and top, so it clamps to the origin.
        Assert.Equal(1920, region!.Value.Left);
        Assert.Equal(0, region.Value.Top);
        Assert.Equal(300, region.Value.Width);
        Assert.Equal(200, region.Value.Height);
    }

    [Fact]
    public void FixedRegion_ShrinksToMonitorWhenRequestExceedsIt()
    {
        // A request larger than the monitor is reduced per-axis to the monitor size.
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            4000, 3000, new PointD(683, 384), new RectD(0, 0, 1366, 768));

        Assert.NotNull(region);
        Assert.Equal(0, region!.Value.Left);
        Assert.Equal(0, region.Value.Top);
        Assert.Equal(1366, region.Value.Width);
        Assert.Equal(768, region.Value.Height);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-10, 100)]
    [InlineData(100, -5)]
    public void FixedRegion_RejectsNonPositiveSize(int width, int height)
    {
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            width, height, new PointD(500, 400), new RectD(0, 0, 1920, 1080));

        Assert.Null(region);
    }

    [Fact]
    public void FixedRegion_RejectsEmptyMonitor()
    {
        RectD? region = FixedRegionPlanner.PlaceAtCursor(
            200, 100, new PointD(0, 0), new RectD(0, 0, 0, 1080));

        Assert.Null(region);
    }
}
