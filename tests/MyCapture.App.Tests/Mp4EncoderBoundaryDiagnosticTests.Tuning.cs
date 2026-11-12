using System.IO;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class Mp4EncoderBoundaryDiagnosticTests
{
    private const string BoundaryReportHash = "68CF6F6DC5C3AAE73134BD26B529BD2A0564A3AE33254C3E5AC39E58AFF9D35D";
    private const string ExpectedHardwareUrlHash = "12D0C3051A4CBC3A441287F2170BCD347174E624E652D8128AB266574E5FC18A";
    private static readonly Guid ExpectedHardwareEncoder = new("4be8d3c0-0515-4a37-ad55-e4bae19af471");

    [EncoderTuningDiagnosticFact]
    public void QualityVsSpeedOnly_RecordEightHardwareReplays() => StaTestHost.Run(() =>
    {
        string evidence = Evidence("quality-vs-speed-only");
        var runs = new List<RunResult>();
        var comparisons = new List<object>();
        var inputFiles = new List<(string Path, string Hash)>();
        bool completed = false;
        string? failure = null, retainedSource = null;
        try
        {
            Assert.False(MediaFoundationVideoEncoder.ForceSoftwareForTest);
            string input = DiagnosticOutputPaths.ExistingDirectory(Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_TUNING_INPUT")
                ?? throw new InvalidOperationException("Set MYCAPTURE_ENCODER_TUNING_INPUT to the retained fixed-720p encoder26 directory."));
            string reportPath = DiagnosticOutputPaths.ExistingFile(input, "report.json");
            string source = DiagnosticOutputPaths.ExistingFile(input, "source.mp4");
            // Each pinned input is hashed and parsed from one read; a mismatch fails before parsing.
            byte[] reportBytes = TestDiagnosticArtifacts.ReadVerified(reportPath, BoundaryReportHash);
            byte[] sourceBytes = TestDiagnosticArtifacts.ReadVerified(source, SourceHash);
            inputFiles.Add((reportPath, BoundaryReportHash));
            inputFiles.Add((source, SourceHash));
            // Media Foundation reopens its input by path, so it only decodes this verified copy.
            retainedSource = TestDiagnosticArtifacts.Retain(evidence, "source.mp4", sourceBytes, SourceHash);
            using JsonDocument report = JsonDocument.Parse(reportBytes);
            JsonElement[] observations = report.RootElement.GetProperty("observations").EnumerateArray()
                .Where(item => item.GetProperty("kind").GetString() == "input-comparison")
                .OrderBy(item => item.GetProperty("index").GetInt32()).ToArray();
            Assert.Equal(Frames, observations.Length);
            double[] timeline = report.RootElement.GetProperty("runs")[0].GetProperty("quality")
                .GetProperty("presentationMs").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.Equal(Frames, timeline.Length);
            var direct = new EncoderFrame[Frames];
            var roundtrip = new EncoderFrame[Frames];
            var references = new byte[Frames][];
            using (var reader = new MediaFoundationVideoFrameReader(retainedSource))
                for (int index = 0; index < Frames; index++)
                {
                    JsonElement observation = observations[index];
                    Assert.Equal(index, observation.GetProperty("index").GetInt32());
                    Assert.Equal(index * (1000.0 / Fps), observation.GetProperty("outputTimestampMs").GetDouble());
                    direct[index] = ReadInput("direct", "directSha256");
                    roundtrip[index] = ReadInput("rgb-roundtrip", "roundtripSha256");
                    DecodedVideoFrame reference = reader.ReadFrameAt(300 + index * (1000.0 / Fps));
                    Assert.Equal((Width, Height, Width * 4), (reference.Width, reference.Height, reference.Stride));
                    Assert.Equal(DecodedVideoPixelFormat.Bgr32, reference.Format);
                    Assert.Equal(observation.GetProperty("rgbReferenceSha256").GetString(), Hash(reference.Pixels));
                    references[index] = reference.Pixels;

                    EncoderFrame ReadInput(string prefix, string hashProperty)
                    {
                        string path = DiagnosticOutputPaths.ExistingFile(input, $"{prefix}-{index:D2}.nv12");
                        string expected = observation.GetProperty(hashProperty).GetString()!;
                        byte[] bytes = TestDiagnosticArtifacts.ReadVerified(path, expected);
                        Assert.Equal(Width * Height * 3 / 2, bytes.Length);
                        inputFiles.Add((path, expected));
                        return new(bytes, Width, Height, Width, index * (1000.0 / Fps))
                        { Format = VideoEncoderInputFormat.Nv12 };
                    }
                }

            CodecTransformObservation? baseline = null;
            for (int repetition = 1; repetition <= 2; repetition++)
            {
                bool[] armOrder = repetition == 1 ? [false, true] : [true, false];
                string[] inputOrder = repetition == 1 ? ["direct", "rgb-roundtrip"] : ["rgb-roundtrip", "direct"];
                foreach (bool candidate in armOrder)
                    foreach (string inputName in inputOrder)
                    {
                        string name = $"{(candidate ? "effort100" : "default")}-{inputName}-{repetition}";
                        var options = new VideoEncoderOptions(DiagnosticOutputPaths.Child(evidence, name + ".mp4"),
                            Width, Height, Fps, VideoEncoderOptions.DeriveBitrate(Width, Height, Fps))
                        { InputFormat = VideoEncoderInputFormat.Nv12, AllowHardwareEncoder = true };
                        Assert.Null(options.ConstantQuality);
                        Assert.Null(options.QualityVsSpeed);
                        RunResult run = Encode(name, options, inputName == "direct" ? direct : roundtrip,
                            references, timeline, diagnosticQualityVsSpeed: candidate ? 100u : null, captureCodecParameters: true, captureCodecCost: true);
                        runs.Add(run);
                        TestDiagnosticArtifacts.WriteJson(evidence, name + ".json", run);
                        Assert.Empty(run.Failures);
                        Assert.NotNull(run.Quality);
                        Assert.True(run.EncodeDisposeWallMs is > 0);
                        Assert.True(run.EncodeDisposeProcessCpuMs is >= 0);
                        CodecTransformObservation before = RequireHardwareCodec(run.CodecBefore);
                        CodecTransformObservation after = RequireHardwareCodec(run.CodecAfter);
                        uint actual = RequireUint(before.Parameters!["QualityVsSpeed"]);
                        Assert.Equal(actual, RequireUint(after.Parameters!["QualityVsSpeed"]));
                        if (baseline is null)
                        {
                            Assert.False(candidate);
                            Assert.InRange(actual, 0u, 100u);
                            Assert.NotEqual(100u, actual); // No variable changed if baseline current is already 100.
                            baseline = before;
                        }
                        Assert.Equal(candidate ? 100u : RequireUint(baseline.Parameters!["QualityVsSpeed"]), actual);
                        RequireSameOtherSettings(baseline, before);
                        RequireSameOtherSettings(baseline, after);
                    }
            }
            Assert.Equal(8, runs.Count);
            foreach (bool candidate in new[] { false, true })
                for (int repetition = 1; repetition <= 2; repetition++)
                {
                    string prefix = candidate ? "effort100" : "default";
                    RunResult left = Assert.Single(runs, run => run.Name == $"{prefix}-direct-{repetition}");
                    RunResult right = Assert.Single(runs, run => run.Name == $"{prefix}-rgb-roundtrip-{repetition}");
                    comparisons.Add(new { candidate, repetition, directPsnr = left.Quality!.Psnr,
                        rgbPsnr = right.Quality!.Psnr, directSse = left.Quality.RgbSquaredErrors.Sum(),
                        rgbSse = right.Quality.RgbSquaredErrors.Sum(),
                        originalRelativeGateObserved = left.Quality.RgbSquaredErrors.Sum() <= right.Quality.RgbSquaredErrors.Sum() });
                }
            foreach ((string path, string hash) in inputFiles)
                Assert.Equal(hash, TestDiagnosticArtifacts.HashFile(path));
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(retainedSource));
            completed = true;
        }
        catch (Exception error) { failure = Failure(error); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", new { collectionCompleted = completed,
                failure, environment = EnvironmentEvidence(), requestedRuns = 8, actualRuns = runs.Count,
                soleRequestedDifference = "ICodecAPI QualityVsSpeed attribute: absent vs 100; ConstantQuality remains null",
                inputFiles = inputFiles.Select(item => new { file = Path.GetFileName(item.Path), sha256 = item.Hash }).ToArray(),
                decodedSource = retainedSource is null ? null : new { file = Path.GetFileName(retainedSource), sha256 = SourceHash,
                    scope = "Verified copy of the input source.mp4; Media Foundation decodes only this copy." },
                runs, relativePsnrComparisons = comparisons,
                qualityVerdict = "Collection and parameter isolation only. Read every originalRelativeGateObserved result; the original Mp4Nv12ExportTests gate is unchanged." });
        }
    });

    [EncoderTuningDiagnosticFact]
    public void SingleFrame_ImmediateFinalizeTwelveRunsWithoutPreFinalizeQueries() => StaTestHost.Run(() =>
    {
        string evidence = Evidence("single-frame-immediate");
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
                        string name = $"immediate-{format}-hardware-{hardware.ToString().ToLowerInvariant()}-{repetition}";
                        var options = new VideoEncoderOptions(DiagnosticOutputPaths.Child(evidence, name + ".mp4"),
                            width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps))
                        { InputFormat = format, AllowHardwareEncoder = hardware };
                        var frame = new EncoderFrame(format == VideoEncoderInputFormat.Nv12 ? nv12 : bgra,
                            width, height, format == VideoEncoderInputFormat.Nv12 ? width : width * 4, 0) { Format = format };
                        RunResult run = Encode(name, options, [frame], null, [0], captureDiagnostics: false);
                        runs.Add(run);
                        TestDiagnosticArtifacts.WriteJson(evidence, name + ".json", run);
                        Assert.Empty(run.CodecBefore);
                        Assert.Empty(run.CodecAfter);
                        Assert.Null(run.EncodeDisposeWallMs);
                        Assert.Null(run.EncodeDisposeProcessCpuMs);
                        if (run.Failures.Count == 0) Assert.Empty(run.Snapshots);
                    }
            Assert.Equal(12, runs.Count);
            Assert.All(runs, run => Assert.Empty(run.Failures));
            completed = true;
        }
        catch (Exception error) { failure = Failure(error); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", new { collectionCompleted = completed,
                failure, environment = EnvironmentEvidence(), requestedRuns = 12, actualRuns = runs.Count, runs,
                queryPolicy = "No query between construction, WriteFrame and Complete. Failure-only queries occur after the error. No retry, delay or extra frame.",
                sourceFixture = "Original 160x90 10fps solid BGRA one-frame service fixture; companion NV12 and hardware-disallowed controls" });
        }
    });

    private static CodecTransformObservation RequireHardwareCodec(IReadOnlyList<CodecTransformObservation> observations)
    {
        CodecTransformObservation encoder = Assert.Single(observations, item => item.Identity is { } identity
            && ((identity.TryGetValue("classId", out object? value) && value is Guid id && id == ExpectedHardwareEncoder)
                || (identity.TryGetValue("persistClassId", out object? persisted) && persisted is Guid persistedId
                    && persistedId == ExpectedHardwareEncoder)));
        Assert.Null(encoder.Error);
        Assert.NotNull(encoder.Parameters);
        Assert.Equal(true, encoder.Identity!["hardwareUrlPresent"]);
        Assert.Equal(ExpectedHardwareUrlHash, Assert.IsType<string>(encoder.Identity["hardwareUrlSha256"]));
        _ = RequireUint(encoder.Parameters["QualityVsSpeed"]);
        _ = RequireUint(encoder.Parameters["RateControlMode"]);
        _ = RequireUint(encoder.Parameters["MeanBitRate"]);
        return encoder;
    }

    private static uint RequireUint(CodecParameterObservation observation)
    {
        Assert.Equal(0, observation.SupportedHResult);
        Assert.Equal(0, observation.Current.HResult);
        Assert.Equal(0, observation.Current.ClearHResult);
        Assert.Equal((ushort)19, observation.Current.VariantType);
        return Assert.IsType<uint>(observation.Current.Value);
    }

    private static void RequireSameOtherSettings(CodecTransformObservation baseline, CodecTransformObservation actual)
    {
        Assert.Equal(baseline.Category, actual.Category);
        Assert.Equal(JsonSerializer.Serialize(baseline.Identity), JsonSerializer.Serialize(actual.Identity));
        foreach ((string name, CodecParameterObservation observation) in baseline.Parameters!)
        {
            CodecParameterObservation value = actual.Parameters![name];
            if (name == "QualityVsSpeed")
            {
                Assert.Equal(observation.SupportedHResult, value.SupportedHResult);
                Assert.Equal(observation.ModifiableHResult, value.ModifiableHResult);
                Assert.Equal(JsonSerializer.Serialize(observation.Default), JsonSerializer.Serialize(value.Default));
            }
            else Assert.Equal(JsonSerializer.Serialize(observation), JsonSerializer.Serialize(value));
        }
    }
}

internal sealed class EncoderTuningDiagnosticFactAttribute : FactAttribute
{
    public EncoderTuningDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_TUNING_DIAGNOSTICS") != "1")
            Skip = "Set MYCAPTURE_ENCODER_TUNING_DIAGNOSTICS=1 and explicit retained input/output paths for the bounded encoder experiment.";
    }
}
