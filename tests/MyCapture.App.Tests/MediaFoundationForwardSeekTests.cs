using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Pins the forward-seek contract of <see cref="MediaFoundationVideoFrameReader"/> against a real
/// Media Foundation MP4 whose frames carry their own index: a distant forward request lands on the
/// key frame at or before the target (as recorded in the file's stss table) and still returns the
/// exact frame sequential decoding would, while short forward steps never seek.
/// </summary>
public sealed class MediaFoundationForwardSeekTests
{
    private const int Width = 320;
    private const int Height = 180;
    private const int Fps = 30;
    private const int Frames = 300;
    private const int IndexBits = 10;
    private const double TimestampEpsilonMs = 0.01;

    [Fact]
    public void DistantForwardRequest_LandsOnPrecedingKeyFrameAndReturnsTheExactFrame()
    {
        string root = NewRoot();
        try
        {
            string clip = Path.Combine(root, "indexed.mp4");
            EncodeIndexedClip(clip);
            Mp4SampleTable table = Mp4SampleTable.Read(clip);
            Assert.Equal(Frames, table.PresentationMs.Count);
            double durationMs;
            using (var probe = new MediaFoundationVideoFrameReader(clip))
            {
                durationMs = probe.DurationMs;
            }

            var targets = new List<double> { durationMs - 1, durationMs, durationMs + 500, 2_500, 5_000.5 };
            foreach (double key in table.KeyFrameMs.Where(key => key > 0).Take(6))
            {
                double frameMs = 1000.0 / Fps;
                // One frame before a key frame must come from the previous interval; just below the
                // key frame resolves to it through the selector's epsilon; exactly on it lands on it.
                targets.AddRange([key - frameMs, key - 0.005, key, key + (frameMs / 2)]);
            }

            bool advancedAtLeastOnce = false;
            foreach (double target in targets.Where(target => target > 0))
            {
                using var reader = new MediaFoundationVideoFrameReader(clip);
                DecodedVideoFrame first = reader.ReadFrameAt(0);
                Assert.Equal(0, ReadIndex(first));
                int decodedBefore = reader.DecodedFrameCount;
                double origin = reader.NewestDecodedMs!.Value;

                DecodedVideoFrame frame = reader.ReadFrameAt(target);

                int expected = ExpectedIndex(table, target);
                Assert.True(expected == ReadIndex(frame), $"target {target}: expected frame {expected}, decoded {ReadIndex(frame)}");
                Assert.Equal(table.PresentationMs[expected], frame.TimestampMs, precision: 3);

                bool forwardSeek = target - origin >= ForwardSeekPolicy.DefaultThresholdMs;
                Assert.Equal(forwardSeek ? 2 : 1, reader.SeekCount);
                if (!forwardSeek)
                {
                    continue;
                }

                // Media Foundation seeks with key-frame precision: the first decoded sample is the
                // last sync sample at or before the (duration-clamped) request, never a later one.
                double position = Math.Min(target, durationMs);
                double landing = table.KeyFrameMs.Last(key => key <= position + 0.0001);
                Assert.Equal(landing, reader.LastSeekLandingMs!.Value, precision: 3);
                int decoded = reader.DecodedFrameCount - decodedBefore;
                Assert.True(
                    decoded <= ((Math.Min(target, durationMs) - landing) * Fps / 1000.0) + 3,
                    $"target {target}: decoded {decoded} samples after landing at {landing}");
                advancedAtLeastOnce |= landing > origin;
            }

            if (table.KeyFrameMs.Any(key => key >= ForwardSeekPolicy.DefaultThresholdMs))
            {
                Assert.True(advancedAtLeastOnce, "No forward seek skipped any decoded samples.");
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ForwardSeeking_MatchesSequentialDecodingAcrossMixedSchedules()
    {
        string root = NewRoot();
        try
        {
            string clip = Path.Combine(root, "indexed.mp4");
            EncodeIndexedClip(clip);
            Mp4SampleTable table = Mp4SampleTable.Read(clip);
            double[] schedule =
            [
                0, 33.3, 66.7, 2_400, 2_433.3, 6_100, 6_100, 4_000, 4_033.3, 9_966.6, 9_999, 12_000,
                150, 7_500, 7_533.3, 8_333.3,
            ];

            using var seeking = new MediaFoundationVideoFrameReader(clip);
            using var sequential = new MediaFoundationVideoFrameReader(clip)
            {
                ForwardSeekPolicy = new ForwardSeekPolicy(double.PositiveInfinity),
            };
            foreach (double target in schedule)
            {
                DecodedVideoFrame expected = sequential.ReadFrameAt(target);
                DecodedVideoFrame actual = seeking.ReadFrameAt(target);
                Assert.Equal(expected.TimestampMs, actual.TimestampMs, precision: 3);
                Assert.Equal(expected.SampleDurationMs, actual.SampleDurationMs, precision: 3);
                Assert.Equal(ReadIndex(expected), ReadIndex(actual));
            }

            Assert.True(seeking.SeekCount > sequential.SeekCount, "Distant forward requests did not seek.");
            if (table.KeyFrameMs.Count >= 3)
            {
                // With periodic key frames the distant jumps must skip work. A single-GOP encoder
                // can only land at 0, which the adaptive threshold bounds but cannot make cheaper.
                Assert.True(
                    seeking.DecodedFrameCount < sequential.DecodedFrameCount,
                    $"Seeking decoded {seeking.DecodedFrameCount} samples, sequential {sequential.DecodedFrameCount}.");
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(1000.0 / Fps)]
    [InlineData(800)] // GIF export at 5 fps and 4x speed: the largest sequential schedule step.
    public void SequentialSchedules_NeverSeekAfterTheFirstRequest(double stepMs)
    {
        string root = NewRoot();
        try
        {
            string clip = Path.Combine(root, "indexed.mp4");
            EncodeIndexedClip(clip);
            Mp4SampleTable table = Mp4SampleTable.Read(clip);
            using var reader = new MediaFoundationVideoFrameReader(clip);
            int lastIndex = 0;
            for (double target = 0; target < reader.DurationMs; target += stepMs)
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(target);
                lastIndex = ExpectedIndex(table, target);
                Assert.Equal(lastIndex, ReadIndex(frame));
            }

            // Every sample up to the final request (plus one look-ahead) was decoded in order.
            Assert.Equal(1, reader.SeekCount);
            Assert.Equal(Math.Min(Frames, lastIndex + 2), reader.DecodedFrameCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void BackwardRequestAfterForwardSeek_SeeksAgainAndStaysExact()
    {
        string root = NewRoot();
        try
        {
            string clip = Path.Combine(root, "indexed.mp4");
            EncodeIndexedClip(clip);
            Mp4SampleTable table = Mp4SampleTable.Read(clip);
            using var reader = new MediaFoundationVideoFrameReader(clip);

            Assert.Equal(0, ReadIndex(reader.ReadFrameAt(0)));
            Assert.Equal(ExpectedIndex(table, 9_000), ReadIndex(reader.ReadFrameAt(9_000)));
            Assert.Equal(2, reader.SeekCount);
            Assert.Equal(ExpectedIndex(table, 1_234), ReadIndex(reader.ReadFrameAt(1_234)));
            Assert.Equal(3, reader.SeekCount);
            Assert.Equal(ExpectedIndex(table, 1_300), ReadIndex(reader.ReadFrameAt(1_300)));
            Assert.Equal(3, reader.SeekCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Probe_StillRejectsAHeaderThatAdvertisesMoreThanItsSamples()
    {
        string root = NewRoot();
        try
        {
            string clip = Path.Combine(root, "declared-longer.mp4");
            EncodeIndexedClip(clip);
            Mp4SampleTable.ScaleDeclaredDurations(clip, 2);

            // Sequential decoding is the reference: it reaches the real final sample and the tail
            // check rejects it. The forward-seeking reader must reach the same verdict.
            InvalidDataException reference = Assert.Throws<InvalidDataException>(() =>
            {
                using var sequential = new MediaFoundationVideoFrameReader(clip)
                {
                    ForwardSeekPolicy = new ForwardSeekPolicy(double.PositiveInfinity),
                };
                Assert.InRange(sequential.DurationMs, 19_999, 20_001);
                _ = sequential.ReadFrameAt(0);
                VideoFrameRenderPipeline.ValidateDecodedTail(
                    sequential.DurationMs,
                    sequential.ReadFrameAt(sequential.DurationMs - 1));
            });

            InvalidDataException probe = RunSta(() =>
                Assert.Throws<InvalidDataException>(() => VideoFrameRenderPipeline.Probe(clip)));

            Assert.Contains("ends at 10000.000 ms", reference.Message, StringComparison.Ordinal);
            Assert.Equal(reference.Message, probe.Message);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static int ExpectedIndex(Mp4SampleTable table, double targetMs)
    {
        int index = 0;
        for (int sample = 0; sample < table.PresentationMs.Count; sample++)
        {
            if (table.PresentationMs[sample] <= targetMs + TimestampEpsilonMs)
            {
                index = sample;
            }
        }

        return index;
    }

    /// <summary>Encodes each frame's index as wide black/white bars that survive H.264 intact.</summary>
    private static void EncodeIndexedClip(string path)
    {
        var options = new VideoEncoderOptions(
            path,
            Width,
            Height,
            Fps,
            VideoEncoderOptions.DeriveBitrate(Width, Height, Fps));
        int stride = Width * 4;
        var pixels = new byte[stride * Height];
        Span<uint> image = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        int bar = Width / IndexBits;
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                for (int y = 0; y < Height; y++)
                {
                    for (int bit = 0; bit < IndexBits; bit++)
                    {
                        uint color = ((frame >> bit) & 1) != 0 ? 0xFFFFFFFFu : 0xFF000000u;
                        image.Slice((y * Width) + (bit * bar), bar).Fill(color);
                    }
                }

                encoder.WriteFrame(new EncoderFrame(pixels, Width, Height, stride, frame * 1000.0 / Fps));
            }

            encoder.Complete();
        }
    }

    private static int ReadIndex(DecodedVideoFrame frame)
    {
        int bar = frame.Width / IndexBits;
        int index = 0;
        for (int bit = 0; bit < IndexBits; bit++)
        {
            int x = (bit * bar) + (bar / 2);
            int y = frame.Height / 2;
            int offset = (y * frame.Stride) + (x * 4);
            int luma = (frame.Pixels[offset] + frame.Pixels[offset + 1] + frame.Pixels[offset + 2]) / 3;
            if (luma >= 128)
            {
                index |= 1 << bit;
            }
        }

        return index;
    }

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }

        return result;
    }

    private static string NewRoot() =>
        TestRecycleBin.CreateTempSubdirectory("mycapture-forward-seek-").FullName;

    private static void DeleteRoot(string root)
    {
        try { TestRecycleBin.DeleteDirectory(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
