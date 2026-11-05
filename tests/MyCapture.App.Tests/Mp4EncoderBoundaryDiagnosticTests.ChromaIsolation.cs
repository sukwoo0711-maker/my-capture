using System.IO;
using System.Text.Json;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Serialization;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class Mp4EncoderBoundaryDiagnosticTests
{
    private const string UvReferenceReportHash = "1ED6A9B05BFDAB693E0FB3731D68229985490A850D9985A442024758CACBE8C6";

    [EncoderTuningDiagnosticFact]
    public void UvOnly_RecordFourHardwareReplays() => RecordPlaneIsolation(yOnly: false);

    [EncoderYIsolationDiagnosticFact]
    public void YOnly_RecordFourHardwareReplays() => RecordPlaneIsolation(yOnly: true);

    [EncoderYRoundtripDiagnosticFact]
    public void YOnlyWithFullRoundtrip_RecordFiveHardwareReplays() =>
        RecordPlaneIsolation(yOnly: true, fullRoundtripControl: true);

    private void RecordPlaneIsolation(bool yOnly, bool fullRoundtripControl = false) => StaTestHost.Run(() =>
    {
        string plane = yOnly ? "y-only" : "uv-only";
        string hybridPrefix = fullRoundtripControl ? "C" : "B";
        (string Name, bool Hybrid)[] schedule = fullRoundtripControl
            ? [("A1-direct", false), ("C1-y-only", true), ("D1-rgb-roundtrip", true), ("C2-y-only", true), ("A2-direct", false)]
            : [("A1-direct", false), ("B1-" + plane, true), ("B2-" + plane, true), ("A2-direct", false)];
        string evidence = Evidence(fullRoundtripControl ? "y-with-full-roundtrip-fixed-720p" : plane + "-fixed-720p");
        var inputFiles = new List<(string Path, string Hash)>();
        var inputs = new List<object>();
        var runs = new List<RunResult>();
        var outputPlanes = new List<object>();
        var comparisons = new List<object>();
        var sameSessionComparisons = new List<object>();
        bool completed = false, inputsUnchanged = false;
        string? failure = null;
        try
        {
            if (fullRoundtripControl) Assert.True(yOnly);
            Assert.False(MediaFoundationVideoEncoder.ForceSoftwareForTest);
            string input = Path.GetFullPath(Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_TUNING_INPUT")
                ?? throw new InvalidOperationException("Set MYCAPTURE_ENCODER_TUNING_INPUT to the retained encoder26 fixed-720p directory."));
            string referencePath = TestDiagnosticArtifacts.Confined(Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_UV_REFERENCE")
                ?? throw new InvalidOperationException("Set MYCAPTURE_ENCODER_UV_REFERENCE to the pinned build34 tuning report.json."));
            string reportPath = DiagnosticOutputPaths.Child(input, "report.json");
            string source = DiagnosticOutputPaths.Child(input, "source.mp4");
            Assert.Equal(BoundaryReportHash, TestDiagnosticArtifacts.HashFile(reportPath));
            Assert.Equal(SourceHash, TestDiagnosticArtifacts.HashFile(source));
            Assert.Equal(UvReferenceReportHash, TestDiagnosticArtifacts.HashFile(referencePath));
            inputFiles.Add((reportPath, BoundaryReportHash));
            inputFiles.Add((source, SourceHash));
            inputFiles.Add((referencePath, UvReferenceReportHash));
            using JsonDocument boundary = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            using JsonDocument referenceDocument = JsonDocument.Parse(File.ReadAllBytes(referencePath));
            JsonElement reference = referenceDocument.RootElement;
            Assert.True(reference.GetProperty("collectionCompleted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, reference.GetProperty("failure").ValueKind);
            JsonElement referenceDirect = Assert.Single(reference.GetProperty("runs").EnumerateArray(),
                row => row.GetProperty("name").GetString() == "default-direct-1");
            JsonElement referenceRgb = Assert.Single(reference.GetProperty("runs").EnumerateArray(),
                row => row.GetProperty("name").GetString() == "default-rgb-roundtrip-1");
            Assert.Empty(referenceDirect.GetProperty("failures").EnumerateArray());
            Assert.Empty(referenceRgb.GetProperty("failures").EnumerateArray());
            JsonElement[] observations = boundary.RootElement.GetProperty("observations").EnumerateArray()
                .Where(row => row.GetProperty("kind").GetString() == "input-comparison")
                .OrderBy(row => row.GetProperty("index").GetInt32()).ToArray();
            Assert.Equal(Frames, observations.Length);
            double[] timeline = referenceDirect.GetProperty("quality").GetProperty("presentationMs")
                .EnumerateArray().Select(value => value.GetDouble()).ToArray();
            Assert.Equal(Frames, timeline.Length);
            Assert.Equal(timeline, boundary.RootElement.GetProperty("runs")[0].GetProperty("quality")
                .GetProperty("presentationMs").EnumerateArray().Select(value => value.GetDouble()).ToArray());

            var direct = new EncoderFrame[Frames];
            var uvOnly = new EncoderFrame[Frames];
            EncoderFrame[] roundtrip = fullRoundtripControl ? new EncoderFrame[Frames] : [];
            var references = new byte[Frames][];
            int luma = checked(Width * Height);
            using (var reader = new MediaFoundationVideoFrameReader(source))
                for (int index = 0; index < Frames; index++)
                {
                    JsonElement observation = observations[index];
                    Assert.Equal(index, observation.GetProperty("index").GetInt32());
                    double timestamp = index * (1000.0 / Fps);
                    Assert.Equal(timestamp, observation.GetProperty("outputTimestampMs").GetDouble());
                    byte[] original = ReadPayload("direct", "directSha256");
                    byte[] donor = ReadPayload("rgb-roundtrip", "roundtripSha256");
                    byte[] hybrid = (byte[])original.Clone();
                    if (yOnly)
                    {
                        Array.Copy(donor, 0, hybrid, 0, luma);
                        Assert.Equal(donor[..luma], hybrid[..luma]);
                        Assert.Equal(original[luma..], hybrid[luma..]);
                    }
                    else
                    {
                        Array.Copy(donor, luma, hybrid, luma, luma / 2);
                        Assert.Equal(original[..luma], hybrid[..luma]);
                        Assert.Equal(donor[luma..], hybrid[luma..]);
                    }
                    direct[index] = new(original, Width, Height, Width, timestamp)
                        { Format = VideoEncoderInputFormat.Nv12 };
                    uvOnly[index] = direct[index] with { Pixels = hybrid };
                    if (fullRoundtripControl) roundtrip[index] = direct[index] with { Pixels = donor };
                    WriteBytes(evidence, $"{plane}-{index:D2}.nv12", hybrid);
                    if (yOnly)
                        inputs.Add(new { index, timestampMs = timestamp, width = Width, height = Height,
                            stride = Width, format = "NV12", directSha256 = Hash(original), donorSha256 = Hash(donor),
                            hybridSha256 = Hash(hybrid), yDonorExact = true, uvDirectExact = true,
                            directVsHybrid = Planes(original, hybrid, Width, Height) });
                    else
                        inputs.Add(new { index, timestampMs = timestamp, width = Width, height = Height,
                            stride = Width, format = "NV12", directSha256 = Hash(original), donorSha256 = Hash(donor),
                            hybridSha256 = Hash(hybrid), yExact = true, uvDonorExact = true,
                            directVsHybrid = Planes(original, hybrid, Width, Height) });
                    DecodedVideoFrame decoded = reader.ReadFrameAt(300 + timestamp);
                    Assert.Equal((Width, Height, Width * 4), (decoded.Width, decoded.Height, decoded.Stride));
                    Assert.Equal(DecodedVideoPixelFormat.Bgr32, decoded.Format);
                    Assert.Equal(observation.GetProperty("rgbReferenceSha256").GetString(), Hash(decoded.Pixels));
                    references[index] = decoded.Pixels;

                    byte[] ReadPayload(string prefix, string hashProperty)
                    {
                        string path = DiagnosticOutputPaths.Child(input, $"{prefix}-{index:D2}.nv12");
                        string expected = observation.GetProperty(hashProperty).GetString()!;
                        byte[] bytes = File.ReadAllBytes(path);
                        Assert.Equal(luma * 3 / 2, bytes.Length);
                        Assert.Equal(expected, Hash(bytes));
                        inputFiles.Add((path, expected));
                        return bytes;
                    }
                }
            TestDiagnosticArtifacts.WriteJson(evidence, "inputs.json", inputs);

            // Existing four-run mode is unchanged. The five-run mode adds one pinned full-roundtrip control.
            foreach ((string name, bool hybrid) in schedule)
            {
                bool isFullRoundtrip = fullRoundtripControl && name == "D1-rgb-roundtrip";
                var options = new VideoEncoderOptions(DiagnosticOutputPaths.Child(evidence, name + ".mp4"),
                    Width, Height, Fps, VideoEncoderOptions.DeriveBitrate(Width, Height, Fps))
                    { InputFormat = VideoEncoderInputFormat.Nv12, AllowHardwareEncoder = true };
                Assert.Null(options.ConstantQuality);
                Assert.Null(options.QualityVsSpeed);
                RunResult run = Encode(name, options, isFullRoundtrip ? roundtrip : hybrid ? uvOnly : direct, references, timeline,
                    captureCodecParameters: true, captureCodecCost: true);
                runs.Add(run);
                TestDiagnosticArtifacts.WriteJson(evidence, name + ".json", run);
                Assert.Empty(run.Failures);
                Assert.Equal(Frames, run.SubmittedFrames);
                QualityResult quality = Assert.IsType<QualityResult>(run.Quality);
                CodecTransformObservation before = RequireHardwareCodec(run.CodecBefore);
                CodecTransformObservation after = RequireHardwareCodec(run.CodecAfter);
                Assert.Equal(50u, RequireUint(before.Parameters!["QualityVsSpeed"]));
                Assert.Equal(50u, RequireUint(after.Parameters!["QualityVsSpeed"]));
                RequireUvJsonEqual(referenceDirect.GetProperty("codecBefore"), run.CodecBefore, "pre-input transform identity/settings");
                RequireUvJsonEqual(referenceDirect.GetProperty("codecAfter"), run.CodecAfter, "pre-finalize transform identity/settings");
                if (isFullRoundtrip)
                {
                    Assert.Equal(referenceRgb.GetProperty("inputHashes").EnumerateArray()
                        .Select(value => value.GetString()!).ToArray(), run.InputHashes);
                    Assert.Equal(referenceRgb.GetProperty("quality").GetProperty("presentationMs").EnumerateArray()
                        .Select(value => value.GetDouble()).ToArray(), quality.PresentationMs);
                    RequireUvJsonEqual(referenceRgb.GetProperty("codecBefore"), run.CodecBefore, "full-roundtrip pre-input transform");
                    RequireUvJsonEqual(referenceRgb.GetProperty("codecAfter"), run.CodecAfter, "full-roundtrip pre-finalize transform");
                }
                // Output names differ by design; every other public option must equal build34's default.
                string referenceOutput = referenceDirect.GetProperty("options").GetProperty("outputPath").GetString()!;
                RequireUvJsonEqual(referenceDirect.GetProperty("options"), run.Options with { OutputPath = referenceOutput }, "encoder options");
                if (!hybrid)
                {
                    Assert.Equal(referenceDirect.GetProperty("inputHashes").EnumerateArray()
                        .Select(value => value.GetString()!).ToArray(), run.InputHashes);
                    Assert.Equal(referenceDirect.GetProperty("decodedHashes").EnumerateArray()
                        .Select(value => value.GetString()!).ToArray(), run.DecodedHashes.ToArray());
                    Assert.Equal(referenceDirect.GetProperty("decodedTimestampsMs").EnumerateArray()
                        .Select(value => value.GetDouble()).ToArray(), run.DecodedTimestampsMs.ToArray());
                    RequireUvJsonEqual(referenceDirect.GetProperty("quality"), quality, "build34 direct output projection");
                }

                var planes = new List<object>();
                using (var outputReader = new MediaFoundationVideoFrameReader(options.OutputPath,
                    new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 }))
                {
                    Assert.Equal(Studio709, outputReader.YuvEncoding);
                    for (int index = 0; index < Frames; index++)
                    {
                        DecodedVideoFrame decoded = outputReader.ReadFrameAt(index * (1000.0 / Fps));
                        Assert.Equal(Studio709, outputReader.YuvEncoding);
                        Assert.Equal(DecodedVideoPixelFormat.Nv12, decoded.Format);
                        Assert.Equal((Width, Height, Width), (decoded.Width, decoded.Height, decoded.Stride));
                        Assert.Equal(luma * 3 / 2, decoded.Pixels.Length);
                        Assert.Equal(run.DecodedTimestampsMs[index], decoded.TimestampMs);
                        WriteBytes(evidence, $"{name}-decoded-{index:D2}.nv12", decoded.Pixels);
                        planes.Add(new { index, decoded.TimestampMs, decodedSha256 = Hash(decoded.Pixels),
                            originalDirectVsOutput = Planes(direct[index].Pixels, decoded.Pixels, Width, Height) });
                    }
                    outputPlanes.Add(new { name, outputReader.IsHardwareAccelerated,
                        outputReader.HardwareFallbackReason, outputReader.YuvEncoding, frames = planes });
                }
                long historicalRgbSse = referenceRgb.GetProperty("quality").GetProperty("rgbSquaredErrors")
                    .EnumerateArray().Sum(value => value.GetInt64());
                comparisons.Add(new { name, hybrid = hybrid && !isFullRoundtrip, rgbSse = quality.RgbSquaredErrors.Sum(), quality.Psnr,
                    historicalFullRoundtripRgbSse = historicalRgbSse,
                    noWorseThanHistoricalFullRoundtripObserved = quality.RgbSquaredErrors.Sum() <= historicalRgbSse });
                TestDiagnosticArtifacts.WriteJson(evidence, name + "-planes.json", planes);
            }
            Assert.Equal(schedule.Length, runs.Count);
            RunResult firstHybrid = Assert.Single(runs, run => run.Name == hybridPrefix + "1-" + plane);
            RunResult secondHybrid = Assert.Single(runs, run => run.Name == hybridPrefix + "2-" + plane);
            Assert.Equal(firstHybrid.InputHashes, secondHybrid.InputHashes);
            Assert.Equal(firstHybrid.DecodedHashes.ToArray(), secondHybrid.DecodedHashes.ToArray());
            Assert.Equal(firstHybrid.DecodedTimestampsMs.ToArray(), secondHybrid.DecodedTimestampsMs.ToArray());
            RequireUvJsonEqual(JsonSerializer.SerializeToElement(firstHybrid.Quality, JsonDefaults.Readable),
                secondHybrid.Quality, "repeated hybrid output projection");
            if (fullRoundtripControl)
            {
                QualityResult full = Assert.IsType<QualityResult>(Assert.Single(runs,
                    run => run.Name == "D1-rgb-roundtrip").Quality);
                foreach (string directName in new[] { "A1-direct", "A2-direct" })
                {
                    QualityResult directQuality = Assert.IsType<QualityResult>(Assert.Single(runs,
                        run => run.Name == directName).Quality);
                    sameSessionComparisons.Add(new { directName, roundtripName = "D1-rgb-roundtrip",
                        directSse = directQuality.RgbSquaredErrors.Sum(), roundtripSse = full.RgbSquaredErrors.Sum(),
                        directPsnr = directQuality.Psnr, roundtripPsnr = full.Psnr,
                        originalRelativeGateObserved = directQuality.RgbSquaredErrors.Sum() <= full.RgbSquaredErrors.Sum() });
                }
            }
            foreach ((string path, string expected) in inputFiles)
                Assert.Equal(expected, TestDiagnosticArtifacts.HashFile(path));
            for (int index = 0; index < Frames; index++)
                Assert.Equal(Hash(uvOnly[index].Pixels), TestDiagnosticArtifacts.HashFile(
                    DiagnosticOutputPaths.Child(evidence, $"{plane}-{index:D2}.nv12")));
            inputsUnchanged = true;
            completed = true;
        }
        catch (Exception error) { failure = Failure(error); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", new
            {
                collectionCompleted = completed, failure, inputsUnchanged, environment = EnvironmentEvidence(),
                requestedRuns = schedule.Length, actualRuns = runs.Count, order = schedule.Select(row => row.Name).ToArray(),
                soleRequestedDifference = fullRoundtripControl
                    ? "A is direct; C replaces only Y with pinned same-index RGB-roundtrip Y and retains direct UV; D submits the whole pinned RGB-roundtrip payload. All frame metadata and encoder options are fixed."
                    : yOnly
                    ? "Same direct UV and frame metadata; B replaces only Y with the pinned same-index RGB-roundtrip Y."
                    : "Same direct Y and frame metadata; B replaces only interleaved UV with the pinned same-index RGB-roundtrip UV.",
                boundaryReportSha256 = BoundaryReportHash, referenceReportSha256 = UvReferenceReportHash,
                inputFiles = inputFiles.Select(row => new { file = Path.GetFileName(row.Path), sha256 = row.Hash }).ToArray(),
                inputs, runs, outputPlanes,
                comparisons = fullRoundtripControl ? (object)new { runs = comparisons, sameSession = sameSessionComparisons } : comparisons,
                qualityVerdict = "Plane-isolation collection only. Original direct >= RGB-roundtrip quality gate remains unchanged; hybrid payload is not a product candidate.",
                identityScope = "Observed actual MFT class/device fingerprint/media types and queried CodecAPI; unavailable private vendor state is not observed.",
                costScope = fullRoundtripControl
                    ? "Existing Encode constructor-through-Dispose timing includes diagnostic getters; RGB/NV12 output decode and evidence writes are outside. Five encodes are not a performance gate; D has one observation."
                    : "Existing Encode constructor-through-Dispose timing includes diagnostic getters; RGB/NV12 output decode and evidence writes are outside. Four encodes are not a performance gate.",
            });
        }
    });

    private static void RequireUvJsonEqual<T>(JsonElement expected, T actual, string what) =>
        Assert.True(JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(actual, JsonDefaults.Readable)),
            "Pinned reference differs: " + what);
}

internal sealed class EncoderYIsolationDiagnosticFactAttribute : FactAttribute
{
    public EncoderYIsolationDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_TUNING_DIAGNOSTICS") != "1"
            || Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_Y_ISOLATION") != "1")
            Skip = "Set MYCAPTURE_ENCODER_TUNING_DIAGNOSTICS=1 and MYCAPTURE_ENCODER_Y_ISOLATION=1 for the bounded Y-only experiment.";
    }
}

internal sealed class EncoderYRoundtripDiagnosticFactAttribute : FactAttribute
{
    public EncoderYRoundtripDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_TUNING_DIAGNOSTICS") != "1"
            || Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_Y_ISOLATION") != "1"
            || Environment.GetEnvironmentVariable("MYCAPTURE_ENCODER_Y_ROUNDTRIP_CONTROL") != "1")
            Skip = "Set the tuning, Y isolation and MYCAPTURE_ENCODER_Y_ROUNDTRIP_CONTROL=1 opt-ins for the bounded five-run experiment.";
    }
}
