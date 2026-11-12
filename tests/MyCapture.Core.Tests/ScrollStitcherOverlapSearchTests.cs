using MyCapture.Core.Capture;
using System.Runtime.Intrinsics;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

public sealed class ScrollStitcherOverlapSearchTests(ITestOutputHelper output)
{
    [Fact]
    public void RowsMatch_EqualsScalarPredicateAtToleranceAndVectorBoundaries()
    {
        output.WriteLine($"Vector256 accelerated={Vector256.IsHardwareAccelerated}, Vector128 accelerated={Vector128.IsHardwareAccelerated}");
        int[] lengths = [0, 1, 3, 15, 16, 17, 31, 32, 33, 47, 63, 64, 65, 95, 129];
        int[] limits = [0, 1, 3, 17];
        var random = new Random(246);
        foreach (int length in lengths)
        {
            byte[] left = new byte[length];
            random.NextBytes(left);
            foreach (int limit in limits)
            {
                int[] counts = [0, Math.Max(0, limit - 1), limit, limit + 1, length];
                int[] starts = [0, length / 2, Math.Max(0, length - 1), length / 32 * 32];
                foreach (int requestedCount in counts)
                {
                    foreach (int start in starts)
                    {
                        byte[] right = (byte[])left.Clone();
                        int count = Math.Min(length, requestedCount);
                        for (int i = 0; i < count; i++)
                        {
                            right[(start + i) % length] ^= (byte)random.Next(1, 256);
                        }

                        Assert.Equal(ScalarRowsMatch(left, right, limit),
                            ScrollStitcher.RowsMatch(left, right, limit));
                        Assert.Equal(ScalarRowsMatch(right, left, limit),
                            ScrollStitcher.RowsMatch(right, left, limit));
                    }
                }
            }
        }
    }

    [Fact]
    public void RowsMatch_PreservesEqualRowBehaviorForNegativeConvertedLimits()
    {
        // NaN is accepted by the existing options validation. Its integer conversion may
        // yield a negative limit: the original loop still accepts a completely equal row.
        byte[] row = new byte[65];
        byte[] different = new byte[65];
        different[^1] = 1;
        foreach (int limit in new[] { -1, int.MinValue })
        {
            Assert.True(ScrollStitcher.RowsMatch(row, row, limit));
            Assert.Equal(ScalarRowsMatch(row, different, limit),
                ScrollStitcher.RowsMatch(row, different, limit));
        }
    }

