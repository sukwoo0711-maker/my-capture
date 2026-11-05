using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;
using Evidence = MyCapture.App.Tests.OcrFastScaleExperimentEvidence;

namespace MyCapture.App.Tests;

/// <summary>Opt-in, development-only comparison of two scale resolvers over the actual Fast Windows pipeline.</summary>
public sealed class OcrFastScaleExperimentTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_FAST_SCALE_RUN";
    internal const string ArmVariable = "MYCAPTURE_OCR_FAST_SCALE_ARM";
    internal const string OutputVariable = "MYCAPTURE_OCR_FAST_SCALE_OUTPUT";
    internal const string RepeatsVariable = "MYCAPTURE_OCR_FAST_SCALE_REPEATS";
    internal const string CategoryVariable = "MYCAPTURE_OCR_FAST_SCALE_CATEGORY";
    internal const string BlockVariable = "MYCAPTURE_OCR_FAST_SCALE_BLOCK";

    [OcrFastScaleExperimentFact]
    [Trait("Category", "OcrFastScaleExperiment")]
    public void Measure()
    {
        Configuration config = ReadConfiguration();
        string corpus = Data.ArtifactPath(Required(OcrCommercialCorpusTests.CorpusVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        string outputPath = Data.ArtifactPath(Required(OutputVariable));
        Assert.False(Overlaps(outputPath, corpus) || Overlaps(outputPath, models),
            "Output must not overlap an immutable corpus/model directory.");
        string root = Data.NewOutput(outputPath);
        StaTestHost.Run(() => Run(root, corpus, models, config));
        output.WriteLine("Fast scale experiment completed: " + root + "; releaseGate=NOT_EVALUATED.");
    }

    private static Configuration ReadConfiguration()
    {
        string mode = Required(RunVariable), arm = Required(ArmVariable);
        Assert.Contains(mode, new[] { "smoke", "quality-dev", "perf" });
        Assert.Contains(arm, new[] { "adaptive", "requested" });
        int repeats = mode == "perf" ? 10 : 1;
        string? supplied = Environment.GetEnvironmentVariable(RepeatsVariable);
        if (supplied is not null)
            Assert.True(int.TryParse(supplied, out repeats), RepeatsVariable + " must be an integer.");
        Assert.InRange(repeats, 1, mode == "perf" ? 20 : mode == "quality-dev" ? 5 : 1);
        string? category = Environment.GetEnvironmentVariable(CategoryVariable);
        string? block = Environment.GetEnvironmentVariable(BlockVariable);
        if (mode == "perf")
        {
            Assert.True(category is not null && Data.Categories.Contains(category, StringComparer.Ordinal));
            Assert.True(block is "A1" or "B1" or "B2" or "A2");
            Assert.Equal(block!.StartsWith('A') ? "adaptive" : "requested", arm);
        }
        else
        {
            Assert.True(category is null && block is null, "Category/block apply only to perf mode.");
        }
        return new(mode, arm, category, block, repeats);
    }

    private static void Run(string root, string corpus, string models, Configuration config)
    {
        var issues = new List<string>();
        var started = DateTimeOffset.UtcNow;
        Evidence.FileReceipt[] sourcesBefore = [], sourcesAfter = [], binariesBefore = [], binariesAfter = [];
        Evidence.FileReceipt[] modelsBefore = [], modelsAfter = [];
        Data.Sample[] selected = [];
        Input[] inputs = [];
        Call[] calls = [];
        int completed = 0, warmups = 0, neuralCalls = 0, neuralAvailabilityChecks = 0;
        bool corpusUnchanged = false, modelsUnchanged = false, binariesUnchanged = false, sourcesUnchanged = false;
        bool windowsAvailable = false;
        string[] windowsLanguages = [];
        int actualMaxDimension = 0;
        double? blockCpuStartMs = null, blockCpuEndMs = null, blockWallMs = null;
        long? blockAllocatedBytes = null;
        Exception? failure = null;
        NeuralOcrEngine? neural = null;
        Evidence.PreparedInputObserver? observer = null;
        using var http = new HttpClient(new NoDownloads());
        using var process = Process.GetCurrentProcess();
        try
        {
            sourcesBefore = Evidence.Sources();
            binariesBefore = Evidence.Binaries();
            modelsBefore = Evidence.Models(models);
            Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
            Data.Manifest manifest = Data.Read(corpus);
            Data.Sample[] development = manifest.Samples.Where(value => value.Split == "development").ToArray();
            Assert.Equal(180, development.Length);
            Assert.Equal(120, manifest.Samples.Count(value => value.Split == "held-out"));
            foreach (string category in Data.Categories) Assert.Equal(36, development.Count(value => value.Category == category));
            selected = Select(development, config);
            Assert.All(selected, sample => Assert.Equal("development", sample.Split));
            inputs = selected.Select(sample => LoadInput(corpus, sample)).ToArray();

            var actual = new WindowsOcrRecognizer(NullLogger.Instance);
            observer = new Evidence.PreparedInputObserver(actual);
            windowsAvailable = actual.IsAvailable;
            windowsLanguages = actual.SupportedLanguages.ToArray();
            actualMaxDimension = actual.MaxImageDimension;
            Assert.True(windowsAvailable, "Actual Windows OCR must be available; fallback maximum is not an observed engine limit.");
            Assert.True(actualMaxDimension > 0);
            var windows = new WindowsOcrService(observer,
                _ => throw new InvalidOperationException("Frozen bitmap experiment only."),
                _ => throw new InvalidOperationException("Frozen bitmap experiment only."),
                NullLogger.Instance, useAdaptiveScale: config.Arm == "adaptive");

            // Ordinary Fast short-circuits before Neural availability/recovery. A fresh empty
            // owned profile avoids copying 100 MB of unused models for every ABBA process.
            // The supplied real model directory is independently pinned before and after.
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
            neural.ModelVerificationTask.GetAwaiter().GetResult();
            NeuralOcrEngine engine = neural;
            var service = new CaptureOcrService(windows,
                (request, token) => { neuralCalls++; return engine.RecognizeAsync(request, token); },
                () => { neuralAvailabilityChecks++; return engine.CanRun; }, () => engine.IsReady,
                NullLogger<CaptureOcrService>.Instance,
                readNeuralRows: (request, rows, token) => { neuralCalls++; return engine.ReadRowsAsync(request, rows, token); });

            int count = checked(inputs.Length * config.Repeats);
            calls = new Call[count];
            var buffers = Enumerable.Range(0, count).Select(_ => new Evidence.PreparedPass[2]).ToArray();
            Data.Write(root, "preflight.json", new
            {
                config, corpus, corpusSha256 = Evidence.CorpusSha256, actualMaxDimension, windowsAvailable, windowsLanguages,
                selected = selected.Select(sample => new { sample.Id, sample.SceneId, sample.Category, sample.Split,
                    sample.Width, sample.Height, sample.PngSha256, sample.PixelSha256, sample.TruthSha256 }).ToArray(),
                expectedCalls = count, expectedWarmups = config.Mode == "perf" ? inputs.Length : 0,
                modelsBefore, sourcesBefore, binariesBefore, processId = Environment.ProcessId,
                logicalProcessors = Environment.ProcessorCount, runtime = Environment.Version.ToString(),
                os = Environment.OSVersion.VersionString, is64BitProcess = Environment.Is64BitProcess,
                neuralProfile = "Fresh empty owned profile; no catalog copied or loaded. Real supplied models are hash-pinned only. Fast must not query or call Neural.",
                qualityCoverage = "Installed Windows language coverage is reported per input; missing packs remain gaps.",
                original19 = new { status = "NOT_RUN", reason = "Historical inputs/baselines are absent; this is a separate synthetic development experiment." },
            });

            if (config.Mode == "perf")
            {
                foreach (Input input in inputs)
                {
                    observer.Begin(new Evidence.PreparedPass[2], hashPixels: false);
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    OcrResult warm = service.RecognizeAsync(input.Request, cancellation.Token).GetAwaiter().GetResult();
                    RequireSuccessfulAttempt(warm, observer.Count);
                    warmups++;
                }
            }
            Assert.Equal(0, neuralCalls);
            Assert.Equal(0, neuralAvailabilityChecks);
            Assert.False(engine.IsReady);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            blockCpuStartMs = process.TotalProcessorTime.TotalMilliseconds;
            long blockStarted = Stopwatch.GetTimestamp();
            try
            {
                for (int index = 0; index < count; index++)
                {
                    Input input = inputs[index % inputs.Length];
                    observer.Begin(buffers[index], hashPixels: config.Mode != "perf");
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    OcrResult? result = null;
                    Exception? callError = null;
                    double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                    long callStarted = Stopwatch.GetTimestamp();
                    double elapsed = 0, cpuAfter = cpuBefore;
                    try { result = service.RecognizeAsync(input.Request, cancellation.Token).GetAwaiter().GetResult(); }
                    catch (Exception error) { callError = error; }
                    finally
                    {
                        elapsed = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds;
                        cpuAfter = process.TotalProcessorTime.TotalMilliseconds;
                    }
                    calls[index] = new(index % inputs.Length, index / inputs.Length, callStarted, elapsed,
                        cpuBefore, cpuAfter, result, buffers[index], observer.Count, callError);
                    completed++;
                    if (callError is not null) throw new InvalidOperationException("Product call threw; raw exception is retained.", callError);
                    RequireSuccessfulAttempt(result!, observer.Count);
                    if (neuralCalls != 0 || neuralAvailabilityChecks != 0 || engine.IsReady)
                        throw new InvalidOperationException("Ordinary Fast unexpectedly queried or invoked Neural.");
                }
            }
            finally
            {
                blockWallMs = Stopwatch.GetElapsedTime(blockStarted).TotalMilliseconds;
                blockCpuEndMs = process.TotalProcessorTime.TotalMilliseconds;
                blockAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            }
        }
        catch (Exception error) { failure = error; issues.Add(error.ToString()); }
        finally
        {
            try { neural?.Dispose(); }
            catch (Exception error) { failure ??= error; issues.Add("Dispose: " + error); }
            try
            {
                modelsAfter = Evidence.Models(models); Evidence.Equal(modelsBefore, modelsAfter); modelsUnchanged = true;
                binariesAfter = Evidence.Binaries(); Evidence.Equal(binariesBefore, binariesAfter); binariesUnchanged = true;
                sourcesAfter = Evidence.Sources(); Evidence.Equal(sourcesBefore, sourcesAfter); sourcesUnchanged = true;
                Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
                _ = Data.Read(corpus);
                foreach (Input input in inputs) Assert.Equal(input.Sample.PixelSha256, Data.PixelHash(input.Bitmap));
                corpusUnchanged = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }

            var observations = new List<object>(completed);
            try
            {
                for (int index = 0; index < completed; index++)
                {
                    Call call = calls[index]; Input input = inputs[call.Input]; Data.Sample sample = input.Sample;
                    Evidence.PreparedPass[] passes = call.Passes.Take(call.PassCount).ToArray();
                    double scale = config.Arm == "adaptive"
                        ? OcrPlanner.ResolveAdaptiveScale(sample.Width, sample.Height, input.Request.UpscaleFactor, actualMaxDimension)
                        : OcrPlanner.ResolveScale(sample.Width, sample.Height, input.Request.UpscaleFactor, actualMaxDimension);
                    foreach (Evidence.PreparedPass pass in passes)
                    {
                        if (pass.ExceptionType is not null || pass.Raw is null)
                            issues.Add(sample.Id + ": recognizer failure/unavailable: " + pass.ExceptionType);
                        if (pass.Width != (int)Math.Round(sample.Width * scale) || pass.Height != (int)Math.Round(sample.Height * scale))
                            issues.Add(sample.Id + ": actual prepared dimensions differ from the selected resolver.");
                    }
                    object observation = new
                    {
                        sequence = index, call.Repeat, sample.Id, sample.SceneId, sample.Category, sample.Split,
                        sample.PngSha256, sample.PixelSha256, sample.TruthSha256,
                        sourceWidth = sample.Width, sourceHeight = sample.Height, calculatedScale = scale, actualMaxDimension,
                        requestedUpscale = input.Request.UpscaleFactor,
                        requestedLanguages = input.Request.PreferredLanguages,
                        windowsLanguageCoverage = sample.Languages.All(language => windowsLanguages.Any(installed => SameLanguage(language, installed))),
                        call.WallMs, call.CpuStartMs, call.CpuEndMs, productCallCpuMs = call.CpuEndMs - call.CpuStartMs,
                        prepared = passes.Select(pass => new { pass.Width, pass.Height, pass.PixelFormat, pass.BitsPerPixel,
                            pixelCount = (long)pass.Width * pass.Height,
                            minimumPackedBytes = ((long)pass.Width * pass.BitsPerPixel + 7) / 8 * pass.Height,
                            pass.Frozen, pass.RequestedLanguage, pass.MaxDimension, pass.PixelSha256,
                            requestToRecognizerMs = (pass.EnteredTimestamp - call.StartedTimestamp) * 1000.0 / Stopwatch.Frequency,
                            pass.NativeWallMs, pass.ExceptionType, pass.HResult, pass.CancellationRequested, pass.Raw }).ToArray(),
                        metric = call.Result is null ? null : Data.Measure(sample, call.Result),
                        result = call.Result is null ? null : Evidence.Result(call.Result),
                        exceptionType = call.Error?.GetType().FullName, hResult = call.Error?.HResult,
                    };
                    observations.Add(observation);
                    Data.Write(root, $"{index:0000}-{sample.Id}.json", observation);
                }
            }
            catch (Exception error) { failure ??= error; issues.Add("Evidence writing: " + error); }
            bool measured = failure is null && issues.Count == 0 && completed == selected.Length * config.Repeats &&
                completed > 0 && observations.Count == completed && corpusUnchanged && modelsUnchanged &&
                binariesUnchanged && sourcesUnchanged && neuralCalls == 0 && neuralAvailabilityChecks == 0;
            Data.Write(root, "report.json", new
            {
                executionStatus = measured ? "MEASURED" : "INCOMPLETE_OR_ERROR", releaseGate = "NOT_EVALUATED",
                config, startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, completed, warmups,
                selectedInputs = selected.Length, expectedCalls = selected.Length * config.Repeats,
                actualMaxDimension, windowsAvailable, windowsLanguages,
                neuralCalls, neuralAvailabilityChecks, recognizerCallsIncludingWarmups = observer?.TotalCalls,
                corpusUnchanged, modelsUnchanged, binariesUnchanged, sourcesUnchanged,
                modelsBefore, modelsAfter, binariesBefore, binariesAfter, sourcesBefore, sourcesAfter,
                blockCpuStartMs, blockCpuEndMs,
                blockCpuMs = blockCpuEndMs - blockCpuStartMs, blockWallMs, blockAllocatedBytes,
                productCallCpuSumMs = calls.Take(completed).Sum(call => call.CpuEndMs - call.CpuStartMs),
                productCallWallSumMs = calls.Take(completed).Sum(call => call.WallMs),
                wallP50Ms = Percentile(calls.Take(completed).Select(call => call.WallMs), 0.5),
                wallP95Ms = Percentile(calls.Take(completed).Select(call => call.WallMs), 0.95),
                issues,
                definitions = new[]
                {
                    "Only development scene00-05 inputs may be recognized. Held-out inputs are integrity-checked but never submitted to OCR.",
                    "Source corpus pixels/truth, models and source/binaries are pinned before/after. No image generation, model download, settings/clipboard/user-file access or cleanup.",
                    "Actual Fast OcrRequestFactory->CaptureOcrService->WindowsOcrService->WindowsOcrRecognizer. Empty owned Neural profile is never queried or invoked; supplied real models are read only for integrity.",
                    "Actual MaxDimension is recorded only with available Windows OCR. Prepared dimensions/format are captured from the exact bitmap forwarded to WinRT.",
                    "Perf disables prepared-pixel hashing. Smoke/quality hash prepared pixels inside the diagnostic call and their latency is not the primary performance benchmark.",
                    "CPU uses process user+kernel cumulative values. Per-call endpoints surround the actual product call; their sum has multiple quantization boundaries. Block CPU has one bracket but includes cancellation timers, clock/CPU sampling, preallocated observation writes and small validation overhead. Neither includes PNG loading, corpus/model hashing, warmup, metric calculation or JSON serialization.",
                    "Request-to-recognizer includes product decode/dispatch, language choice, rotation/resize; it is not pure resize. NativeWall includes bitmap bridge+WinRT recognition+raw conversion.",
                    "Timing and allocations are descriptive. No automatic CPU/p95 or quality adoption is claimed; root compares ABBA with prespecified quantization and non-regression criteria.",
                    "No installed language pack is added. Missing requested English remains incomplete coverage even if profile-language fallback returns text.",
                    "Each token is cooperative 3-minute cancellation; owning runner must enforce process-tree timeout and stop subsequent blocks on failure.",
                },
            });
        }
        Assert.True(failure is null && issues.Count == 0 && completed > 0,
            "Experiment failed/incomplete; preserved output: " + root + "\n" + string.Join('\n', issues));
    }

    private static Data.Sample[] Select(Data.Sample[] development, Configuration config)
    {
        if (config.Mode == "quality-dev")
            return Enumerable.Range(0, 36).SelectMany(index => Data.Categories.Select(category =>
                development.Where(sample => sample.Category == category).ElementAt(index))).ToArray();
        if (config.Mode == "smoke")
            return Data.Categories.Select(category => development.First(sample => sample.Category == category)).ToArray();
        Data.Sample[] selected = development.Where(sample => sample.Category == config.Category && sample.Scale == 1 &&
            sample.Theme == "light" && Enumerable.Range(0, 5).Any(index => sample.SceneId == config.Category + "-" + index.ToString("00")))
            .OrderBy(sample => sample.SceneId, StringComparer.Ordinal).ToArray();
        Assert.Equal(5, selected.Length);
        return selected;
    }

    private static Input LoadInput(string corpus, Data.Sample sample)
    {
        BitmapSource bitmap = ImageCodec.TryLoad(DiagnosticOutputPaths.Child(corpus, sample.Png))
            ?? throw new InvalidDataException("Cannot decode selected input: " + sample.Id);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
        var settings = new OcrSettings { Quality = OcrQuality.Fast, PreferredLanguages = sample.Languages.ToList() };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
        Assert.Equal(1.0, request.UpscaleFactor);
        Assert.False(request.SearchRotatedOrientations || request.EnhanceContrast || request.UseNeuralModel ||
            request.LocalCorrection || request.UseSuperResolution || request.RecoverLatinIntrusions);
        return new(sample, bitmap, request);
    }

    private static void RequireSuccessfulAttempt(OcrResult result, int passes)
    {
        Assert.True(result.Status is OcrStatus.Success or OcrStatus.NoText, "Unexpected OCR status: " + result.Status);
        Assert.InRange(passes, 1, 2);
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing " + name);
    private static bool SameLanguage(string left, string right) =>
        string.Equals(left.Split('-')[0], right.Split('-')[0], StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
            left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static double? Percentile(IEnumerable<double> source, double p)
    {
        double[] values = source.Order().ToArray();
        return values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * p) - 1];
    }
    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fast scale experiment never downloads models.");
    }
    private sealed record Configuration(string Mode, string Arm, string? Category, string? Block, int Repeats);
    private sealed record Input(Data.Sample Sample, BitmapSource Bitmap, OcrRequest Request);
    private readonly record struct Call(int Input, int Repeat, long StartedTimestamp, double WallMs,
        double CpuStartMs, double CpuEndMs, OcrResult? Result, Evidence.PreparedPass[] Passes, int PassCount, Exception? Error);
}

public sealed class OcrFastScaleExperimentFactAttribute : FactAttribute
{
    public OcrFastScaleExperimentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OcrFastScaleExperimentTests.RunVariable) is null)
            Skip = "Opt-in Fast scale experiment. Set " + OcrFastScaleExperimentTests.RunVariable + " and select this exact test.";
        // A supplied invalid mode must fail rather than becoming an all-skipped pass.
    }
}
