using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text.Json;
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

public sealed class OcrWholeSpinLifecycleTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_WHOLE_LIFECYCLE_RUN";
    private const string Prefix = "MYCAPTURE_OCR_WHOLE_LIFECYCLE_";
    private const string ContractRelative = "work/quality-implementation/whole-spin-lifecycle-66/acceptance.json";
    private const string BackoffFourSpinCandidate = "spin-1000-backoff4";
    private const string ConfigReceiptMeaning =
        "Applied callback entries completed AddSessionConfigEntry before InitModels; this is not a native effective-value readback.";
    private static readonly string[] Ids = ["ko-00-100-light", "en-00-100-light", "mixed-00-100-light", "small-00-100-light", "code-00-100-light"];
    // ORT can override zero thread counts from these; the candidate comparison requires both absent.
    private static readonly string[] ThreadVariables = ["ORT_INTRA_OP_NUM_THREADS", "ORT_INTER_OP_NUM_THREADS"];

    [OcrWholeSpinLifecycleFact]
    [Trait("Category", "OcrWholeSpinLifecycle")]
    public void CancelRecoverAndReload()
    {
        Assert.Equal("1", Required(RunVariable));
        string arm = Required(Prefix + "ARM");
        Assert.Contains(arm, new[] { "default", "no-spin", BackoffFourSpinCandidate });
        string corpus = Data.ArtifactPath(Required(OcrCommercialCorpusTests.CorpusVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        string destination = Data.ArtifactPath(Required(Prefix + "OUTPUT"));
        string? reference = Environment.GetEnvironmentVariable(Prefix + "REFERENCE");
        if (reference is not null) reference = Data.ArtifactPath(reference);
        // Every non-default arm compares against a preserved default run under the same contract.
        Assert.Equal(arm != "default", reference is not null);
        Assert.False(Overlaps(destination, corpus) || Overlaps(destination, models) ||
            reference is not null && Overlaps(destination, reference));
        string root = Data.NewOutput(destination);
        StaTestHost.Run(() => Run(root, corpus, models, reference, arm));
        output.WriteLine("Whole OCR lifecycle evidence retained at " + root);
    }

    private static void Run(string root, string corpus, string models, string? reference, string arm)
    {
        var started = DateTimeOffset.UtcNow;
        var issues = new List<string>();
        Dictionary<string, string?> threadEnvironment = ThreadEnvironment();
        WholeSpinLifecycleScenario.ConfigEntry[] requestedConfigEntries = RequestedEntries(arm);
        Evidence.FileReceipt[] sources = [], binaries = [], originalModels = [], copiedModels = [], resolved = [], native = [], inputs = [];
        Evidence.FileReceipt? contractBefore = null, referenceQuality = null, referenceReport = null;
        WholeSpinLifecycleScenario? scenario = null;
        Loaded[] selected = [];
        object? outcome = null;
        JsonElement? snapshot = null;
        string? identity = null;
        bool warmPreflight = false, integrity = false, scenarioCompleted = false, disposed = false;
        Exception? failure = null;
        using var http = new HttpClient(new NoDownloads());
        try
        {
            string contractPath = Path.Combine(Data.RepositoryRoot(), ContractRelative);
            contractBefore = Evidence.File("lifecycle-contract", contractPath);
            Assert.Equal(Required(Prefix + "CONTRACT_SHA256"), contractBefore.Sha256);
            using JsonDocument contract = JsonDocument.Parse(File.ReadAllBytes(contractPath));
            JsonElement rules = contract.RootElement;
            Assert.Equal("whole-spin-lifecycle-v2", rules.GetProperty("id").GetString());
            VerifyEntries(requestedConfigEntries, rules.GetProperty("callbackSessionConfigEntries").GetProperty(arm));
            if (arm == BackoffFourSpinCandidate) Assert.All(threadEnvironment.Values, value => Assert.Null(value));
            sources = Sources(); binaries = Evidence.Binaries(); originalModels = Evidence.Catalog(models);
            VerifySources(sources, rules.GetProperty("frozenSourceHashes"));
            VerifyTyped(originalModels, rules.GetProperty("catalogModels"));
            string corpusPath = DiagnosticOutputPaths.Child(corpus, "corpus.json");
            Assert.Equal(Evidence.CorpusSha256, Data.FileHash(corpusPath));
            var manifest = JsonSerializer.Deserialize<Data.Manifest>(File.ReadAllBytes(corpusPath), Data.Json)
                ?? throw new InvalidDataException("Missing immutable corpus manifest.");
            Assert.Equal(Data.CorpusId, manifest.Id); Assert.Equal(Data.Seed, manifest.Seed);
            Assert.Equal(300, manifest.Samples.Length);
            // Read the immutable manifest, but decode only these five development images.
            selected = Ids.Select(id => Load(corpus, Assert.Single(manifest.Samples, item => item.Id == id))).ToArray();
            VerifySelected(selected, rules.GetProperty("selectedInputs"));
            inputs = selected.Select(item => Evidence.File("input:" + item.Sample.Id,
                DiagnosticOutputPaths.Child(corpus, item.Sample.Png))).ToArray();

            Dictionary<string, string>? referenceValues = null;
            JsonElement? referenceData = null;
            string? referenceIdentity = null;
            if (reference is not null)
            {
                referenceQuality = Evidence.File("reference-quality", DiagnosticOutputPaths.Child(reference, "quality.json"));
                referenceReport = Evidence.File("reference-report", DiagnosticOutputPaths.Child(reference, "report.json"));
                using var reportDocument = JsonDocument.Parse(File.ReadAllBytes(referenceReport.Path));
                referenceData = reportDocument.RootElement.Clone();
                Assert.Equal("PASS", referenceData.Value.GetProperty("executionStatus").GetString());
                Assert.Equal("default", referenceData.Value.GetProperty("arm").GetString());
                Assert.True(referenceData.Value.GetProperty("integrity").GetBoolean());
                Assert.True(referenceData.Value.GetProperty("scenarioCompleted").GetBoolean());
                Assert.Equal(contractBefore.Sha256, referenceData.Value.GetProperty("contractSha256").GetString());
                using var qualityDocument = JsonDocument.Parse(File.ReadAllBytes(referenceQuality.Path));
                Assert.Equal("default", qualityDocument.RootElement.GetProperty("arm").GetString());
                referenceIdentity = qualityDocument.RootElement.GetProperty("identity").GetString();
                referenceValues = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    qualityDocument.RootElement.GetProperty("values").GetRawText(), Data.Json)!;
                Assert.Equal(Ids.Order().ToArray(), referenceValues.Keys.Order().ToArray());
            }

            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            foreach (Evidence.FileReceipt file in originalModels)
            {
                string destination = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, Path.GetFileName(file.Path));
                File.Copy(file.Path, destination, overwrite: false);
                Assert.Equal(file.Sha256, Data.FileHash(destination));
            }
            copiedModels = Evidence.Catalog(paths.OcrModelsRoot);
            VerifyTyped(copiedModels, rules.GetProperty("catalogModels"));
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            scenario = new WholeSpinLifecycleScenario(store, arm == "no-spin", requestedConfigEntries, session =>
            {
                RequireSession(session, arm);
                Evidence.FileReceipt[] actual = Evidence.Resolved(session);
                VerifyTyped(actual, rules.GetProperty("resolvedModels"));
                if (resolved.Length == 0) resolved = actual;
                else Evidence.Equal(resolved, actual);
            });
            WholeSpinLifecycleScenario actualScenario = scenario;
            outcome = scenario.RunAsync(selected.Select(item => new WholeSpinLifecycleScenario.Input(item.Sample, item.Request)).ToArray(),
                referenceValues, values =>
                {
                    native = Evidence.LoadedNative();
                    string sessionIdentity = JsonSerializer.Serialize(SessionIdentity(actualScenario.Sessions.Single()), Data.Json);
                    identity = JsonSerializer.Serialize(new
                    {
                        evidence = Evidence.Identity(sources, binaries, resolved, native),
                        sessionIdentity, actualScenario.WindowsAvailable, actualScenario.WindowsLanguages,
                        threadEnvironment,
                    }, Data.Json);
                    if (referenceIdentity is not null) Assert.Equal(referenceIdentity, identity);
                    if (referenceData is not null)
                    {
                        Assert.Equal(actualScenario.WindowsAvailable, referenceData.Value.GetProperty("windowsAvailable").GetBoolean());
                        Assert.Equal(actualScenario.WindowsLanguages,
                            JsonSerializer.Deserialize<string[]>(referenceData.Value.GetProperty("windowsLanguages").GetRawText(), Data.Json));
                    }
                    Data.Write(root, "warmup-quality.json", new { arm, identity, values });
                    Data.Write(root, "preflight.json", new
                    {
                        arm, identity, contractSha256 = contractBefore.Sha256, sources, binaries, originalModels, copiedModels,
                        resolved, native, inputs, selected = selected.Select(item => item.Sample),
                        actualScenario.WindowsAvailable, actualScenario.WindowsLanguages,
                        session = actualScenario.Sessions.Single(), processId = Environment.ProcessId,
                        logicalProcessors = Environment.ProcessorCount, stopwatchFrequency = Stopwatch.Frequency,
                        threadEnvironment, requestedConfigEntries,
                        appliedCallbackEntries = actualScenario.ConfigApplications.ToArray(),
                        productConfigEntries = NeuralOcrEngine.WholeSessionConfigEntries(arm == "no-spin", arm != "no-spin"),
                        configReceiptMeaning = ConfigReceiptMeaning,
                    });
                    warmPreflight = true;
                }).GetAwaiter().GetResult();
            JsonElement result = JsonSerializer.SerializeToElement(outcome, Data.Json);
            Assert.True(result.GetProperty("pipelineCancellationGatePassed").GetBoolean());
            Assert.True(result.GetProperty("idleReloadGatePassed").GetBoolean());
            Assert.StartsWith("NOT_OBSERVED", result.GetProperty("activeKernelOverlap").GetString()!);
            Data.Write(root, "quality.json", new { arm, identity, values = result.GetProperty("quality") });
            scenarioCompleted = true;
        }
        catch (Exception error) { failure = error; issues.Add(error.ToString()); }
        finally
        {
            // Snapshot before cleanup Dispose can raise an additional release event after a failed stage.
            if (scenario is not null) snapshot = JsonSerializer.SerializeToElement(scenario.DiagnosticSnapshot(), Data.Json);
            try
            {
                Assert.True(warmPreflight, "Warm preflight was not completed.");
                Assert.NotNull(contractBefore);
                Assert.Equal(contractBefore, Evidence.File("lifecycle-contract", contractBefore!.Path));
                Evidence.Equal(sources, Sources()); Evidence.Equal(binaries, Evidence.Binaries());
                Evidence.Equal(originalModels, Evidence.Catalog(models));
                foreach (Evidence.FileReceipt file in copiedModels.Concat(resolved).Concat(native).Concat(inputs))
                    Assert.Equal(file, Evidence.File(file.Role, file.Path));
                Assert.Equal(Evidence.CorpusSha256, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
                foreach (Loaded item in selected) Assert.Equal(item.Sample.PixelSha256, Data.PixelHash(item.Bitmap));
                if (referenceQuality is not null) Assert.Equal(referenceQuality, Evidence.File(referenceQuality.Role, referenceQuality.Path));
                if (referenceReport is not null) Assert.Equal(referenceReport, Evidence.File(referenceReport.Role, referenceReport.Path));
                Assert.Equal(JsonSerializer.Serialize(threadEnvironment, Data.Json), JsonSerializer.Serialize(ThreadEnvironment(), Data.Json));
                integrity = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }
            try { scenario?.Dispose(); disposed = scenario is not null; }
            catch (Exception error) { failure ??= error; issues.Add("Dispose: " + error); }
            bool passed = failure is null && scenarioCompleted && integrity && disposed && issues.Count == 0;
            Data.Write(root, "report.json", new
            {
                executionStatus = passed ? "PASS" : "INCOMPLETE_OR_ERROR", releaseGate = "NOT_EVALUATED",
                arm, started, finished = DateTimeOffset.UtcNow, contractSha256 = contractBefore?.Sha256,
                processId = Environment.ProcessId, stopwatchFrequency = Stopwatch.Frequency,
                warmPreflight, scenarioCompleted, integrity, disposed, identity,
                threadEnvironment, requestedConfigEntries,
                appliedCallbackEntries = scenario?.ConfigApplications.ToArray(), configReceiptMeaning = ConfigReceiptMeaning,
                productConfigEntries = NeuralOcrEngine.WholeSessionConfigEntries(arm == "no-spin", arm != "no-spin"),
                sources, binaries, originalModels, copiedModels, resolved, native, inputs,
                windowsAvailable = scenario?.WindowsAvailable, windowsLanguages = scenario?.WindowsLanguages,
                outcome, snapshot, issues, errorType = failure?.GetType().FullName, hResult = failure?.HResult,
                original19 = "NOT_RUN: unavailable original inputs", heldOutRecognition = "NOT_RUN: development-only",
                activeKernelOverlap = "NOT_OBSERVED", configuredIdleSeconds = NeuralOcrEngine.EngineIdleRelease.TotalSeconds,
            });
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(scenarioCompleted && integrity && disposed);
    }

    private static Loaded Load(string corpus, Data.Sample sample)
    {
        Assert.Equal("development", sample.Split); Assert.Equal(1.0, sample.Scale); Assert.Equal("light", sample.Theme);
        Assert.Equal(string.Join('\n', sample.Lines.Select(line => line.Text)), sample.Text);
        string path = DiagnosticOutputPaths.Child(corpus, sample.Png);
        Assert.Equal(sample.PngSha256, Data.FileHash(path));
        BitmapSource bitmap = ImageCodec.TryLoad(path) ?? throw new InvalidDataException(sample.Id);
        Assert.True(bitmap.IsFrozen); Assert.Equal(sample.Width, bitmap.PixelWidth); Assert.Equal(sample.Height, bitmap.PixelHeight);
        Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
        var settings = new OcrSettings { Quality = OcrQuality.Accurate, PreferredLanguages = sample.Languages.ToList() };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
        Assert.True(request.UseNeuralModel && request.EnhanceContrast && request.SearchRotatedOrientations);
        Assert.False(request.RecoverLatinIntrusions || request.LocalCorrection || request.UseSuperResolution);
        return new(sample, bitmap, request);
    }
    private static void VerifySelected(Loaded[] selected, JsonElement pinned)
    {
        JsonElement[] expected = pinned.EnumerateArray().ToArray();
        Assert.Equal(5, expected.Length); Assert.Equal(5, selected.Length);
        for (int index = 0; index < selected.Length; index++)
        {
            Data.Sample value = selected[index].Sample;
            Assert.Equal(value.Id, expected[index].GetProperty("id").GetString());
            Assert.Equal(value.PngSha256, expected[index].GetProperty("pngSha256").GetString());
            Assert.Equal(value.PixelSha256, expected[index].GetProperty("pixelSha256").GetString());
            Assert.Equal(value.TruthSha256, expected[index].GetProperty("truthSha256").GetString());
        }
    }
    private static Evidence.FileReceipt[] Sources() => Evidence.Sources().Concat(new[]
    {
        Evidence.File("tests/MyCapture.App.Tests/OcrWholeSpinLifecycleTests.cs", Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrWholeSpinLifecycleTests.cs")),
        Evidence.File("tests/MyCapture.App.Tests/WholeSpinLifecycleScenario.cs", Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/WholeSpinLifecycleScenario.cs")),
    }).ToArray();
    private static void VerifySources(Evidence.FileReceipt[] sources, JsonElement pins)
    {
        Assert.Equal(sources.Length, pins.EnumerateObject().Count());
        foreach (Evidence.FileReceipt source in sources) Assert.Equal(pins.GetProperty(source.Role).GetString(), source.Sha256);
    }
    private static void VerifyTyped(Evidence.FileReceipt[] actual, JsonElement expected)
    {
        JsonElement[] values = expected.EnumerateArray().OrderBy(item => item.GetProperty("role").GetString(), StringComparer.Ordinal).ToArray();
        Evidence.FileReceipt[] files = actual.OrderBy(item => item.Role, StringComparer.Ordinal).ToArray();
        Assert.Equal(values.Length, files.Length); Assert.Equal(files.Length, files.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count());
        for (int index = 0; index < files.Length; index++)
        {
            Assert.Equal(values[index].GetProperty("role").GetString(), files[index].Role);
            Assert.Equal(values[index].GetProperty("bytes").GetInt64(), files[index].Bytes);
            Assert.Equal(values[index].GetProperty("sha256").GetString(), files[index].Sha256);
        }
    }
    private static void RequireSession(Evidence.SessionReceipt session, string arm)
    {
        using var defaults = RapidOcrNet.RapidOcr.GetDefaultSessionOptions();
        Assert.Equal(defaults.IntraOpNumThreads, session.IntraThreads); Assert.Equal(defaults.InterOpNumThreads, session.InterThreads);
        Assert.Equal(defaults.ExecutionMode.ToString(), session.ExecutionMode);
        Assert.Equal(defaults.GraphOptimizationLevel.ToString(), session.Optimization);
        Assert.Equal(defaults.EnableMemoryPattern, session.MemoryPattern);
        Assert.False(session.CpuArena); Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 8), session.RecognitionDop);
        Assert.Equal(arm == "no-spin", session.DisableSpinning);
    }
    private static object SessionIdentity(Evidence.SessionReceipt session) => new
    { session.IntraThreads, session.InterThreads, session.ExecutionMode, session.Optimization, session.CpuArena, session.MemoryPattern, session.RecognitionDop };
    private static WholeSpinLifecycleScenario.ConfigEntry[] RequestedEntries(string arm) => arm switch
    {
        // no-spin keeps using the product disableWholeSessionSpinning flag, never callback entries.
        "default" or "no-spin" => Array.Empty<WholeSpinLifecycleScenario.ConfigEntry>(),
        BackoffFourSpinCandidate => new WholeSpinLifecycleScenario.ConfigEntry[]
        {
            new("session.intra_op.spin_duration_us", "1000"), new("session.inter_op.spin_duration_us", "1000"),
            new("session.intra_op.spin_backoff_max", "4"), new("session.inter_op.spin_backoff_max", "4"),
        },
        _ => throw new InvalidOperationException("Unknown lifecycle arm."),
    };
    private static void VerifyEntries(WholeSpinLifecycleScenario.ConfigEntry[] requested, JsonElement pinned)
    {
        JsonElement[] expected = pinned.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, requested.Length);
        for (int index = 0; index < requested.Length; index++)
        {
            Assert.Equal(expected[index].GetProperty("key").GetString(), requested[index].Key);
            Assert.Equal(expected[index].GetProperty("value").GetString(), requested[index].Value);
        }
    }
    private static Dictionary<string, string?> ThreadEnvironment() =>
        ThreadVariables.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name), StringComparer.Ordinal);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing " + name);
    private static bool Overlaps(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(Path.TrimEndingDirectorySeparator(right) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(Path.TrimEndingDirectorySeparator(left) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed record Loaded(Data.Sample Sample, BitmapSource Bitmap, OcrRequest Request);
    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle regression never downloads models.");
    }
}

internal sealed class OcrWholeSpinLifecycleFactAttribute : FactAttribute
{
    public OcrWholeSpinLifecycleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrWholeSpinLifecycleTests.RunVariable)))
            Skip = "Opt-in real whole-OCR progress cancellation and natural 20-second unload/reload.";
    }
}
