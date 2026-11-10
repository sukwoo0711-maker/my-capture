using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using RapidOcrNet;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;
using Evidence = MyCapture.App.Tests.OcrWholeSpinExperimentEvidence;
using ProductResult = MyCapture.Ocr.OcrResult;

namespace MyCapture.App.Tests;

/// <summary>Four development inputs only; stage observations, not threshold tuning or a performance gate.</summary>
public sealed class OcrSmallStageDiagnosticTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_SMALL_STAGE";
    private const string OutputVariable = "MYCAPTURE_OCR_SMALL_STAGE_OUTPUT";
    private static readonly (string File, string Sha256)[] Baselines =
    [
        ("048-0-small-01-125-dark.json", "9E2E9AB80716016A1391CC236E6F7067549B931E5B8CCC28BD55FAC3EDC7FF86"),
        ("043-0-small-01-125-light.json", "6CFBE566E20587DBA710DC8D7C7CCC9095FE16C6E1534D5D79E53E5B8D5B1C0F"),
        ("098-0-small-03-100-dark.json", "656BCE65EED8621169CD251740DC2B371C8DAA8E41244F908868CB10FFE2AC8A"),
        ("093-0-small-03-100-light.json", "1440909CFE5E919CADFFD1408AB474D8DF488FFA8B056922A891419FB6A939CD"),
    ];

    [OcrSmallStageFact]
    [Trait("Category", "OcrSmallStageDiagnostic")]
    public void ObserveFourDevelopmentInputsWithoutChangingThresholds()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(RunVariable));
        string destination = Data.ArtifactPath(Required(OutputVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        Assert.False(Overlaps(destination, models));
        Assert.False(Overlaps(destination, Path.Combine(Data.RepositoryRoot(), "artifacts/ocr-commercial/corpus-v1-26")));
        Assert.False(Overlaps(destination, Path.Combine(Data.RepositoryRoot(), "artifacts/ocr-commercial/full-accurate-26")));
        string root = Data.NewOutput(destination);
        StaTestHost.Run(() => Run(root, models));
        output.WriteLine("Preserved development-only stage evidence: " + root + "; releaseGate=NOT_EVALUATED.");
    }

    private static void Run(string root, string models)
    {
        var issues = new List<string>();
        var recognition = new List<Reading>();
        var detections = new List<Detection>();
        Evidence.FileReceipt[] sources = [], binaries = [], originals = [], copied = [], resolved = [], native = [];
        Evidence.SessionReceipt? session = null;
        Input[] inputs = [];
        Reading? active = null;
        NeuralOcrEngine? neural = null;
        RapidOcr? detector = null;
        Exception? failure = null;
        bool integrity = false, productDisposedBeforeDetector = false;
        int initializations = 0, rowCalls = 0;
        long? productDisposedAt = null, detectorInitializedAt = null;
        string[] languages = [];
        using var http = new HttpClient(new NoDownloads());
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            sources = [.. Evidence.Sources(), Evidence.File("tests/MyCapture.App.Tests/OcrSmallStageDiagnosticTests.cs",
                Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrSmallStageDiagnosticTests.cs"))];
            binaries = Evidence.Binaries(); originals = Evidence.Catalog(models);
            // Do not call Data.Read or enumerate the corpus. It decodes all300, including held-out.
            // Only these four pinned development raw files and their four PNGs are opened.
            inputs = Baselines.Select(value => LoadInput(root, value.File, value.Sha256)).ToArray();
            Assert.Equal(4, inputs.Select(input => input.Sample.Id).Distinct(StringComparer.Ordinal).Count());
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            foreach (Evidence.FileReceipt file in originals)
            {
                string target = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, Path.GetFileName(file.Path));
                File.Copy(file.Path, target, overwrite: false);
                Assert.Equal(file.Sha256, Data.FileHash(target));
            }
            copied = Evidence.Catalog(paths.OcrModelsRoot);
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance,
                disableWholeSessionSpinning: false,
                wholeSessionInitializing: (options, det, cls, rec, keys) =>
                {
                    initializations++;
                    session = Evidence.Session(options, det, cls, rec, keys, noSpin: false);
                    resolved = Evidence.Resolved(session);
                },
                wholeResultObserved: raw =>
                {
                    Reading reading = active ?? throw new InvalidOperationException("Unowned recognition callback.");
                    reading.RawCount++;
                    reading.Raw = Evidence.Raw(raw);
                    reading.RawBoxes = (raw.TextBlocks ?? []).Select((block, index) =>
                        new Box(index, block.BoxScore, Points(block.BoxPoints))).ToArray();
                    reading.MappingFacts = (raw.TextBlocks ?? []).Select((block, index) => new
                    {
                        index, block.Text,
                        blankBlockText = string.IsNullOrWhiteSpace(block.Text),
                        wordCount = block.WordResults?.Length,
                        nonblankWords = block.WordResults?.Count(word => !string.IsNullOrWhiteSpace(word.Text)),
                        productMappingDrop = string.IsNullOrWhiteSpace(block.Text) ? "blank-block-text" :
                            block.WordResults is { Length: > 0 } && block.WordResults.All(word => string.IsNullOrWhiteSpace(word.Text))
                                ? "all-returned-words-blank" : null,
                        charCount = block.Chars?.Length,
                        charScoreCount = block.CharScores?.Length,
                        averageReturnedCharScore = block.CharScores is { Length: > 0 } ? (double?)block.CharScores.Average() : null,
                        stage = "Detect-return: after Rapid empty-text/average-char-score filter, before product ToResult",
                    }).ToArray();
                });
            NeuralOcrEngine engine = neural;
            engine.ModelVerificationTask.GetAwaiter().GetResult();
            Assert.True(engine.CanRun);
            var actualWindows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
            languages = actualWindows.SupportedLanguages.ToArray();
            var windows = new ObservedWindows(actualWindows, () => active!);
            var service = new CaptureOcrService(windows,
                async (request, token) =>
                {
                    Reading reading = active!;
                    reading.NeuralCount++;
                    try { return reading.Neural = await engine.RecognizeAsync(request, token).ConfigureAwait(false); }
                    catch (Exception error) { reading.NeuralError = Error(error); throw; }
                }, () => engine.CanRun, () => engine.IsReady, NullLogger<CaptureOcrService>.Instance,
                readNeuralRows: (_, _, _) =>
                {
                    rowCalls++;
                    throw new InvalidOperationException("This Accurate diagnostic must not invoke row recognition.");
                });

            Data.Write(root, "input-preflight.json", new
            {
                selected = inputs.Select(input => input.Sample), sources, binaries, originals, copied, languages,
                recognitionCallsPlanned = 8, separateDetectorCallsPlanned = 8,
                rapidOptions = NeuralOcrEngine.CreateOptions(), requestedSpinEntries = new Dictionary<string, string>(),
                productConfigEntries = NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: false, runtimeSpinning: false),
                heldOutAccess = "None: only four explicitly named development raw files/PNGs are read.",
                original19 = "NOT_RUN: absent historical corpus", purpose = "Stage observation without tuning",
            });
            for (int repeat = 0; repeat < 2; repeat++)
            foreach (Input input in inputs)
            {
                active = new Reading(input.Sample.Id, repeat);
                recognition.Add(active);
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    active.Result = service.RecognizeAsync(input.Request, cancellation.Token).GetAwaiter().GetResult();
                    Assert.Equal(1, active.NeuralCount); Assert.Equal(1, active.RawCount);
                    Assert.NotNull(active.Raw); Assert.NotNull(active.Neural);
                    Assert.True(active.Result.Status is OcrStatus.Success or OcrStatus.NoText);
                    Assert.True(active.Neural!.Status is OcrStatus.Success or OcrStatus.NoText);
                    Assert.Null(active.NeuralError); Assert.Null(active.WindowsError);
                    Assert.InRange(active.WindowsCount, 0, 1);
                    if (active.Windows is not null) Assert.True(active.Windows.Status is OcrStatus.Success or OcrStatus.NoText);
                }
                catch (Exception error) { active.Error = Error(error); throw; }
                finally
                {
                    active.WallMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    Data.Write(root, $"recognition-{recognition.Count - 1:00}-{input.Sample.Id}.json", RecognitionEvidence(active, input));
                }
            }
            Assert.Equal(1, initializations); Assert.Equal(0, rowCalls); Assert.NotNull(session);
            Assert.False(session!.DisableSpinning);
            native = Evidence.LoadedNative();
            Data.Write(root, "product-session.json", new { session, resolved, native, initializations, rowCalls });
            // This is a stage diagnostic. Dispose is explicit here and is NOT natural-unload evidence.
            // All actual product tasks above are awaited before releasing this private instance.
            engine.Dispose();
            Assert.False(engine.IsReady);
            neural = null;
            productDisposedAt = Stopwatch.GetTimestamp();
            productDisposedBeforeDetector = true;

            OnnxRuntimeInitialization.Shared.EnsureInitialized();
            using (SessionOptions options = RapidOcr.GetDefaultSessionOptions())
            {
                options.EnableCpuMemArena = false;
                // The product sessions add these spin entries too (#223); the receipt below does not cover them.
                foreach ((string key, string value) in NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: false, runtimeSpinning: false))
                    options.AddSessionConfigEntry(key, value);
                Evidence.SessionReceipt detectorSession = Evidence.Session(options,
                    session.Detector, session.Classifier, session.Recognizer, session.Dictionary, noSpin: false);
                Assert.Equal(session, detectorSession);
                Evidence.Equal(resolved, Evidence.Resolved(detectorSession));
                detector = new RapidOcr();
                detector.InitModels(session.Detector, session.Classifier, session.Recognizer, session.Dictionary, options);
                detectorInitializedAt = Stopwatch.GetTimestamp();
                Assert.True(detectorInitializedAt.Value > productDisposedAt.Value);
            }
            RapidOcr separate = detector ?? throw new InvalidOperationException("Detector was not initialized.");
            Evidence.Equal(native, Evidence.LoadedNative());
            for (int repeat = 0; repeat < 2; repeat++)
            foreach (Input input in inputs)
            {
                var observation = new Detection(input.Sample.Id, repeat);
                detections.Add(observation);
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    Task.Run(() =>
                    {
                        // Use exactly the same product contrast/conversion path and source-size bitmap.
                        BitmapSource prepared = ImageCodec.StretchContrastForRecognition(input.Bitmap);
                        (BitmapSource pixels, SKAlphaType alpha) = NeuralOcrEngine.ForSkia(prepared);
                        using SKBitmap bitmap = NeuralOcrEngine.CopyToSkia(pixels, alpha,
                            new Int32Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
                        observation.Pixels = PixelEvidence(input.Bitmap, prepared, bitmap);
                        Assert.Equal(input.PreparedJson, JsonSerializer.Serialize(observation.Pixels, Data.Json));
                        IReadOnlyList<RapidOcrNet.TextBox> boxes = separate.DetectBoxes(bitmap, NeuralOcrEngine.CreateOptions(), cancellation.Token);
                        observation.Boxes = boxes.Select((box, index) => new Box(index, box.Score, Points(box.BoxPoints))).ToArray();
                        Assert.Equal(input.PreparedJson, JsonSerializer.Serialize(PixelEvidence(input.Bitmap, prepared, bitmap), Data.Json));
                    }, cancellation.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) { observation.Error = Error(error); throw; }
                finally
                {
                    observation.WallMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    Data.Write(root, $"detector-only-{detections.Count - 1:00}-{input.Sample.Id}.json", new
                    {
                        input.Sample.Id, repeat, observation.Pixels, observation.Boxes, observation.Error, observation.WallMs,
                        stage = "Separate public DetectBoxes run: post detector contour/box filters; source coordinates; no recognition",
                        sameInferenceAsRecognition = false, rapidOptions = NeuralOcrEngine.CreateOptions(),
                        productDisposedBeforeDetector, productDisposedAt, detectorInitializedAt,
                    });
                }
            }
            Assert.Equal(8, recognition.Count); Assert.Equal(8, detections.Count);
            Data.Write(root, "stage-comparison.json", new
            {
                rows = inputs.Select(input => new
                {
                    input.Sample.Id,
                    readings = recognition.Where(value => value.Id == input.Sample.Id).Select(value => new
                    {
                        value.Repeat,
                        baselineFinalStoredFieldsEqual = StoredFinalEquals(value.Result!, input.Baseline.GetProperty("result")),
                        finalMetric = Data.Measure(input.Sample, value.Result!),
                        truthLines = input.Sample.Lines.Select((line, index) => new
                        {
                            index, line.Text, line.Bounds,
                            rawReturnedBoxHits = value.RawBoxes!.Count(box => Intersects(line.Bounds, box.Points)),
                            neuralMappedLineHits = value.Neural!.Lines.Count(item => line.Bounds.Intersects(item.Bounds)),
                            finalLineHits = value.Result!.Lines.Count(item => line.Bounds.Intersects(item.Bounds)),
                            finalContainsLiteralLine = value.Result.Text.Contains(line.Text, StringComparison.Ordinal),
                            detectorReruns = detections.Where(item => item.Id == input.Sample.Id).Select(item => new
                            { item.Repeat, boxHits = item.Boxes!.Count(box => Intersects(line.Bounds, box.Points)) }),
                        }),
                    }),
                }),
                intersection = "Existing positive axis-aligned intersection only; partial text loss is also visible in exact truth/text.",
                interpretation = "A detector rerun is not the same inference. Raw-return absence with detector boxes narrows the gap to crop/classifier/recognizer/filter or variability; pre-filter scores are not observed. No threshold or cause is inferred automatically.",
            });
        }
        catch (Exception error) { failure = error; issues.Add(error.ToString()); }
        finally
        {
            try { detector?.Dispose(); }
            catch (Exception error) { failure ??= error; issues.Add("Detector Dispose: " + error); }
            try { neural?.Dispose(); }
            catch (Exception error) { failure ??= error; issues.Add("Product Dispose: " + error); }
            try
            {
                Evidence.Equal(sources, [.. Evidence.Sources(), Evidence.File("tests/MyCapture.App.Tests/OcrSmallStageDiagnosticTests.cs",
                    Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrSmallStageDiagnosticTests.cs"))]);
                Evidence.Equal(binaries, Evidence.Binaries()); Evidence.Equal(originals, Evidence.Catalog(models));
                foreach (Evidence.FileReceipt file in copied.Concat(resolved).Concat(native))
                    Assert.Equal(file, Evidence.File(file.Role, file.Path));
                foreach (Input input in inputs)
                {
                    Assert.Equal(input.RawReceipt, Evidence.File(input.RawReceipt.Role, input.RawReceipt.Path));
                    Assert.Equal(input.PngReceipt, Evidence.File(input.PngReceipt.Role, input.PngReceipt.Path));
                    Assert.Equal(input.Sample.PngSha256, Data.FileHash(input.CopiedPng));
                    Assert.Equal(input.Sample.PixelSha256, Data.PixelHash(input.Bitmap));
                }
                integrity = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }
            Data.Write(root, "report.json", new
            {
                executionStatus = failure is null && integrity && recognition.Count == 8 && detections.Count == 8 ? "OBSERVED" : "INCOMPLETE_OR_ERROR",
                releaseGate = "NOT_EVALUATED", started, finished = DateTimeOffset.UtcNow, integrity,
                recognitionAttempts = recognition.Count, detectorOnlyAttempts = detections.Count,
                completedRecognitions = recognition.Count(item => item.Result is not null && item.Error is null),
                completedDetectorCalls = detections.Count(item => item.Boxes is not null && item.Error is null),
                initializations, rowCalls, session, resolved, native, productDisposedBeforeDetector,
                productDisposedAt, detectorInitializedAt, issues,
                naturalUnload = "NOT_MEASURED: explicit Dispose separates the two stage instances.",
                scope = "Four fixed development inputs, each actual CaptureOcr twice plus separate public DetectBoxes twice; no held-out access or tuning.",
            });
        }
        Assert.True(failure is null && integrity && recognition.Count == 8 && detections.Count == 8,
            "Incomplete small stage evidence: " + root + "\n" + string.Join('\n', issues));
    }

    private static Input LoadInput(string root, string rawName, string rawSha)
    {
        string repository = Data.RepositoryRoot();
        string rawPath = DiagnosticOutputPaths.Child(Path.Combine(repository, "artifacts/ocr-commercial/full-accurate-26"), rawName);
        Evidence.FileReceipt rawReceipt = Evidence.File("baseline:" + rawName, rawPath);
        Assert.Equal(rawSha, rawReceipt.Sha256);
        using JsonDocument baseline = JsonDocument.Parse(File.ReadAllText(rawPath));
        Data.Sample sample = JsonSerializer.Deserialize<Data.Sample>(baseline.RootElement.GetProperty("truth").GetRawText(), Data.Json)!;
        Assert.Equal("development", sample.Split); Assert.Equal("small", sample.Category);
        Assert.Contains(sample.SceneId, new[] { "small-01", "small-03" });
        Assert.Equal(sample.Id, baseline.RootElement.GetProperty("measurement").GetProperty("id").GetString());
        Assert.Equal(sample.TruthSha256, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            sample with { PngSha256 = "", PixelSha256 = "", TruthSha256 = "" }, Data.Json))));
        string path = DiagnosticOutputPaths.Child(Path.Combine(repository, "artifacts/ocr-commercial/corpus-v1-26"), sample.Png);
        Evidence.FileReceipt pngReceipt = Evidence.File("png:" + sample.Id, path);
        Assert.Equal(sample.PngSha256, pngReceipt.Sha256);
        string copied = DiagnosticOutputPaths.Child(root, sample.Png);
        File.Copy(path, copied, overwrite: false);
        Assert.Equal(sample.PngSha256, Data.FileHash(copied));
        BitmapSource bitmap = ImageCodec.TryLoad(path) ?? throw new InvalidDataException(sample.Id);
        Assert.True(bitmap.IsFrozen); Assert.Equal(sample.Width, bitmap.PixelWidth); Assert.Equal(sample.Height, bitmap.PixelHeight);
        Assert.Equal(sample.PixelSha256, Data.PixelHash(bitmap));
        var settings = new OcrSettings { Quality = OcrQuality.Accurate, PreferredLanguages = sample.Languages.ToList() };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
        Assert.True(request.UseNeuralModel && request.EnhanceContrast && request.SearchRotatedOrientations);
        Assert.False(request.LocalCorrection || request.UseSuperResolution || request.RecoverLatinIntrusions);
        BitmapSource prepared = ImageCodec.StretchContrastForRecognition(bitmap);
        (BitmapSource pixels, SKAlphaType alpha) = NeuralOcrEngine.ForSkia(prepared);
        using SKBitmap skia = NeuralOcrEngine.CopyToSkia(pixels, alpha, new Int32Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
        object evidence = PixelEvidence(bitmap, prepared, skia);
        Data.Write(root, sample.Id + "-input.json", new { sample, rawReceipt, pngReceipt, copied, pixels = evidence });
        return new(sample, bitmap, request, rawReceipt, pngReceipt, copied, baseline.RootElement.Clone(), JsonSerializer.Serialize(evidence, Data.Json));
    }

    private static object PixelEvidence(BitmapSource original, BitmapSource prepared, SKBitmap bitmap)
    {
        byte[] bytes = new byte[bitmap.ByteCount];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new { width = bitmap.Width, height = bitmap.Height, bitmap.RowBytes, bitmap.ByteCount,
            colorType = bitmap.ColorType.ToString(), alphaType = bitmap.AlphaType.ToString(),
            sourcePixelSha256 = Data.PixelHash(original), preparedPixelSha256 = Data.PixelHash(prepared),
            contrastReturnedOriginal = ReferenceEquals(original, prepared), skiaBytesSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    }
    private static object RecognitionEvidence(Reading value, Input input) => new
    {
        input.Sample.Id, value.Repeat, input.Sample.TruthSha256, input.Sample.PngSha256, input.Sample.PixelSha256,
        stage = "Actual CaptureOcr Accurate; callback after Rapid filtering and before product ToResult",
        value.Raw, value.RawBoxes, value.MappingFacts, value.RawCount, value.NeuralCount, value.WindowsCount,
        neural = value.Neural is null ? null : Evidence.Result(value.Neural),
        windows = value.Windows is null ? null : Evidence.Result(value.Windows),
        result = value.Result is null ? null : Evidence.Result(value.Result), value.Error, value.NeuralError, value.WindowsError, value.WallMs,
        preparedPixels = JsonSerializer.Deserialize<JsonElement>(input.PreparedJson),
        preparedEvidenceScope = "Separately evaluated identical product preprocessing functions; no hidden internal stage callback.",
    };
    private static bool StoredFinalEquals(ProductResult value, JsonElement original)
    {
        JsonElement actual = JsonSerializer.SerializeToElement(Evidence.Result(value), Data.Json);
        return new[] { "status", "text", "languageTag", "lines", "message", "textAngle" }
            .All(key => JsonElement.DeepEquals(actual.GetProperty(key), original.GetProperty(key)));
    }
    private static Point[] Points(SKPointI[]? points) => points?.Select(point => new Point(point.X, point.Y)).ToArray() ?? [];
    private static bool Intersects(Data.Box truth, Point[] points) => points.Length > 0 && truth.Intersects(new OcrRect(
        points.Min(point => point.X), points.Min(point => point.Y),
        Math.Max(1, points.Max(point => point.X) - points.Min(point => point.X)),
        Math.Max(1, points.Max(point => point.Y) - points.Min(point => point.Y))));
    private static object Error(Exception error) => new { type = error.GetType().FullName, error.HResult, error.Message };
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing " + name);
    private static bool Overlaps(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(Path.TrimEndingDirectorySeparator(right) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(Path.TrimEndingDirectorySeparator(left) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private sealed record Point(int X, int Y);
    private sealed record Box(int Index, float Score, Point[] Points);
    private sealed record Input(Data.Sample Sample, BitmapSource Bitmap, OcrRequest Request, Evidence.FileReceipt RawReceipt,
        Evidence.FileReceipt PngReceipt, string CopiedPng, JsonElement Baseline, string PreparedJson);
    private sealed class Reading(string id, int repeat)
    {
        internal string Id { get; } = id;
        internal int Repeat { get; } = repeat;
        internal ProductResult? Result, Neural, Windows;
        internal object? Raw, MappingFacts, Error, NeuralError, WindowsError;
        internal Box[]? RawBoxes;
        internal int RawCount, NeuralCount, WindowsCount;
        internal double WallMs;
    }
    private sealed class Detection(string id, int repeat)
    {
        internal string Id { get; } = id;
        internal int Repeat { get; } = repeat;
        internal Box[]? Boxes;
        internal object? Pixels, Error;
        internal double WallMs;
    }
    private sealed class ObservedWindows(WindowsOcrService actual, Func<Reading> current) : IOcrService
    {
        public bool IsAvailable => actual.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => actual.SupportedLanguages;
        public async Task<ProductResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Reading value = current(); value.WindowsCount++;
            try { return value.Windows = await actual.RecognizeAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { value.WindowsError = Error(error); throw; }
        }
    }
    private sealed class NoDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This diagnostic uses only verified existing models.");
    }
}

internal sealed class OcrSmallStageFactAttribute : FactAttribute
{
    public OcrSmallStageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrSmallStageDiagnosticTests.RunVariable)))
            Skip = "Opt-in development-only four-input stage observation with preserved original pixels and outputs.";
    }
}
