using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;
using CorpusSample = MyCapture.App.Tests.Ocr226PreprocessingDiagnosticTests.CorpusSample;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;
using Metric = MyCapture.App.Tests.Ocr226PreprocessingDiagnosticTests.Metric;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in #226 comparison of the OCR paths the product settings reach: Fast, Balanced, Accurate
/// before the model download (Windows OCR with the Accurate preprocessing) and Accurate with the
/// model (Windows Fast and PP-OCR in parallel, merged), plus PP-OCR alone for reference. Every
/// path runs the real CaptureOcrService, WindowsOcrService and NeuralOcrEngine with requests from
/// OcrRequestFactory; only the models are isolated, hash-checked copies. Results and rerun
/// settings: docs/maintenance/ocr-226-windows-table-diagnosis.md.
/// </summary>
public sealed class Ocr226EngineComparisonTests(ITestOutputHelper output)
{
    internal const string RunValue = "engines";
    internal const string CategoriesVariable = "MYCAPTURE_OCR226_CATEGORIES";
    internal const string PathsVariable = "MYCAPTURE_OCR226_PATHS";
    private const StringSplitOptions ListOptions = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
    private static readonly string[] KnownPaths = ["fast", "balanced", "accurate-nomodel", "neural", "dual"];

    [Ocr226EngineFact]
    [Trait("Category", "Ocr226Diagnostic")]
    public void Compare()
    {
        string root = Data.NewOutput(Required(Ocr226PreprocessingDiagnosticTests.OutputVariable));
        string[] corpora = Required(Ocr226PreprocessingDiagnosticTests.CorpusVariable).Split(',', ListOptions).Select(Data.ArtifactPath).ToArray();
        string[]? categories = Environment.GetEnvironmentVariable(CategoriesVariable) is { Length: > 0 } names ? names.Split(',', ListOptions) : null;
        string[] paths = Environment.GetEnvironmentVariable(PathsVariable) is { Length: > 0 } chosen ? chosen.Split(',', ListOptions) : KnownPaths;
        foreach (string path in paths)
            if (!KnownPaths.Contains(path)) throw new InvalidDataException("Unknown OCR path: " + path);
        StaTestHost.Run(() => CompareOnSta(root, corpora, categories, paths));
        output.WriteLine("Engine comparison written: " + root);
    }

    private static void CompareOnSta(string root, string[] corpora, string[]? categories, string[] paths)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var inputs = new List<(string Corpus, CorpusSample Sample)>();
        foreach (string corpus in corpora)
            inputs.AddRange(Ocr226PreprocessingDiagnosticTests.LoadCorpus(corpus)
                .Where(sample => categories is null || categories.Contains(sample.Category)).Select(sample => (Corpus: corpus, Sample: sample)));
        foreach (string category in categories ?? [])
            if (!inputs.Any(input => input.Sample.Category == category)) throw new InvalidDataException("No sample has the category " + category);
        Assert.NotEmpty(inputs);
        (BitmapSource fixtureImage, OcrResult fixtureTruth) = Ocr410Fixture.Table();
        CorpusSample fixture = Ocr226PreprocessingDiagnosticTests.FixtureSample(fixtureTruth, fixtureImage);

        bool needsModel = paths.Any(path => path is "neural" or "dual");
        var trace = new ConcurrentQueue<Reading>();
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        Assert.True(windows.IsAvailable, "Windows OCR is unavailable.");
        var observedWindows = new ObservedWindows(windows, trace);
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        using var http = new HttpClient(new NoDownloads());
        AppPaths appPaths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile")));
        Directory.CreateDirectory(appPaths.OcrModelsRoot);
        ModelReceipt[] models = needsModel ? CopyModels(appPaths.OcrModelsRoot) : [];
        var store = new OcrModelStore(appPaths, http, NullLogger.Instance);
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
        engine.ModelVerificationTask.GetAwaiter().GetResult();
        if (needsModel) Assert.True(engine.CanRun, "The neural paths need verified catalog models in " + NeuralOcrLiveFactAttribute.ModelsVariable + ".");
        bool detectorPresent = store.IsPresent(OcrModelCatalog.Detection);

        // The app's own wiring: one service holds the Windows engine and the installed model.
        var withModel = new CaptureOcrService(observedWindows,
            (request, token) => Observe("neural", request, run => engine.RecognizeAsync(request, run), token, trace),
            () => engine.CanRun, () => engine.IsReady, NullLogger<CaptureOcrService>.Instance,
            readNeuralRows: (request, rows, token) => Observe("neural-rows", request, run => engine.ReadRowsAsync(request, rows, run), token, trace));
        // The same service before the model download: the product's own missing-model check fails.
        var withoutModel = new CaptureOcrService(observedWindows,
            (_, _) => throw new InvalidOperationException("No model is installed on this path."),
            () => false, () => false, NullLogger<CaptureOcrService>.Instance);

