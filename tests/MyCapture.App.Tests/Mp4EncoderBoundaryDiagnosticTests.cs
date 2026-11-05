using System.Diagnostics;
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

/// <summary>Opt-in, retained causal evidence. Existing export quality assertions are unchanged.</summary>
public sealed partial class Mp4EncoderBoundaryDiagnosticTests(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720, Fps = 30, Frames = 30;
    private const string SourceHash = "DF8A3E89229ADD70189F9FECBBF7A52B33049AB8BA6C2E9619ACBDDFF73A4D07";
    private const string TimelineReferenceHash = "FBFBA56A4E2D40B4FA93FE96133103A96DE8C15CC00CB0FE26491E63415828B9";
    private static readonly VideoYuvEncoding Studio709 = new(true, false);

    [EncoderBoundaryDiagnosticFact]
    public void Fixed720pPayloads_RecordBoundaryAndEightEncoderReplays() => StaTestHost.Run(() =>
    {
        string evidence = Evidence("fixed-720p");
        var observations = new List<object>();
        var runs = new List<RunResult>();
        var comparisons = new List<object>();
        bool completed = false;
        string? failure = null;
        try
        {
            Assert.False(MediaFoundationVideoEncoder.ForceSoftwareForTest);
            string source = TestDiagnosticArtifacts.Confined(Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_BOUNDARY_SOURCE")
                ?? throw new InvalidOperationException("MYCAPTURE_ENCODER_BOUNDARY_SOURCE must identify the retained failing source."));
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(source));
            string timelineReference = Path.Combine(Path.GetDirectoryName(source)!, "nv12.mp4");
            Assert.Equal(TimelineReferenceHash, TestDiagnosticArtifacts.HashFile(timelineReference));
            double[] expectedTimeline = Mp4SampleTable.Read(timelineReference).PresentationMs.Order().ToArray();
            Assert.Equal(Frames, expectedTimeline.Length);
            string retainedSource = DiagnosticOutputPaths.Child(evidence, "source.mp4");
            File.Copy(source, retainedSource, overwrite: false);
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(retainedSource));
            Assert.Equal(45, Mp4SampleTable.Read(retainedSource).PresentationMs.Count);
            var recording = new RecordingResult(retainedSource, 1500, Fps, 45, Width, Height);
            EncoderFrame[] direct = Collect("direct", null);
            EncoderFrame[] roundtrip = Collect("rgb-roundtrip", new Mp4RenderOptions { PassThroughNv12 = false });
            var references = new byte[Frames][];
            using (var software = new MediaFoundationVideoFrameReader(retainedSource,
                       new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 }))
            using (var legacyRgb = new MediaFoundationVideoFrameReader(retainedSource))
            {
                Assert.Equal(Studio709, software.YuvEncoding);
                for (int index = 0; index < Frames; index++)
                {
                    double time = 300 + index * (1000.0 / Fps);
                    DecodedVideoFrame decoded = software.ReadFrameAt(time);
                    Assert.Equal(DecodedVideoPixelFormat.Nv12, decoded.Format);
                    Assert.Equal((Width, Height, Width), (decoded.Width, decoded.Height, decoded.Stride));
                    byte[] rgb = Nv12FrameConverter.ToBgra(decoded.Pixels, Width, Height, software.YuvEncoding, Width, Height);
                    byte[] converted = new byte[BgraToNv12Converter.RequiredLength(Width, Height)];
                    BgraToNv12Converter.Convert(rgb, Width * 4, Width, Height, Studio709, converted);
                    Assert.Equal(converted, roundtrip[index].Pixels);
                    DecodedVideoFrame reference = legacyRgb.ReadFrameAt(time);
                    Assert.Equal((Width, Height, Width * 4), (reference.Width, reference.Height, reference.Stride));
                    Assert.Equal(DecodedVideoPixelFormat.Bgr32, reference.Format);
                    Assert.Equal(decoded.TimestampMs, reference.TimestampMs);
                    references[index] = reference.Pixels;
                    observations.Add(new
                    {
                        kind = "input-comparison", index, sourceTimestampMs = decoded.TimestampMs,
                        outputTimestampMs = direct[index].TimestampMs,
                        directSha256 = Hash(direct[index].Pixels), softwareSha256 = Hash(decoded.Pixels),
                        roundtripSha256 = Hash(roundtrip[index].Pixels), rgbReferenceSha256 = Hash(reference.Pixels),
                        directVsSoftware = Planes(direct[index].Pixels, decoded.Pixels, Width, Height),
                        directVsRoundtrip = Planes(direct[index].Pixels, roundtrip[index].Pixels, Width, Height),
                        applicationRgbVsReference = RgbError(rgb, reference.Pixels),
                    });
                    WriteBytes(evidence, $"direct-{index:D2}.nv12", direct[index].Pixels);
                    WriteBytes(evidence, $"rgb-roundtrip-{index:D2}.nv12", roundtrip[index].Pixels);
                }
                observations.Add(new { kind = "software-reader", software.IsHardwareAccelerated,
                    software.HardwareFallbackReason, software.YuvEncoding });
            }

            foreach (bool hardware in new[] { true, false })
                for (int repetition = 1; repetition <= 2; repetition++)
                {
                    var order = repetition == 1 ? new[] { "direct", "rgb-roundtrip" } : new[] { "rgb-roundtrip", "direct" };
                    foreach (string input in order)
                    {
                        string name = $"{input}-hardware-{hardware.ToString().ToLowerInvariant()}-{repetition}";
                        var options = new VideoEncoderOptions(DiagnosticOutputPaths.Child(evidence, name + ".mp4"),
                            Width, Height, Fps, VideoEncoderOptions.DeriveBitrate(Width, Height, Fps))
                        { InputFormat = VideoEncoderInputFormat.Nv12, AllowHardwareEncoder = hardware };
                        RunResult run = Encode(name, options, input == "direct" ? direct : roundtrip,
                            references, expectedTimeline);
                        runs.Add(run);
                        TestDiagnosticArtifacts.WriteJson(evidence, name + ".json", run);
                    }
                    RunResult left = runs[^2].Name.StartsWith("direct", StringComparison.Ordinal) ? runs[^2] : runs[^1];
                    RunResult right = ReferenceEquals(left, runs[^2]) ? runs[^1] : runs[^2];
                    comparisons.Add(new { hardwareAllowed = hardware, repetition,
                        directPsnr = left.Quality?.Psnr, rgbPsnr = right.Quality?.Psnr,
                        relativeGateObserved = left.Quality is not null && right.Quality is not null
                            ? (bool?)(left.Quality.RgbSquaredErrors.Sum() <= right.Quality.RgbSquaredErrors.Sum()) : null });
                }
            Assert.Equal(8, runs.Count);
            Assert.All(runs, run => Assert.Empty(run.Failures));
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(source));
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(retainedSource));
            completed = true;

            EncoderFrame[] Collect(string name, Mp4RenderOptions? options)
            {
                var collectors = new List<FrameCollector>();
                Mp4RenderStatistics statistics = TrimReencoder.ReencodeWithStatistics(retainedSource,
                    DiagnosticOutputPaths.Child(evidence, name + "-boundary-only.mp4"), 300, 1300, recording,
                    encoderOptions =>
                    {
                        Assert.Equal(VideoEncoderInputFormat.Nv12, encoderOptions.InputFormat);
                        var collector = new FrameCollector(Width, Height);
                        collectors.Add(collector);
                        return collector;
                    }, NullLogger.Instance, renderOptions: options);
                FrameCollector finished = Assert.Single(collectors, item => item.Completed);
                if (name == "direct") Assert.StartsWith("nv12-", statistics.FrameSource, StringComparison.Ordinal);
                else Assert.Equal("rgb-software", statistics.FrameSource);
                Assert.Equal((Frames, 0), (statistics.Frames, statistics.ComposedFrames));
                Assert.Equal(Frames, finished.Frames.Count);
                Assert.All(collectors, item => Assert.True(item.Disposed));
                Assert.Equal(Frames, finished.Frames.Select(frame => Hash(frame.Pixels)).Distinct(StringComparer.Ordinal).Count());
                for (int index = 0; index < Frames; index++)
                {
                    Assert.Equal(VideoEncoderInputFormat.Nv12, finished.Frames[index].Format);
                    Assert.Equal(index * (1000.0 / Fps), finished.Frames[index].TimestampMs);
                    Assert.Equal(Width * Height * 3 / 2, finished.Frames[index].Pixels.Length);
                }
                observations.Add(new { kind = "pipeline-boundary", name, statistics,
                    attempts = collectors.Select(item => new { frames = item.Frames.Count, item.Completed, item.Disposed }).ToArray() });
                return finished.Frames.ToArray();
            }
        }
        catch (Exception ex) { failure = Failure(ex); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", new
            {
                collectionCompleted = completed, failure, environment = EnvironmentEvidence(),
                sourceSha256 = SourceHash, timelineReferenceSha256 = TimelineReferenceHash,
                requestedRuns = 8, actualRuns = runs.Count, observations, runs, relativePsnrComparisons = comparisons,
                qualityVerdict = "Observed comparisons above; the existing Mp4Nv12ExportTests quality gate is unchanged.",
            });
        }
    });

    [EncoderBoundaryDiagnosticFact]
    public void SingleFrame_RecordTwelveIndependentEncoderFinalizations() => StaTestHost.Run(() =>
    {
        string evidence = Evidence("single-frame");
        var runs = new List<RunResult>();
        bool completed = false;
        string? failure = null;
        try
        {
            Assert.False(MediaFoundationVideoEncoder.ForceSoftwareForTest);
            const int width = 160, height = 90, fps = 10;
            byte[] bgra = new byte[width * height * 4];
            for (int index = 0; index < bgra.Length; index += 4)
            { bgra[index] = 0x30; bgra[index + 1] = 0x20; bgra[index + 2] = 0x10; bgra[index + 3] = 0xFF; }
            byte[] nv12 = new byte[BgraToNv12Converter.RequiredLength(width, height)];
            BgraToNv12Converter.Convert(bgra, width * 4, width, height, new VideoYuvEncoding(false, false), nv12);
            WriteBytes(evidence, "input.bgra", bgra);
            WriteBytes(evidence, "input.nv12", nv12);
            foreach (bool hardware in new[] { true, false })
                foreach (VideoEncoderInputFormat format in new[] { VideoEncoderInputFormat.Bgra32, VideoEncoderInputFormat.Nv12 })
                    for (int repetition = 1; repetition <= 3; repetition++)
                    {
                        string name = $"{format}-hardware-{hardware.ToString().ToLowerInvariant()}-{repetition}";
                        var options = new VideoEncoderOptions(DiagnosticOutputPaths.Child(evidence, name + ".mp4"),
                            width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps))
                        { InputFormat = format, AllowHardwareEncoder = hardware };
                        var frame = new EncoderFrame(format == VideoEncoderInputFormat.Nv12 ? nv12 : bgra,
                            width, height, format == VideoEncoderInputFormat.Nv12 ? width : width * 4, 0) { Format = format };
                        RunResult run = Encode(name, options, [frame], null, [0]);
                        runs.Add(run);
                        TestDiagnosticArtifacts.WriteJson(evidence, name + ".json", run);
                    }
            Assert.Equal(12, runs.Count);
            Assert.All(runs, run => Assert.Empty(run.Failures));
            completed = true;
        }
        catch (Exception ex) { failure = Failure(ex); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", new
            {
                collectionCompleted = completed, failure, environment = EnvironmentEvidence(),
                requestedRuns = 12, actualRuns = runs.Count, runs,
                sourceFixture = "VideoLibraryServiceTests.EncodeClip solid BGRA 160x90, 10fps, one frame; explicit studio BT.601 conversion for NV12 cases",
            });
        }
    });

    private static RunResult Encode(string name, VideoEncoderOptions options, EncoderFrame[] frames,
        byte[][]? references, double[] expectedTimeline, bool captureDiagnostics = true,
        uint? diagnosticQualityVsSpeed = null, bool captureCodecParameters = false, bool captureCodecCost = false)
    {
        if (!captureDiagnostics && (frames.Length != 1 || captureCodecParameters || captureCodecCost || diagnosticQualityVsSpeed.HasValue))
            throw new ArgumentException("The immediate-finalize probe accepts exactly one default-configuration frame.");
        var run = new RunResult { Name = name, Options = options with { OutputPath = Path.GetFileName(options.OutputPath) },
            InputHashes = frames.Select(frame => Hash(frame.Pixels)).ToArray() };
        MediaFoundationVideoEncoder? encoder = null;
        bool finalized = false;
        using Process? costProcess = captureCodecCost ? Process.GetCurrentProcess() : null;
        TimeSpan cpuStarted = costProcess?.TotalProcessorTime ?? default;
        long wallStarted = captureCodecCost ? Stopwatch.GetTimestamp() : 0;
        try
        {
            encoder = diagnosticQualityVsSpeed is { } effort
                ? new MediaFoundationVideoEncoder(options, NullLogger.Instance, effort)
                : new MediaFoundationVideoEncoder(options, NullLogger.Instance);
            if (!captureDiagnostics)
            {
                encoder.WriteFrame(frames[0]);
                encoder.Complete();
                finalized = true;
                run.SubmittedFrames = 1;
            }
            else
            {
                run.Snapshots.Add(new { stage = "initialized", value = encoder.CaptureDiagnostics() });
                if (captureCodecParameters) run.CodecBefore.AddRange(encoder.CaptureCodecParameters());
                foreach (EncoderFrame frame in frames) { encoder.WriteFrame(frame); run.SubmittedFrames++; }
                run.Snapshots.Add(new { stage = "before-finalize", value = encoder.CaptureDiagnostics() });
                if (captureCodecParameters) run.CodecAfter.AddRange(encoder.CaptureCodecParameters());
                encoder.Complete();
                finalized = true;
                run.Snapshots.Add(new { stage = "after-finalize", value = encoder.CaptureDiagnostics() });
            }
        }
        catch (Exception ex)
        {
            run.Failures.Add(Failure(ex));
            if (encoder is not null) run.Snapshots.Add(new { stage = "operation-failed", value = encoder.CaptureDiagnostics() });
        }
        finally
        {
            try { encoder?.Dispose(); }
            catch (Exception ex) { run.Failures.Add("Dispose: " + Failure(ex)); }
            if (captureCodecCost)
            {
                long wallStopped = Stopwatch.GetTimestamp();
                costProcess!.Refresh();
                run.EncodeDisposeWallMs = Stopwatch.GetElapsedTime(wallStarted, wallStopped).TotalMilliseconds;
                run.EncodeDisposeProcessCpuMs = (costProcess.TotalProcessorTime - cpuStarted).TotalMilliseconds;
                run.EncodeDisposeCostScope = "Constructor through Dispose, including diagnostic getters; output decode/hash excluded. Process CPU includes managed runtime and native worker threads.";
            }
        }
        try
        {
            Assert.Equal(run.InputHashes, frames.Select(frame => Hash(frame.Pixels)).ToArray());
            if (File.Exists(options.OutputPath))
            {
                run.OutputBytes = new FileInfo(options.OutputPath).Length;
                run.OutputHash = TestDiagnosticArtifacts.HashFile(options.OutputPath);
            }
            if (finalized)
            {
                double[] timeline = Mp4SampleTable.Read(options.OutputPath).PresentationMs.Order().ToArray();
                Assert.Equal(expectedTimeline, timeline);
                Assert.Equal(frames.Length, timeline.Length);
                double duration = MediaFoundationVideoFrameReader.ReadContainerDurationMs(options.OutputPath);
                if (frames.Length == 1) Assert.InRange(duration, 99.9, 100.1); // Original service contract.
                using var reader = new MediaFoundationVideoFrameReader(options.OutputPath);
                var errors = new long[frames.Length];
                for (int index = 0; index < frames.Length; index++)
                {
                    DecodedVideoFrame decoded = reader.ReadFrameAt(index * (1000.0 / options.Fps));
                    Assert.Equal((options.Width, options.Height, options.Width * 4), (decoded.Width, decoded.Height, decoded.Stride));
                    Assert.Equal(DecodedVideoPixelFormat.Bgr32, decoded.Format);
                    if (references is not null) errors[index] = RgbError(references[index], decoded.Pixels);
                    run.DecodedHashes.Add(Hash(decoded.Pixels));
                    run.DecodedTimestampsMs.Add(decoded.TimestampMs);
                }
                double? psnr = null;
                if (references is not null)
                {
                    double mse = errors.Sum() / (frames.Length * (double)options.Width * options.Height * 3);
                    psnr = mse == 0 ? null : 10 * Math.Log10(255.0 * 255.0 / mse);
                }
                run.Quality = new QualityResult(timeline, duration, errors, psnr,
                    references is not null && errors.Sum() == 0);
            }
        }
        catch (Exception ex) { run.Failures.Add("Output validation: " + Failure(ex)); }
        return run;
    }

    private string Evidence(string name)
    {
        string directory = TestDiagnosticArtifacts.Create("MYCAPTURE_ENCODER_BOUNDARY_OUTPUT", name)
            ?? throw new InvalidOperationException("MYCAPTURE_ENCODER_BOUNDARY_OUTPUT must be a repository artifacts child.");
        output.WriteLine("Retained encoder boundary evidence: " + directory);
        return directory;
    }

    private static object EnvironmentEvidence() => new
    {
        runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        forceSoftwareForTest = MediaFoundationVideoEncoder.ForceSoftwareForTest,
        platformSha256 = TestDiagnosticArtifacts.HashFile(typeof(MediaFoundationVideoEncoder).Assembly.Location),
        appSha256 = TestDiagnosticArtifacts.HashFile(typeof(TrimReencoder).Assembly.Location),
        testsSha256 = TestDiagnosticArtifacts.HashFile(typeof(Mp4EncoderBoundaryDiagnosticTests).Assembly.Location),
    };

    private static object[] Planes(byte[] left, byte[] right, int width, int height)
    {
        int luma = checked(width * height);
        Assert.Equal(luma * 3 / 2, left.Length); Assert.Equal(left.Length, right.Length);
        var result = new List<object>();
        foreach ((string plane, int start, int count, int step) in new[]
                     { ("Y", 0, luma, 1), ("U", luma, luma / 4, 2), ("V", luma + 1, luma / 4, 2) })
        {
            long squaredError = 0, signedError = 0; int different = 0, maxAbsolute = 0;
            for (int index = 0, offset = start; index < count; index++, offset += step)
            {
                int error = left[offset] - right[offset];
                squaredError += (long)error * error; signedError += error;
                if (error != 0) different++;
                maxAbsolute = Math.Max(maxAbsolute, Math.Abs(error));
            }
            result.Add(new { plane, samples = count, different, maxAbsolute, squaredError, signedError });
        }
        return result.ToArray();
    }

    private static long RgbError(byte[] left, byte[] right)
    {
        Assert.Equal(left.Length, right.Length);
        long result = 0;
        for (int offset = 0; offset < left.Length; offset += 4)
            for (int channel = 0; channel < 3; channel++)
            { int delta = left[offset + channel] - right[offset + channel]; result += (long)delta * delta; }
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Failure(Exception ex) => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
    private static void WriteBytes(string directory, string name, byte[] bytes)
    {
        using var stream = new FileStream(DiagnosticOutputPaths.Child(directory, name), FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private sealed class FrameCollector(int width, int height) : IVideoEncoder
    {
        public int Width => width;
        public int Height => height;
        internal List<EncoderFrame> Frames { get; } = [];
        internal bool Completed { get; private set; }
        internal bool Disposed { get; private set; }
        public void WriteFrame(in EncoderFrame frame)
        {
            Assert.False(Completed || Disposed);
            Assert.Equal((width, height, width), (frame.Width, frame.Height, frame.Stride));
            Frames.Add(frame with { Pixels = (byte[])frame.Pixels.Clone() });
        }
        public void Complete() => Completed = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class RunResult
    {
        public required string Name { get; init; }
        public required VideoEncoderOptions Options { get; init; }
        public required string[] InputHashes { get; init; }
        public int SubmittedFrames { get; set; }
        public List<object> Snapshots { get; } = [];
        public List<CodecTransformObservation> CodecBefore { get; } = [];
        public List<CodecTransformObservation> CodecAfter { get; } = [];
        public List<string> Failures { get; } = [];
        public List<string> DecodedHashes { get; } = [];
        public List<double> DecodedTimestampsMs { get; } = [];
        public double? EncodeDisposeWallMs { get; set; }
        public double? EncodeDisposeProcessCpuMs { get; set; }
        public string? EncodeDisposeCostScope { get; set; }
        public long? OutputBytes { get; set; }
        public string? OutputHash { get; set; }
        public QualityResult? Quality { get; set; }
    }
    private sealed record QualityResult(double[] PresentationMs, double DurationMs, long[] RgbSquaredErrors,
        double? Psnr, bool ZeroRgbError);
}

internal sealed class EncoderBoundaryDiagnosticFactAttribute : FactAttribute
{
    public EncoderBoundaryDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_BOUNDARY_DIAGNOSTICS") != "1")
            Skip = "Set MYCAPTURE_ENCODER_BOUNDARY_DIAGNOSTICS=1 and explicit source/output paths for retained encoder diagnostics.";
    }
}