    [Fact]
    public void DifferentialCorpus_MatchesOriginalAcrossSuccessiveAppends()
    {
        var scenarios = new List<(string Family, int Period, int Marker)>();
        scenarios.AddRange(new[] { "noise", "text", "scrollbar", "gutter", "at-limit", "over-limit" }
            .Select(family => (family, 0, -1)));
        scenarios.AddRange(new[] { 1, 2, 7, 13 }.Select(period => ("stripes", period, -1)));
        scenarios.AddRange(new[] { 5, 17, 31 }.Select(period => ("grid", period, -1)));
        foreach (int period in new[] { 16, 32, 60, 120 })
        {
            foreach (int marker in new[] { -1, 0, 96, 191 })
            {
                scenarios.Add(("table", period, marker));
            }
        }

        foreach (int seed in new[] { 246, 947 })
        {
            foreach ((string family, int period, int marker) in scenarios)
            {
                int width = family is "scrollbar" or "gutter" ? 128 : 32;
                var options = ScrollStitchOptions.Default with
                {
                    AutoDetectFixedHeader = false,
                    MaxRowMismatchRatio = family is "at-limit" or "over-limit" ? 4.0 / (width * 4) : 0.02,
                };
                CheckScenario(width, options, [192, 192, 192, 192, 192], [0, 7, 19, 39, 39],
                    (height, offset, append) => PatternFrame(width, height, offset, family,
                        period, marker, seed, append));
            }

            foreach (int minOverlap in new[] { 1, 8, 17 })
            {
                foreach (int headerMode in new[] { 0, 1, 2 })
                {
                    // Mode 0 has no header, 1 explicitly sets it, 2 detects it.
                    int header = headerMode == 0 ? 0 : 6;
                    var options = ScrollStitchOptions.Default with
                    {
                        MinOverlapRows = minOverlap,
                        FixedHeaderHeight = headerMode == 1 ? header : 0,
                        AutoDetectFixedHeader = headerMode == 2,
                    };
                    CheckScenario(32, options, [160, 144, 176, 128, 128], [0, 11, 29, 43, 43],
                        (height, offset, append) => PatternFrame(32, height, offset, "noise",
                            0, -1, seed, append, header));
                }
            }

            foreach (int width in new[] { 1, 2, 3, 5, 7, 9, 33 })
            {
                // Exercise hash/vector tails as well as complete 32-byte hash chunks.
                var options = ScrollStitchOptions.Default with { AutoDetectFixedHeader = false };
                CheckScenario(width, options, [48, 48, 48, 48], [0, 5, 11, 11],
                    (height, offset, append) => PatternFrame(width, height, offset, "noise",
                        0, -1, seed, append));
            }
        }
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void RepeatedRowsWithDifferentLastRow_RejectsWithLinearPairChecks(int width, int height)
    {
        int stride = width * ScrollFrame.BytesPerPixel;
        byte[] seedPixels = new byte[stride * height];
        seedPixels.AsSpan().Fill(23);
        seedPixels.AsSpan((height - 1) * stride, stride).Fill(97);
        byte[] incomingPixels = new byte[seedPixels.Length];
        incomingPixels.AsSpan().Fill(23);
        var stitcher = new ScrollStitcher(width, ScrollStitchOptions.Default);
        Assert.Equal(ScrollAppendKind.Seeded, stitcher.Append(new ScrollFrame(width, height, seedPixels)).Kind);
        Assert.Equal(default, stitcher.LastOverlapSearch);

        ScrollAppendResult result = stitcher.Append(new ScrollFrame(width, height, incomingPixels));
        ScrollOverlapSearchStatistics stats = stitcher.LastOverlapSearch;
        int n = height - stitcher.EffectiveHeaderHeight;
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoOverlap, 0, 0), result);
        Assert.Equal(height, stitcher.Height);
        Assert.Equal(seedPixels, stitcher.ToImage().Pixels);
        Assert.False(stats.StoppedAtWorkLimit);
        Assert.Equal(n - ScrollStitchOptions.Default.MinOverlapRows + 1, stats.CandidatesConsidered);
        Assert.Equal(2, stats.DistinctRowClasses);
        Assert.InRange(stats.PixelRowComparisons, 1, 2);
        // The first candidate scans n pairs. Its final canvas row then kills every
        // remaining candidate; the one unequal class pair is memoized after its first use.
        Assert.True(stats.RowPairChecks <= 2L * n);
        Assert.Equal(0, stats.VerificationRowComparisons);
        Assert.Equal(stitcher.EffectiveHeaderHeight, stats.HeaderRowComparisons);
        Assert.Equal(ScrollStitcher.WorkLimitPerOverlapRow * n + ScrollStitcher.WorkLimitBase, stats.WorkLimit);
        output.WriteLine($"Issue {width}x{height}: n={n}, header={stitcher.EffectiveHeaderHeight}, " +
            $"candidates={stats.CandidatesConsidered}, pairs={stats.RowPairChecks}, " +
            $"pixels={stats.PixelRowComparisons}, verification={stats.VerificationRowComparisons}, " +
            $"headerComparisons={stats.HeaderRowComparisons}, classes={stats.DistinctRowClasses}, limit={stats.WorkLimit}");
    }

    [Theory]
    [InlineData("stripes", 13)]
    [InlineData("grid", 31)]
    [InlineData("table", 120)]
    public void LargeRepeatingPatterns_WithUniqueAnchor_FindTrueScrollInLinearWork(string family, int period)
    {
        const int width = 2560;
        const int height = 1440;
        const int scroll = 73;
        const int marker = 1100;
        var options = ScrollStitchOptions.Default with { AutoDetectFixedHeader = false, MaxRowMismatchRatio = 0 };
        var stitcher = new ScrollStitcher(width, options);
        stitcher.Append(PatternFrame(width, height, 0, family, period, marker, 246, 0));
        ScrollAppendResult result = stitcher.Append(PatternFrame(width, height, scroll, family, period, marker, 246, 0));
        ScrollOverlapSearchStatistics stats = stitcher.LastOverlapSearch;

        // Both frames contain the single all-251 row. A longer candidate necessarily
        // misaligns it, so periodic content cannot manufacture a larger valid overlap.
        Assert.Equal(new ScrollAppendResult(ScrollAppendKind.Appended, height - scroll, scroll), result);
        Assert.False(stats.StoppedAtWorkLimit);
        Assert.True(stats.PixelRowComparisons <= 2 * height);
        Assert.True(stats.RowPairChecks <= 4L * height);
        Assert.Equal(height - scroll, stats.VerificationRowComparisons);
        Assert.Equal(0, stats.HeaderRowComparisons);
        Assert.Equal(PatternFrame(width, height + scroll, 0, family, period, marker, 246, 0).Pixels,
            stitcher.ToImage().Pixels);
        output.WriteLine($"True scroll {family}: candidates={stats.CandidatesConsidered}, " +
            $"pairs={stats.RowPairChecks}, pixels={stats.PixelRowComparisons}, " +
            $"verification={stats.VerificationRowComparisons}, classes={stats.DistinctRowClasses}");
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(128, true)]
    [InlineData(640, true)]
    public void PairSpecificMismatches_ObeyLimitAndKeepCanvasWhenStopped(int n, bool shouldStop)
    {
        int width = n / ScrollFrame.BytesPerPixel;
        var options = ScrollStitchOptions.Default with
        {
            AutoDetectFixedHeader = false,
            MaxRowMismatchRatio = (n - 1) / (double)n,
        };
        (ScrollFrame seed, ScrollFrame incoming) = AdversarialFrames(n);
        var stitcher = new ScrollStitcher(width, options);
        var reference = new ReferenceScrollStitcher(width, options);
        Assert.Equal(reference.Append(seed), stitcher.Append(seed));
        ScrollAppendResult expected = reference.Append(incoming);
        ScrollAppendResult actual = stitcher.Append(incoming);
        ScrollOverlapSearchStatistics stats = stitcher.LastOverlapSearch;
        Assert.Equal(2 * n, stats.DistinctRowClasses);
        Assert.Equal(shouldStop, stats.StoppedAtWorkLimit);
        Assert.True(stats.PixelRowComparisons <= stats.WorkLimit);
        Assert.Equal(ScrollStitcher.WorkLimitPerOverlapRow * n + ScrollStitcher.WorkLimitBase, stats.WorkLimit);
        if (shouldStop)
        {
            Assert.Equal(ScrollAppendKind.Appended, expected.Kind);
            Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoOverlap, 0, 0), actual);
            Assert.Equal(stats.WorkLimit, stats.PixelRowComparisons);
            Assert.Equal(0, stats.VerificationRowComparisons);
            Assert.Equal(n, stitcher.Height);
            Assert.Equal(seed.Pixels, stitcher.ToImage().Pixels);
            // The same search must start fresh, including a cleared sparse/dense memo.
            Assert.Equal(actual, stitcher.Append(incoming));
            Assert.Equal(stats, stitcher.LastOverlapSearch);
            Assert.Equal(seed.Pixels, stitcher.ToImage().Pixels);
            // A previous cap must not poison a later valid duplicate, nor its statistics.
            Assert.Equal(new ScrollAppendResult(ScrollAppendKind.NoNewContent, n, 0), stitcher.Append(seed));
            Assert.False(stitcher.LastOverlapSearch.StoppedAtWorkLimit);
            Assert.Equal(0, stitcher.LastOverlapSearch.PixelRowComparisons);
            Assert.Equal(n, stitcher.LastOverlapSearch.VerificationRowComparisons);
        }
        else
        {
            Assert.Equal(expected, actual);
            Assert.Equal(reference.Height, stitcher.Height);
            Assert.Equal(reference.EffectiveHeaderHeight, stitcher.EffectiveHeaderHeight);
            Assert.Equal(reference.ToImage().Pixels, stitcher.ToImage().Pixels);
            Assert.Equal(actual.OverlapRows, stats.VerificationRowComparisons);
        }

        output.WriteLine($"Adversary n={n}: stopped={stats.StoppedAtWorkLimit}, candidates={stats.CandidatesConsidered}, " +
            $"pairs={stats.RowPairChecks}, pixels={stats.PixelRowComparisons}, classes={stats.DistinctRowClasses}");
    }

    [Fact]
    public void Statistics_AreDefaultWhenSeededOrNoSearchCanRun()
    {
        var options = ScrollStitchOptions.Default with { AutoDetectFixedHeader = false };
        var stitcher = new ScrollStitcher(8, options);
        ScrollFrame seed = PatternFrame(8, 20, 0, "noise", 0, -1, 246, 0);
        Assert.Equal(default, stitcher.LastOverlapSearch);
        stitcher.Append(seed);
        Assert.Equal(default, stitcher.LastOverlapSearch);
        stitcher.Append(seed);
        Assert.Equal(1, stitcher.LastOverlapSearch.CandidatesConsidered);
        Assert.Equal(ScrollAppendKind.NoOverlap,
            stitcher.Append(PatternFrame(8, 7, 0, "noise", 0, -1, 246, 0)).Kind);
        Assert.Equal(default, stitcher.LastOverlapSearch);

        var tooSmall = new ScrollStitcher(8, options with { MaxOutputHeight = 10 });
        Assert.Equal(ScrollAppendKind.LimitReached, tooSmall.Append(seed).Kind);
        Assert.Equal(default, tooSmall.LastOverlapSearch);
    }

    private static void CheckScenario(int width, ScrollStitchOptions options, int[] heights, int[] offsets,
        Func<int, int, int, ScrollFrame> makeFrame)
    {
        var actual = new ScrollStitcher(width, options);
        var reference = new ReferenceScrollStitcher(width, options);
        for (int i = 0; i < heights.Length; i++)
        {
            ScrollFrame frame = makeFrame(heights[i], offsets[i], i);
            ScrollAppendResult expectedResult = reference.Append(frame);
            ScrollAppendResult actualResult = actual.Append(frame);
            Assert.Equal(expectedResult, actualResult);
            Assert.Equal(reference.Height, actual.Height);
            Assert.Equal(reference.EffectiveHeaderHeight, actual.EffectiveHeaderHeight);
            Assert.False(actual.LastOverlapSearch.StoppedAtWorkLimit);
            Assert.Equal(reference.ToImage().Pixels, actual.ToImage().Pixels);
            // A successful candidate is verified exactly once; rejected candidates do not
            // pass the class predicate and therefore cannot consume verification rows.
            Assert.Equal(actualResult.OverlapRows, actual.LastOverlapSearch.VerificationRowComparisons);
        }
    }

    private static ScrollFrame PatternFrame(int width, int height, int offset, string family, int period,
        int marker, int seed, int append, int header = 0)
    {
        int stride = width * ScrollFrame.BytesPerPixel;
        byte[] pixels = new byte[stride * height];
        for (int row = 0; row < height; row++)
        {
            Span<byte> bytes = pixels.AsSpan(row * stride, stride);
            if (row < header)
            {
                bytes.Fill((byte)(200 + row));
                continue;
            }

            int worldRow = offset + row - header;
            if (worldRow == marker)
            {
                bytes.Fill(251);
                continue;
            }

            for (int b = 0; b < stride; b++)
            {
                bytes[b] = family switch
                {
                    "stripes" => (byte)(worldRow / period % 2 == 0 ? 20 : 230),
                    "grid" => (byte)(worldRow % period == 0 || b / 4 % 11 == 0 ? 10 : 240),
                    "table" => Noise(worldRow % period, b, seed),
                    "text" => (byte)(worldRow % 13 < 3 || b / 4 % 9 < 2 ? 255 : Noise(worldRow / 13, b, seed) % 90),
                    "gutter" => 238,
                    _ => Noise(worldRow, b, seed),
                };
            }

            if (family == "scrollbar")
            {
                bytes[^4..].Fill((byte)(row >= 20 + append * 9 && row < 48 + append * 9 ? 90 : 180));
            }
            else if (family == "gutter")
            {
                // Only four gutter bytes can differ; the 512-byte row allows ten.
                bytes[0] = (byte)worldRow;
                bytes[1] = (byte)(worldRow >> 8);
                bytes[2] = (byte)(worldRow ^ 0x55);
                bytes[3] = (byte)(worldRow >> 1);
            }
            else if (family is "at-limit" or "over-limit")
            {
                int count = family == "at-limit" ? 4 : 5;
                for (int b = stride - count; b < stride; b++)
                {
                    bytes[b] ^= (byte)(append % 2);
                }
            }
        }

        return new ScrollFrame(width, height, pixels);
    }

    private static byte Noise(int row, int column, int seed)
    {
        uint value = unchecked((uint)row * 2654435761U + (uint)column * 2246822519U + (uint)seed);
        value ^= value >> 16;
        value *= 3266489917U;
        value ^= value >> 13;
        return (byte)value;
    }

    private static (ScrollFrame Seed, ScrollFrame Incoming) AdversarialFrames(int n)
    {
        byte[] seed = new byte[n * n];
        seed.AsSpan().Fill(1);
        byte[] incoming = new byte[n * n];
        int half = n / 2;
        int quarter = n / 4;
        for (int k = 0; k < n; k++)
        {
            int missing = k < half ? (k + 1) % half
                : (k - half) % 2 == 0 ? half + (k - half) / 2
                : 3 * quarter + (k - half) / 2;
            seed[k * n + missing] = 0;
            incoming[k * n + k] = 1;
        }

        // Every row is distinct. F[r] and C[k] differ in n bytes only when r=missing(k),
        // otherwise in n-2. With limit n-1, each rejected candidate has a new mismatch
        // at r=half+s, k=half+2*s; neither previous frame nor canvas killers finds it.
        return (new ScrollFrame(n / 4, n, seed), new ScrollFrame(n / 4, n, incoming));
    }

    private static bool ScalarRowsMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        int mismatches = 0;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i] && ++mismatches > limit)
            {
                return false;
            }
        }

        return true;
    }
}