        using var process = Process.GetCurrentProcess();
        var allRows = new List<Row>();
        var fixtureRows = new List<object>();
        var memory = new List<object>();
        foreach (string path in paths)
        {
            Snapshot before = Snapshot.Take(process);
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long warmStart = Stopwatch.GetTimestamp();
            _ = Read(path, inputs[0].Sample.Load(inputs[0].Corpus), withModel, withoutModel, engine, trace);
            double warmupMs = Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds;
            long maxPrivate = 0, maxWorkingSet = 0;
            var rows = new List<Row>();
            long pathStart = Stopwatch.GetTimestamp();
            foreach ((string corpus, CorpusSample sample) in inputs)
            {
                Row row = Measure(path, Path.GetFileName(corpus), sample, sample.Load(corpus), withModel, withoutModel, engine, trace, process);
                rows.Add(row);
                process.Refresh();
                maxPrivate = Math.Max(maxPrivate, process.PrivateMemorySize64);
                maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
            }
            Row fixtureRow = Measure(path, "fixture", fixture, fixtureImage, withModel, withoutModel, engine, trace, process);
            double pathMs = Stopwatch.GetElapsedTime(pathStart).TotalMilliseconds;
            Snapshot after = Snapshot.Take(process);
            memory.Add(new
            {
                path, before, after, maxPrivateBytes = Math.Max(maxPrivate, after.PrivateBytes), maxWorkingSetBytes = Math.Max(maxWorkingSet, after.WorkingSetBytes),
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, warmupMs, pathMs, neuralReadyAfter = engine.IsReady,
            });
            string fixtureCsv = fixtureRow.Csv;
            bool amounts = fixtureCsv.Contains("128,400", StringComparison.Ordinal) && fixtureCsv.Contains("1,204,000", StringComparison.Ordinal);
            fixtureRows.Add(new
            {
                path, fixtureRow.Text, csv = fixtureCsv, columns = fixtureRow.TableColumns, rows = fixtureRow.TableRows, hasHeader = fixtureRow.TableHasHeader,
                header = fixtureCsv.Contains("항목", StringComparison.Ordinal), amounts,
                // The table conditions of Ocr410WindowsEngineTests.InstalledWindowsEngineRecognizesIsolatedExtractionExamples("table").
                service = fixtureRow.TableColumns == 3 && fixtureRow.TableRows == 4 && fixtureRow.TableHasHeader && amounts,
                fixtureRow.Metric, fixtureRow.HeaderWordsFound, fixtureRow.HeaderLineFound, fixtureRow.Readings,
            });
            Data.Write(root, "rows-" + path + ".json", rows);
            allRows.AddRange(rows);
        }

