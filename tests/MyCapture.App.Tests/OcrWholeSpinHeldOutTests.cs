using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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

/// <summary>One fixed pass over the 120 held-out synthetic inputs; never used to tune the candidate.</summary>
public sealed class OcrWholeSpinHeldOutTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_HELDOUT_RUN";
    private const string Prefix = "MYCAPTURE_OCR_HELDOUT_";
    private const string Candidate = "spin-1000-backoff4";
    private const string ContractName = "work/quality-implementation/whole-spin-heldout-67/acceptance.json";

    [OcrWholeSpinHeldOutFact]
    [Trait("Category", "OcrWholeSpinHeldOut")]
    public void CompareFixedHeldOut()
    {
        Assert.Equal("1", Required(RunVariable));
        string block = Required(Prefix + "BLOCK");
        Assert.Contains(block, new[] { "A1", "B1", "A2" });
        string corpus = Data.ArtifactPath(Required(OcrCommercialCorpusTests.CorpusVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        string destination = Data.ArtifactPath(Required(Prefix + "OUTPUT"));
        string? reference = Environment.GetEnvironmentVariable(Prefix + "REFERENCE");
        if (reference is not null) reference = Data.ArtifactPath(reference);
        Assert.Equal(block == "A1", reference is null);
        Assert.False(Overlaps(destination, corpus) || Overlaps(destination, models) ||
            reference is not null && Overlaps(destination, reference));
        string root = Data.NewOutput(destination);
        StaTestHost.Run(() => Run(root, corpus, models, reference, block));
        output.WriteLine("Held-out evidence retained at " + root);
    }

    private static void Run(string root, string corpus, string models, string? reference, string block)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string arm = block == "B1" ? Candidate : "default";
        var issues = new List<string>();
        var quality = new Dictionary<string, string>(StringComparer.Ordinal);
        var observations = new List<object>();
        var inputFiles = new List<Evidence.FileReceipt>();
        var requested = Entries(arm);
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string?> threadEnvironment = Threads();
        Evidence.FileReceipt[] sources = [], binaries = [], originalModels = [], copiedModels = [], resolved = [], native = [];
        Evidence.FileReceipt? contractReceipt = null, referenceQuality = null, referenceReport = null;
        Evidence.SessionReceipt? session = null;
        Data.Sample[] selected = [];
        Call? active = null;
        NeuralOcrEngine? neural = null;
        string? identity = null;
        Dictionary<string, string>? expected = null;
        Exception? failure = null;
        int initializations = 0, rows = 0, releases = 0;
        long? configTimestamp = null, releaseTimestamp = null;
        bool integrity = false, naturalRelease = false, disposed = false;
        var released = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new NoDownloads());
        try
        {
            Assert.All(threadEnvironment.Values, value => Assert.Null(value));
            string contractPath = Path.Combine(Data.RepositoryRoot(), ContractName);
            contractReceipt = Evidence.File("held-out-contract", contractPath);
            Assert.Equal(Required(Prefix + "CONTRACT_SHA256"), contractReceipt.Sha256);
            using JsonDocument contract = JsonDocument.Parse(File.ReadAllBytes(contractPath));
            Assert.Equal("whole-spin-heldout-v1", contract.RootElement.GetProperty("id").GetString());
            sources = Sources(); binaries = Evidence.Binaries(); originalModels = Evidence.Catalog(models);
            foreach (Evidence.FileReceipt source in sources)
                Assert.Equal(contract.RootElement.GetProperty("frozenSourceHashes").GetProperty(source.Role).GetString(), source.Sha256);
            string manifestPath = DiagnosticOutputPaths.Child(corpus, "corpus.json");
            Assert.Equal(Evidence.CorpusSha256, Data.FileHash(manifestPath));
            // Metadata-only: Data.Read decodes all 300 images. Decode only the current call below.
            Data.Manifest manifest = JsonSerializer.Deserialize<Data.Manifest>(File.ReadAllBytes(manifestPath), Data.Json)
                ?? throw new InvalidDataException("Missing manifest.");
            Assert.Equal(Data.CorpusId, manifest.Id); Assert.Equal(300, manifest.Samples.Length);
            selected = manifest.Samples.Where(sample => sample.Split == "held-out").ToArray();
            Assert.Equal(120, selected.Length);
            Assert.Equal(120, selected.Select(sample => sample.Id).Distinct().Count());
            Assert.Equal(20, selected.Select(sample => sample.SceneId).Distinct().Count());
            Assert.Equal(180, manifest.Samples.Count(sample => sample.Split == "development"));
            Assert.Empty(selected.Select(sample => sample.SceneId).Intersect(
                manifest.Samples.Where(sample => sample.Split == "development").Select(sample => sample.SceneId)));
            foreach (string category in Data.Categories) Assert.Equal(24, selected.Count(sample => sample.Category == category));
            Assert.Equal(JsonSerializer.Serialize(selected.Select(Pin).ToArray(), Data.Json),
                JsonSerializer.Serialize(contract.RootElement.GetProperty("selectedInputs"), Data.Json));
            Data.Sample warmup = Assert.Single(manifest.Samples, sample => sample.Id == "ko-00-100-light");
            Assert.Equal("development", warmup.Split);
            Assert.Equal(warmup.Id, contract.RootElement.GetProperty("warmupId").GetString());
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            foreach (Evidence.FileReceipt model in originalModels)
            {
                string target = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, Path.GetFileName(model.Path));
                File.Copy(model.Path, target, overwrite: false);
                Assert.Equal(model.Sha256, Data.FileHash(target));
            }
            copiedModels = Evidence.Catalog(paths.OcrModelsRoot);
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance,
                runtime: null, engineIdleRelease: null, disableWholeSessionSpinning: false,
                wholeSessionInitializing: (options, det, cls, rec, keys) =>
                {
                    initializations++;
                    foreach ((string key, string value) in requested)
                    {
                        options.AddSessionConfigEntry(key, value);
                        applied.Add(key, value);
                    }
                    configTimestamp = Stopwatch.GetTimestamp();
                    session = Evidence.Session(options, det, cls, rec, keys, false);
                    resolved = Evidence.Resolved(session);
                },
                wholeResultObserved: raw =>
                {
                    Call call = active ?? throw new InvalidOperationException("Unowned result.");
                    call.RawCount++; call.Raw = Evidence.Raw(raw);
                });
            NeuralOcrEngine engine = neural;
            engine.EngineReleased += () =>
            {
                Interlocked.Increment(ref releases);
                released.TrySetResult(Stopwatch.GetTimestamp());
            };
            engine.ModelVerificationTask.GetAwaiter().GetResult();
            Assert.True(engine.CanRun);
            Assert.Equal(TimeSpan.FromSeconds(20), NeuralOcrEngine.EngineIdleRelease);
            var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
            var observedWindows = new ObservedWindows(windows, () => active!);
            var service = new CaptureOcrService(observedWindows,
                async (request, token) =>
                {
                    Call call = active!; call.NeuralCount++;
                    try { return call.Neural = await engine.RecognizeAsync(request, token).ConfigureAwait(false); }
                    catch (Exception error) { call.NeuralError = error.ToString(); throw; }
                    finally { call.NeuralFinished = Stopwatch.GetTimestamp(); }
                }, () => engine.CanRun, () => engine.IsReady, NullLogger<CaptureOcrService>.Instance,
                readNeuralRows: (_, _, _) =>
                {
                    Interlocked.Increment(ref rows);
                    throw new InvalidOperationException("Held-out whole-image route invoked row recognition.");
                });

            void Recognize(Data.Sample sample, int index)
            {
                active = new Call(sample.Id);
                try
                {
                    string path = DiagnosticOutputPaths.Child(corpus, sample.Png);
                    Evidence.FileReceipt file = Evidence.File(sample.Id, path);
                    Assert.Equal(sample.PngSha256, file.Sha256); inputFiles.Add(file);
                    var bitmap = ImageCodec.TryLoad(path) ?? throw new InvalidDataException(sample.Id);
                    Assert.True(bitmap.IsFrozen); Assert.Equal(sample.Width, bitmap.PixelWidth);
                    Assert.Equal(sample.Height, bitmap.PixelHeight);
                    active.PixelSha256 = Data.PixelHash(bitmap); Assert.Equal(sample.PixelSha256, active.PixelSha256);
                    var settings = new OcrSettings { Quality = OcrQuality.Accurate, PreferredLanguages = sample.Languages.ToList() };
                    OcrQualityProfile.Synchronize(settings);
                    OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
                    Assert.True(request.UseNeuralModel && request.EnhanceContrast && request.SearchRotatedOrientations);
                    Assert.False(request.RecoverLatinIntrusions || request.LocalCorrection || request.UseSuperResolution);
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    active.Started = Stopwatch.GetTimestamp();
                    try { active.Result = service.RecognizeAsync(request, cancellation.Token).GetAwaiter().GetResult(); }
                    finally { active.Finished = Stopwatch.GetTimestamp(); }
                    RequireHealthy(active);
                    string exact = JsonSerializer.Serialize(new
                    {
                        active.Raw, neural = Evidence.Result(active.Neural!),
                        windows = active.Windows is null ? null : Evidence.Result(active.Windows),
                        merged = Evidence.Result(active.Result!), sample.PngSha256, sample.TruthSha256,
                    }, Data.Json);
                    quality.Add(sample.Id, exact);
                    // Preserve all fixed inputs even when one differs; no retry, exclusion or parameter tuning.
                    if (expected is not null && expected[sample.Id] != exact) issues.Add("Exact output mismatch: " + sample.Id);
                    Assert.Equal(1, initializations); Assert.Equal(0, rows); Assert.Equal(0, Volatile.Read(ref releases));
                }
                catch (Exception error) { active.Error = error.ToString(); throw; }
                finally
                {
                    object observation = new
                    {
                        index, sample.Id, sample.Category, sample.Split, sample.PngSha256, sample.TruthSha256,
                        active.PixelSha256, active.RawCount, active.NeuralCount, active.WindowsCount,
                        active.Started, active.NeuralFinished, active.Finished,
                        active.Error, active.NeuralError, active.WindowsError,
                        raw = active.Raw, neural = active.Neural is null ? null : Evidence.Result(active.Neural),
                        windows = active.Windows is null ? null : Evidence.Result(active.Windows),
                        merged = active.Result is null ? null : Evidence.Result(active.Result),
                    };
                    observations.Add(observation);
                    Data.Write(root, $"call-{index:000}-{sample.Id}.json", observation);
                }
            }

            Recognize(warmup, 0);
            Assert.NotNull(session); Assert.Equal(1, initializations); Assert.True(engine.IsReady);
            Assert.Equal(JsonSerializer.Serialize(requested, Data.Json), JsonSerializer.Serialize(applied, Data.Json));
            Assert.InRange(configTimestamp!.Value, active!.Started, active.NeuralFinished);
            using (var defaults = RapidOcrNet.RapidOcr.GetDefaultSessionOptions())
            {
                Assert.Equal(defaults.IntraOpNumThreads, session!.IntraThreads);
                Assert.Equal(defaults.InterOpNumThreads, session.InterThreads);
                Assert.Equal(defaults.ExecutionMode.ToString(), session.ExecutionMode);
                Assert.Equal(defaults.GraphOptimizationLevel.ToString(), session.Optimization);
                Assert.Equal(defaults.EnableMemoryPattern, session.MemoryPattern);
            }
            Assert.False(session!.CpuArena); Assert.False(session.DisableSpinning);
            Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 8), session.RecognitionDop);
            native = Evidence.LoadedNative();
            identity = JsonSerializer.Serialize(new
            {
                files = Evidence.Identity(sources, binaries, resolved, native), threadEnvironment,
                windowsAvailable = windows.IsAvailable, windowsLanguages = windows.SupportedLanguages.Order().ToArray(),
                os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
                logicalProcessors = Environment.ProcessorCount,
                session.IntraThreads, session.InterThreads, session.ExecutionMode, session.Optimization,
                session.CpuArena, session.MemoryPattern, session.RecognitionDop, session.DisableSpinning,
            }, Data.Json);
            if (reference is not null)
            {
                referenceQuality = Evidence.File("reference-quality", DiagnosticOutputPaths.Child(reference, "quality.json"));
                referenceReport = Evidence.File("reference-report", DiagnosticOutputPaths.Child(reference, "report.json"));
                Assert.Equal(Required(Prefix + "REFERENCE_QUALITY_SHA256"), referenceQuality.Sha256);
                Assert.Equal(Required(Prefix + "REFERENCE_REPORT_SHA256"), referenceReport.Sha256);
                using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(referenceReport.Path));
                Assert.Equal("PASS", report.RootElement.GetProperty("status").GetString());
                Assert.Equal("A1", report.RootElement.GetProperty("block").GetString());
                Assert.Equal("default", report.RootElement.GetProperty("arm").GetString());
                Assert.True(report.RootElement.GetProperty("integrity").GetBoolean());
                Assert.True(report.RootElement.GetProperty("naturalRelease").GetBoolean());
                Assert.Equal(contractReceipt.Sha256, report.RootElement.GetProperty("contractReceipt").GetProperty("sha256").GetString());
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(referenceQuality.Path));
                Assert.Equal(identity, document.RootElement.GetProperty("identity").GetString());
                expected = JsonSerializer.Deserialize<Dictionary<string, string>>(document.RootElement.GetProperty("values").GetRawText(), Data.Json)!;
                Assert.Equal(new[] { warmup.Id }.Concat(selected.Select(sample => sample.Id)).Order().ToArray(), expected.Keys.Order().ToArray());
                if (expected[warmup.Id] != quality[warmup.Id]) issues.Add("Exact output mismatch: " + warmup.Id);
            }
            Data.Write(root, "preflight.json", new { block, arm, identity, contractReceipt, selected = selected.Select(Pin),
                requested, applied, configTimestamp, session, sources, binaries, originalModels, copiedModels, resolved, native,
                referenceQuality, referenceReport, threadEnvironment, processId = Environment.ProcessId });
            for (int index = 0; index < selected.Length; index++) Recognize(selected[index], index + 1);
            Assert.Equal(121, quality.Count); Assert.Equal(1, initializations); Assert.Equal(0, rows);
            Assert.Equal(0, Volatile.Read(ref releases));
            // Product idle timer only: no explicit release, initialization, GC or inference in this interval.
            releaseTimestamp = released.Task.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
            Assert.True(releaseTimestamp >= active!.NeuralFinished);
            Assert.Equal(1, Volatile.Read(ref releases)); Assert.False(engine.IsReady);
            naturalRelease = true;
            Data.Write(root, "quality.json", new { identity, block, arm, values = quality });
        }
        catch (Exception error) { failure = error; issues.Add(error.ToString()); }
        finally
        {
            try
            {
                Assert.NotNull(contractReceipt);
                Assert.Equal(contractReceipt, Evidence.File(contractReceipt!.Role, contractReceipt.Path));
                if (referenceQuality is not null) Assert.Equal(referenceQuality, Evidence.File(referenceQuality.Role, referenceQuality.Path));
                if (referenceReport is not null) Assert.Equal(referenceReport, Evidence.File(referenceReport.Role, referenceReport.Path));
                Evidence.Equal(sources, Sources()); Evidence.Equal(binaries, Evidence.Binaries());
                Evidence.Equal(originalModels, Evidence.Catalog(models));
                foreach (var file in copiedModels.Concat(resolved).Concat(native).Concat(inputFiles))
                    Assert.Equal(file, Evidence.File(file.Role, file.Path));
                Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
                Assert.Equal(JsonSerializer.Serialize(threadEnvironment, Data.Json), JsonSerializer.Serialize(Threads(), Data.Json));
                integrity = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }
            try { neural?.Dispose(); disposed = neural is not null; }
            catch (Exception error) { failure ??= error; issues.Add("Dispose: " + error); }
            bool pass = failure is null && issues.Count == 0 && integrity && naturalRelease && disposed && quality.Count == 121;
            Data.Write(root, "report.json", new
            {
                status = pass ? "PASS" : "FAIL", block, arm, started, finished = DateTimeOffset.UtcNow,
                identity, contractReceipt, referenceQuality, referenceReport, requested, applied, configTimestamp,
                session, sources, binaries, originalModels, copiedModels, resolved, native, threadEnvironment,
                qualityCount = quality.Count, observations, inputFiles, initializations, rows, releases,
                naturalRelease, configuredIdleSeconds = NeuralOcrEngine.EngineIdleRelease.TotalSeconds,
                lastNeuralFinished = active?.NeuralFinished, releaseTimestamp, integrity, disposed, issues,
                actualNativeAllowSpinning = "UNKNOWN", original19 = "NOT_RUN: historical inputs unavailable",
                releaseGate = "NOT_EVALUATED", scope = "Fixed synthetic held-out exact equivalence; no performance or commercial quality verdict.",
            });
        }
        Assert.True(failure is null && issues.Count == 0 && integrity && naturalRelease && disposed && quality.Count == 121,
            "Preserved held-out failure: " + root + "\n" + string.Join('\n', issues));
    }

    private static Evidence.FileReceipt[] Sources() => Evidence.Sources().Append(Evidence.File(
        "tests/MyCapture.App.Tests/OcrWholeSpinHeldOutTests.cs",
        Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrWholeSpinHeldOutTests.cs"))).ToArray();
    private static object Pin(Data.Sample sample) => new { sample.Id, sample.PngSha256, sample.PixelSha256, sample.TruthSha256 };
    private static Dictionary<string, string?> Threads() => new(StringComparer.Ordinal)
    {
        ["ORT_INTRA_OP_NUM_THREADS"] = Environment.GetEnvironmentVariable("ORT_INTRA_OP_NUM_THREADS"),
        ["ORT_INTER_OP_NUM_THREADS"] = Environment.GetEnvironmentVariable("ORT_INTER_OP_NUM_THREADS"),
    };
    private static Dictionary<string, string> Entries(string arm) => arm == "default" ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal)
    {
        ["session.intra_op.spin_duration_us"] = "1000", ["session.inter_op.spin_duration_us"] = "1000",
        ["session.intra_op.spin_backoff_max"] = "4", ["session.inter_op.spin_backoff_max"] = "4",
    };
    private static void RequireHealthy(Call call)
    {
        Assert.Equal(1, call.NeuralCount); Assert.Equal(1, call.RawCount); Assert.NotNull(call.Raw);
        Assert.NotNull(call.Neural); Assert.NotNull(call.Result);
        Assert.True(call.Neural!.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.True(call.Result!.Status is OcrStatus.Success or OcrStatus.NoText);
        Assert.Null(call.NeuralError); Assert.Null(call.WindowsError);
        Assert.InRange(call.WindowsCount, 0, 1);
        Assert.Equal(call.WindowsCount == 0, call.Windows is null);
        if (call.Windows is not null) Assert.True(call.Windows.Status is OcrStatus.Success or OcrStatus.NoText);
    }
    private sealed class Call(string id)
    {
        internal string Id = id;
        internal OcrResult? Result, Neural, Windows;
        internal object? Raw;
        internal int RawCount, NeuralCount, WindowsCount;
        internal long Started, NeuralFinished, Finished;
        internal string? Error, NeuralError, WindowsError, PixelSha256;
    }
    private sealed class ObservedWindows(WindowsOcrService actual, Func<Call> current) : IOcrService
    {
        public bool IsAvailable => actual.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => actual.SupportedLanguages;
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Call call = current(); call.WindowsCount++;
            try { return call.Windows = await actual.RecognizeAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { call.WindowsError = error.ToString(); throw; }
        }
    }
    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Model downloads are forbidden in held-out validation.");
    }
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing " + key);
    private static bool Overlaps(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(Path.TrimEndingDirectorySeparator(a) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal sealed class OcrWholeSpinHeldOutFactAttribute : FactAttribute
{
    public OcrWholeSpinHeldOutFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OcrWholeSpinHeldOutTests.RunVariable) != "1")
            Skip = "Opt-in fixed held-out OCR equivalence; requires pinned corpus, models and contract.";
    }
}
