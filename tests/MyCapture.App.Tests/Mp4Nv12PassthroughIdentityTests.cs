using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Checks plain-trim pixels before the lossy encoder, independently of output PSNR.</summary>
public sealed class Mp4Nv12PassthroughIdentityTests(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720, Fps = 30, SourceFrames = 45, OutputFrames = 30;
    private const double StartMs = 300, EndMs = 1300;
    private static readonly VideoYuvEncoding Studio709 = new(Bt709: true, FullRange: false);

    [Fact]
    public void Plain720pTrim_SubmitsExactDecodedNv12BytesAcrossRunsAndDecoders() => StaTestHost.Run(() =>
    {
        string evidence = CreateEvidenceDirectory();
        output.WriteLine($"Retained NV12 input evidence: {evidence}");
        var observations = new List<object>();
        string? sourceBefore = null, sourceAfter = null, failure = null;
        bool passed = false;
        try
        {
            string source = DiagnosticOutputPaths.Child(evidence, "source.mp4");
            Assert.False(File.Exists(source));
            RecordingResult recording = SyntheticDocumentClip.Encode(source, Width, Height, Fps, SourceFrames);
            sourceBefore = TestDiagnosticArtifacts.HashFile(source);
            SourceTimeline sourceTimeline = ReadExactSourceTimeline(source);
            double[] presentationMs = Mp4SampleTable.Read(source).PresentationMs.Order().ToArray();
            observations.Add(new { kind = "source", bytes = new FileInfo(source).Length,
                sha256 = sourceBefore, presentationMs, sourceTimeline.TimeScale,
                presentationMediaTicks = sourceTimeline.PresentationTicks });
            Assert.Equal(SourceFrames, presentationMs.Length);
            Assert.Equal(presentationMs,
                sourceTimeline.PresentationTicks.Select(value => value * 1000.0 / sourceTimeline.TimeScale).ToArray());

            var software = ReadReference("direct-software", preferHardware: false);
            var preferred = ReadReference("direct-preferred", preferHardware: true);
            for (int index = 0; index < OutputFrames; index++)
            {
                DecodedVideoFrame left = software.Frames[index], right = preferred.Frames[index];
                int differentBytes = 0;
                for (int offset = 0; offset < left.Pixels.Length; offset++)
                    if (left.Pixels[offset] != right.Pixels[offset]) differentBytes++;
                // Decoder-to-decoder bit identity is an observation, not this product gate.
                observations.Add(new { kind = "decoder-byte-comparison", index, equal = differentBytes == 0,
                    differentBytes, softwareSha256 = Hash(left.Pixels), preferredSha256 = Hash(right.Pixels),
                    softwareTimestampMs = left.TimestampMs, preferredTimestampMs = right.TimestampMs,
                    softwareSampleDurationMs = left.SampleDurationMs, preferredSampleDurationMs = right.SampleDurationMs });
            }

            Run("default", null);
            Run("default-repeat", null);
            Run("software", new Mp4RenderOptions { PreferHardwareDecode = false });
            sourceAfter = TestDiagnosticArtifacts.HashFile(source);
            Assert.Equal(sourceBefore, sourceAfter);
            passed = true;

            (DecodedVideoFrame[] Frames, bool[] HardwareByFrame) ReadReference(string name, bool preferHardware)
            {
                using var reader = new MediaFoundationVideoFrameReader(source,
                    new MediaFoundationVideoFrameReaderOptions
                    {
                        OutputFormat = DecodedVideoPixelFormat.Nv12,
                        PreferHardwareDecode = preferHardware,
                    });
                observations.Add(new { kind = "direct-reader-open", name, preferHardware,
                    reader.IsHardwareAccelerated, reader.HardwareFallbackReason, reader.OutputFormat,
                    reader.Width, reader.Height, reader.YuvEncoding });
                Assert.Equal(DecodedVideoPixelFormat.Nv12, reader.OutputFormat);
                Assert.Equal((Width, Height), (reader.Width, reader.Height));
                Assert.Equal(Studio709, reader.YuvEncoding);
                if (!preferHardware) Assert.False(reader.IsHardwareAccelerated);
                var frames = new DecodedVideoFrame[OutputFrames];
                var hashes = new string[OutputFrames];
                var hardwareByFrame = new bool[OutputFrames];
                for (int index = 0; index < frames.Length; index++)
                {
                    double requestedMs = StartMs + index * (1000.0 / Fps);
                    DecodedVideoFrame frame = reader.ReadFrameAt(requestedMs);
                    hashes[index] = Hash(frame.Pixels);
                    hardwareByFrame[index] = reader.IsHardwareAccelerated;
                    long sourceMediaTicks = sourceTimeline.PresentationTicks[index + 9];
                    (long lowerTicks, long upperTicks) = Quantized100nsInterval(sourceMediaTicks, sourceTimeline.TimeScale);
                    long actualTicks = Read100nsTicks(frame.TimestampMs);
                    observations.Add(new { kind = "direct-frame", name, index, requestedMs,
                        frame.Format, frame.Width, frame.Height, frame.Stride, frame.TimestampMs,
                        frame.SampleDurationMs, frame.SampleDurationInferred,
                        reader.IsHardwareAccelerated, reader.HardwareFallbackReason,
                        sourceMediaTicks, sourceTimeline.TimeScale, lowerTicks, upperTicks, actualTicks,
                        bytes = frame.Pixels.Length, sha256 = hashes[index] });
                    Assert.Equal(DecodedVideoPixelFormat.Nv12, frame.Format);
                    Assert.Equal((Width, Height, Width), (frame.Width, frame.Height, frame.Stride));
                    Assert.Equal(Width * Height * 3 / 2, frame.Pixels.Length);
                    // Source PTS is an exact rational number of mdhd units. ReadSample exposes
                    // integral 100 ns units, without promising a rounding direction when the
                    // conversion has a remainder. Accept only its adjacent floor/ceiling ticks;
                    // an exactly representable PTS has a single allowed value. No ms epsilon.
                    Assert.True(IsQuantizedSourceTime(sourceMediaTicks, sourceTimeline.TimeScale, actualTicks),
                        $"{name} frame {index}: {actualTicks} x 100 ns is outside [{lowerTicks}, {upperTicks}] "
                        + $"for source PTS {sourceMediaTicks}/{sourceTimeline.TimeScale} seconds.");
                    Assert.Equal(Studio709, reader.YuvEncoding);
                    if (!preferHardware) Assert.False(reader.IsHardwareAccelerated);
                    frames[index] = frame;
                }
                observations.Add(new { kind = "direct-reader-end", name, reader.IsHardwareAccelerated,
                    reader.HardwareFallbackReason, reader.OutputFormat, reader.YuvEncoding });
                Assert.Equal(OutputFrames, hashes.Distinct(StringComparer.Ordinal).Count());
                // CopyNv12 allocates a fresh packed array; later reads must not mutate it.
                for (int index = 0; index < frames.Length; index++) Assert.Equal(hashes[index], Hash(frames[index].Pixels));
                return (frames, hardwareByFrame);
            }

            void Run(string name, Mp4RenderOptions? options)
            {
                var encoders = new List<ComparingEncoder>();
                string unusedOutput = DiagnosticOutputPaths.Child(evidence, name + "-not-encoded.mp4");
                Mp4RenderStatistics statistics = TrimReencoder.ReencodeWithStatistics(
                    source, unusedOutput, StartMs, EndMs, recording,
                    encoderOptions =>
                    {
                        observations.Add(new { kind = "encoder-request", name, attempt = encoders.Count,
                            options = encoderOptions with { OutputPath = Path.GetFileName(encoderOptions.OutputPath) } });
                        Assert.Equal(VideoEncoderInputFormat.Nv12, encoderOptions.InputFormat);
                        Assert.Equal((Width, Height, Fps), (encoderOptions.Width, encoderOptions.Height, encoderOptions.Fps));
                        Assert.False(encoderOptions.FullRange);
                        var encoder = new ComparingEncoder(software.Frames, preferred.Frames, name, encoders.Count, observations);
                        encoders.Add(encoder);
                        return encoder;
                    },
                    NullLogger.Instance,
                    renderOptions: options);
                observations.Add(new { kind = "render", name, statistics,
                    attempts = encoders.Select(encoder => new { encoder.Count, encoder.Completed, encoder.Disposed }).ToArray() });
                Assert.Contains(statistics.FrameSource, new[] { "nv12-hardware", "nv12-software" });
                if (options?.PreferHardwareDecode == false) Assert.Equal("nv12-software", statistics.FrameSource);
                Assert.Equal((OutputFrames, 0), (statistics.Frames, statistics.ComposedFrames));
                ComparingEncoder completed = Assert.Single(encoders, encoder => encoder.Completed);
                Assert.Equal(OutputFrames, completed.Count);
                bool hardware = statistics.FrameSource == "nv12-hardware";
                foreach (FrameComparison comparison in completed.Comparisons)
                {
                    if (hardware) Assert.True(preferred.HardwareByFrame[comparison.Index],
                        "The completed render used hardware decode, but no matching hardware reference was captured.");
                    bool equal = hardware ? comparison.PreferredEqual : comparison.SoftwareEqual;
                    observations.Add(new { kind = "matched-mode-byte-gate", name, comparison.Index,
                        frameSource = statistics.FrameSource, reference = hardware ? "direct-preferred" : "direct-software", equal });
                    Assert.True(equal, $"{name} frame {comparison.Index} changed NV12 bytes from its {statistics.FrameSource} reference.");
                    Assert.Equal(hardware ? comparison.PreferredHash : comparison.SoftwareHash, comparison.ActualHash);
                }
                Assert.All(encoders, encoder => Assert.True(encoder.Disposed));
                Assert.False(File.Exists(unusedOutput));
            }
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            throw;
        }
        finally
        {
            // Keep the synthetic source and partial evidence on both pass and failure.
            // This test performs no file or directory cleanup.
            TestDiagnosticArtifacts.WriteJson(evidence, "pre-encoder-evidence.json", new
            {
                status = passed ? "PASS" : "FAIL", failure,
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                platformAssemblySha256 = TestDiagnosticArtifacts.HashFile(typeof(MediaFoundationVideoFrameReader).Assembly.Location),
                appAssemblySha256 = TestDiagnosticArtifacts.HashFile(typeof(TrimReencoder).Assembly.Location),
                testAssemblySha256 = TestDiagnosticArtifacts.HashFile(typeof(Mp4Nv12PassthroughIdentityTests).Assembly.Location),
                width = Width, height = Height, fps = Fps, sourceFrames = SourceFrames, outputFrames = OutputFrames,
                trimInMs = StartMs, trimOutMs = EndMs, sourceBefore, sourceAfter,
                sourceFixture = "SyntheticDocumentClip.Encode; default BGRA32 options with derived bitrate",
                boundary = "IVideoEncoder.WriteFrame before H.264 encoding; full packed NV12 bytes compared, no output codec or PSNR assertion",
                observations,
            });
        }
    });

    private sealed class ComparingEncoder(
        DecodedVideoFrame[] software, DecodedVideoFrame[] preferred, string name, int attempt, List<object> observations) : IVideoEncoder
    {
        public int Width => Mp4Nv12PassthroughIdentityTests.Width;
        public int Height => Mp4Nv12PassthroughIdentityTests.Height;
        internal int Count { get; private set; }
        internal bool Completed { get; private set; }
        internal bool Disposed { get; private set; }
        internal List<FrameComparison> Comparisons { get; } = [];

        public void WriteFrame(in EncoderFrame frame)
        {
            Assert.False(Completed || Disposed);
            Assert.InRange(Count, 0, OutputFrames - 1);
            DecodedVideoFrame expected = software[Count], preferredFrame = preferred[Count];
            var comparison = new FrameComparison(Count,
                expected.Pixels.AsSpan().SequenceEqual(frame.Pixels), preferredFrame.Pixels.AsSpan().SequenceEqual(frame.Pixels),
                Hash(expected.Pixels), Hash(preferredFrame.Pixels), Hash(frame.Pixels));
            Comparisons.Add(comparison);
            observations.Add(new { kind = "encoder-frame", name, attempt, index = Count,
                frame.Format, frame.Width, frame.Height, frame.Stride, frame.TimestampMs,
                expectedSourceTimestampMs = expected.TimestampMs, expectedOutputTimestampMs = Count * (1000.0 / Fps),
                bytes = frame.Pixels.Length, expectedBytes = expected.Pixels.Length,
                comparison });
            Assert.Equal(VideoEncoderInputFormat.Nv12, frame.Format);
            Assert.Equal((expected.Width, expected.Height, expected.Stride), (frame.Width, frame.Height, frame.Stride));
            Assert.Equal(expected.Pixels.Length, frame.Pixels.Length);
            Assert.Equal(Count * (1000.0 / Fps), frame.TimestampMs);
            Count++;
        }

        public void Complete()
        {
            Assert.False(Completed || Disposed);
            Assert.Equal(OutputFrames, Count);
            Completed = true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed record FrameComparison(int Index, bool SoftwareEqual, bool PreferredEqual,
        string SoftwareHash, string PreferredHash, string ActualHash);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public void SourceTimebaseQuantization_OnlyAcceptsAdjacent100nsTicks()
    {
        Assert.Equal((3_666_666L, 3_666_667L), Quantized100nsInterval(11_000, 30_000));
        Assert.True(IsQuantizedSourceTime(11_000, 30_000, 3_666_666));
        Assert.True(IsQuantizedSourceTime(11_000, 30_000, 3_666_667));
        Assert.False(IsQuantizedSourceTime(11_000, 30_000, 3_666_665));
        Assert.False(IsQuantizedSourceTime(11_000, 30_000, 3_666_668));
        Assert.False(IsQuantizedSourceTime(11_000, 30_000, 3_333_333)); // Previous source frame.
        Assert.Equal((3_000_000L, 3_000_000L), Quantized100nsInterval(9_000, 30_000));
        Assert.True(IsQuantizedSourceTime(9_000, 30_000, 3_000_000));
        Assert.False(IsQuantizedSourceTime(9_000, 30_000, 2_999_999));
        Assert.False(IsQuantizedSourceTime(9_000, 30_000, 3_000_001));
    }

    private static (long Lower, long Upper) Quantized100nsInterval(long mediaTicks, uint timeScale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mediaTicks);
        ArgumentOutOfRangeException.ThrowIfZero(timeScale);
        long numerator = checked(mediaTicks * 10_000_000L);
        long lower = Math.DivRem(numerator, timeScale, out long remainder);
        return (lower, checked(lower + (remainder == 0 ? 0 : 1)));
    }

    private static bool IsQuantizedSourceTime(long mediaTicks, uint timeScale, long timestampTicks)
    {
        (long lower, long upper) = Quantized100nsInterval(mediaTicks, timeScale);
        return timestampTicks >= lower && timestampTicks <= upper;
    }

    private static long Read100nsTicks(double milliseconds)
    {
        Assert.True(double.IsFinite(milliseconds) && milliseconds >= 0);
        long ticks = checked((long)Math.Round(milliseconds * 10_000));
        // The product divides ReadSample's integral ticks by 10,000. Recover that integer,
        // and require exact round-trip of the public double rather than accepting off-grid time.
        Assert.Equal(milliseconds, ticks / 10_000.0);
        return ticks;
    }

    private sealed record SourceTimeline(uint TimeScale, long[] PresentationTicks);

    private static SourceTimeline ReadExactSourceTimeline(string path)
    {
        // Keep the fixture's integer timebase before Mp4SampleTable converts it to double ms.
        // Reuse its box reader; this is confined to the generated, non-edited video fixture.
        byte[] file = File.ReadAllBytes(path);
        (int Start, int End) Require((int Start, int End) parent, string type) =>
            Mp4SampleTable.Find(file, parent.Start, parent.End, type)
                ?? throw new InvalidDataException($"{type} box is missing.");
        (int Start, int End) moov = Require((0, file.Length), "moov");
        int cursor = moov.Start;
        while (Mp4SampleTable.Find(file, cursor, moov.End, "trak") is { } track)
        {
            cursor = track.End;
            var media = Require(track, "mdia");
            var handler = Require(media, "hdlr");
            if (!file.AsSpan(handler.Start + 8, 4).SequenceEqual("vide"u8)) continue;
            if (Mp4SampleTable.Find(file, track.Start, track.End, "edts") is { } edits
                && Mp4SampleTable.Find(file, edits.Start, edits.End, "elst") is not null)
                throw new InvalidDataException("The synthetic source has an edit list; its time mapping needs an explicit oracle.");
            var header = Require(media, "mdhd");
            uint timeScale = Mp4SampleTable.ReadUInt32(file, header.Start + (file[header.Start] == 1 ? 20 : 12));
            ArgumentOutOfRangeException.ThrowIfZero(timeScale);
            var table = Require(Require(media, "minf"), "stbl");
            var times = Require(table, "stts");
            var ticks = new List<long>();
            long decodeTicks = 0;
            uint entries = Mp4SampleTable.ReadUInt32(file, times.Start + 4);
            for (int entry = 0; entry < entries; entry++)
            {
                uint count = Mp4SampleTable.ReadUInt32(file, times.Start + 8 + entry * 8);
                uint delta = Mp4SampleTable.ReadUInt32(file, times.Start + 12 + entry * 8);
                Assert.InRange(count, 1u, (uint)SourceFrames);
                for (uint sample = 0; sample < count; sample++)
                {
                    ticks.Add(decodeTicks);
                    decodeTicks = checked(decodeTicks + delta);
                }
            }
            Assert.Equal(SourceFrames, ticks.Count);
            if (Mp4SampleTable.Find(file, table.Start, table.End, "ctts") is { } offsets)
            {
                bool signed = file[offsets.Start] == 1;
                uint runs = Mp4SampleTable.ReadUInt32(file, offsets.Start + 4);
                int sampleIndex = 0;
                for (int entry = 0; entry < runs; entry++)
                {
                    uint count = Mp4SampleTable.ReadUInt32(file, offsets.Start + 8 + entry * 8);
                    long offset = signed
                        ? BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offsets.Start + 12 + entry * 8, 4))
                        : Mp4SampleTable.ReadUInt32(file, offsets.Start + 12 + entry * 8);
                    Assert.InRange(count, 1u, (uint)SourceFrames);
                    for (uint sample = 0; sample < count; sample++)
                    {
                        Assert.InRange(sampleIndex, 0, ticks.Count - 1);
                        ticks[sampleIndex] = checked(ticks[sampleIndex] + offset);
                        sampleIndex++;
                    }
                }
                Assert.Equal(ticks.Count, sampleIndex);
            }
            return new SourceTimeline(timeScale, ticks.Order().ToArray());
        }
        throw new InvalidDataException("No video track.");
    }

    private static string CreateEvidenceDirectory()
    {
        string? configured = TestDiagnosticArtifacts.Create("MYCAPTURE_NV12_QUALITY_EVIDENCE", "plain-input-1280x720");
        if (configured is not null) return configured;
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "src", "MyCapture.App", "MyCapture.App.csproj")))
            repository = repository.Parent;
        if (repository is null) throw new IOException("Could not locate the test repository for retained NV12 evidence.");
        string parent = DiagnosticOutputPaths.Create(Path.Combine(repository.FullName, "artifacts", "nv12-preencoder-evidence"));
        string directory = DiagnosticOutputPaths.Child(parent, "plain-input-1280x720-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("NV12 evidence directory already exists.");
        return DiagnosticOutputPaths.Create(directory);
    }
}
