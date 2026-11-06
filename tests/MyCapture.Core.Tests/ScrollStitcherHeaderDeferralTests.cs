using MyCapture.Core.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

public sealed class ScrollStitcherHeaderDeferralTests(ITestOutputHelper output)
{
    private const int Width = 16;
    private const int FrameHeight = 400;
    private const int Stride = Width * ScrollFrame.BytesPerPixel;
    private const int StickyHeaderHeight = 40;

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(392)]
    public void Bug_DuplicateBeforeFirstMove_EqualsDirectMove(int shift)
    {
        ScrollFrame seed = Frame(0);
        ScrollFrame moved = Frame(shift);
        var direct = NewStitcher();
        var deferred = NewStitcher();
        Assert.Equal(direct.Append(seed), deferred.Append(seed));

        AssertUnchangedSeed(deferred, seed, seed);

        ScrollAppendResult expected = direct.Append(moved);
        ScrollAppendResult actual = deferred.Append(moved);
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.Appended, FrameHeight - shift, shift), actual);
        Assert.Equal(expected, actual);
        AssertSameState(direct, deferred);
        Assert.Equal(0, deferred.EffectiveHeaderHeight);
        Assert.Equal(FrameHeight + shift, deferred.Height);
        Assert.Equal(Frame(0, FrameHeight + shift).Pixels, deferred.ToImage().Pixels);
        Assert.False(deferred.LastOverlapSearch.StoppedAtWorkLimit);
    }

    [Fact]
    public void Bug_SeveralDuplicatesBeforeFirstMove_KeepSeedUntilDirectMove()
    {
        ScrollFrame seed = Frame(0);
        var direct = NewStitcher();
        var deferred = NewStitcher();
        direct.Append(seed);
        deferred.Append(seed);
        for (int i = 0; i < 3; i++)
        {
            AssertUnchangedSeed(deferred, seed, seed);
            Assert.Equal(140, deferred.LastOverlapSearch.HeaderRowComparisons);
            Assert.Equal(260, deferred.LastOverlapSearch.UnchangedFrameRowComparisons);
        }

        Assert.Equal(direct.Append(Frame(300)), deferred.Append(Frame(300)));
        AssertSameState(direct, deferred);
        Assert.Equal(Frame(0, 700).Pixels, deferred.ToImage().Pixels);
    }

    [Fact]
    public void Bug_ToleranceDuplicate_DefersWithoutWritingCaretPixels()
    {
        ScrollFrame seed = Frame(0);
        byte[] caretPixels = (byte[])seed.Pixels.Clone();
        foreach (int firstRow in new[] { 120, 300 })
        {
            for (int row = firstRow; row < firstRow + 16; row++)
            {
                // A single changed red byte reaches, but does not exceed, floor(64*.02).
                caretPixels[row * Stride + 2] ^= 1;
            }
        }

        var caret = new ScrollFrame(Width, FrameHeight, caretPixels);
        var direct = NewStitcher();
        var deferred = NewStitcher();
        direct.Append(seed);
        deferred.Append(seed);
        AssertUnchangedSeed(deferred, seed, caret);
        Assert.Equal(140, deferred.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(260, deferred.LastOverlapSearch.UnchangedFrameRowComparisons);

        Assert.Equal(direct.Append(Frame(300)), deferred.Append(Frame(300)));
        AssertSameState(direct, deferred);
        Assert.Equal(Frame(0, 700).Pixels, deferred.ToImage().Pixels);
    }

    [Fact]
    public void AsToday_OverToleranceBottomChange_ResolvesHeaderImmediately()
    {
        ScrollFrame seed = Frame(0);
        byte[] changedPixels = (byte[])seed.Pixels.Clone();
        changedPixels[(FrameHeight - 1) * Stride] ^= 1;
        changedPixels[(FrameHeight - 1) * Stride + 1] ^= 1;
        var changed = new ScrollFrame(Width, FrameHeight, changedPixels);
        var actual = NewStitcher();
        var reference = new ReferenceScrollStitcher(Width, ScrollStitchOptions.Default);

        AssertAsToday(actual, reference, seed);
        Assert.Equal(ScrollAppendKind.NoOverlap, AssertAsToday(actual, reference, changed).Kind);
        Assert.Equal(140, actual.EffectiveHeaderHeight);
        Assert.Equal(140, actual.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(1, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
        Assert.Equal(seed.Pixels, actual.ToImage().Pixels);
        AssertAsToday(actual, reference, Frame(300));
        Assert.Equal(0, actual.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
    }

    [Fact]
    public void Bug_RealStickyHeaderAfterDuplicate_ResolvesAtFirstContentMove()
    {
        ScrollFrame seed = StickyFrame(0);
        ScrollFrame moved = StickyFrame(300);
        var direct = NewStitcher();
        var deferred = NewStitcher();
        var reference = new ReferenceScrollStitcher(Width, ScrollStitchOptions.Default);
        AssertAsToday(direct, reference, seed);
        deferred.Append(seed);
        AssertUnchangedSeed(deferred, seed, seed);

        ScrollAppendResult expected = AssertAsToday(direct, reference, moved);
        ScrollAppendResult actual = deferred.Append(moved);
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.Appended, 60, 300), actual);
        Assert.Equal(expected, actual);
        AssertSameState(direct, deferred);
        Assert.Equal(StickyHeaderHeight, deferred.EffectiveHeaderHeight);
        Assert.Equal(700, deferred.Height);
        // The complete expected image contains one header followed by content rows 0..659.
        Assert.Equal(StickyFrame(0, 700).Pixels, deferred.ToImage().Pixels);
        Assert.False(deferred.LastOverlapSearch.StoppedAtWorkLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsToday_EndOfPageDuplicateAfterMove_PreservesCanvasAndResolvedHeader(bool stickyHeader)
    {
        ScrollFrame seed = stickyHeader ? StickyFrame(0) : Frame(0);
        ScrollFrame moved = stickyHeader ? StickyFrame(300) : Frame(300);
        var actual = NewStitcher();
        var reference = new ReferenceScrollStitcher(Width, ScrollStitchOptions.Default);
        AssertAsToday(actual, reference, seed);
        AssertAsToday(actual, reference, moved);
        byte[] before = actual.ToImage().Pixels;
        int heightBefore = actual.Height;
        int headerBefore = actual.EffectiveHeaderHeight;

        ScrollAppendResult duplicate = AssertAsToday(actual, reference, moved);
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoNewContent, FrameHeight - headerBefore, 0), duplicate);
        Assert.Equal(heightBefore, actual.Height);
        Assert.Equal(headerBefore, actual.EffectiveHeaderHeight);
        Assert.Equal(before, actual.ToImage().Pixels);
        Assert.Equal(0, actual.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsToday_OptionsWithoutAutomaticDetection_KeepExistingSequence(bool explicitHeader)
    {
        ScrollStitchOptions options = explicitHeader
            ? ScrollStitchOptions.Default with { FixedHeaderHeight = StickyHeaderHeight }
            : ScrollStitchOptions.Default with { AutoDetectFixedHeader = false };
        ScrollFrame seed = explicitHeader ? StickyFrame(0) : Frame(0);
        ScrollFrame moved = explicitHeader ? StickyFrame(300) : Frame(300);
        var actual = new ScrollStitcher(Width, options);
        var reference = new ReferenceScrollStitcher(Width, options);
        foreach (ScrollFrame frame in new[] { seed, seed, moved })
        {
            AssertAsToday(actual, reference, frame);
            Assert.Equal(0, actual.LastOverlapSearch.HeaderRowComparisons);
            Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
        }

        Assert.Equal(700, actual.Height);
        Assert.Equal(explicitHeader ? StickyHeaderHeight : 0, actual.EffectiveHeaderHeight);
        Assert.Equal(explicitHeader ? StickyFrame(0, 700).Pixels : Frame(0, 700).Pixels, actual.ToImage().Pixels);
    }

    [Fact]
    public void AsToday_DifferentHeightWithEqualTopRows_ResolvesWithoutUnchangedScan()
    {
        var actual = NewStitcher();
        var reference = new ReferenceScrollStitcher(Width, ScrollStitchOptions.Default);
        AssertAsToday(actual, reference, Frame(0));
        AssertAsToday(actual, reference, Frame(0, 300));
        Assert.Equal(105, actual.EffectiveHeaderHeight);
        Assert.Equal(105, actual.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
        Assert.Equal(FrameHeight, actual.Height);
        Assert.Equal(Frame(0).Pixels, actual.ToImage().Pixels);
    }

    [Fact]
    public void Bug_ExactDuplicateStatistics_SeparatePrefixAndRemainingRows()
    {
        ScrollFrame seed = Frame(0);
        var stitcher = NewStitcher();
        stitcher.Append(seed);
        Assert.Equal(default, stitcher.LastOverlapSearch);
        AssertUnchangedSeed(stitcher, seed, seed);
        ScrollOverlapSearchStatistics stats = stitcher.LastOverlapSearch;
        Assert.Equal(140, stats.HeaderRowComparisons);
        Assert.Equal(260, stats.UnchangedFrameRowComparisons);
        Assert.Equal(1, stats.CandidatesConsidered);
        Assert.Equal(FrameHeight, stats.RowPairChecks);
        Assert.Equal(0, stats.PixelRowComparisons);
        Assert.Equal(FrameHeight, stats.VerificationRowComparisons);
        Assert.Equal(ScrollStitcher.WorkLimitPerOverlapRow * FrameHeight + ScrollStitcher.WorkLimitBase, stats.WorkLimit);
        Assert.False(stats.StoppedAtWorkLimit);
    }

    [Theory]
    [InlineData(1280, 720, 251, 462, 930)]
    [InlineData(1920, 1080, 378, 695, 1396)]
    [InlineData(2560, 1440, 503, 930, 1866)]
    public void AsToday_Issue246Statistics_AddOnlyOneBottomRowComparison(
        int width, int height, int expectedHeader, int expectedCandidates, long expectedPairs)
    {
        int stride = width * ScrollFrame.BytesPerPixel;
        byte[] seedPixels = new byte[stride * height];
        seedPixels.AsSpan().Fill(23);
        seedPixels.AsSpan((height - 1) * stride, stride).Fill(97);
        byte[] incomingPixels = new byte[seedPixels.Length];
        incomingPixels.AsSpan().Fill(23);
        var stitcher = new ScrollStitcher(width, ScrollStitchOptions.Default);
        stitcher.Append(new ScrollFrame(width, height, seedPixels));

        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoOverlap, 0, 0),
            stitcher.Append(new ScrollFrame(width, height, incomingPixels)));
        ScrollOverlapSearchStatistics stats = stitcher.LastOverlapSearch;
        Assert.Equal(height, stitcher.Height);
        Assert.Equal(seedPixels, stitcher.ToImage().Pixels);
        Assert.Equal(expectedHeader, stitcher.EffectiveHeaderHeight);
        Assert.Equal(stitcher.EffectiveHeaderHeight, stats.HeaderRowComparisons);
        Assert.Equal(1, stats.UnchangedFrameRowComparisons);
        // Exact counts from the completed #246 run are retained, not just looser O(n) bounds.
        Assert.Equal(expectedCandidates, stats.CandidatesConsidered);
        Assert.Equal(expectedPairs, stats.RowPairChecks);
        Assert.Equal(1, stats.PixelRowComparisons);
        Assert.Equal(0, stats.VerificationRowComparisons);
        Assert.Equal(2, stats.DistinctRowClasses);
        Assert.False(stats.StoppedAtWorkLimit);
        output.WriteLine($"Issue {width}x{height}: header={stitcher.EffectiveHeaderHeight}, " +
            $"candidates={stats.CandidatesConsidered}, pairs={stats.RowPairChecks}, " +
            $"pixels={stats.PixelRowComparisons}, verification={stats.VerificationRowComparisons}, " +
            $"classes={stats.DistinctRowClasses}, headerComparisons={stats.HeaderRowComparisons}, " +
            $"unchangedComparisons={stats.UnchangedFrameRowComparisons}, limit={stats.WorkLimit}");
    }

    [Fact]
    public void Control_DirectShift300_MatchesReferenceAndWholeDocument()
    {
        var actual = NewStitcher();
        var reference = new ReferenceScrollStitcher(Width, ScrollStitchOptions.Default);
        AssertAsToday(actual, reference, Frame(0));
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.Appended, 100, 300),
            AssertAsToday(actual, reference, Frame(300)));
        Assert.Equal(0, actual.EffectiveHeaderHeight);
        Assert.Equal(700, actual.Height);
        Assert.Equal(Frame(0, 700).Pixels, actual.ToImage().Pixels);
        Assert.Equal(1, actual.LastOverlapSearch.HeaderRowComparisons);
        Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsToday_ZeroHeaderRoom_ResolvesWithoutDeferral(bool zeroRatioCap)
    {
        ScrollStitchOptions options = zeroRatioCap
            ? ScrollStitchOptions.Default with { MaxAutoHeaderRatio = 0 }
            : ScrollStitchOptions.Default with { MinOverlapRows = 1 };
        ScrollFrame[] frames = zeroRatioCap
            ? [Frame(0), Frame(0), Frame(300), Frame(300)]
            : [Frame(0, 1), Frame(0, 1), Frame(0), Frame(0)];
        var actual = new ScrollStitcher(Width, options);
        var reference = new ReferenceScrollStitcher(Width, options);
        foreach (ScrollFrame frame in frames)
        {
            AssertAsToday(actual, reference, frame);
            Assert.Equal(0, actual.EffectiveHeaderHeight);
            Assert.Equal(0, actual.LastOverlapSearch.HeaderRowComparisons);
            Assert.Equal(0, actual.LastOverlapSearch.UnchangedFrameRowComparisons);
        }

        // In the one-row case the later canvas grows to 400 rows. The final duplicate
        // still does not scan a header, proving that maxRows<=0 resolved permanently.
        Assert.Equal(zeroRatioCap ? 700 : FrameHeight, actual.Height);
    }

    private static ScrollStitcher NewStitcher() => new(Width, ScrollStitchOptions.Default);

    private static void AssertUnchangedSeed(ScrollStitcher stitcher, ScrollFrame seed, ScrollFrame duplicate)
    {
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoNewContent, FrameHeight, 0), stitcher.Append(duplicate));
        Assert.Equal(0, stitcher.EffectiveHeaderHeight);
        Assert.Equal(FrameHeight, stitcher.Height);
        Assert.Equal(seed.Pixels, stitcher.ToImage().Pixels);
        Assert.False(stitcher.LastOverlapSearch.StoppedAtWorkLimit);
    }

    private static void AssertSameState(ScrollStitcher expected, ScrollStitcher actual)
    {
        Assert.Equal(expected.EffectiveHeaderHeight, actual.EffectiveHeaderHeight);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.ToImage().Pixels, actual.ToImage().Pixels);
    }

    private static ScrollAppendResult AssertAsToday(
        ScrollStitcher actual, ReferenceScrollStitcher reference, ScrollFrame frame)
    {
        ScrollAppendResult expected = reference.Append(frame);
        ScrollAppendResult result = actual.Append(frame);
        Assert.Equal(expected, result);
        Assert.Equal(reference.EffectiveHeaderHeight, actual.EffectiveHeaderHeight);
        Assert.Equal(reference.Height, actual.Height);
        Assert.Equal(reference.ToImage().Pixels, actual.ToImage().Pixels);
        Assert.False(actual.LastOverlapSearch.StoppedAtWorkLimit);
        return result;
    }

    private static ScrollFrame Frame(int top, int height = FrameHeight)
    {
        byte[] pixels = new byte[Stride * height];
        for (int row = 0; row < height; row++)
        {
            WriteDocumentRow(pixels.AsSpan(row * Stride, Stride), top + row);
        }

        return new ScrollFrame(Width, height, pixels);
    }

    private static ScrollFrame StickyFrame(int contentTop, int height = FrameHeight)
    {
        byte[] pixels = new byte[Stride * height];
        for (int row = 0; row < height; row++)
        {
            int documentRow = row < StickyHeaderHeight ? 10000 + row : contentTop + row - StickyHeaderHeight;
            WriteDocumentRow(pixels.AsSpan(row * Stride, Stride), documentRow);
        }

        return new ScrollFrame(Width, height, pixels);
    }

    private static void WriteDocumentRow(Span<byte> row, int documentRow)
    {
        // Repeating the row code in all sixteen pixels makes any two distinct document
        // rows differ in at least sixteen bytes, well above the one-byte tolerance.
        for (int pixel = 0; pixel < Width; pixel++)
        {
            int offset = pixel * ScrollFrame.BytesPerPixel;
            row[offset] = (byte)(documentRow & 0xff);
            row[offset + 1] = (byte)(documentRow >> 8);
            row[offset + 2] = (byte)((documentRow * 37) & 0xff);
            row[offset + 3] = 255;
        }
    }
}