        bool readyBeforeIdle = engine.IsReady;
        Snapshot beforeIdle = Snapshot.Take(process);
        Thread.Sleep(NeuralOcrEngine.EngineIdleRelease + TimeSpan.FromSeconds(5));
        Snapshot afterIdle = Snapshot.Take(process);
        Data.Write(root, "memory.json", new
        {
            paths = memory, idle = new { waitSeconds = (NeuralOcrEngine.EngineIdleRelease + TimeSpan.FromSeconds(5)).TotalSeconds,
                readyBefore = readyBeforeIdle, beforeIdle, afterIdle, readyAfter = engine.IsReady },
        });
        Data.Write(root, "fixture.json", fixtureRows);
        Data.Write(root, "summary.json", Summaries(allRows));
        Data.Write(root, "run.json", new
        {
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            processors = Environment.ProcessorCount, windowsLanguages = windows.SupportedLanguages, recognizer.MaxImageDimension,
            paths, categories, corpora = corpora.Select(corpus => new { name = Path.GetFileName(corpus), samples = inputs.Count(input => input.Corpus == corpus) }).ToArray(),
            models, detectorPresent, engineIdleReleaseSeconds = NeuralOcrEngine.EngineIdleRelease.TotalSeconds,
            definitions = new[]
            {
                "fast, balanced and dual read through one CaptureOcrService holding the installed model; accurate-nomodel reads through the same service with no runnable model.",
                "neural is CaptureOcrService.PrepareDualRequest of the Accurate request read by NeuralOcrEngine alone, the PP-OCR half of dual.",
                "Each path starts with one unrecorded warm-up recognition on the first sample, then reads every sample in corpus order and the #226 table once.",
                "wallMs is the Stopwatch time of one RecognizeAsync call; cpuMs is process user plus kernel time over the same call, across all threads.",
                "Header metrics use the first truth line (the column header row) of table and grid categories and of the #226 table.",
            },
        });
    }

    private static Row Measure(string path, string corpus, CorpusSample sample, BitmapSource bitmap, CaptureOcrService withModel,
        CaptureOcrService withoutModel, NeuralOcrEngine engine, ConcurrentQueue<Reading> trace, Process process)
    {
        trace.Clear();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        OcrResult result = Read(path, bitmap, withModel, withoutModel, engine, trace);
        double wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Metric metric = Ocr226PreprocessingDiagnosticTests.Score(sample, result);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result).Table;
        bool tabular = corpus == "fixture" || sample.Category.Contains("table", StringComparison.Ordinal) || sample.Category.Contains("grid", StringComparison.Ordinal);
        (string[]? Words, int? Found, bool? LineFound) header = tabular ? Header(sample, result) : default;
        return new Row(corpus, sample.Id, sample.Category, sample.Split, sample.FontPixels, sample.Width, sample.Height, path,
            result.Status.ToString(), result.LanguageTag, result.Text, wallMs, cpuMs, allocated, result.Elapsed.TotalMilliseconds, metric,
            header.Words, header.Found, header.LineFound, table.Columns.Count, table.Rows.Count, table.HasHeader, table.HasStructure, OcrTableExport.Csv(table),
            Lines(result.Lines), trace.ToArray());
    }

    private static OcrResult Read(string path, BitmapSource bitmap, CaptureOcrService withModel, CaptureOcrService withoutModel,
        NeuralOcrEngine engine, ConcurrentQueue<Reading> trace)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        if (path == "neural")
        {
            OcrRequest request = CaptureOcrService.PrepareDualRequest(Request(bitmap, OcrQuality.Accurate));
            return Observe("neural", request, token => engine.RecognizeAsync(request, token), cancellation.Token, trace).GetAwaiter().GetResult();
        }
        (CaptureOcrService service, OcrQuality quality) = path switch
        {
            "fast" => (withModel, OcrQuality.Fast),
            "balanced" => (withModel, OcrQuality.Balanced),
            "accurate-nomodel" => (withoutModel, OcrQuality.Accurate),
            "dual" => (withModel, OcrQuality.Accurate),
            _ => throw new InvalidDataException("Unknown OCR path: " + path),
        };
        return service.RecognizeAsync(Request(bitmap, quality), cancellation.Token).GetAwaiter().GetResult();
    }

    private static OcrRequest Request(BitmapSource bitmap, OcrQuality quality)
    {
        // Default preferred languages, as a user who never changed the OCR settings has them.
        var settings = new OcrSettings { Quality = quality };
        OcrQualityProfile.Synchronize(settings);
        return OcrRequestFactory.FromBitmap(bitmap, settings);
    }

    private static (string[]? Words, int? Found, bool? LineFound) Header(CorpusSample sample, OcrResult result)
    {
        string header = sample.Lines[0].Text.Normalize(NormalizationForm.FormC);
        string[] words = header.Split((char[]?)null, ListOptions);
        List<string> tokens = result.Text.Normalize(NormalizationForm.FormC).Split((char[]?)null, ListOptions).ToList();
        int found = 0;
        foreach (string word in words)
        {
            int index = tokens.IndexOf(word);
            if (index < 0) continue;
            tokens.RemoveAt(index);
            found++;
        }
        string compact = Compact(header);
        bool lineFound = result.Text.Normalize(NormalizationForm.FormC).Split('\n').Any(line => Compact(line) == compact);
        return (words, found, lineFound);
    }

    private static string Compact(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    private static LineEvidence[] Lines(IEnumerable<OcrLine> lines) =>
        lines.Select(line => new LineEvidence(line.Text, line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height)).ToArray();

    private static object[] Summaries(List<Row> rows)
    {
        object Summary(string path, string category, IReadOnlyList<Row> group) => new
        {
            path, category, count = group.Count,
            characterEdits = group.Sum(row => row.Metric.CharacterEdits), truthCharacters = group.Sum(row => row.Metric.TruthCharacters),
            microCer = (double)group.Sum(row => row.Metric.CharacterEdits) / Math.Max(1, group.Sum(row => row.Metric.TruthCharacters)),
            missingLines = group.Sum(row => row.Metric.MissingLineCount), truthLines = group.Sum(row => row.Metric.TruthLines),
            separatorTokens = group.Sum(row => row.Metric.SeparatorTokens), separatorMatched = group.Sum(row => row.Metric.SeparatorMatched),
            numericTokens = group.Sum(row => row.Metric.NumericTokens), numericMatched = group.Sum(row => row.Metric.NumericMatched),
            headerWords = group.Sum(row => row.HeaderWords?.Length ?? 0), headerWordsFound = group.Sum(row => row.HeaderWordsFound ?? 0),
            headerLinesFound = group.Count(row => row.HeaderLineFound == true), tableHasHeader = group.Count(row => row.TableHasHeader),
            meanWallMs = group.Average(row => row.WallMs), p95WallMs = Percentile(group.Select(row => row.WallMs), .95),
            meanCpuMs = group.Average(row => row.CpuMs),
        };
        return rows.GroupBy(row => row.Path).SelectMany(byPath => byPath.GroupBy(row => row.Category)
                .Select(group => Summary(byPath.Key, group.Key, group.ToArray()))
                .Append(Summary(byPath.Key, "all", byPath.ToArray())))
            .ToArray();
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    private static ModelReceipt[] CopyModels(string target)
    {
        string source = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        var receipts = new List<ModelReceipt>();
        foreach (OcrModelFile model in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
        {
            string from = DiagnosticOutputPaths.Child(source, model.FileName);
            if (!File.Exists(from) && OcrModelCatalog.Optional.Contains(model)) continue;
            var receipt = new ModelReceipt(model.FileName, new FileInfo(from).Length, Data.FileHash(from));
            Assert.Equal(model.ExpectedBytes, receipt.Bytes);
            Assert.Equal(model.ExpectedSha256, receipt.Sha256, ignoreCase: true);
            string to = DiagnosticOutputPaths.Child(target, model.FileName);
            File.Copy(from, to, overwrite: false);
            Assert.Equal(receipt.Sha256, Data.FileHash(to));
            receipts.Add(receipt);
        }
        return receipts.ToArray();
    }

    private static async Task<OcrResult> Observe(string engine, OcrRequest request, Func<CancellationToken, Task<OcrResult>> run,
        CancellationToken token, ConcurrentQueue<Reading> trace)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            OcrResult result = await run(token).ConfigureAwait(false);
            trace.Enqueue(new Reading(engine, Stopwatch.GetElapsedTime(start).TotalMilliseconds, request.UpscaleFactor, request.EnhanceContrast,
                request.SearchRotatedOrientations, result.Status.ToString(), result.LanguageTag, result.Text, Lines(result.Lines), null, result.Message));
            return result;
        }
        catch (Exception error)
        {
            trace.Enqueue(new Reading(engine, Stopwatch.GetElapsedTime(start).TotalMilliseconds, request.UpscaleFactor, request.EnhanceContrast,
                request.SearchRotatedOrientations, "Exception", null, "", [], error.GetType().FullName, error.Message));
            throw; // The product's own fallback decides what happens next.
        }
    }

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value :
        throw new InvalidOperationException("Missing required opt-in setting: " + variable);

    private sealed class ObservedWindows(WindowsOcrService service, ConcurrentQueue<Reading> trace) : IOcrService
    {
        public bool IsAvailable => service.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => service.SupportedLanguages;
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Observe("windows", request, token => service.RecognizeAsync(request, token), cancellationToken, trace);
    }

    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The engine comparison never downloads models.");
    }

    private sealed record Snapshot(long PrivateBytes, long WorkingSetBytes, long ManagedBytes, DateTimeOffset Utc)
    {
        internal static Snapshot Take(Process process)
        {
            process.Refresh();
            return new Snapshot(process.PrivateMemorySize64, process.WorkingSet64, GC.GetTotalMemory(forceFullCollection: false), DateTimeOffset.UtcNow);
        }
    }

    private sealed record ModelReceipt(string Name, long Bytes, string Sha256);
    private sealed record LineEvidence(string Text, double X, double Y, double Width, double Height);
    private sealed record Reading(string Engine, double WallMs, double RequestedUpscale, bool EnhanceContrast, bool SearchRotated,
        string Status, string? LanguageTag, string Text, LineEvidence[] Lines, string? ExceptionType, string? Message);
    private sealed record Row(string Corpus, string Id, string Category, string Split, double FontPixels, int Width, int Height, string Path,
        string Status, string? LanguageTag, string Text, double WallMs, double CpuMs, long AllocatedBytes, double ResultElapsedMs, Metric Metric,
        string[]? HeaderWords, int? HeaderWordsFound, bool? HeaderLineFound, int TableColumns, int TableRows, bool TableHasHeader,
        bool TableHasStructure, string Csv, LineEvidence[] Lines, Reading[] Readings);
}

internal sealed class Ocr226EngineFactAttribute : FactAttribute
{
    public Ocr226EngineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Ocr226PreprocessingDiagnosticTests.RunVariable) != Ocr226EngineComparisonTests.RunValue)
            Skip = "Opt-in #226 OCR path comparison: set " + Ocr226PreprocessingDiagnosticTests.RunVariable + "=" + Ocr226EngineComparisonTests.RunValue + ".";
    }
}
