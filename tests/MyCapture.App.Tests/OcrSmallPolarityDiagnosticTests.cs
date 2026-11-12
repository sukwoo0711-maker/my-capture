using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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

/// <summary>Polarity-only A1/B1/B2/A2 on four development inputs; observation, not adoption.</summary>
public sealed class OcrSmallPolarityDiagnosticTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR_SMALL_POLARITY";
    private const string OutputVariable = "MYCAPTURE_OCR_SMALL_POLARITY_OUTPUT";
    private static readonly (string File, string Sha256)[] Baselines =
    [
        ("048-0-small-01-125-dark.json", "9E2E9AB80716016A1391CC236E6F7067549B931E5B8CCC28BD55FAC3EDC7FF86"),
        ("043-0-small-01-125-light.json", "6CFBE566E20587DBA710DC8D7C7CCC9095FE16C6E1534D5D79E53E5B8D5B1C0F"),
        ("098-0-small-03-100-dark.json", "656BCE65EED8621169CD251740DC2B371C8DAA8E41244F908868CB10FFE2AC8A"),
        ("093-0-small-03-100-light.json", "1440909CFE5E919CADFFD1408AB474D8DF488FFA8B056922A891419FB6A939CD"),
    ];

    [OcrSmallPolarityFact]
    [Trait("Category", "OcrSmallPolarityDiagnostic")]
    public void ObservePolarityOnlyAbbaOnFourDevelopmentInputs()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(RunVariable));
        string destination = Data.ArtifactPath(Required(OutputVariable));
        string models = Data.ArtifactPath(Required(NeuralOcrLiveFactAttribute.ModelsVariable));
        Assert.False(Overlaps(destination, models));
        Assert.False(Overlaps(destination, Path.Combine(Data.RepositoryRoot(), "artifacts/ocr-commercial/corpus-v1-26")));
        Assert.False(Overlaps(destination, Path.Combine(Data.RepositoryRoot(), "artifacts/ocr-commercial/full-accurate-26")));
        Assert.False(Overlaps(destination, Path.Combine(Data.RepositoryRoot(), "artifacts/validation-quality-implementation/small-stage-32/evidence")));
        string root = Data.NewOutput(destination);
        StaTestHost.Run(() => RunCampaign(root, models));
        output.WriteLine("Preserved development-only stage evidence: " + root + "; releaseGate=NOT_EVALUATED.");
    }


    private static readonly string[] BlockOrder = ["A1", "B1", "B2", "A2"];
    private static readonly Dictionary<string, string> ReferenceHashes = new(StringComparer.Ordinal)
    {
        ["report.json"] = "9500551B5990FCF889B7AEF63AD313253747FFD1F875033E415A433F4DA098AC",
        ["input-preflight.json"] = "1E6C02F4FFEF77F15D25AEC990A89FC27D16B60BB2B5833AD19E33EB989DBD2C",
        ["recognition-00-small-01-125-dark.json"] = "424D3E9021E8AF3B71800892DE118DC5C85A32BC6D6510A8AD1BE1F1F75215B3",
        ["recognition-01-small-01-125-light.json"] = "DE7AD31AED5FD159A4DBAD7E46173960B4882FCE087BCBD0E1A07CA2C3A31AF7",
        ["recognition-02-small-03-100-dark.json"] = "DA5058533310C01CA4FA97993D7EEF25D86501672515D19E9D8B84AC3AA6C7C0",
        ["recognition-03-small-03-100-light.json"] = "E77FB0985D4E81C0C05EF2B0C4E186FB707204D675EF22BF211271D5DD0AFAE0",
        ["recognition-04-small-01-125-dark.json"] = "8855157F07B0CF59674CBF29041CB43154BDD92B5DD3B0378BEFD2CC463FDD63",
        ["recognition-05-small-01-125-light.json"] = "DAC8192BEAA58C6C5C6720220C3169E80A4F6D4B1B37AC3003545736CECB103C",
        ["recognition-06-small-03-100-dark.json"] = "96411287FC41BF39DD93F7FCF565201B9D8C49543A77DF469D2A5D20B73B43E3",
        ["recognition-07-small-03-100-light.json"] = "F35B3B8F67704418E443E07FB59F3458A71D63F3BE0E72B4550BEE45766D7313",
        ["detector-only-00-small-01-125-dark.json"] = "8EFAC088187C3B70A8006BDE758DB3AD7672AD239F527D48FE19DE35F2C4F886",
        ["detector-only-01-small-01-125-light.json"] = "28873198410EBB0A4F4BA34D30FF63C99655E148BE458A55C5C7B06CE8125E29",
        ["detector-only-02-small-03-100-dark.json"] = "0577E3DAD5542682E9E4E9403A0BD30FD38E64BE944900986266736050025B73",
        ["detector-only-03-small-03-100-light.json"] = "254838E7115213EE86542E0875AF222E0F520349E83B7A4A1C32E7D6F88021DB",
        ["detector-only-04-small-01-125-dark.json"] = "95046DA6D2E37767E6A3761907E539ED064078D88D85EDBCB2437702C23EC1DC",
        ["detector-only-05-small-01-125-light.json"] = "452A2575A541A0A3D1264C4744B20C4E2FB2339EE7141506244E5FEE6EA0DB45",
        ["detector-only-06-small-03-100-dark.json"] = "B199864BDFA25C584DD3B2BEEA73CD54E9315410E606C91BAD0FE7D76F00FE91",
        ["detector-only-07-small-03-100-light.json"] = "36DDDCA45F654F12C612A518C12F1BD376339B773AAADE7393CF46A55F228C0D",
    };

    private static JsonElement ReadReference(string name)
    {
        string directory = Path.Combine(Data.RepositoryRoot(), "artifacts/validation-quality-implementation/small-stage-32/evidence");
        string path = DiagnosticOutputPaths.Child(directory, name);
        Assert.Equal(ReferenceHashes[name], Data.FileHash(path));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static void RunCampaign(string root, string models)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var completed = new List<object>();
        Exception? failure = null;
        object? comparison = null;
        try
        {
            JsonElement prior = ReadReference("report.json");
            Assert.Equal("OBSERVED", prior.GetProperty("executionStatus").GetString());
            Assert.True(prior.GetProperty("integrity").GetBoolean());
            Assert.Equal(8, prior.GetProperty("completedRecognitions").GetInt32());
            Assert.Equal(8, prior.GetProperty("completedDetectorCalls").GetInt32());
            Assert.Equal(0, prior.GetProperty("issues").GetArrayLength());
            Data.Write(root, "campaign-preflight.json", new { order = BlockOrder, referenceHashes = ReferenceHashes,
                maximumCaptureCalls = 16, maximumSeparateDetectorCalls = 16,
                variable = "Before OcrRequestFactory: B immutable Pbgra C'=A-C; A original BitmapSource. Product preprocessing unchanged.",
                baseline = "Stage32 pixels/model/native and product source/binary identities pinned before OCR.",
                qualityGate = "NOT_EVALUATED: four development inputs cannot adopt a product policy." });
            for (int index = 0; index < BlockOrder.Length; index++)
            {
                string block = BlockOrder[index];
                string directory = Data.NewOutput(DiagnosticOutputPaths.Child(root, block));
                DateTimeOffset blockStarted = DateTimeOffset.UtcNow;
                RunBlock(directory, models, block, block.StartsWith('B'), index);
                string path = DiagnosticOutputPaths.Child(directory, "report.json");
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal("OBSERVED", report.RootElement.GetProperty("executionStatus").GetString());
                Assert.Equal(4, report.RootElement.GetProperty("completedRecognitions").GetInt32());
                Assert.Equal(4, report.RootElement.GetProperty("completedDetectorCalls").GetInt32());
                completed.Add(new { block, blockStarted, finished = DateTimeOffset.UtcNow, reportSha256 = Data.FileHash(path) });
            }
            var assessed = CampaignComparison(root);
            comparison = assessed.Rows;
            Assert.True(assessed.Stable, "B1/B2 raw, mapped, final, pixels or separate detector observations changed.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { foreach (string name in ReferenceHashes.Keys) _ = ReadReference(name); }
            catch (Exception error) { failure ??= error; }
            Data.Write(root, "campaign-report.json", new
            {
                executionStatus = failure is null && completed.Count == 4 ? "OBSERVED" : "INCOMPLETE_OR_ERROR",
                releaseGate = "NOT_EVALUATED", productAdoption = "NOT_EVALUATED_DEVELOPMENT180_PENDING",
                started, finished = DateTimeOffset.UtcNow, order = BlockOrder, completed, comparison,
                error = failure is null ? null : Error(failure),
                timingScope = "Per-call process CPU and wall include callbacks and lazy initialization; no p95 or timing superiority gate.",
                naturalUnload = "NOT_MEASURED: explicit Dispose between product/detector instances and all blocks.",
            });
        }
        Assert.True(failure is null && completed.Count == 4, "Incomplete polarity observation: " + failure);
    }

    private static (object Rows, bool Stable) CampaignComparison(string root)
    {
        var rows = new List<object>();
        bool stable = true;
        foreach (var (rawName, _) in Baselines)
        {
            int index = Array.FindIndex(Baselines, entry => entry.File == rawName);
            using JsonDocument original = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data.RepositoryRoot(),
                "artifacts/ocr-commercial/full-accurate-26", rawName)));
            Data.Sample truth = JsonSerializer.Deserialize<Data.Sample>(original.RootElement.GetProperty("truth"), Data.Json)!;
            var readings = new List<JsonElement>();
            var detections = new List<JsonElement>();
            foreach (string block in BlockOrder)
            {
                string path = DiagnosticOutputPaths.Child(Path.Combine(root, block), $"recognition-{index:00}-{truth.Id}.json");
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                readings.Add(document.RootElement.Clone());
                string detectorPath = DiagnosticOutputPaths.Child(Path.Combine(root, block), $"detector-only-{index:00}-{truth.Id}.json");
                using JsonDocument detector = JsonDocument.Parse(File.ReadAllText(detectorPath));
                detections.Add(detector.RootElement.Clone());
            }
            bool candidateStable = new[] { "raw", "neural", "result", "preparedPixels" }.All(field =>
                JsonElement.DeepEquals(readings[1].GetProperty(field), readings[2].GetProperty(field))) &&
                JsonElement.DeepEquals(detections[1].GetProperty("boxes"), detections[2].GetProperty("boxes"));
            stable &= candidateStable;
            rows.Add(new { truth.Id, candidateStable,
                exactTruthLines = readings.Select((reading, ordinal) => new { block = BlockOrder[ordinal],
                    matches = truth.Lines.Select(line => reading.GetProperty("result").GetProperty("text").GetString()!
                        .Contains(line.Text, StringComparison.Ordinal)).ToArray() }).ToArray(),
                interpretation = "Detection and final-text quality are separate per-input observations. Light loss rejects unconditional inversion, not the dark-polarity hypothesis. No selector or production adoption is tested." });
        }
        return (rows, stable);
    }

    private static void ValidateProductProvenance(Evidence.FileReceipt[] sources, Evidence.FileReceipt[] binaries)
    {
        JsonElement prior = ReadReference("input-preflight.json");
        foreach (JsonElement file in prior.GetProperty("sources").EnumerateArray())
        {
            string role = file.GetProperty("role").GetString()!;
            if (!role.StartsWith("src/", StringComparison.Ordinal) && role != "Directory.Packages.props") continue;
            Evidence.FileReceipt current = Assert.Single(sources, item => item.Role == role);
            Assert.Equal(file.GetProperty("sha256").GetString(), current.Sha256);
        }
        foreach (JsonElement file in prior.GetProperty("binaries").EnumerateArray())
        {
            string role = file.GetProperty("role").GetString()!;
            if (role == "MyCapture.App.Tests.dll") continue; // The added fixture necessarily changes this assembly.
            Evidence.FileReceipt current = Assert.Single(binaries, item => item.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(file.GetProperty("sha256").GetString(), current.Sha256);
            Assert.Equal(file.GetProperty("bytes").GetInt64(), current.Bytes);
        }
    }

    private static void ValidateModels(Evidence.FileReceipt[] resolved)
    {
        foreach (JsonElement file in ReadReference("report.json").GetProperty("resolved").EnumerateArray())
        {
            Evidence.FileReceipt actual = Assert.Single(resolved, item => item.Role == file.GetProperty("role").GetString());
            Assert.Equal(file.GetProperty("sha256").GetString(), actual.Sha256);
            Assert.Equal(file.GetProperty("bytes").GetInt64(), actual.Bytes);
        }
    }

    private static void ValidateSession(Evidence.SessionReceipt session)
    {
        JsonElement actual = JsonSerializer.SerializeToElement(session, Data.Json);
        JsonElement reference = ReadReference("report.json").GetProperty("session");
        foreach (string key in new[] { "intraThreads", "interThreads", "executionMode", "optimization",
            "cpuArena", "memoryPattern", "recognitionDop", "disableSpinning" })
            Assert.True(JsonElement.DeepEquals(actual.GetProperty(key), reference.GetProperty(key)));
    }

    private static void ValidateNative(Evidence.FileReceipt[] native)
    {
        foreach (JsonElement file in ReadReference("report.json").GetProperty("native").EnumerateArray())
        {
            Evidence.FileReceipt actual = Assert.Single(native, item => item.Role.Equals(file.GetProperty("role").GetString(), StringComparison.OrdinalIgnoreCase));
            Assert.Equal(file.GetProperty("sha256").GetString(), actual.Sha256);
            Assert.Equal(file.GetProperty("bytes").GetInt64(), actual.Bytes);
        }
    }

    private static void AssertOriginalReading(Reading value, Input input)
    {
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(value.Raw, Data.Json), input.Reference.GetProperty("raw")),
            "A returned raw score/text/geometry changed from both stable stage32 repetitions.");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Evidence.Result(value.Neural!), Data.Json), input.Reference.GetProperty("neural")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Evidence.Result(value.Result!), Data.Json), input.Reference.GetProperty("result")));
    }

    private static byte[] Invert(byte[] pixels)
    {
        Assert.Equal(0, pixels.Length % 4);
        byte[] inverted = (byte[])pixels.Clone();
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.True(pixels[index + channel] <= alpha, "Invalid premultiplied source channel.");
                inverted[index + channel] = (byte)(alpha - pixels[index + channel]);
            }
            Assert.Equal(alpha, inverted[index + 3]);
        }
        return inverted;
    }

    private static string PreparedSkiaHash(BitmapSource bitmap)
    {
        BitmapSource prepared = ImageCodec.StretchContrastForRecognition(bitmap);
        Assert.Same(bitmap, prepared); // Stop if this input triggers an additional contrast transform.
        (BitmapSource pixels, SKAlphaType alpha) = NeuralOcrEngine.ForSkia(prepared);
        Assert.Equal(SKAlphaType.Premul, alpha);
        using SKBitmap skia = NeuralOcrEngine.CopyToSkia(pixels, alpha, new Int32Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
        byte[] bytes = new byte[skia.ByteCount]; Marshal.Copy(skia.GetPixels(), bytes, 0, bytes.Length);
        return Hash(bytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void RunBlock(string root, string models, string block, bool invert, int blockIndex)
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
            sources = [.. Evidence.Sources(), Evidence.File("tests/MyCapture.App.Tests/OcrSmallPolarityDiagnosticTests.cs",
                Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrSmallPolarityDiagnosticTests.cs"))];
            binaries = Evidence.Binaries(); originals = Evidence.Catalog(models);
            ValidateProductProvenance(sources, binaries);
            // Do not call Data.Read or enumerate the corpus. It decodes all300, including held-out.
            // Only these four pinned development raw files and their four PNGs are opened.
            inputs = Baselines.Select(value => LoadInput(root, value.File, value.Sha256, invert)).ToArray();
            Assert.Equal(4, inputs.Select(input => input.Sample.Id).Distinct(StringComparer.Ordinal).Count());
            string profile = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(Path.GetDirectoryName(root)!, "isolated-profile"));
            AppPaths paths = AppPaths.CreateForRoot(profile);
            DiagnosticOutputPaths.Create(paths.OcrModelsRoot);
            foreach (Evidence.FileReceipt file in originals)
            {
                string target = DiagnosticOutputPaths.Child(paths.OcrModelsRoot, Path.GetFileName(file.Path));
                if (!File.Exists(target)) File.Copy(file.Path, target, overwrite: false);
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
                    ValidateModels(resolved);
                    ValidateSession(session);
                    native = Evidence.LoadedNative();
                    ValidateNative(native);
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
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(languages, Data.Json),
                ReadReference("input-preflight.json").GetProperty("languages")));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(NeuralOcrEngine.CreateOptions(), Data.Json),
                ReadReference("input-preflight.json").GetProperty("rapidOptions")));
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
                block, invert, blockIndex, recognitionCallsPlanned = 4, separateDetectorCallsPlanned = 4,
                rapidOptions = NeuralOcrEngine.CreateOptions(), requestedSpinEntries = new Dictionary<string, string>(),
                productConfigEntries = NeuralOcrEngine.WholeSessionConfigEntries(disableSpinning: false, runtimeSpinning: false),
                heldOutAccess = "None: only four explicitly named development raw files/PNGs are read.",
                original19 = "NOT_RUN: absent historical corpus", purpose = "Only Pbgra C = A - C changes; no product defaults or thresholds change",
            });
            foreach (Input input in inputs)
            {
                active = new Reading(input.Sample.Id, blockIndex);
                recognition.Add(active);
                TimeSpan cpuStarted = Environment.CpuUsage.TotalTime;
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
                    Assert.Equal(0, active.WindowsCount);
                    Assert.Null(active.Windows);
                    Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Evidence.Result(active.Neural!), Data.Json),
                        JsonSerializer.SerializeToElement(Evidence.Result(active.Result!), Data.Json)));
                    if (!invert) AssertOriginalReading(active, input);
                    if (active.Windows is not null) Assert.True(active.Windows.Status is OcrStatus.Success or OcrStatus.NoText);
                }
                catch (Exception error) { active.Error = Error(error); throw; }
                finally
                {
                    active.WallMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    active.ProcessCpuMs = (Environment.CpuUsage.TotalTime - cpuStarted).TotalMilliseconds;
                    Data.Write(root, $"recognition-{recognition.Count - 1:00}-{input.Sample.Id}.json", RecognitionEvidence(active, input));
                }
            }
            Assert.Equal(1, initializations); Assert.Equal(0, rowCalls); Assert.NotNull(session);
            Assert.False(session!.DisableSpinning);
            native = Evidence.LoadedNative();
            ValidateNative(native);
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
            foreach (Input input in inputs)
            {
                var observation = new Detection(input.Sample.Id, blockIndex);
                detections.Add(observation);
                TimeSpan cpuStarted = Environment.CpuUsage.TotalTime;
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
                        if (!invert) Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(observation.Boxes, Data.Json),
                            input.ReferenceDetector.GetProperty("boxes")), "Original detector no longer matches stage32.");
                        Assert.Equal(input.PreparedJson, JsonSerializer.Serialize(PixelEvidence(input.Bitmap, prepared, bitmap), Data.Json));
                    }, cancellation.Token).GetAwaiter().GetResult();
                }
                catch (Exception error) { observation.Error = Error(error); throw; }
                finally
                {
                    observation.WallMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    observation.ProcessCpuMs = (Environment.CpuUsage.TotalTime - cpuStarted).TotalMilliseconds;
                    Data.Write(root, $"detector-only-{detections.Count - 1:00}-{input.Sample.Id}.json", new
                    {
                        input.Sample.Id, block, invert, repeat = blockIndex, observation.Pixels, observation.Boxes, observation.Error, observation.WallMs, observation.ProcessCpuMs,
                        stage = "Separate public DetectBoxes run: post detector contour/box filters; source coordinates; no recognition",
                        sameInferenceAsRecognition = false, rapidOptions = NeuralOcrEngine.CreateOptions(),
                        productDisposedBeforeDetector, productDisposedAt, detectorInitializedAt,
                    });
                }
            }
            Assert.Equal(4, recognition.Count); Assert.Equal(4, detections.Count);
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
                Evidence.Equal(sources, [.. Evidence.Sources(), Evidence.File("tests/MyCapture.App.Tests/OcrSmallPolarityDiagnosticTests.cs",
                    Path.Combine(Data.RepositoryRoot(), "tests/MyCapture.App.Tests/OcrSmallPolarityDiagnosticTests.cs"))]);
                Evidence.Equal(binaries, Evidence.Binaries()); Evidence.Equal(originals, Evidence.Catalog(models));
                foreach (Evidence.FileReceipt file in copied.Concat(resolved).Concat(native))
                    Assert.Equal(file, Evidence.File(file.Role, file.Path));
                foreach (Input input in inputs)
                {
                    Assert.Equal(input.RawReceipt, Evidence.File(input.RawReceipt.Role, input.RawReceipt.Path));
                    Assert.Equal(input.PngReceipt, Evidence.File(input.PngReceipt.Role, input.PngReceipt.Path));
                    Assert.Equal(input.Sample.PngSha256, Data.FileHash(input.CopiedPng));
                    Assert.Equal(input.Sample.PixelSha256, Data.PixelHash(input.OriginalBitmap));
                    Assert.Equal(input.RequestPixelHash, Data.PixelHash(input.Bitmap));
                    Assert.Equal(input.SkiaInputHash, PreparedSkiaHash(input.Bitmap));
                }
                integrity = true;
            }
            catch (Exception error) { failure ??= error; issues.Add("Integrity: " + error); }
            Data.Write(root, "report.json", new
            {
                executionStatus = failure is null && integrity && recognition.Count == 4 && detections.Count == 4 ? "OBSERVED" : "INCOMPLETE_OR_ERROR",
                releaseGate = "NOT_EVALUATED", block, invert, blockIndex, started, finished = DateTimeOffset.UtcNow, integrity,
                recognitionAttempts = recognition.Count, detectorOnlyAttempts = detections.Count,
                completedRecognitions = recognition.Count(item => item.Result is not null && item.Error is null),
                completedDetectorCalls = detections.Count(item => item.Boxes is not null && item.Error is null),
                initializations, rowCalls, session, resolved, native, productDisposedBeforeDetector,
                productDisposedAt, detectorInitializedAt, issues,
                naturalUnload = "NOT_MEASURED: explicit Dispose separates the two stage instances.",
                scope = "One ABBA block: four fixed development inputs, four CaptureOcr and four separate DetectBoxes; no held-out access.",
            });
        }
        Assert.True(failure is null && integrity && recognition.Count == 4 && detections.Count == 4,
            "Incomplete small stage evidence: " + root + "\n" + string.Join('\n', issues));
    }

    private static Input LoadInput(string root, string rawName, string rawSha, bool invert)
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
        BitmapSource originalBitmap = bitmap;
        int inputIndex = Array.FindIndex(Baselines, entry => entry.File == rawName);
        JsonElement reference = ReadReference($"recognition-{inputIndex:00}-{sample.Id}.json");
        JsonElement referenceAgain = ReadReference($"recognition-{inputIndex + 4:00}-{sample.Id}.json");
        JsonElement referenceDetector = ReadReference($"detector-only-{inputIndex:00}-{sample.Id}.json");
        JsonElement detectorAgain = ReadReference($"detector-only-{inputIndex + 4:00}-{sample.Id}.json");
        foreach (string field in new[] { "raw", "neural", "result", "preparedPixels" })
            Assert.True(JsonElement.DeepEquals(reference.GetProperty(field), referenceAgain.GetProperty(field)));
        Assert.True(JsonElement.DeepEquals(referenceDetector.GetProperty("boxes"), detectorAgain.GetProperty("boxes")));
        Assert.Equal(0, reference.GetProperty("windowsCount").GetInt32());
        Assert.Equal(0, referenceAgain.GetProperty("windowsCount").GetInt32());
        BitmapSource originalPrepared = ImageCodec.StretchContrastForRecognition(originalBitmap);
        Assert.Same(originalBitmap, originalPrepared);
        (BitmapSource originalPixels, SKAlphaType originalAlpha) = NeuralOcrEngine.ForSkia(originalPrepared);
        Assert.Equal(SKAlphaType.Premul, originalAlpha);
        Assert.Equal(PixelFormats.Pbgra32, originalPixels.Format);
        int stride = checked(sample.Width * 4);
        byte[] originalBytes = new byte[checked(stride * sample.Height)];
        originalPixels.CopyPixels(originalBytes, stride, 0);
        string originalSkiaHash = Hash(originalBytes);
        Assert.Equal(reference.GetProperty("preparedPixels").GetProperty("skiaBytesSha256").GetString(), originalSkiaHash);
        byte[] inverse = Invert(originalBytes);
        Assert.Equal(originalBytes, Invert(inverse));
        Assert.NotEqual(originalSkiaHash, Hash(inverse));
        byte[] chosen = invert ? inverse : originalBytes;
        if (invert)
        {
            bitmap = BitmapSource.Create(sample.Width, sample.Height, originalBitmap.DpiX, originalBitmap.DpiY,
                PixelFormats.Pbgra32, null, chosen, stride);
            bitmap.Freeze();
        }
        Assert.Equal(originalBitmap.PixelWidth, bitmap.PixelWidth); Assert.Equal(originalBitmap.PixelHeight, bitmap.PixelHeight);
        Assert.Equal(originalBitmap.DpiX, bitmap.DpiX); Assert.Equal(originalBitmap.DpiY, bitmap.DpiY);
        Assert.Equal(Hash(chosen), PreparedSkiaHash(bitmap));
        string pixelFile = DiagnosticOutputPaths.Child(root, sample.Id + "-request.pbgra");
        using (var stream = new FileStream(pixelFile, FileMode.CreateNew, FileAccess.Write)) stream.Write(chosen);
        string previewFile = DiagnosticOutputPaths.Child(root, sample.Id + "-request.png");
        var pngEncoder = new PngBitmapEncoder(); pngEncoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(previewFile, FileMode.CreateNew, FileAccess.Write)) pngEncoder.Save(stream);
        string requestPixelHash = Data.PixelHash(bitmap);
        var settings = new OcrSettings { Quality = OcrQuality.Accurate, PreferredLanguages = sample.Languages.ToList() };
        OcrQualityProfile.Synchronize(settings);
        OcrRequest request = OcrRequestFactory.FromBitmap(bitmap, settings);
        Assert.True(request.UseNeuralModel && request.EnhanceContrast && request.SearchRotatedOrientations);
        Assert.False(request.LocalCorrection || request.UseSuperResolution || request.RecoverLatinIntrusions);
        BitmapSource prepared = ImageCodec.StretchContrastForRecognition(bitmap);
        (BitmapSource pixels, SKAlphaType alpha) = NeuralOcrEngine.ForSkia(prepared);
        using SKBitmap skia = NeuralOcrEngine.CopyToSkia(pixels, alpha, new Int32Rect(0, 0, pixels.PixelWidth, pixels.PixelHeight));
        object evidence = PixelEvidence(bitmap, prepared, skia);
        Data.Write(root, sample.Id + "-input.json", new { sample, rawReceipt, pngReceipt, copied, invert,
            originalPixelSha256 = sample.PixelSha256, originalSkiaHash, requestPixelHash, transformedSkiaSha256 = Hash(chosen),
            alphaUnchanged = true, inversionInvolutionExact = true, pixelFile, previewFile,
            pixelFileSha256 = Data.FileHash(pixelFile), previewFileSha256 = Data.FileHash(previewFile), pixels = evidence });
        return new(sample, bitmap, request, rawReceipt, pngReceipt, copied, baseline.RootElement.Clone(), JsonSerializer.Serialize(evidence, Data.Json),
            originalBitmap, requestPixelHash, Hash(chosen), reference, referenceDetector);
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
        result = value.Result is null ? null : Evidence.Result(value.Result), value.Error, value.NeuralError, value.WindowsError, value.WallMs, value.ProcessCpuMs,
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
        Evidence.FileReceipt PngReceipt, string CopiedPng, JsonElement Baseline, string PreparedJson, BitmapSource OriginalBitmap,
        string RequestPixelHash, string SkiaInputHash, JsonElement Reference, JsonElement ReferenceDetector);
    private sealed class Reading(string id, int repeat)
    {
        internal string Id { get; } = id;
        internal int Repeat { get; } = repeat;
        internal ProductResult? Result, Neural, Windows;
        internal object? Raw, MappingFacts, Error, NeuralError, WindowsError;
        internal Box[]? RawBoxes;
        internal int RawCount, NeuralCount, WindowsCount;
        internal double WallMs, ProcessCpuMs;
    }
    private sealed class Detection(string id, int repeat)
    {
        internal string Id { get; } = id;
        internal int Repeat { get; } = repeat;
        internal Box[]? Boxes;
        internal object? Pixels, Error;
        internal double WallMs, ProcessCpuMs;
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

internal sealed class OcrSmallPolarityFactAttribute : FactAttribute
{
    public OcrSmallPolarityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrSmallPolarityDiagnosticTests.RunVariable)))
            Skip = "Opt-in development-only four-input stage observation with preserved original pixels and outputs.";
    }
}
