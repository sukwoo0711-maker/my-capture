using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Evidence = MyCapture.App.Tests.OcrWholeSpinExperimentEvidence;

namespace MyCapture.App.Tests;

/// <summary>Opt-in whole-image spinning comparison; all normal construction defaults remain unchanged.</summary>
public sealed class OcrWholeSpinExperimentTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_WHOLE_SPIN_RUN";
    private const string Prefix = "MYCAPTURE_OCR_WHOLE_SPIN_";
    private const string BoundedSpinCandidate = "spin-1000-backoff8";
    private const string BackoffFourSpinCandidate = "spin-1000-backoff4";
    private const string StageObservationVersion = "small-stages-v1";
    private const string WorkerObservationVersion = "small-neural-worker-v2";
    private const string RapidObservationVersion = "small-rapid-public-v3";

    [OcrWholeSpinExperimentFact]
    [Trait("Category", "OcrWholeSpinExperiment")]
    public void Measure()
    {
        string mode = Required(RunVariable), arm = Required(Prefix + "ARM");
        Assert.Contains(mode, new[] { "smoke", "perf" });
        string comparisonCandidate = Environment.GetEnvironmentVariable(Prefix + "CANDIDATE") ?? "no-spin";
        Assert.Contains(comparisonCandidate, new[] { "no-spin", BoundedSpinCandidate, BackoffFourSpinCandidate });
        Assert.Contains(arm, new[] { "default", comparisonCandidate });
        string? category = Environment.GetEnvironmentVariable(Prefix + "CATEGORY");
        string? block = Environment.GetEnvironmentVariable(Prefix + "BLOCK");
        if (mode == "perf")
        {
            Assert.True(category is not null && Data.Categories.Contains(category, StringComparer.Ordinal));
            Assert.True(block is "A1" or "B1" or "B2" or "A2");
            Assert.Equal(block!.StartsWith('A') ? "default" : comparisonCandidate, arm);
        }
        else Assert.True(category is null && block is null);
        string? observation = Environment.GetEnvironmentVariable(Prefix + "OBSERVATION");
        Assert.True(observation is null || observation == StageObservationVersion ||
            observation == WorkerObservationVersion || observation == RapidObservationVersion);
        bool observeRapidTimes = observation == RapidObservationVersion;
        bool observeWorkerStages = observation == WorkerObservationVersion || observeRapidTimes;
        bool observeStages = observation is not null;
        if (observeStages)
            Assert.True(mode == "perf" && category == "small" && comparisonCandidate == BoundedSpinCandidate,
                "Stage observation is restricted to the fixed small bounded-spin ABBA diagnostic.");
        string corpus = Data.ArtifactPath(Required(OcrCommercialCorpusTests.CorpusVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        string destination = Data.ArtifactPath(Required(Prefix + "OUTPUT"));
        string? reference = Environment.GetEnvironmentVariable(Prefix + "REFERENCE");
        if (reference is not null) reference = Data.ArtifactPath(reference);
        Assert.True(reference is not null || arm == "default" && block != "A2",
            "Candidate and A2 require the matching preserved A1/smoke-default reference.");
        Assert.False(Overlaps(destination, corpus) || Overlaps(destination, models) ||
            reference is not null && Overlaps(destination, reference));
        string root = Data.NewOutput(destination);
        StaTestHost.Run(() => Run(root, corpus, models, reference, mode, arm, category, block, comparisonCandidate, observeStages, observeWorkerStages, observeRapidTimes));
        output.WriteLine("Whole-spin measurements retained at " + root + "; releaseGate=NOT_EVALUATED.");
    }

    private static void Run(string root, string corpus, string models, string? reference,
        string mode, string arm, string? category, string? block, string comparisonCandidate, bool observeStages, bool observeWorkerStages, bool observeRapidTimes)
    {
        var issues = new List<string>();
        var started = DateTimeOffset.UtcNow;
        StageClock? stageClock = observeStages ? StageClock.Read(observeRapidTimes ? RapidObservationVersion : observeWorkerStages ? WorkerObservationVersion : StageObservationVersion) : null;
        Evidence.FileReceipt[] sources = [], binaries = [], originalModels = [], copiedModels = [], resolvedBefore = [], loadedNative = [];
        Evidence.SessionReceipt? session = null;
        Dictionary<string, string> requestedConfigEntries = RequestedWholeEntries(arm);
        var appliedCallbackEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var threadEnvironment = new Dictionary<string, string?>
        {
            ["ORT_INTRA_OP_NUM_THREADS"] = Environment.GetEnvironmentVariable("ORT_INTRA_OP_NUM_THREADS"),
            ["ORT_INTER_OP_NUM_THREADS"] = Environment.GetEnvironmentVariable("ORT_INTER_OP_NUM_THREADS"),
        };
        Evidence.FileReceipt? referenceBefore = null, referenceReportBefore = null;
        var quality = new Dictionary<string, string>(StringComparer.Ordinal);
        var calls = new List<Call>();
        Input[] inputs = [];
        Call? active = null;
        Exception? failure = null;
        NeuralOcrEngine? neural = null;
        int warmups = 0, initializations = 0, rows = 0;
        bool integrity = false, naturalRelease = false;
        double? blockCpuMs = null, blockCpuStartMs = null, blockCpuEndMs = null, blockWallMs = null;
        double? idleCpuMs = null, idleWallMs = null, afterReleaseCpuMs = null, afterReleaseWallMs = null;
        long? allocatedBytes = null, releaseTimestamp = null;
        string[] windowsLanguages = [];
        bool windowsAvailable = false;
        var released = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new NoDownloads());
        using var process = Process.GetCurrentProcess();
        try
        {
            // ORT 1.30 can override zero thread counts from these environment variables.
            // The fixed window experiment requires unchanged automatic thread selection.
            if (comparisonCandidate is BoundedSpinCandidate or BackoffFourSpinCandidate)
                Assert.All(threadEnvironment.Values, value => Assert.Null(value));
            sources = Evidence.Sources(); binaries = Evidence.Binaries(); originalModels = Evidence.Catalog(models);
            Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
            Data.Manifest manifest = Data.Read(corpus);
            Data.Sample[] development = manifest.Samples.Where(value => value.Split == "development").ToArray();
            Assert.Equal(180, development.Length);
            Assert.Equal(120, manifest.Samples.Count(value => value.Split == "held-out"));
            Data.Sample[] selected = mode == "smoke"
                ? Data.Categories.Select(value => development.First(sample => sample.Category == value)).ToArray()
                : development.Where(sample => sample.Category == category && sample.Scale == 1 && sample.Theme == "light" &&
                    Enumerable.Range(0, 5).Any(index => sample.SceneId == category + "-" + index.ToString("00")))
                    .OrderBy(sample => sample.SceneId, StringComparer.Ordinal).ToArray();
            Assert.Equal(5, selected.Length);
            Assert.All(selected, sample => Assert.Equal("development", sample.Split));
            inputs = selected.Select(sample => Load(corpus, sample)).ToArray();

            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            foreach (Evidence.FileReceipt item in originalModels)
            {
                string target = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, Path.GetFileName(item.Path));
                File.Copy(item.Path, target, overwrite: false);
                Assert.Equal(item.Sha256, Data.FileHash(target));
            }
            copiedModels = Evidence.Catalog(paths.OcrModelsRoot);
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance,
                runtime: null, engineIdleRelease: null,
                disableWholeSessionSpinning: arm == "no-spin",
                wholeSessionInitializing: (options, det, cls, rec, keys) =>
                {
                    initializations++;
                    if (arm is BoundedSpinCandidate or BackoffFourSpinCandidate)
                    {
                        // This is the same SessionOptions that InitModels opens. Keep allow_spinning,
                        // thread counts, graph, models, DOP and the production default unchanged.
                        foreach ((string key, string value) in requestedConfigEntries)
                        {
                            options.AddSessionConfigEntry(key, value);
                            appliedCallbackEntries.Add(key, value);
                        }
                    }
                    session = Evidence.Session(options, det, cls, rec, keys, arm == "no-spin");
                    // Hash the exact resolved paths before InitModels opens them; cold initialization
                    // is outside the throughput block. Recheck these same paths after natural unload.
                    resolvedBefore = Evidence.Resolved(session);
                },
                wholeResultObserved: raw =>
                {
                    Call call = active ?? throw new InvalidOperationException("Unexpected unowned whole-image result.");
                    call.RawCount++;
                    call.Raw = Evidence.Raw(raw);
                    if (observeRapidTimes) call.Stages!.RapidTimes = Evidence.CaptureRapidTimings(raw);
                },
                wholeProgressObserved: null,
                // The receiver only stores numbers. Do not assert, throw, allocate or log here.
                wholeStageObserved: observeWorkerStages
                    ? (stage, timestamp) => active!.Stages!.RecordWorkerStage(stage, timestamp)
                    : null);
            NeuralOcrEngine engine = neural;
            engine.EngineReleased += () => released.TrySetResult(Stopwatch.GetTimestamp());
            engine.ModelVerificationTask.GetAwaiter().GetResult();
            Assert.True(engine.CanRun);
            Assert.Equal(TimeSpan.FromSeconds(20), NeuralOcrEngine.EngineIdleRelease);
            var actualWindows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
            windowsAvailable = actualWindows.IsAvailable;
            windowsLanguages = actualWindows.SupportedLanguages.ToArray();
            var windows = new ObservedWindows(actualWindows, () => active!);
            var service = new CaptureOcrService(windows,
                (request, token) => ObserveNeural(engine, request, token, active!),
                () => engine.CanRun, () => engine.IsReady, NullLogger<CaptureOcrService>.Instance,
                readNeuralRows: (_, _, _) =>
                {
                    Interlocked.Increment(ref rows);
                    throw new InvalidOperationException("The Accurate whole-image experiment must not invoke row recognition.");
                });

            // Every selected input warms the actual route. All initialization, copied-model hashing
            // and first-use work is outside the warm throughput block and is retained separately.
            foreach (Input input in inputs)
            {
                active = new Call(input.Sample.Id, -1, observeStages);
                Invoke(service, input, active, process);
                RequireHealthy(active);
                if (observeWorkerStages) active.Stages!.RequireWorkerStages();
                if (observeRapidTimes) active.Stages!.RequireRapidTimings();
                Data.Write(root, $"warmup-{warmups:00}-{input.Sample.Id}.json", Observation(active, input.Sample));
                warmups++;
            }
            Assert.True(engine.IsReady);
            Assert.False(released.Task.IsCompleted);
            Assert.Equal(1, initializations);
            Assert.NotNull(session);
            using (var defaults = RapidOcrNet.RapidOcr.GetDefaultSessionOptions())
            {
                Assert.Equal(defaults.IntraOpNumThreads, session!.IntraThreads);
                Assert.Equal(defaults.InterOpNumThreads, session.InterThreads);
                Assert.Equal(defaults.ExecutionMode.ToString(), session.ExecutionMode);
                Assert.Equal(defaults.GraphOptimizationLevel.ToString(), session.Optimization);
                Assert.Equal(defaults.EnableMemoryPattern, session.MemoryPattern);
            }
            Assert.False(session!.CpuArena);
            Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 8), session.RecognitionDop);
            Assert.Equal(arm == "no-spin", session.DisableSpinning);
            Assert.Equal(arm is BoundedSpinCandidate or BackoffFourSpinCandidate ? 4 : 0, appliedCallbackEntries.Count);
            foreach ((string key, string value) in appliedCallbackEntries)
                Assert.Equal(requestedConfigEntries[key], value);
            loadedNative = Evidence.LoadedNative();
            string identity = JsonSerializer.Serialize(Evidence.Identity(sources, binaries, resolvedBefore, loadedNative), Data.Json);
            Dictionary<string, string>? expected = null;
            if (reference is not null)
            {
                string referencePath = DiagnosticOutputPaths.Child(reference, "quality.json");
                referenceBefore = Evidence.File("reference-quality", referencePath);
                string referenceReportPath = DiagnosticOutputPaths.Child(reference, "report.json");
                referenceReportBefore = Evidence.File("reference-report", referenceReportPath);
                using JsonDocument referenceReport = JsonDocument.Parse(File.ReadAllText(referenceReportPath));
                Assert.Equal("MEASURED", referenceReport.RootElement.GetProperty("executionStatus").GetString());
                Assert.True(referenceReport.RootElement.GetProperty("naturalRelease").GetBoolean());
                Assert.True(referenceReport.RootElement.GetProperty("integrity").GetBoolean());
                Assert.Equal(comparisonCandidate, referenceReport.RootElement.GetProperty("comparisonCandidate").GetString());
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(referencePath));
                Assert.Equal(identity, document.RootElement.GetProperty("identity").GetString());
                Assert.Equal(mode, document.RootElement.GetProperty("mode").GetString());
                Assert.Equal(category, document.RootElement.GetProperty("category").GetString());
                Assert.Equal("default", document.RootElement.GetProperty("arm").GetString());
                expected = JsonSerializer.Deserialize<Dictionary<string, string>>(document.RootElement.GetProperty("values").GetRawText(), Data.Json)!;
                Assert.Equal(inputs.Select(value => value.Sample.Id).Order().ToArray(), expected.Keys.Order().ToArray());
            }
            Data.Write(root, "preflight.json", new
            {
                stageClock,
                mode, arm, category, block, comparisonCandidate, corpus, corpusSha256 = Evidence.CorpusSha256,
                selected = inputs.Select(value => value.Sample), session, resolvedBefore, originalModels, copiedModels,
                sources, binaries, loadedNative, windowsAvailable, windowsLanguages, identity, referenceBefore, referenceReportBefore,
                processId = Environment.ProcessId, logicalProcessors = Environment.ProcessorCount,
                requestedConfigEntries, appliedCallbackEntries, threadEnvironment,
                configReceiptMeaning = "Applied callback entries completed AddSessionConfigEntry; this is not a native effective-default getter.",
                baselineSpinning = "Unset; runtime build defaults are not assumed to equal 1.",
            });
            int count = mode == "perf" ? 50 : 5;
            calls = Enumerable.Range(0, count).Select(index => new Call(inputs[index % 5].Sample.Id, index / 5, observeStages)).ToList();
            long allocationStart = GC.GetTotalAllocatedBytes(precise: true);
            double cpuStart = Cpu(process);
            blockCpuStartMs = cpuStart;
            long blockStart = Stopwatch.GetTimestamp();
            try
            {
                for (int index = 0; index < calls.Count; index++)
                {
                    active = calls[index];
                    Invoke(service, inputs[index % 5], active, process);
                    RequireHealthy(active);
                }
            }
            finally
            {
                blockWallMs = Stopwatch.GetElapsedTime(blockStart).TotalMilliseconds;
                blockCpuEndMs = Cpu(process);
                blockCpuMs = blockCpuEndMs - cpuStart;
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
            }
            if (observeWorkerStages)
                foreach (Call call in calls) call.Stages!.RequireWorkerStages();
            if (observeRapidTimes)
                foreach (Call call in calls) call.Stages!.RequireRapidTimings();
            Assert.Equal(1, initializations);
            Assert.Equal(0, rows);
            Assert.False(released.Task.IsCompleted);
            // Do not Dispose, force GC, or call TryInitialize/recognize during this quiet spell.
            // The unchanged product timer must raise EngineReleased; cleanup happens only later.
            double idleCpuStart = Cpu(process);
            long idleStart = Stopwatch.GetTimestamp();
            long releasedAt = released.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
            releaseTimestamp = releasedAt;
            Assert.True(releasedAt >= calls[^1].NeuralFinishedTimestamp);
            idleWallMs = Stopwatch.GetElapsedTime(idleStart).TotalMilliseconds;
            idleCpuMs = Cpu(process) - idleCpuStart;
            Assert.False(engine.IsReady);
            naturalRelease = true;
            double afterStart = Cpu(process);
            long afterWallStart = Stopwatch.GetTimestamp();
            Task.Delay(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            afterReleaseCpuMs = Cpu(process) - afterStart;
            afterReleaseWallMs = Stopwatch.GetElapsedTime(afterWallStart).TotalMilliseconds;
            Assert.False(engine.IsReady);

            for (int index = 0; index < calls.Count; index++)
            {
                Call call = calls[index]; Input input = inputs[index % 5];
                string exact = JsonSerializer.Serialize(new
                {
                    call.Raw, neural = Evidence.Result(call.Neural!),
                    windows = call.Windows is null ? null : Evidence.Result(call.Windows),
                    merged = Evidence.Result(call.Result!), input.Sample.PngSha256, input.Sample.TruthSha256,
                }, Data.Json);
                if (quality.TryGetValue(call.Id, out string? earlier)) Assert.Equal(earlier, exact);
                else quality.Add(call.Id, exact);
                if (expected is not null) Assert.Equal(expected[call.Id], exact);
            }
            Data.Write(root, "quality.json", new { identity, mode, arm, category, values = quality });
        }
        catch (Exception error) { failure = error; issues.Add(error.ToString()); }
        finally
        {
            try
            {
                if (referenceBefore is not null) Assert.Equal(referenceBefore, Evidence.File("reference-quality", referenceBefore.Path));
                if (referenceReportBefore is not null) Assert.Equal(referenceReportBefore, Evidence.File("reference-report", referenceReportBefore.Path));
                Evidence.Equal(sources, Evidence.Sources()); Evidence.Equal(binaries, Evidence.Binaries());
                Evidence.Equal(originalModels, Evidence.Catalog(models));
                foreach (Evidence.FileReceipt file in copiedModels.Concat(resolvedBefore).Concat(loadedNative))
                    Assert.Equal(file, Evidence.File(file.Role, file.Path));
                Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
                _ = Data.Read(corpus);
                foreach (Input input in inputs) Assert.Equal(input.Sample.PixelSha256, Data.PixelHash(input.Bitmap));
                integrity = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }
            // Cleanup is separate from, and cannot satisfy, the natural-release assertion.
            try { neural?.Dispose(); }
            catch (Exception error) { failure ??= error; issues.Add("Dispose: " + error); }
            for (int index = 0; index < calls.Count; index++)
                Data.Write(root, $"call-{index:0000}-{calls[index].Id}.json", Observation(calls[index], inputs[index % 5].Sample));
            double[] walls = calls.Where(call => call.Result is not null).Select(call => call.WallMs).Order().ToArray();
            bool measured = failure is null && issues.Count == 0 && integrity && naturalRelease &&
                quality.Count == 5 && calls.Count == (mode == "perf" ? 50 : 5);
            Data.Write(root, "report.json", new
            {
                executionStatus = measured ? "MEASURED" : "INCOMPLETE_OR_ERROR", releaseGate = "NOT_EVALUATED",
                stageClock,
                mode, arm, category, block, comparisonCandidate, started, finished = DateTimeOffset.UtcNow, warmups,
                requestedConfigEntries, appliedCallbackEntries, threadEnvironment,
                completed = calls.Count(call => call.Result is not null), expectedCalls = mode == "perf" ? 50 : 5,
                naturalRelease, configuredIdleSeconds = NeuralOcrEngine.EngineIdleRelease.TotalSeconds,
                lastNeuralCompletedTimestamp = calls.LastOrDefault()?.NeuralFinishedTimestamp, releaseTimestamp,
                idleCpuMs, idleWallMs, afterReleaseCpuMs, afterReleaseWallMs,
                blockCpuMs, blockCpuStartMs, blockCpuEndMs, blockWallMs, allocatedBytes, perCallCpuSumMs = calls.Sum(call => call.CpuMs),
                wallP50Ms = Percentile(walls, .5), wallP95Ms = Percentile(walls, .95),
                initializations, rows, integrity, session, resolvedBefore, originalModels, copiedModels,
                sources, binaries, loadedNative, windowsAvailable, windowsLanguages, referenceBefore, referenceReportBefore, issues,
                original19 = new { status = "NOT_RUN", reason = "Historical corpus is absent; fixed synthetic development inputs are separate." },
                scope = "Accurate actual CaptureOcrService/Neural whole-image route, including actual Windows dual/fallback routing. No row inference; no Fast attribution. Sources/model hashes and exact raw scores/text/geometry must match across arms.",
                timing = "Warm block excludes input/model IO, initialization, output serialization, quality comparison and idle. CPU is process cumulative user+kernel; callback snapshots and call CPU/clock reads are included symmetrically. Per-call CPU has repeated quantization. Idle begins after the capture call block; Neural timer begins slightly earlier at whole inference completion. Ten-second post-release CPU is separate. Natural release is required before Dispose.",
            });
        }
        Assert.True(failure is null && issues.Count == 0 && integrity && naturalRelease,
            "Preserved failed/incomplete whole-spin experiment: " + root + "\n" + string.Join('\n', issues));
    }

    private static Dictionary<string, string> RequestedWholeEntries(string arm) => arm switch
    {
        "no-spin" => new(StringComparer.Ordinal)
        {
            ["session.intra_op.allow_spinning"] = "0", ["session.inter_op.allow_spinning"] = "0",
        },
        BoundedSpinCandidate => new(StringComparer.Ordinal)
        {
            ["session.intra_op.spin_duration_us"] = "1000", ["session.inter_op.spin_duration_us"] = "1000",
            ["session.intra_op.spin_backoff_max"] = "8", ["session.inter_op.spin_backoff_max"] = "8",
        },
        BackoffFourSpinCandidate => new(StringComparer.Ordinal)
        {
            ["session.intra_op.spin_duration_us"] = "1000", ["session.inter_op.spin_duration_us"] = "1000",
            ["session.intra_op.spin_backoff_max"] = "4", ["session.inter_op.spin_backoff_max"] = "4",
        },
        "default" => new(StringComparer.Ordinal),
        _ => throw new InvalidOperationException("Unknown whole-session candidate."),
    };

    private static Input Load(string corpus, Data.Sample sample)
    {
        BitmapSource bitmap = ImageCodec.TryLoad(DiagnosticOutputPaths.Child(corpus, sample.Png))
            ?? throw new InvalidDataException(sample.Id);
        Assert.True(bitmap.IsFrozen); Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
        var settings = new OcrSettings { Quality = OcrQuality.Accurate, PreferredLanguages = sample.Languages.ToList() };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
        Assert.True(request.UseNeuralModel && request.EnhanceContrast && request.SearchRotatedOrientations);
        Assert.False(request.RecoverLatinIntrusions || request.LocalCorrection || request.UseSuperResolution);
        return new(sample, bitmap, request);
    }

    private static void Invoke(CaptureOcrService service, Input input, Call call, Process process)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // Cumulative process GC observations bracket the call; reads can span a concurrent GC.
        // They do not force collection and are outside the original per-call CPU/wall interval.
        if (call.Stages is { } before) before.BeforeGc = GcObservation.Read();
        double cpuStart = Cpu(process);
        call.CpuStartMs = cpuStart;
        long start = Stopwatch.GetTimestamp();
        if (call.Stages is { } beginning) beginning.StartedTimestamp = start;
        try { call.Result = service.RecognizeAsync(input.Request, cancellation.Token).GetAwaiter().GetResult(); }
        catch (Exception error) { call.Error = error.ToString(); call.HResult = error.HResult; throw; }
        finally
        {
            if (call.Stages is { } finished)
            {
                finished.FinishedTimestamp = Stopwatch.GetTimestamp();
                call.WallMs = Stopwatch.GetElapsedTime(start, finished.FinishedTimestamp).TotalMilliseconds;
            }
            else call.WallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            call.CpuEndMs = Cpu(process); call.CpuMs = call.CpuEndMs - cpuStart;
            if (call.Stages is { } after) after.AfterGc = GcObservation.Read();
        }
    }

    private static async Task<OcrResult> ObserveNeural(NeuralOcrEngine engine, OcrRequest request, CancellationToken token, Call call)
    {
        call.NeuralCount++;
        if (call.Stages is { } beginning) beginning.NeuralStartedTimestamp = Stopwatch.GetTimestamp();
        try { return call.Neural = await engine.RecognizeAsync(request, token).ConfigureAwait(false); }
        catch (Exception error) { call.NeuralError = error.ToString(); throw; }
        finally
        {
            call.NeuralFinishedTimestamp = Stopwatch.GetTimestamp();
            if (call.Stages is { } finished) finished.NeuralFinishedTimestamp = call.NeuralFinishedTimestamp;
        }
    }

    private static void RequireHealthy(Call call)
    {
        Assert.Equal(1, call.NeuralCount); Assert.Equal(1, call.RawCount); Assert.NotNull(call.Raw);
        Assert.NotNull(call.Neural); Assert.NotNull(call.Result);
        Assert.True(call.Neural!.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.True(call.Result!.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.Null(call.Error); Assert.Null(call.NeuralError); Assert.Null(call.WindowsError);
        Assert.InRange(call.WindowsCount, 0, 1);
        if (call.Windows is not null) Assert.True(call.Windows.Status is OcrStatus.Success or OcrStatus.NoText);
    }

    private static object Observation(Call call, Data.Sample sample) => new
    {
        call.Id, call.Repeat, sample.Category, sample.Split, sample.PngSha256, sample.PixelSha256, sample.TruthSha256,
        call.Raw, neural = call.Neural is null ? null : Evidence.Result(call.Neural),
        windows = call.Windows is null ? null : Evidence.Result(call.Windows),
        result = call.Result is null ? null : Evidence.Result(call.Result),
        metric = call.Result is null ? null : Data.Measure(sample, call.Result),
        call.WallMs, call.CpuMs, call.CpuStartMs, call.CpuEndMs, call.RawCount, call.NeuralCount, call.WindowsCount,
        call.Error, call.HResult, call.NeuralError, call.WindowsError, call.NeuralFinishedTimestamp,
        stages = call.Stages,
    };
    private static double Cpu(Process process) { process.Refresh(); return process.TotalProcessorTime.TotalMilliseconds; }
    private static double? Percentile(double[] sorted, double p) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing " + name);
    private static bool Overlaps(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(Path.TrimEndingDirectorySeparator(right) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(Path.TrimEndingDirectorySeparator(left) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed record Input(Data.Sample Sample, BitmapSource Bitmap, OcrRequest Request);
    private sealed class Call(string id, int repeat, bool observeStages)
    {
        // One object per Call is allocated before the measured block; snapshots are value types.
        internal StageObservation? Stages { get; } = observeStages ? new() : null;
        internal string Id { get; } = id;
        internal int Repeat { get; } = repeat;
        internal OcrResult? Result, Neural, Windows;
        internal object? Raw;
        internal int RawCount, NeuralCount, WindowsCount;
        internal string? Error, NeuralError, WindowsError;
        internal int? HResult;
        internal double WallMs, CpuMs, CpuStartMs, CpuEndMs;
        internal long NeuralFinishedTimestamp;
    }
    private sealed class ObservedWindows(WindowsOcrService actual, Func<Call> current) : IOcrService
    {
        public bool IsAvailable => actual.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => actual.SupportedLanguages;
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Call call = current(); call.WindowsCount++;
            if (call.Stages is { } beginning) beginning.WindowsStartedTimestamp = Stopwatch.GetTimestamp();
            try { return call.Windows = await actual.RecognizeAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { call.WindowsError = error.ToString(); throw; }
            finally
            {
                if (call.Stages is { } finished) finished.WindowsFinishedTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }
    private sealed record StageClock(string Version, long Frequency, long BeforeTimestamp,
        DateTimeOffset Utc, long AfterTimestamp)
    {
        internal static StageClock Read(string version)
        {
            long before = Stopwatch.GetTimestamp();
            DateTimeOffset utc = DateTimeOffset.UtcNow;
            long after = Stopwatch.GetTimestamp();
            return new(version, Stopwatch.Frequency, before, utc, after);
        }
    }
    internal readonly record struct GcObservation(long BeforeTimestamp, long AfterTimestamp,
        int Gen0, int Gen1, int Gen2, long PauseTimeSpanTicks)
    {
        internal static GcObservation Read()
        {
            long before = Stopwatch.GetTimestamp();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            long pause = GC.GetTotalPauseDuration().Ticks;
            long after = Stopwatch.GetTimestamp();
            return new(before, after, gen0, gen1, gen2, pause);
        }
    }
    internal sealed class StageObservation
    {
        // v3 only: the old raw quality projection and v1/v2 JSON shape stay unchanged.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Evidence.RapidTimingSnapshot? RapidTimes { get; set; }

        internal void RequireRapidTimings() =>
            (RapidTimes ?? throw new InvalidDataException("Missing same-call Rapid timing observation.")).RequireValid();

        public long StartedTimestamp { get; set; }
        public long FinishedTimestamp { get; set; }
        public long NeuralStartedTimestamp { get; set; }
        public long NeuralFinishedTimestamp { get; set; }
        // Zero/zero means the Windows route was not invoked; the existing count is authoritative.
        public long WindowsStartedTimestamp { get; set; }
        public long WindowsFinishedTimestamp { get; set; }
        public GcObservation BeforeGc { get; set; }
        public GcObservation AfterGc { get; set; }
        // Omit these zero defaults to retain the original v1/unobserved JSON shape.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long BeforeTaskRunTimestamp { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long WorkerEnteredTimestamp { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long BeforeDetectTimestamp { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long AfterDetectTimestamp { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int WorkerEventCount { get; private set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int WorkerEventOrderErrors { get; private set; }

        internal void RecordWorkerStage(NeuralOcrEngine.WholeObservationStage stage, long timestamp)
        {
            if ((int)stage != WorkerEventCount) WorkerEventOrderErrors++;
            WorkerEventCount++;
            switch (stage)
            {
                case NeuralOcrEngine.WholeObservationStage.BeforeTaskRun: BeforeTaskRunTimestamp = timestamp; break;
                case NeuralOcrEngine.WholeObservationStage.WorkerEntered: WorkerEnteredTimestamp = timestamp; break;
                case NeuralOcrEngine.WholeObservationStage.BeforeDetect: BeforeDetectTimestamp = timestamp; break;
                case NeuralOcrEngine.WholeObservationStage.AfterDetect: AfterDetectTimestamp = timestamp; break;
                default: WorkerEventOrderErrors++; break;
            }
        }

        internal void RequireWorkerStages()
        {
            if (WorkerEventCount != 4 || WorkerEventOrderErrors != 0 || NeuralStartedTimestamp <= 0 ||
                !(NeuralStartedTimestamp <= BeforeTaskRunTimestamp && BeforeTaskRunTimestamp <= WorkerEnteredTimestamp &&
                  WorkerEnteredTimestamp <= BeforeDetectTimestamp && BeforeDetectTimestamp <= AfterDetectTimestamp &&
                  AfterDetectTimestamp <= NeuralFinishedTimestamp))
                throw new InvalidDataException("Missing, duplicated, unordered, or out-of-call Neural worker observation.");
        }
    }
    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The whole-spin experiment never downloads models.");
    }
}

internal sealed class OcrWholeSpinExperimentFactAttribute : FactAttribute
{
    public OcrWholeSpinExperimentFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrWholeSpinExperimentTests.RunVariable)))
            Skip = "Opt-in actual whole-image ONNX comparison with preserved development inputs and 20-second natural unload.";
    }
}
