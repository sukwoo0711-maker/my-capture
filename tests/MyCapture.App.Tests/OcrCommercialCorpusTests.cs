using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in measurement of the unchanged product pipeline. A completed measurement is not a
/// commercial quality gate. Generate once, then run exactly one quality in each fresh testhost.
/// </summary>
public sealed class OcrCommercialCorpusTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_COMMERCIAL_RUN";
    internal const string OutputVariable = "MYCAPTURE_OCR_COMMERCIAL_OUTPUT";
    internal const string CorpusVariable = "MYCAPTURE_OCR_COMMERCIAL_CORPUS";
    internal const string RepeatsVariable = "MYCAPTURE_OCR_COMMERCIAL_REPEATS";
    internal const string SplitVariable = "MYCAPTURE_OCR_COMMERCIAL_SPLIT";

    [OcrCommercialCorpusFact("generate")]
    [Trait("Category", "OcrCommercialCorpus")]
    public void GenerateSynthetic300()
    {
        Assert.Equal("generate", Environment.GetEnvironmentVariable(RunVariable));
        Assert.Equal("all", ResolveSplit(Environment.GetEnvironmentVariable(SplitVariable), smoke: false));
        MetricExamples();
        string root = Data.NewOutput(Required(OutputVariable));
        StaTestHost.Run(() =>
        {
            Data.Manifest manifest = Data.Generate(root);
            Data.Manifest reopened = Data.Read(root);
            Assert.Equal(manifest.Samples.Select(sample => sample.PngSha256), reopened.Samples.Select(sample => sample.PngSha256));
        });
        output.WriteLine($"Generated and reopened 300 synthetic inputs: {root}");
    }

    [OcrCommercialCorpusFact("fast")]
    [Trait("Category", "OcrCommercialCorpus")]
    public void MeasureFast() => Measure("fast", OcrQuality.Fast);

    [OcrCommercialCorpusFact("normal")]
    [Trait("Category", "OcrCommercialCorpus")]
    public void MeasureNormal() => Measure("normal", OcrQuality.Balanced);

    [OcrCommercialCorpusFact("accurate")]
    [Trait("Category", "OcrCommercialCorpus")]
    public void MeasureAccurate() => Measure("accurate", OcrQuality.Accurate);

    private void Measure(string name, OcrQuality quality)
    {
        string mode = Required(RunVariable);
        Assert.True(mode == "smoke-" + name || mode == "full-" + name, "Select one documented corpus mode.");
        bool smoke = mode.StartsWith("smoke-", StringComparison.Ordinal);
        string split = ResolveSplit(Environment.GetEnvironmentVariable(SplitVariable), smoke);
        string corpus = Data.ArtifactPath(Required(CorpusVariable));
        string root = Data.NewOutput(Required(OutputVariable));
        Assert.False(string.Equals(corpus, root, StringComparison.OrdinalIgnoreCase));
        MetricExamples();
        int repeats = 1;
        string? repeatSetting = Environment.GetEnvironmentVariable(RepeatsVariable);
        if (repeatSetting is not null)
        {
            Assert.True(int.TryParse(repeatSetting, out repeats) && repeats is >= 1 and <= 5,
                RepeatsVariable + " must be an integer from 1 to 5.");
        }
        Assert.True(!smoke || repeats == 1, "Smoke is exactly one recognition for each of five categories.");
        StaTestHost.Run(() => MeasureOnSta(root, corpus, mode, quality, smoke, repeats, split));
        output.WriteLine($"Corpus measurement ended: {root}. Read report.json executionStatus and qualityCoverage; releaseGate is NOT_EVALUATED.");
    }

    private static void MeasureOnSta(string root, string corpus, string mode, OcrQuality quality, bool smoke, int repeats, string split)
    {
        var observations = new List<Observation>();
        var issues = new List<string>();
        var trace = new ConcurrentQueue<EngineReading>();
        var started = DateTimeOffset.UtcNow;
        FileReceipt[] binariesBefore = BinaryReceipts();
        FileReceipt[] sourcesBefore = SourceReceipts();
        string corpusHash = Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json"));
        Data.Manifest manifest = Data.Read(corpus);
        Data.Sample[] selected = SelectSamples(manifest, smoke, split);
        using var process = Process.GetCurrentProcess();
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        var observedWindows = new ObservedWindows(windows, trace);
        string? modelsSource = Environment.GetEnvironmentVariable(NeuralOcrLiveFactAttribute.ModelsVariable);
        FileReceipt[] modelsBefore = [];
        FileReceipt[] modelsAfter = [];
        FileReceipt[] binariesAfter = [];
        FileReceipt[] sourcesAfter = [];
        SelectedModel[] selectedModelsBefore = [];
        SelectedModel[] selectedModelsAfter = [];
        OcrModelStore? store = null;
        bool modelReady = false;
        bool corpusUnchanged = false;
        Exception? failure = null;
        NeuralOcrEngine? neural = null;
        using var http = new HttpClient(new NoDownloads());
        try
        {
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            Directory.CreateDirectory(paths.OcrModelsRoot);
            if (!string.IsNullOrWhiteSpace(modelsSource))
            {
                modelsSource = Path.GetFullPath(modelsSource);
                modelsBefore = ModelReceipts(modelsSource);
                foreach (FileReceipt file in modelsBefore)
                {
                    string target = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, file.Name);
                    File.Copy(DiagnosticOutputPaths.Child(modelsSource, file.Name), target, overwrite: false);
                    Assert.Equal(file.Sha256, Data.FileHash(target));
                }
            }
            store = new OcrModelStore(paths, http, NullLogger.Instance);
            neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
            neural.ModelVerificationTask.GetAwaiter().GetResult();
            selectedModelsBefore = SelectedModels(store);
            modelReady = neural.CanRun;
            if (quality == OcrQuality.Accurate)
            {
                Assert.True(modelReady, "Accurate measurement requires verified catalog models; fallback-only runs cannot stand for Accurate validation.");
                Assert.All(selectedModelsBefore, model => Assert.True(model.Exists, "Selected whole-image model is missing: " + model.Role));
            }
            NeuralOcrEngine engine = neural;
            var service = new CaptureOcrService(observedWindows,
                (request, cancellation) => Observe("neural-whole", request, token => engine.RecognizeAsync(request, token), cancellation, trace),
                () => engine.CanRun, () => engine.IsReady, NullLogger<CaptureOcrService>.Instance,
                readNeuralRows: (request, rows, cancellation) => Observe("neural-rows", request,
                    token => engine.ReadRowsAsync(request, rows, token), cancellation, trace));
            Data.Write(root, "preflight.json", new
            {
                mode, corpusSplit = split, qualityEnum = quality.ToString(), normalMeans = "OcrQuality.Balanced", corpusId = manifest.Id,
                corpusSha256 = corpusHash, selected = selected.Select(sample => sample.Id).ToArray(), repeats,
                windowsAvailable = windows.IsAvailable, windowsLanguages = windows.SupportedLanguages,
                modelReady, modelCatalog = OcrModelCatalog.CatalogVersion, models = modelsBefore, selectedModelsBefore,
                binaries = binariesBefore, sources = sourcesBefore, processId = Environment.ProcessId,
                os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
                logicalProcessors = Environment.ProcessorCount, is64BitProcess = Environment.Is64BitProcess,
                original19 = new { status = "NOT_RUN", reason = "Original inputs, manifests and baselines are absent on this computer." },
            });
            for (int index = 0; index < selected.Length; index++)
            {
                Data.Sample sample = selected[index];
                string image = DiagnosticOutputPaths.Child(corpus, sample.Png);
                Assert.Equal(sample.PngSha256, Data.FileHash(image));
                BitmapSource bitmap = ImageCodec.TryLoad(image) ?? throw new InvalidDataException("Cannot load input " + sample.Id);
                Assert.True(bitmap.IsFrozen);
                Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
                var settings = new OcrSettings { Quality = quality, PreferredLanguages = sample.Languages.ToList() };
                OcrQualityProfile.Synchronize(settings);
                for (int repeat = 0; repeat < repeats; repeat++)
                {
                    trace.Clear();
                    OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
                    bool sessionReadyBefore = engine.IsReady;
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    long allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                    TimeSpan cpuBefore = process.TotalProcessorTime;
                    long wallBefore = Stopwatch.GetTimestamp();
                    OcrResult result = service.RecognizeAsync(request, cancellation.Token).GetAwaiter().GetResult();
                    double wallMs = Stopwatch.GetElapsedTime(wallBefore).TotalMilliseconds;
                    double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
                    bool sessionReadyAfter = engine.IsReady;
                    EngineReading[] engines = trace.ToArray();
                    Assert.NotEmpty(engines);
                    bool windowsLanguageCoverage = sample.Languages.All(language => windows.SupportedLanguages.Any(installed => SameLanguage(language, installed)));
                    var observation = new Observation(index, repeat, sample.Id, sample.SceneId, sample.Category, sample.Split,
                        sample.TruthSha256, sample.PngSha256, sample.PixelSha256, sample.Languages,
                        windowsLanguageCoverage, sessionReadyBefore, sessionReadyAfter, wallMs, cpuMs, allocated,
                        result.Status.ToString(), result.LanguageTag, Data.Measure(sample, result), engines.Select(reading => reading.Engine).ToArray());
                    observations.Add(observation);
                    Data.Write(root, $"{index:000}-{repeat}-{sample.Id}.json", new
                    {
                        measurement = observation,
                        request = new { request.UpscaleFactor, request.SearchRotatedOrientations, request.EnhanceContrast,
                            request.UseNeuralModel, request.LocalCorrection, request.UseSuperResolution, request.RecoverLatinIntrusions, request.PreferredLanguages },
                        truth = sample, result = ResultEvidence(result), engines,
                        note = "Raw text/geometry and code indentation remain unmodified. Normalized CER is reported separately.",
                    });
                    if (result.Status is OcrStatus.Failed or OcrStatus.Cancelled or OcrStatus.Unavailable)
                        issues.Add(sample.Id + ": " + result.Status + ": " + result.Message);
                    foreach (EngineReading reading in engines.Where(reading => reading.ExceptionType is not null || reading.Status is "Failed" or "Cancelled" or "Unavailable"))
                        issues.Add(sample.Id + ": attempted " + reading.Engine + " returned " + reading.Status + "; a successful fallback does not validate that attempted engine.");
                    if (quality == OcrQuality.Accurate && !engines.Any(reading => reading.Engine == "neural-whole"))
                        issues.Add(sample.Id + ": Accurate did not execute its requested neural engine.");
                    Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            issues.Add(error.ToString());
            foreach (EngineReading reading in trace.Where(reading => reading.ExceptionType is not null))
                issues.Add("Attempted " + reading.Engine + " threw " + reading.ExceptionType + "; HRESULT=" + reading.HResult);
        }
        finally
        {
            try
            {
                if (neural is not null)
                {
                    try { neural.ModelVerificationTask.GetAwaiter().GetResult(); }
                    finally { neural.Dispose(); }
                }
            }
            catch (Exception error) { failure ??= error; issues.Add("Dispose: " + error); }
            try
            {
                if (!string.IsNullOrWhiteSpace(modelsSource))
                {
                    modelsAfter = ModelReceipts(modelsSource);
                    Assert.Equal(JsonSerializer.Serialize(modelsBefore, Data.Json), JsonSerializer.Serialize(modelsAfter, Data.Json));
                }
                binariesAfter = BinaryReceipts();
                sourcesAfter = SourceReceipts();
                if (store is not null)
                {
                    selectedModelsAfter = SelectedModels(store);
                    Assert.Equal(JsonSerializer.Serialize(selectedModelsBefore, Data.Json), JsonSerializer.Serialize(selectedModelsAfter, Data.Json));
                }
                Assert.Equal(JsonSerializer.Serialize(binariesBefore, Data.Json), JsonSerializer.Serialize(binariesAfter, Data.Json));
                Assert.Equal(JsonSerializer.Serialize(sourcesBefore, Data.Json), JsonSerializer.Serialize(sourcesAfter, Data.Json));
                Assert.Equal(corpusHash, Data.FileHash(DiagnosticOutputPaths.Child(corpus, "corpus.json")));
                _ = Data.Read(corpus);
                corpusUnchanged = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Evidence integrity: " + error); }
            bool complete = failure is null && observations.Count == selected.Length * repeats && issues.Count == 0;
            bool languageGap = quality != OcrQuality.Accurate && observations.Any(value => !value.WindowsLanguageCoverage);
            Data.Write(root, "report.json", new
            {
                executionStatus = complete ? "MEASURED" : "INCOMPLETE_OR_ERROR", releaseGate = "NOT_EVALUATED",
                qualityCoverage = languageGap ? "INCOMPLETE_REQUESTED_WINDOWS_LANGUAGE_PACKS" : "ACTUAL_PRODUCT_PATH_MEASURED",
                mode, corpusSplit = split, qualityEnum = quality.ToString(), startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                expectedImages = selected.Length, observedImages = observations.Select(value => value.Id).Distinct().Count(),
                expectedRecognitions = selected.Length * repeats, observedRecognitions = observations.Count,
                corpusId = manifest.Id, corpusSha256 = corpusHash, corpusUnchanged,
                windowsAvailable = windows.IsAvailable, windowsLanguages = windows.SupportedLanguages, modelReady,
                modelsBefore, modelsAfter, selectedModelsBefore, selectedModelsAfter,
                binariesBefore, binariesAfter, sourcesBefore, sourcesAfter, lastAttemptReadings = trace.ToArray(),
                original19 = new { status = "NOT_RUN", reason = "Missing original inputs and historical baselines; synthetic300 is a separate new corpus." },
                summary = Summaries(observations), observations, issues,
                definitions = new[]
                {
                    "The measured call is the actual CaptureOcrService with actual WindowsOcrService/NeuralOcrEngine and OcrRequestFactory; no model, spin or thread options are changed.",
                    "SelectedModels records the classifier, selected optional-server/bundled-mobile detector, recognizer and dictionary paths and hashes using the product's resolution order. These are initialization inputs, not evidence that Fast loaded a neural session; actual attempts are recorded separately, including thrown exceptions before fallback.",
                    "CPU is process user+kernel time across all its threads. It includes routing, preparation, lazy model initialization, recognition and mapping; observer timestamps add small overhead. It excludes corpus rendering/PNG decoding, model copying/hash verification, and result serialization.",
                    "Each invocation includes first-session cost when sessionReadyBefore is false. Later inputs are not claimed to have an independently cold model or filesystem cache. Request order and each repeat are retained.",
                    "corpusSplit selects all/development/held-out from the unchanged validated 300-image corpus. Split-specific runs start a fresh process/session and are not timing-matched to a prefix of an earlier full run.",
                    "Each engine reading records its actual OcrResult.TextAngle; the merged result may select a different engine's most-tilted angle. Windows reading angle is not a per-language raw WinRT trace.",
                    "Per-observation CPU has Windows counter quantization; aggregate CPU seconds before interpreting small differences. No A/B inference is made by this baseline runner.",
                    "CER is NFC text with normalized line endings, horizontal whitespace collapsed and each line trimmed; punctuation is preserved. RawExact separately preserves code whitespace and all raw characters. UTF-16 distance is sufficient for this BMP-only synthetic truth.",
                    "MissingLineCount counts truth line ink boxes having no positive-area overlap with any nonempty OCR line. ExactUnmatchedLines is a text LCS mismatch proxy, not proof of absent ink. CER, geometry coverage and raw results must be read together.",
                    "Latency percentiles are descriptive over this fixed heterogeneous sample set; repeated derivatives of 50 scenes are not 300 independent content examples. No quality or commercial-release threshold is added or relaxed.",
                    "Unavailable language packs are recorded even if product profile-language fallback returns text. Accurate's selected single/dual path is shown by actual engine readings; absent Windows English is not silently counted as Windows English validation.",
                    "The 3-minute per-request token is cooperative cancellation; the owning runner must also enforce a process-tree timeout. All generated files/model copies are retained. This runner never installs language packs, downloads models, reads user captures, changes settings or uses the clipboard.",
                },
            });
        }
        Assert.True(failure is null && issues.Count == 0, "Measurement was incomplete or encountered runtime errors. Preserved report: " + root + "\n" + string.Join('\n', issues));
    }

    internal static string ResolveSplit(string? setting, bool smoke)
    {
        string split = setting ?? "all";
        if (split is not ("all" or "development" or "held-out"))
            throw new InvalidDataException(SplitVariable + " must be all, development or held-out.");
        if (smoke && split != "all")
            throw new InvalidDataException("Smoke selects its five existing development examples; use a full mode for an explicit split.");
        return split;
    }

    internal static Data.Sample[] SelectSamples(Data.Manifest manifest, bool smoke, string split)
    {
        _ = ResolveSplit(split, smoke);
        // Data.Read already validates every manifest/PNG/pixel. Keep the previous full order
        // and select by the manifest's authored split; never infer split from result quality.
        Data.Sample[] ordered = Enumerable.Range(0, 60).SelectMany(index => Data.Categories.Select(category =>
            manifest.Samples.Where(sample => sample.Category == category).ElementAt(index))).ToArray();
        Data.Sample[] selected = smoke ? Data.Categories.Select(category => ordered.First(sample => sample.Category == category)).ToArray()
            : ordered.Where(sample => split == "all" || sample.Split == split).ToArray();
        int expected = smoke ? 5 : split == "development" ? 180 : split == "held-out" ? 120 : 300;
        if (selected.Length != expected || selected.Select(sample => sample.Id).Distinct(StringComparer.Ordinal).Count() != expected
            || Data.Categories.Any(category => selected.Count(sample => sample.Category == category) != expected / 5))
            throw new InvalidDataException("Selected corpus split has unexpected identities or category counts.");
        if (!smoke && split != "all" && (selected.Any(sample => sample.Split != split)
            || selected.Select(sample => sample.SceneId).Distinct(StringComparer.Ordinal).Count() != expected / 6
            || selected.GroupBy(sample => sample.SceneId).Any(group => group.Count() != 6)))
            throw new InvalidDataException("Every selected scene must retain its six variants in one split.");
        return selected;
    }

    private static object[] Summaries(List<Observation> rows) => rows.GroupBy(row => new { row.Category, row.Split })
        .Select(group => (object)new
        {
            group.Key.Category, group.Key.Split, count = group.Count(), distinctImages = group.Select(row => row.Id).Distinct().Count(),
            windowsLanguageGapCount = group.Count(row => !row.WindowsLanguageCoverage),
            cpuMillisecondsTotal = group.Sum(row => row.CpuMs), meanCpuMs = group.Average(row => row.CpuMs),
            meanWallMs = group.Average(row => row.WallMs), medianWallMs = Percentile(group.Select(row => row.WallMs), .50),
            p95WallMs = Percentile(group.Select(row => row.WallMs), .95), meanAllocatedBytes = group.Average(row => row.AllocatedBytes),
            microCer = (double)group.Sum(row => row.Metric.CharacterEdits) / Math.Max(1, group.Sum(row => row.Metric.TruthCharacters)),
            missingLines = group.Sum(row => row.Metric.MissingLineCount), truthLines = group.Sum(row => row.Metric.TruthLines),
            exactUnmatchedLines = group.Sum(row => row.Metric.ExactUnmatchedLines), rawExactCount = group.Count(row => row.Metric.RawExact),
        }).ToArray();

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private static bool SameLanguage(string expected, string actual) =>
        string.Equals(expected.Split('-')[0], actual.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value :
        throw new InvalidOperationException("Missing required opt-in setting: " + variable);

    private static void MetricExamples()
    {
        Assert.Equal(0, Data.EditDistance("한글", "한글"));
        Assert.Equal(1, Data.EditDistance("한글", "한"));
        Assert.Equal(2, Data.EditDistance("1,204,000", "1 204 000"));
        Assert.Equal(1, Data.EditDistance("abc", "abxc"));
        var lines = new[] { new Data.TruthLine("    return 1", new Data.Box(10, 10, 80, 20), ["    return 1"]) };
        var sample = new Data.Sample("metric", "metric", "code", "development", ["en-US"], 1, "light", 16,
            "Consolas", false, 200, 100, "    return 1", lines, [], "", "", "", "");
        OcrResult result = OcrResult.Success("return 1", "en", [new OcrLine("return 1", new OcrRect(10, 10, 80, 20), [])], TimeSpan.Zero);
        Data.Metric sameWords = Data.Measure(sample, result);
        Assert.Equal(0, sameWords.CharacterEdits);
        Assert.False(sameWords.RawExact);
        Assert.Equal(0, sameWords.MissingLineCount);
        Data.Metric absent = Data.Measure(sample, OcrResult.NoText("en", TimeSpan.Zero));
        Assert.Equal(1, absent.MissingLineCount);
        Assert.Equal(1, absent.ExactUnmatchedLines);
    }

    private static FileReceipt[] ModelReceipts(string source)
    {
        var receipts = new List<FileReceipt>();
        foreach (OcrModelFile model in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
        {
            string path = DiagnosticOutputPaths.Child(source, model.FileName);
            if (!File.Exists(path) && OcrModelCatalog.Optional.Contains(model)) continue;
            var receipt = new FileReceipt(model.FileName, new FileInfo(path).Length, Data.FileHash(path));
            Assert.Equal(model.ExpectedBytes, receipt.Bytes);
            Assert.Equal(model.ExpectedSha256, receipt.Sha256, ignoreCase: true);
            receipts.Add(receipt);
        }
        return receipts.ToArray();
    }

    private static FileReceipt[] BinaryReceipts()
    {
        string[] managed = [typeof(OcrCommercialCorpusTests).Assembly.Location, typeof(OcrRequestFactory).Assembly.Location,
            typeof(OcrSettings).Assembly.Location, typeof(ImageCodec).Assembly.Location,
            typeof(RapidOcrNet.RapidOcr).Assembly.Location, typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly.Location];
        string[] native = Directory.GetFiles(AppContext.BaseDirectory, "onnxruntime*.dll", SearchOption.TopDirectoryOnly);
        return managed.Concat(native).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileReceipt(Path.GetFileName(path), new FileInfo(path).Length, Data.FileHash(path))).ToArray();
    }

    private static SelectedModel[] SelectedModels(OcrModelStore store)
    {
        // Match NeuralOcrEngine.Bundled/CandidateRoots and TryInitializeLocked exactly.
        // Only catalog copies and the selected bundled inputs are read; no model is downloaded or initialized.
        string? Bundled(string name)
        {
            foreach (string? root in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath),
                         Path.GetDirectoryName(typeof(NeuralOcrEngine).Assembly.Location) })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string path = Path.Combine(root, RapidOcrNet.RapidOcr.ModelsFolderName, RapidOcrNet.RapidOcr.ModelsVersion, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }
        SelectedModel Receipt(string role, string source, string? path) => path is not null && File.Exists(path)
            ? new SelectedModel(role, source, path, true, new FileInfo(path).Length, Data.FileHash(path))
            : new SelectedModel(role, source, path, false, null, null);
        bool server = store.IsPresent(OcrModelCatalog.Detection);
        return
        [
            Receipt("classifier", "bundled", Bundled(RapidOcrNet.RapidOcr.DefaultClsModelPath)),
            Receipt("detector", server ? "verified-optional-server-copy" : "bundled-mobile",
                server ? store.PathFor(OcrModelCatalog.Detection) : Bundled(RapidOcrNet.RapidOcr.DefaultDetModelPath)),
            Receipt("recognizer", "isolated-catalog-copy", store.PathFor(OcrModelCatalog.Recognition)),
            Receipt("dictionary", "isolated-catalog-copy", store.PathFor(OcrModelCatalog.Dictionary)),
        ];
    }

    private static FileReceipt[] SourceReceipts()
    {
        string[] names = ["tests/MyCapture.App.Tests/OcrCommercialCorpusTests.cs", "tests/MyCapture.App.Tests/OcrCommercialCorpusData.cs",
            "src/MyCapture.Ocr/CaptureOcrService.cs", "src/MyCapture.Ocr/WindowsOcrService.cs", "src/MyCapture.Ocr/WindowsOcrRecognizer.cs",
            "src/MyCapture.Ocr/WindowsOcrBitmapBridge.cs", "src/MyCapture.Ocr/NeuralOcrEngine.cs", "src/MyCapture.Ocr/OcrModelCatalog.cs",
            "src/MyCapture.Ocr/OcrRequestFactory.cs", "src/MyCapture.Core/Settings/OcrQuality.cs"];
        return names.Select(name => { string path = Path.Combine(Data.RepositoryRoot(), name); return new FileReceipt(name, new FileInfo(path).Length, Data.FileHash(path)); }).ToArray();
    }

    private static object ResultEvidence(OcrResult result) => new { status = result.Status.ToString(), result.Text,
        result.LanguageTag, result.Lines, elapsedMs = result.Elapsed.TotalMilliseconds, result.Message, result.TextAngle };

    private static async Task<OcrResult> Observe(string engine, OcrRequest request, Func<CancellationToken, Task<OcrResult>> run,
        CancellationToken token, ConcurrentQueue<EngineReading> trace)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            OcrResult result = await run(token).ConfigureAwait(false);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            trace.Enqueue(new EngineReading(engine, start, elapsed, request.UpscaleFactor, request.PreferredLanguages.ToArray(),
                result.Status.ToString(), result.LanguageTag, result.Text, result.Lines.ToArray(), result.TextAngle, null, null, result.Message, token.IsCancellationRequested));
            return result;
        }
        catch (Exception error)
        {
            trace.Enqueue(new EngineReading(engine, start, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                request.UpscaleFactor, request.PreferredLanguages.ToArray(), error is OperationCanceledException ? "CancelledException" : "Exception",
                null, "", [], null, error.GetType().FullName, error.HResult, error.Message, token.IsCancellationRequested));
            throw; // Preserve the product's own exception/cancellation fallback behavior.
        }
    }

    private sealed class ObservedWindows(WindowsOcrService service, ConcurrentQueue<EngineReading> trace) : IOcrService
    {
        public bool IsAvailable => service.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => service.SupportedLanguages;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Observe("windows", request, token => service.RecognizeAsync(request, token), cancellationToken, trace);
    }

    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The corpus runner never downloads models.");
    }

    private sealed record FileReceipt(string Name, long Bytes, string Sha256);
    private sealed record SelectedModel(string Role, string Source, string? Path, bool Exists, long? Bytes, string? Sha256);
    private sealed record EngineReading(string Engine, long StartedTimestamp, double WallMs, double RequestedUpscale,
        string[] RequestedLanguages, string Status, string? LanguageTag, string Text, OcrLine[] Lines, double? TextAngle,
        string? ExceptionType, int? HResult, string? Message, bool CancellationRequested);
    private sealed record Observation(int Sequence, int Repeat, string Id, string SceneId, string Category, string Split,
        string TruthSha256, string PngSha256, string PixelSha256, string[] RequestedLanguages, bool WindowsLanguageCoverage,
        bool SessionReadyBefore, bool SessionReadyAfter, double WallMs, double CpuMs, long AllocatedBytes,
        string Status, string? LanguageTag, Data.Metric Metric, string[] Engines);
}

public sealed class OcrCommercialCorpusFactAttribute : FactAttribute
{
    public OcrCommercialCorpusFactAttribute(string quality)
    {
        string? mode = Environment.GetEnvironmentVariable(OcrCommercialCorpusTests.RunVariable);
        string[] known = ["generate", "smoke-fast", "smoke-normal", "smoke-accurate", "full-fast", "full-normal", "full-accurate"];
        if (mode is null || known.Contains(mode, StringComparer.Ordinal) &&
            !(mode == quality || mode == "smoke-" + quality || mode == "full-" + quality))
            Skip = "Opt-in synthetic OCR corpus. Set " + OcrCommercialCorpusTests.RunVariable + " and select this exact test.";
        // An invalid, explicitly supplied mode does not silently become an all-skipped success.
    }
}
