using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AnimatedGifWriterTests
{
    [Fact]
    public void Complete_WritesAllFramesWithDelayLoopDimensionsAndSingleTrailer() => RunSta(() =>
    {
        const int width = 17;
        const int height = 11;
        const int delay = 7;
        const int loopCount = 4;

        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(
                   output,
                   width,
                   height,
                   delay,
                   loopCount,
                   leaveOpen: true))
        {
            writer.AddFrame(Solid(width, height, 0xE0, 0x20, 0x20));
            long afterFirstFrame = output.Length;
            writer.AddFrame(Solid(width, height, 0x20, 0xD0, 0x30));
            long afterSecondFrame = output.Length;
            writer.AddFrame(Solid(width, height, 0x20, 0x40, 0xE0));
            long afterThirdFrame = output.Length;
            writer.Complete();

            // Each image is held only until the next different frame fixes its delay.
            Assert.Equal(32, afterFirstFrame);
            Assert.True(afterSecondFrame > afterFirstFrame, "the first frame was not streamed once the second arrived");
            Assert.True(afterThirdFrame > afterSecondFrame, "the second frame was retained instead of streamed");
        }

        byte[] bytes = output.ToArray();
        ParsedAnimation parsed = ParseAnimation(bytes);

        Assert.Equal("GIF89a", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(width, parsed.Width);
        Assert.Equal(height, parsed.Height);
        Assert.Equal(loopCount, parsed.LoopCount);
        Assert.Equal(3, parsed.FrameDimensions.Count);
        Assert.All(parsed.FrameDimensions, dimensions => Assert.Equal((width, height), dimensions));
        Assert.Equal([delay, delay, delay], parsed.FrameDelays);
        Assert.True(parsed.HasTrailer);
        Assert.Equal(0x3B, bytes[^1]);
    });

    [Fact]
    public void AddFrame_WritesIndependentPerFrameDelays() => RunSta(() =>
    {
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(
                   output,
                   9,
                   7,
                   frameDelayCentiseconds: 10,
                   leaveOpen: true))
        {
            writer.AddFrame(Solid(9, 7, 0xE0, 0x20, 0x20), frameDelayCentiseconds: 5);
            writer.AddFrame(Solid(9, 7, 0x20, 0xD0, 0x30), frameDelayCentiseconds: 3);
            writer.AddFrame(Solid(9, 7, 0x20, 0x40, 0xE0));
            writer.Complete();
        }

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal([5, 3, 10], parsed.FrameDelays);
    });

    [Fact]
    public void Dispose_CompletesExactlyOnceAndLeavesRequestedStreamOpen() => RunSta(() =>
    {
        using var output = new MemoryStream();
        var writer = new AnimatedGifWriter(output, 8, 6, 3, loopCount: 0, leaveOpen: true);
        writer.AddFrame(Solid(8, 6, 0x44, 0x66, 0x88));

        writer.Dispose();
        long completedLength = output.Length;
        writer.Dispose();

        Assert.Equal(completedLength, output.Length);
        Assert.True(output.CanWrite);
        Assert.Equal(0x3B, output.ToArray()[^1]);

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Single(parsed.FrameDimensions);
        Assert.Equal(0, parsed.LoopCount);
        Assert.Equal([3], parsed.FrameDelays);
    });

    [Fact]
    public void AddFrame_RejectsDimensionMismatchWithoutAppendingPartialFrame() => RunSta(() =>
    {
        using var output = new MemoryStream();
        using var writer = new AnimatedGifWriter(output, 12, 9, 5, leaveOpen: true);
        writer.AddFrame(Solid(12, 9, 0x10, 0x20, 0x30));
        long beforeRejectedFrame = output.Length;

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => writer.AddFrame(Solid(11, 9, 0xAA, 0xBB, 0xCC)));

        Assert.Contains("12x9", failure.Message, StringComparison.Ordinal);
        Assert.Equal(beforeRejectedFrame, output.Length);

        writer.AddFrame(Solid(12, 9, 0x90, 0x70, 0x50));
        writer.Complete();

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal(2, parsed.FrameDimensions.Count);
        Assert.Equal([5, 5], parsed.FrameDelays);
    });

    [Fact]
    public void ParseSingleFrame_RejectsTruncatedOrDimensionallyInvalidCodecBytes() => RunSta(() =>
    {
        byte[] valid = EncodeSingleFrame(Solid(10, 7, 0x33, 0x77, 0xBB));
        byte[] truncated = valid[..^2];

        Assert.Throws<InvalidDataException>(() =>
        {
            _ = AnimatedGifWriter.ParseSingleFrame(truncated, 10, 7);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = AnimatedGifWriter.ParseSingleFrame(valid, 9, 7);
        });
    });

    [Theory]
    [InlineData(0, 10, 1, 0)]
    [InlineData(10, 0, 1, 0)]
    [InlineData(65_536, 10, 1, 0)]
    [InlineData(10, 65_536, 1, 0)]
    [InlineData(10, 10, -1, 0)]
    [InlineData(10, 10, 65_536, 0)]
    [InlineData(10, 10, 1, -1)]
    [InlineData(10, 10, 1, 65_536)]
    public void Constructor_RejectsValuesOutsideGifUnsignedShortRanges(
        int width,
        int height,
        int delay,
        int loopCount)
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var writer = new AnimatedGifWriter(
                output,
                width,
                height,
                delay,
                loopCount,
                leaveOpen: true);
        });
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void IdenticalFrames_MergeIntoOneImageWithTheSummedDelay() => RunSta(() =>
    {
        BitmapSource red = Solid(10, 8, 0xE0, 0x20, 0x20);
        BitmapSource blue = Solid(10, 8, 0x20, 0x40, 0xE0);
        using var output = new MemoryStream();
        var updates = new List<GifFrameUpdate>();
        using (var writer = new AnimatedGifWriter(output, 10, 8, 10, leaveOpen: true))
        {
            foreach ((BitmapSource frame, int delay) in new[] { (red, 7), (red, 3), (blue, 10), (blue, 10), (blue, 4), (red, 1) })
            {
                updates.Add(writer.AddFrame(frame, delay));
            }

            Assert.Equal(3, writer.ImageCount);
            writer.Complete();
        }

        Assert.Equal([false, true, false, true, true, false], updates.Select(update => update.IsUnchanged));
        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal([10, 24, 1], parsed.FrameDelays);
        Assert.Equal(35, parsed.FrameDelays.Sum());
        Assert.All(parsed.Disposals, disposal => Assert.Equal(1, disposal));
    });

    [Fact]
    public void ChangedFrame_WritesOnlyItsChangedRectangleAtItsOffset() => RunSta(() =>
    {
        const int width = 24;
        const int height = 16;
        byte[] first = SolidPixels(width, height, 0x30, 0x60, 0x90);
        byte[] second = (byte[])first.Clone();
        Paint(second, width, 5, 3, 4, 2, 0xF0, 0x10, 0x10);
        Paint(second, width, 11, 9, 1, 1, 0x10, 0xF0, 0x10);
        byte[] third = (byte[])second.Clone();
        Paint(third, width, 23, 15, 1, 1, 0x10, 0x10, 0xF0);

        string root = OwnedTestDirectory.Create("mc-gif-writer-rect-");
        try
        {
            string path = Path.Combine(root, "rect.gif");
            using (FileStream output = File.Create(path))
            using (var writer = new AnimatedGifWriter(output, width, height, 10))
            {
                Assert.Equal(new GifPixelRect(0, 0, width, height), writer.AddFrame(first, width * 4, 10).Bounds);
                Assert.Equal(new GifPixelRect(5, 3, 7, 7), writer.AddFrame(second, width * 4, 10).Bounds);
                Assert.Equal(new GifPixelRect(23, 15, 1, 1), writer.AddFrame(third, width * 4, 10).Bounds);
                writer.Complete();
            }

            ParsedAnimation parsed = ParseAnimation(File.ReadAllBytes(path));
            Assert.Equal([(0, 0), (5, 3), (23, 15)], parsed.FrameOffsets);
            Assert.Equal([(width, height), (7, 7), (1, 1)], parsed.FrameDimensions);

            // Composited like a browser, every frame shows exactly its source: four colours
            // quantise without loss.
            GifSampleFrames frames = GifSampleDecoder.Decode(path);
            Assert.Equal(first, CopyPixels(frames.Frames[0]));
            Assert.Equal(second, CopyPixels(frames.Frames[1]));
            Assert.Equal(third, CopyPixels(frames.Frames[2]));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void DifferencingModes_KeepFullFramesOnlyWhenAsked() => RunSta(() =>
    {
        byte[] grey = SolidPixels(6, 4, 100, 100, 100);
        byte[] patched = (byte[])grey.Clone();
        Paint(patched, 6, 2, 1, 1, 1, 0, 0, 0);

        ParsedAnimation none = Write(GifFrameDifferencing.None, grey, grey, patched);
        Assert.Equal([10, 10, 10], none.FrameDelays);
        Assert.All(none.FrameDimensions, dimensions => Assert.Equal((6, 4), dimensions));

        ParsedAnimation merged = Write(GifFrameDifferencing.MergeIdentical, grey, grey, patched);
        Assert.Equal([20, 10], merged.FrameDelays);
        Assert.All(merged.FrameDimensions, dimensions => Assert.Equal((6, 4), dimensions));

        foreach (GifFrameDifferencing partial in new[] { GifFrameDifferencing.ChangedRectangle, GifFrameDifferencing.ErrorBounded })
        {
            ParsedAnimation written = Write(partial, grey, grey, patched);
            Assert.Equal([20, 10], written.FrameDelays);
            Assert.Equal([(6, 4), (1, 1)], written.FrameDimensions);
            Assert.Equal([(0, 0), (2, 1)], written.FrameOffsets);
        }

        static ParsedAnimation Write(GifFrameDifferencing mode, params byte[][] frames)
        {
            using var output = new MemoryStream();
            using (var writer = new AnimatedGifWriter(output, 6, 4, 10, leaveOpen: true, differencing: mode))
            {
                foreach (byte[] frame in frames)
                {
                    _ = writer.AddFrame(frame, 24, 10);
                }

                writer.Complete();
            }

            return ParseAnimation(output.ToArray());
        }
    });

    /// <summary>
    /// Regression for kept pixels that were quantised under a busier palette: noisy opening
    /// frames, then clean ones over the same still background. Exact source differencing keeps
    /// the noisy quantisation; the error bound repaints wherever the full frame does better.
    /// </summary>
    [Fact]
    public void ErrorBoundedFrames_AreNeverFurtherFromTheSourceThanFullFrames() => RunSta(() =>
    {
        const int width = 96;
        const int height = 64;
        var random = new Random(17);
        byte[] background = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int x = pixel % width, y = pixel / width;
            background[(pixel * 4) + 0] = (byte)(40 + (x * 2));
            background[(pixel * 4) + 1] = (byte)(30 + (y * 3));
            background[(pixel * 4) + 2] = (byte)((x * y) % 200);
            background[(pixel * 4) + 3] = 255;
        }

        var frames = new List<byte[]>();
        for (int index = 0; index < 12; index++)
        {
            byte[] frame = (byte[])background.Clone();
            if (index < 3)
            {
                // A busy opening (noise over a band) that later clears.
                for (int pixel = 0; pixel < width * 20; pixel++)
                {
                    frame[pixel * 4] = (byte)random.Next(256);
                    frame[(pixel * 4) + 1] = (byte)random.Next(256);
                }
            }

            Paint(frame, width, 4 + (index * 6), 30, 10, 10, 250, 250, 20);
            frames.Add(frame);
        }

        string root = OwnedTestDirectory.Create("mc-gif-writer-bounded-");
        try
        {
            var errors = new Dictionary<GifFrameDifferencing, long[]>();
            var sizes = new Dictionary<GifFrameDifferencing, long>();
            foreach (GifFrameDifferencing mode in new[] { GifFrameDifferencing.None, GifFrameDifferencing.ErrorBounded })
            {
                string path = Path.Combine(root, $"{mode}.gif");
                using (FileStream output = File.Create(path))
                using (var writer = new AnimatedGifWriter(output, width, height, 10, differencing: mode))
                {
                    foreach (byte[] frame in frames)
                    {
                        _ = writer.AddFrame(frame, width * 4, 10);
                    }

                    writer.Complete();
                }

                sizes[mode] = new FileInfo(path).Length;
                GifSampleFrames decoded = GifSampleDecoder.Decode(path);
                Assert.Equal(frames.Count * 10, decoded.DelaysCentiseconds.Sum());
                var perFrame = new long[frames.Count];
                int image = 0;
                int imageEnd = decoded.DelaysCentiseconds[0];
                for (int index = 0; index < frames.Count; index++)
                {
                    while (index * 10 >= imageEnd)
                    {
                        imageEnd += decoded.DelaysCentiseconds[++image];
                    }

                    perFrame[index] = SquaredError(frames[index], CopyPixels(decoded.Frames[image]));
                }

                errors[mode] = perFrame;
            }

            for (int index = 0; index < frames.Count; index++)
            {
                Assert.True(
                    errors[GifFrameDifferencing.ErrorBounded][index] <= errors[GifFrameDifferencing.None][index],
                    $"Frame {index}: bounded error {errors[GifFrameDifferencing.ErrorBounded][index]} exceeds full-frame error {errors[GifFrameDifferencing.None][index]}.");
            }

            Assert.True(sizes[GifFrameDifferencing.ErrorBounded] < sizes[GifFrameDifferencing.None] / 2,
                $"Bounded {sizes[GifFrameDifferencing.ErrorBounded]} B is not well below full-frame {sizes[GifFrameDifferencing.None]} B.");
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }

        static long SquaredError(byte[] expected, byte[] actual)
        {
            long sum = 0;
            for (int offset = 0; offset < expected.Length; offset += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = expected[offset + channel] - actual[offset + channel];
                    sum += difference * difference;
                }
            }

            return sum;
        }
    });

    [Fact]
    public void Lzw_RoundTripsAndAgreesWithThePlatformDecoder() => RunSta(() =>
    {
        var random = new Random(3);
        foreach ((int bits, int width, int height, bool runs) in new[] { (2, 7, 5, false), (4, 64, 48, true), (8, 300, 200, false), (8, 320, 240, true), (5, 1, 1, false) })
        {
            int colours = 1 << bits;
            byte[] indices = new byte[width * height];
            for (int pixel = 0; pixel < indices.Length; pixel++)
            {
                indices[pixel] = runs ? (byte)((pixel / 97) % colours) : (byte)random.Next(colours);
            }

            byte[] data = GifLzw.Encode(indices, bits);
            Assert.Equal(bits, data[0]);
            Assert.Equal(0, data[^1]);
            Assert.Equal(indices, GifLzw.Decode(data, indices.Length));

            // The platform decoder reads the same indices from a GIF built around the data.
            using var gif = new MemoryStream();
            gif.Write("GIF89a"u8);
            gif.Write([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0]);
            int code = Math.Max(1, bits) - 1;
            gif.Write([0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)(0x80 | code)]);
            for (int entry = 0; entry < 1 << (code + 1); entry++)
            {
                gif.Write([(byte)entry, (byte)(255 - entry), (byte)(entry * 7)]);
            }

            gif.Write(data);
            gif.WriteByte(0x3B);
            gif.Position = 0;
            BitmapFrame frame = new GifBitmapDecoder(gif, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(PixelFormats.Indexed8, frame.Format);
            byte[] platform = new byte[width * height];
            frame.CopyPixels(platform, width, 0);
            Assert.Equal(indices, platform);
        }

        // And it decodes what the platform encoder writes.
        byte[] payload = AnimatedGifWriter.EncodeFrame(Solid(40, 30, 0x12, 0x80, 0xF0)).ToArray();
        AnimatedGifWriter.EncodedGifFrame parsed = AnimatedGifWriter.ParseSingleFrame(payload, 40, 30);
        byte[] decoded = GifLzw.Decode(payload.AsSpan(parsed.ImageDataOffset, parsed.ImageDataLength), 40 * 30);
        BitmapFrame reference = new GifBitmapDecoder(new MemoryStream(payload), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        byte[] expected = new byte[40 * 30];
        reference.CopyPixels(expected, 40, 0);
        Assert.Equal(expected, decoded);
        Assert.Throws<InvalidDataException>(() => GifLzw.Decode(payload.AsSpan(parsed.ImageDataOffset, parsed.ImageDataLength), (40 * 30) - 1));
    });

    [Fact]
    public void LongHold_SplitsAtTheSixteenBitDelayLimitWithoutLosingTime() => RunSta(() =>
    {
        byte[] frame = SolidPixels(4, 3, 10, 20, 30);
        byte[] other = SolidPixels(4, 3, 200, 20, 30);
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(output, 4, 3, 10, leaveOpen: true))
        {
            _ = writer.AddFrame(frame, 16, 65_000);
            _ = writer.AddFrame(frame, 16, 600);
            _ = writer.AddFrame(frame, 16, 65_535);
            _ = writer.AddFrame(other, 16, 5);
            writer.Complete();
        }

        // 131,135 cs = 65,535 + 65,535 + 65: the same image is drawn again for the remainder.
        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal([65_535, 65_535, 65, 5], parsed.FrameDelays);
        Assert.Equal([(4, 3), (4, 3), (4, 3), (4, 3)], parsed.FrameDimensions);
    });

    [Fact]
    public void ReplayedUpdates_WithOtherDelays_EqualAFreshRender() => RunSta(() =>
    {
        byte[][] frames =
        [
            SolidPixels(8, 8, 10, 10, 10),
            SolidPixels(8, 8, 10, 10, 10),
            PatchedPixels(8, 8, 3, 3),
            PatchedPixels(8, 8, 3, 3),
            PatchedPixels(8, 8, 6, 1),
        ];
        int[] slow = [10, 10, 10, 10, 10];
        int[] fast = [3, 2, 3, 2, 3];
        (byte[] fresh, GifFrameUpdate[] updates) = Render(fast);
        (_, GifFrameUpdate[] slowUpdates) = Render(slow);
        Assert.Equal(updates.Select(update => update.Bounds), slowUpdates.Select(update => update.Bounds));

        using var replayed = new MemoryStream();
        using (var writer = new AnimatedGifWriter(replayed, 8, 8, 10, leaveOpen: true))
        {
            for (int index = 0; index < slowUpdates.Length; index++)
            {
                writer.AddEncodedFrame(slowUpdates[index], fast[index]);
            }

            writer.Complete();
        }

        Assert.Equal(fresh, replayed.ToArray());
        Assert.Equal([5, 5, 3], ParseAnimation(fresh).FrameDelays);

        using var invalid = new MemoryStream();
        using var first = new AnimatedGifWriter(invalid, 8, 8, 10, leaveOpen: true);
        Assert.Throws<InvalidOperationException>(() => first.AddEncodedFrame(GifFrameUpdate.Unchanged, 10));
        Assert.Throws<ArgumentException>(() => first.AddEncodedFrame(updates[2], 10));

        (byte[] Bytes, GifFrameUpdate[] Updates) Render(int[] delays)
        {
            using var output = new MemoryStream();
            var produced = new GifFrameUpdate[frames.Length];
            using (var writer = new AnimatedGifWriter(output, 8, 8, 10, leaveOpen: true))
            {
                for (int index = 0; index < frames.Length; index++)
                {
                    produced[index] = writer.AddFrame(frames[index], 32, delays[index]);
                }

                writer.Complete();
            }

            return (output.ToArray(), produced);
        }

        static byte[] PatchedPixels(int width, int height, int x, int y)
        {
            byte[] pixels = SolidPixels(width, height, 10, 10, 10);
            Paint(pixels, width, x, y, 1, 1, 250, 250, 250);
            return pixels;
        }
    });

    [Fact]
    public void ChangedBounds_FindsTheExactRectangleAcrossStrides()
    {
        const int width = 7;
        const int height = 5;
        const int stride = 32; // padded rows
        byte[] before = new byte[stride * height];
        byte[] after = (byte[])before.Clone();
        Assert.True(GifFrameDifference.ChangedBounds(before, stride, after, stride, width, height).IsEmpty);

        // Padding beyond the row is ignored.
        after[(2 * stride) + 30] = 9;
        Assert.True(GifFrameDifference.ChangedBounds(before, stride, after, stride, width, height).IsEmpty);

        after[(1 * stride) + (6 * 4) + 2] = 1;   // pixel (6, 1)
        after[(3 * stride) + (2 * 4)] = 1;       // pixel (2, 3)
        Assert.Equal(new GifPixelRect(2, 1, 5, 3), GifFrameDifference.ChangedBounds(before, stride, after, stride, width, height));
        after[(4 * stride) + 3] = 200;           // alpha of pixel (0, 4)
        Assert.Equal(new GifPixelRect(0, 1, 7, 4), GifFrameDifference.ChangedBounds(before, stride, after, stride, width, height));

        // A tightly packed and a padded buffer of the same pixels compare equal.
        byte[] packed = new byte[width * 4 * height];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(after, row * stride, packed, row * width * 4, width * 4);
        }

        Assert.True(GifFrameDifference.ChangedBounds(after, stride, packed, width * 4, width, height).IsEmpty);
        Assert.Throws<ArgumentException>(() => GifFrameDifference.ChangedBounds(before, stride, packed[..^1], width * 4, width, height));
    }

    /// <summary>
    /// The direct WIC encoder and WPF's encoder write the same animation in every differencing
    /// mode, over frames that merge, change in part, change whole and carry alpha.
    /// </summary>
    [Fact]
    public void WicAndWpfEncoders_WriteIdenticalAnimationsInEveryDifferencingMode() => RunSta(() =>
    {
        const int width = 120;
        const int height = 68;
        byte[][] frames = AnimationFrames(width, height);
        foreach (GifFrameDifferencing mode in Enum.GetValues<GifFrameDifferencing>())
        {
            byte[] wpf = Write(frames, width, height, mode, GifImageEncoder.Wpf, candidates: null, out _);
            byte[] wic = Write(frames, width, height, mode, GifImageEncoder.Wic, candidates: null, out _);
            Assert.True(wpf.AsSpan().SequenceEqual(wic), $"{mode}: the WIC and WPF encoders wrote different files.");
        }

        Assert.Equal(0, WicGifFrameEncoder.Failures);
    });

    /// <summary>
    /// Candidates encoded on another thread replace exactly the writer's whole-canvas encodes:
    /// same bytes, used only for their own buffer, never by a WPF writer.
    /// </summary>
    [Fact]
    public void EncodedCandidates_ChangeNoByteAndAreUsedOnlyForTheirOwnBuffer() => RunSta(() =>
    {
        const int width = 120;
        const int height = 68;
        byte[][] frames = AnimationFrames(width, height);
        GifEncodedCandidate?[] candidates = EncodeOnMta(frames, width, height, read: true);
        Assert.All(candidates, candidate => Assert.NotNull(candidate));

        foreach (GifFrameDifferencing mode in Enum.GetValues<GifFrameDifferencing>())
        {
            byte[] expected = Write(frames, width, height, mode, GifImageEncoder.Wic, candidates: null, out int none);
            Assert.Equal(0, none);
            byte[] actual = Write(frames, width, height, mode, GifImageEncoder.Wic, candidates, out int used);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{mode}: candidates changed the file.");

            // Merged frames need no encode, and a changed rectangle is encoded by the writer.
            int expectedUse = mode switch
            {
                GifFrameDifferencing.None => frames.Length,
                GifFrameDifferencing.ChangedRectangle => 2, // the first frame and the whole-frame change
                _ => frames.Length - 1, // one repeat merges
            };
            Assert.Equal(expectedUse, used);

            // A candidate of other pixels (or of an equal copy) is never taken for a buffer.
            GifEncodedCandidate?[] shifted = [.. candidates.Skip(1), candidates[0]];
            Assert.True(expected.AsSpan().SequenceEqual(Write(frames, width, height, mode, GifImageEncoder.Wic, shifted, out int misused)));
            Assert.Equal(0, misused);
            byte[][] copies = frames.Select(frame => (byte[])frame.Clone()).ToArray();
            Assert.True(expected.AsSpan().SequenceEqual(Write(copies, width, height, mode, GifImageEncoder.Wic, candidates, out int copied)));
            Assert.Equal(0, copied);
            Assert.True(expected.AsSpan().SequenceEqual(Write(frames, width, height, mode, GifImageEncoder.Wpf, candidates, out int wpfUsed)));
            Assert.Equal(0, wpfUsed);
        }

        // Without the error-bounded reading the writer reads the payload itself.
        GifEncodedCandidate?[] unread = EncodeOnMta(frames, width, height, read: false);
        Assert.True(Write(frames, width, height, GifFrameDifferencing.ErrorBounded, GifImageEncoder.Wic, candidates: null, out _)
            .AsSpan()
            .SequenceEqual(Write(frames, width, height, GifFrameDifferencing.ErrorBounded, GifImageEncoder.Wic, unread, out _)));
    });

    /// <summary>
    /// Six frames: a noisy start, a repeat, a small patch, a moved block, a whole-frame colour
    /// change and a frame with partial alpha.
    /// </summary>
    private static byte[][] AnimationFrames(int width, int height)
    {
        var random = new Random(23);
        byte[] first = new byte[width * height * 4];
        random.NextBytes(first);
        for (int offset = 3; offset < first.Length; offset += 4)
        {
            first[offset] = 255;
        }

        byte[] repeat = (byte[])first.Clone();
        byte[] patched = (byte[])first.Clone();
        Paint(patched, width, 10, 12, 6, 5, 250, 30, 30);
        byte[] moved = (byte[])patched.Clone();
        Paint(moved, width, 70, 40, 20, 14, 20, 220, 90);
        byte[] recoloured = SolidPixels(width, height, 40, 80, 160);
        Paint(recoloured, width, 0, 0, width / 2, height / 3, 200, 200, 30);
        byte[] alpha = (byte[])recoloured.Clone();
        for (int offset = 3; offset < alpha.Length / 2; offset += 28)
        {
            alpha[offset] = (byte)(offset & 0x7F);
        }

        return [first, repeat, patched, moved, recoloured, alpha];
    }

    private static GifEncodedCandidate?[] EncodeOnMta(byte[][] frames, int width, int height, bool read)
    {
        var candidates = new GifEncodedCandidate?[frames.Length];
        var thread = new Thread(() =>
        {
            for (int index = 0; index < frames.Length; index++)
            {
                candidates[index] = AnimatedGifWriter.EncodeCandidate(frames[index], width * 4, width, height, read);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return candidates;
    }

    private static byte[] Write(
        byte[][] frames,
        int width,
        int height,
        GifFrameDifferencing mode,
        GifImageEncoder encoder,
        GifEncodedCandidate?[]? candidates,
        out int preEncoded)
    {
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(output, width, height, 10, leaveOpen: true, differencing: mode, encoder: encoder))
        {
            for (int index = 0; index < frames.Length; index++)
            {
                _ = writer.AddFrame(frames[index], width * 4, 10, candidates?[index]);
            }

            writer.Complete();
            preEncoded = writer.PreEncodedImages;
        }

        return output.ToArray();
    }

    private static byte[] SolidPixels(int width, int height, byte red, byte green, byte blue)
    {
        byte[] pixels = new byte[width * height * 4];
        Paint(pixels, width, 0, 0, width, height, red, green, blue);
        return pixels;
    }

    private static void Paint(byte[] pixels, int width, int x, int y, int w, int h, byte red, byte green, byte blue)
    {
        for (int row = y; row < y + h; row++)
        {
            for (int column = x; column < x + w; column++)
            {
                int offset = ((row * width) + column) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 0xFF;
            }
        }
    }

    private static byte[] CopyPixels(BitmapSource frame)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    private static BitmapSource Solid(int width, int height, byte red, byte green, byte blue)
    {
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeSingleFrame(BitmapSource frame)
    {
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Independent structural reader for the generated animation. It deliberately does not use
    /// AnimatedGifWriter.ParseSingleFrame, so production and verification cannot share a parser bug.
    /// </summary>
    private static ParsedAnimation ParseAnimation(ReadOnlySpan<byte> bytes)
    {
        Assert.True(bytes.Length >= 14, "GIF is too short");
        Assert.True(bytes[..6].SequenceEqual("GIF89a"u8), "expected a GIF89a header");

        int index = 6;
        int width = ReadUInt16(bytes, ref index);
        int height = ReadUInt16(bytes, ref index);
        byte packed = ReadByte(bytes, ref index);
        _ = ReadByte(bytes, ref index);
        _ = ReadByte(bytes, ref index);
        if ((packed & 0x80) != 0)
        {
            Skip(bytes, ref index, ColourTableLength(packed));
        }

        var delays = new List<int>();
        var dimensions = new List<(int Width, int Height)>();
        var offsets = new List<(int Left, int Top)>();
        var disposals = new List<int>();
        int? pendingDelay = null;
        int? loopCount = null;
        bool trailer = false;

        while (index < bytes.Length)
        {
            byte introducer = ReadByte(bytes, ref index);
            switch (introducer)
            {
                case 0x21:
                    {
                        byte label = ReadByte(bytes, ref index);
                        if (label == 0xF9)
                        {
                            Assert.Equal(4, ReadByte(bytes, ref index));
                            disposals.Add((ReadByte(bytes, ref index) >> 2) & 0x07);
                            pendingDelay = ReadUInt16(bytes, ref index);
                            _ = ReadByte(bytes, ref index);
                            Assert.Equal(0, ReadByte(bytes, ref index));
                        }
                        else if (label == 0xFF)
                        {
                            int identifierLength = ReadByte(bytes, ref index);
                            Assert.True(identifierLength > 0);
                            ReadOnlySpan<byte> identifier = Slice(bytes, ref index, identifierLength);
                            List<byte> data = ReadSubBlocks(bytes, ref index);
                            if (identifier.SequenceEqual("NETSCAPE2.0"u8))
                            {
                                Assert.True(data.Count >= 3 && data[0] == 1, "malformed NETSCAPE2.0 loop data");
                                loopCount = data[1] | (data[2] << 8);
                            }
                        }
                        else
                        {
                            _ = ReadSubBlocks(bytes, ref index);
                        }

                        break;
                    }

                case 0x2C:
                    {
                        int left = ReadUInt16(bytes, ref index);
                        int top = ReadUInt16(bytes, ref index);
                        int frameWidth = ReadUInt16(bytes, ref index);
                        int frameHeight = ReadUInt16(bytes, ref index);
                        byte imagePacked = ReadByte(bytes, ref index);
                        if ((imagePacked & 0x80) != 0)
                        {
                            Skip(bytes, ref index, ColourTableLength(imagePacked));
                        }

                        int minimumCodeSize = ReadByte(bytes, ref index);
                        Assert.InRange(minimumCodeSize, 2, 8);
                        List<byte> imageData = ReadSubBlocks(bytes, ref index);
                        Assert.NotEmpty(imageData);

                        dimensions.Add((frameWidth, frameHeight));
                        offsets.Add((left, top));
                        delays.Add(Assert.IsType<int>(pendingDelay));
                        Assert.True(left + frameWidth <= width && top + frameHeight <= height, "image outside the canvas");
                        pendingDelay = null;
                        break;
                    }

                case 0x3B:
                    trailer = true;
                    Assert.Equal(bytes.Length, index);
                    break;

                default:
                    throw new Xunit.Sdk.XunitException($"unexpected GIF introducer 0x{introducer:X2}");
            }

            if (trailer)
            {
                break;
            }
        }

        Assert.True(trailer, "GIF trailer was not found");
        Assert.Null(pendingDelay);
        return new ParsedAnimation(width, height, loopCount, delays, dimensions, offsets, disposals, trailer);
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int index)
    {
        Assert.True(index < bytes.Length, "unexpected end of GIF");
        return bytes[index++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> bytes, ref int index)
    {
        ReadOnlySpan<byte> value = Slice(bytes, ref index, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, ref int index, int length)
    {
        Assert.True(length >= 0 && bytes.Length - index >= length, "truncated GIF block");
        ReadOnlySpan<byte> result = bytes.Slice(index, length);
        index += length;
        return result;
    }

    private static void Skip(ReadOnlySpan<byte> bytes, ref int index, int length) =>
        _ = Slice(bytes, ref index, length);

    private static List<byte> ReadSubBlocks(ReadOnlySpan<byte> bytes, ref int index)
    {
        var result = new List<byte>();
        while (true)
        {
            int length = ReadByte(bytes, ref index);
            if (length == 0)
            {
                return result;
            }

            result.AddRange(Slice(bytes, ref index, length).ToArray());
        }
    }

    private static int ColourTableLength(byte packed) => 3 * (1 << ((packed & 0x07) + 1));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private sealed record ParsedAnimation(
        int Width,
        int Height,
        int? LoopCount,
        IReadOnlyList<int> FrameDelays,
        IReadOnlyList<(int Width, int Height)> FrameDimensions,
        IReadOnlyList<(int Left, int Top)> FrameOffsets,
        IReadOnlyList<int> Disposals,
        bool HasTrailer);
}
