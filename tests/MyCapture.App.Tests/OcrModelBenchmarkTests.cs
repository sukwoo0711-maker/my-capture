using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using RapidOcrNet;
using SkiaSharp;
using Xunit;
using ProductResult = MyCapture.Ocr.OcrResult;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in, local-only model experiment. No application, real profile, clipboard, model-store
/// download method, or existing test cleanup is used. All generated evidence stays in the
/// explicitly selected output directory; this test deliberately retains it for inspection.
/// </summary>
public sealed class OcrModelBenchmarkTests
{
    private const string DefaultModels = @"C:\GitRepositories\my-capture-ux461-ocrbench\artifacts\ocr-bench\models";
    private const int Repetitions = 3;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void BenchmarkLocalOcrModels_WhenExplicitlyEnabled()
    {
        // Keep this guard before even constructing WPF, WinRT, models, or filesystem state.
        string? requestedOutput = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH");
        if (string.IsNullOrWhiteSpace(requestedOutput)) return;

        string output = SafeOutputRoot(requestedOutput);
        string models = Path.GetFullPath(Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH_MODELS")
            ?? DefaultModels);
        Directory.CreateDirectory(output);
        StaTestHost.Run(() => Run(output, models));
    }

    private static void Run(string output, string models)
    {
        VerifyMetricExamples();
        List<Sample> corpus = CreateCorpus(output);
        Assert.True(corpus.Count >= 48);
        var report = new BenchmarkReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            OutputDirectory = output,
            ModelsDirectory = models,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            RapidOcrAssembly = typeof(RapidOcr).Assembly.FullName ?? "",
            OnnxRuntimeAssembly = typeof(InferenceSession).Assembly.FullName ?? "",
            CorpusCount = corpus.Count,
        };
        report.Notes.AddRange([
            "Synthetic deterministic screenshots; results describe this corpus, not real-world prevalence. Fonts and PNG SHA-256 are recorded in truth.json.",
            "CER: NFC; CRLF/CR become LF; horizontal whitespace collapses to one space; trim each line and remove empty lines. LF remains one character. Micro-average total edit distance / total truth characters (may exceed 100%).",
            "Line-insensitive CER: the same normalization, then all whitespace including LF collapses to one space. Reading order is retained.",
            "Exact-line match: LCS of normalized nonempty output/truth lines divided by truth line count; also report matches / max(truth lines, output lines) to expose extra lines.",
            "Cold init is first construction of one reusable wrapper/model set per pipeline in this process; filesystem and native DLL caches are not flushed. First inference is measured separately and excluded from all warm samples. Every corpus image then has three full pipeline warm runs; first warm output supplies accuracy, repeated-output changes are counted.",
            "Warm time includes product preprocessing, SR when applicable, PNG encoding, SKBitmap decode, detection/classification/recognition and product text reconstruction. Image generation, disk writes, metrics, and model file hashing are outside timed inference.",
            "Windows uses one real WindowsOcrService/WindowsOcrRecognizer wrapper. The product creates its WinRT OcrEngine on every call, so native init is included in each W latency; wrapper construction is the reported W init. Only Korean is requested, and unavailable Korean is skipped.",
            "Accurate uses the actual contrast stretch without resizing: the neural product ignores its configured 4x factor unless LocalCorrection is true. Enhanced uses actual local correction, actual SR tiling, or actual nearest-neighbour 2x fallback capped at 4096.",
            "Enhanced variants share the small-text and low-contrast subsets, including their JPEG variants. Their detector is selected between A-cur and A-v6 by corpus CER, then warm median as tie-breaker. This is an in-sample exploratory selection, not independent validation.",
            "Working-set samples are taken every 20 ms and at inference boundaries. Peak delta is max sampled working set minus pre-init baseline; lifetime high-water increase is also recorded. Sequential shared-process measurements are order-dependent and include allocator/cache reuse; they are not isolated model memory requirements.",
            "Latency and memory in the shared sandbox may be noisy. All pipelines use product CPU options and serial image execution; no throughput parallelism is added.",
        ]);

        string current = Path.Combine(models, "current");
        string candidate = Path.Combine(models, "new");
        string classifier = Bundled(RapidOcr.DefaultClsModelPath);
        string mobile = Bundled(RapidOcr.DefaultDetModelPath);
        string recognizer = Path.Combine(current, "korean_PP-OCRv5_rec_mobile.onnx");
        string dictionary = Path.Combine(current, "ppocrv5_korean_dict.txt");
        string server = Path.Combine(current, "ch_PP-OCRv5_det_server.onnx");
        string v6 = Path.Combine(candidate, "PP-OCRv6_det_small.onnx");
        RapidOcrOptions defaults = ProductOverrides(RapidOcrOptions.Default);
        RapidOcrOptions? v6Options = GetV6Options();
        report.V6PresetAvailable = v6Options is not null;

        Execute("W", corpus, () => new WindowsRunner(), [], "Fast, Korean only", null);
        ExecuteNeural("A-cur", corpus, server, defaults, OcrQuality.Accurate);
        ExecuteNeural("A-mob", corpus, mobile, defaults, OcrQuality.Accurate);
        ExecuteNeural("A-v6", corpus, v6, defaults, OcrQuality.Accurate);
        if (v6Options is not null)
            ExecuteNeural("A-v6opt", corpus, v6, ProductOverrides(v6Options), OcrQuality.Accurate);
        else
        {
            report.Pipelines.Add(new PipelineReport
            {
                Name = "A-v6opt", Status = "skipped", Note = "RapidOcrOptions.PPOCRv6 is absent from the installed package.",
            });
            SaveReport(output, report);
        }

        PipelineReport? selected = report.Pipelines
            .Where(p => (p.Name == "A-cur" || p.Name == "A-v6") && p.Status == "completed")
            .OrderBy(p => p.Overall!.Cer).ThenBy(p => p.Overall!.WarmMedianMs).FirstOrDefault();
        if (selected is not null)
        {
            report.EnhancedDetector = selected.Name;
            string detector = selected.Name == "A-v6" ? v6 : server;
            List<Sample> difficult = corpus.Where(s => s.Truth.EnhancedSubset).ToList();
            // Run fallback first so a long SR experiment still leaves its baseline on disk.
            ExecuteNeural("E-none", difficult, detector, defaults, OcrQuality.Enhanced);
            ExecuteNeural("E-cur", difficult, detector, defaults, OcrQuality.Enhanced,
                Path.Combine(current, "realesrgan_x4.onnx"), Path.Combine(current, "real_esrgan_x4plus.data"));
            ExecuteNeural("E-v3", difficult, detector, defaults, OcrQuality.Enhanced,
                Path.Combine(candidate, "real_esrgan_general_x4v3.onnx"), Path.Combine(candidate, "real_esrgan_general_x4v3.data"));
        }
        else
        {
            foreach (string name in new[] { "E-none", "E-cur", "E-v3" })
                report.Pipelines.Add(new PipelineReport { Name = name, Status = "failed", Note = "Neither detector baseline completed; Enhanced selection is unavailable." });
        }

        report.FinishedUtc = DateTimeOffset.UtcNow;
        report.Status = report.Pipelines.Any(p => p.Status == "failed") ? "failed" : "completed";
        SaveReport(output, report);
        WriteJson(Path.Combine(output, "progress.json"), new { state = report.Status, completedUtc = report.FinishedUtc });
        Assert.True(report.Status == "completed", "Benchmark failures were retained in results.json and summary.md: " +
            string.Join("; ", report.Pipelines.Where(p => p.Status == "failed").Select(p => p.Name + ": " + p.Note)));

        void ExecuteNeural(string name, List<Sample> samples, string detector, RapidOcrOptions options,
            OcrQuality quality, string? sr = null, string? srData = null)
        {
            string[] files = sr is null
                ? [detector, classifier, recognizer, dictionary]
                : [detector, classifier, recognizer, dictionary, sr, srData!];
            Execute(name, samples,
                () => new NeuralRunner(detector, classifier, recognizer, dictionary, options, quality, sr, output),
                files, quality + (sr is null ? "; no SR" : "; product SR tiling"), DescribeOptions(options));
        }

        void Execute(string name, List<Sample> samples, Func<IRunner> factory, string[] files,
            string description, Dictionary<string, string>? options)
        {
            var pipeline = new PipelineReport { Name = name, Description = description, Options = options };
            report.Pipelines.Add(pipeline);
            IRunner? runner = null;
            try
            {
                pipeline.Models = files.Distinct(StringComparer.OrdinalIgnoreCase).Select(ModelEvidence).ToList();
                pipeline.ModelBytes = pipeline.Models.Sum(m => m.Bytes);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using var memory = new WorkingSetProbe();
                pipeline.BaselineWorkingSetBytes = memory.Baseline;
                var timer = Stopwatch.StartNew();
                try { runner = factory(); }
                finally { pipeline.InitMs = timer.Elapsed.TotalMilliseconds; memory.Sample(); }
                if (runner.UnavailableReason is not null)
                {
                    pipeline.Status = "skipped";
                    pipeline.Note = runner.UnavailableReason;
                }
                else
                {
                    SaveReport(output, report);
                    WriteProgress(name, 0, samples.Count, "cold inference");
                    timer.Restart();
                    ProductResult cold = runner.Recognize(samples[0].Bitmap);
                    pipeline.FirstInferenceMs = timer.Elapsed.TotalMilliseconds;
                    pipeline.FirstInferenceImage = samples[0].Truth.Id;
                    pipeline.FirstInferenceStatus = cold.Status.ToString();
                    if (name == "W" && cold.Status == OcrStatus.Unavailable)
                    {
                        pipeline.Status = "skipped";
                        pipeline.Note = "Windows Korean OCR could not create an engine in this host: " + cold.Message;
                        memory.Sample();
                        pipeline.SampledPeakWorkingSetBytes = memory.Peak;
                        pipeline.SampledPeakDeltaBytes = Math.Max(0, memory.Peak - memory.Baseline);
                        pipeline.LifetimePeakIncreaseBytes = memory.LifetimeIncrease;
                        return;
                    }
                    CheckResult(cold);
                    memory.Sample();
                    for (int i = 0; i < samples.Count; i++)
                    {
                        Sample sample = samples[i];
                        WriteProgress(name, i, samples.Count, sample.Truth.Id);
                        var image = new ImageMeasurement
                        {
                            Id = sample.Truth.Id, Category = sample.Truth.Category, Truth = sample.Truth.Text,
                            EnhancedSubset = sample.Truth.EnhancedSubset,
                        };
                        for (int repeat = 0; repeat < Repetitions; repeat++)
                        {
                            timer.Restart();
                            ProductResult recognized = runner.Recognize(sample.Bitmap);
                            double elapsed = timer.Elapsed.TotalMilliseconds;
                            CheckResult(recognized);
                            image.WarmMs.Add(elapsed);
                            if (repeat == 0)
                            {
                                image.Output = recognized.Text;
                                image.Status = recognized.Status.ToString();
                                image.Language = recognized.LanguageTag;
                            }
                            else if (!string.Equals(image.Output, recognized.Text, StringComparison.Ordinal))
                                image.DifferentRepeatedOutputs++;
                            memory.Sample();
                        }
                        image.Metrics = Measure(image.Truth, image.Output);
                        pipeline.Images.Add(image);
                        pipeline.Overall = Aggregate(pipeline.Images);
                        pipeline.Categories = pipeline.Images.GroupBy(s => s.Category)
                            .ToDictionary(g => g.Key, g => Aggregate(g.ToList()));
                        pipeline.SampledPeakWorkingSetBytes = memory.Peak;
                        pipeline.SampledPeakDeltaBytes = Math.Max(0, memory.Peak - memory.Baseline);
                        pipeline.LifetimePeakIncreaseBytes = memory.LifetimeIncrease;
                        // Incremental evidence survives a long-running SR model or an interrupted host.
                        WriteJson(Path.Combine(output, "results.json"), report);
                    }
                    pipeline.Status = "completed";
                }
                pipeline.SampledPeakWorkingSetBytes = memory.Peak;
                pipeline.SampledPeakDeltaBytes = Math.Max(0, memory.Peak - memory.Baseline);
                pipeline.LifetimePeakIncreaseBytes = memory.LifetimeIncrease;
            }
            catch (Exception ex)
            {
                pipeline.Status = "failed";
                pipeline.Note = ex.ToString();
            }
            finally
            {
                runner?.Dispose();
                SaveReport(output, report);
                WriteProgress(name, pipeline.Images.Count, samples.Count, pipeline.Status);
            }
        }

        void WriteProgress(string pipeline, int completedImages, int totalImages, string phase) =>
            WriteJson(Path.Combine(output, "progress.json"), new
            {
                state = "running", pipeline, completedImages, totalImages, phase, updatedUtc = DateTimeOffset.UtcNow,
            });
    }

    private static void CheckResult(ProductResult result)
    {
        if (result.Status is not (OcrStatus.Success or OcrStatus.NoText))
            throw new InvalidOperationException($"Recognition returned {result.Status}: {result.Message}");
    }

    private interface IRunner : IDisposable
    {
        string? UnavailableReason { get; }
        ProductResult Recognize(BitmapSource bitmap);
    }

    private sealed class WindowsRunner : IRunner
    {
        private readonly WindowsOcrService _service = new(NullLogger<WindowsOcrService>.Instance);
        public string? UnavailableReason => !_service.IsAvailable ? "Windows OCR is unavailable in this host."
            : !_service.SupportedLanguages.Any(s => s.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
                ? "Korean Windows OCR language pack is unavailable. Installed: " + string.Join(", ", _service.SupportedLanguages)
                : null;
        public ProductResult Recognize(BitmapSource bitmap) =>
            _service.RecognizeAsync(Request(bitmap, OcrQuality.Fast)).GetAwaiter().GetResult();
        public void Dispose() { }
    }

    private sealed class NeuralRunner : IRunner
    {
        private static readonly MethodInfo ToResult = typeof(NeuralOcrEngine).GetMethod(
            "ToResult", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(NeuralOcrEngine), "ToResult");
        private readonly RapidOcr _engine = new();
        private readonly RapidOcrOptions _options;
        private readonly OcrQuality _quality;
        private readonly SuperResolutionEngine? _sr;
        private readonly HttpClient? _offlineHttp;
        public string? UnavailableReason => null;

        public NeuralRunner(string detector, string classifier, string recognizer, string dictionary,
            RapidOcrOptions options, OcrQuality quality, string? srModel, string output)
        {
            _options = options;
            _quality = quality;
            try
            {
                _engine.InitModels(detector, classifier, recognizer, dictionary);
                if (srModel is not null)
                {
                    _offlineHttp = new HttpClient(new NoNetworkHandler());
                    var store = new OcrModelStore(AppPaths.CreateForRoot(Path.Combine(output, "isolated-profile")),
                        _offlineHttp, NullLogger<OcrModelStore>.Instance);
                    _sr = new SuperResolutionEngine(store, NullLogger<SuperResolutionEngine>.Instance);
                    FieldInfo field = typeof(SuperResolutionEngine).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException(nameof(SuperResolutionEngine), "_session");
                    var session = new InferenceSession(srModel);
                    try
                    {
                        // Product tiling has fixed names/shapes. Fail instead of silently measuring fallback.
                        if (!session.InputMetadata.TryGetValue("image", out NodeMetadata? input)
                            || !input.Dimensions.SequenceEqual(new[] { 1, 3, 128, 128 })
                            || !session.OutputMetadata.TryGetValue("upscaled_image", out NodeMetadata? result)
                            || !result.Dimensions.SequenceEqual(new[] { 1, 3, 512, 512 }))
                            throw new InvalidOperationException("SR model is incompatible with product image/upscaled_image 128-to-512 tiling.");
                        field.SetValue(_sr, session);
                    }
                    catch { session.Dispose(); throw; }
                }
            }
            catch { Dispose(); throw; }
        }

        public ProductResult Recognize(BitmapSource bitmap)
        {
            OcrRequest request = Request(bitmap, _quality);
            BitmapSource source = bitmap;
            double scale = 1.0;
            // Deliberately mirrors RecognizeOnWorker; calls the product's actual pixel operations.
            if (request.LocalCorrection)
            {
                source = ImageCodec.CorrectImageForRecognition(source);
                if (request.UseSuperResolution && _sr?.IsReady == true
                    && Math.Max(source.PixelWidth, source.PixelHeight) <= SuperResolutionEngine.MaxLongEdge)
                {
                    source = _sr.Upscale(source) ?? throw new InvalidOperationException("Selected SR unexpectedly returned no image.");
                    scale = SuperResolutionEngine.Scale;
                }
                if (scale == 1.0 && request.UpscaleFactor > 1.0)
                {
                    double cap = 4096.0 / Math.Max(1, Math.Max(source.PixelWidth, source.PixelHeight));
                    scale = cap < 1.0 ? 1.0 : Math.Min(request.UpscaleFactor, cap);
                    if (scale > 1.0) source = ImageCodec.UpscaleForRecognition(source, scale);
                }
            }
            else if (request.EnhanceContrast) source = ImageCodec.StretchContrastForRecognition(source);
            using SKBitmap decoded = SKBitmap.Decode(ImageCodec.EncodePng(source))
                ?? throw new InvalidOperationException("Prepared bitmap could not be decoded.");
            RapidOcrNet.OcrResult raw = _engine.Detect(decoded, _options, CancellationToken.None);
            return (ProductResult)ToResult.Invoke(null, [raw, scale])!;
        }

        public void Dispose()
        {
            _engine.Dispose();
            _sr?.Dispose();
            _offlineHttp?.Dispose();
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Network access is forbidden in the local OCR benchmark.");
    }

    private static OcrRequest Request(BitmapSource bitmap, OcrQuality quality)
    {
        var settings = new OcrSettings { Quality = quality, PreferredLanguages = ["ko-KR"] };
        OcrQualityProfile.Synchronize(settings);
        return OcrRequestFactory.FromBitmap(bitmap, settings);
    }

    private static RapidOcrOptions ProductOverrides(RapidOcrOptions options) => options with
    {
        ReturnWordBox = true, DoAngle = true,
        RecMaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
    };

    private static RapidOcrOptions? GetV6Options() =>
        (typeof(RapidOcrOptions).GetProperty("PPOCRv6", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
         ?? typeof(RapidOcrOptions).GetField("PPOCRv6", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)) as RapidOcrOptions;

    private static Dictionary<string, string> DescribeOptions(RapidOcrOptions options) =>
        typeof(RapidOcrOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(options), CultureInfo.InvariantCulture) ?? "null");

    private static string Bundled(string name)
    {
        MethodInfo bundled = typeof(NeuralOcrEngine).GetMethod("Bundled", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(NeuralOcrEngine), "Bundled");
        return (string?)bundled.Invoke(null, [name])
            ?? throw new FileNotFoundException("Product bundled model was not found in the build output.", name);
    }

    private static List<Sample> CreateCorpus(string output)
    {
        string images = Path.Combine(output, "images");
        Directory.CreateDirectory(images);
        var samples = new List<Sample>();
        string uiFont = SelectFont("Malgun Gothic", "Pretendard", "Segoe UI");
        string codeFont = SelectFont("Cascadia Mono", "Consolas");
        string[][] uiTexts =
        [
            ["파일 편집 보기 도움말", "새로 만들기  열기  저장"],
            ["변경 내용을 저장하시겠습니까?", "저장  취소  닫기"],
            ["알림 설정", "시작할 때 자동 실행", "다운로드 폴더 선택"],
            ["검색 결과 12개", "선택 항목 복사", "이전  다음  확인"],
        ];
        for (int text = 0; text < uiTexts.Length; text++)
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        foreach (bool dark in new[] { false, true })
            Add("korean-ui", uiTexts[text], uiFont, 11 + text, scale, dark, false, false, 300);

        string[][] codeTexts =
        [
            ["if (items[0] >= limit) {", "  total += price; // 합계 계산", "}"],
            ["var key = \"name_id\";", "map[key] = 'A'; // 이름 저장", "return (x < 10) ? x : 10;"],
            ["const rows = [1, 2, 3];", "fn({ id: 7, value: 0.25 });", "// 결과 확인: ok_done"],
        ];
        for (int text = 0; text < codeTexts.Length; text++)
        foreach (bool dark in new[] { false, true })
            Add("code", codeTexts[text], codeFont, 12, dark ? 1.25 : 1, dark, false, false, 330);

        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        foreach (bool dark in new[] { false, true })
            Add("spreadsheet", ["품목\t금액\t날짜", "용지\t1,250.50\t2026-10-23", "잉크\t23,400\t2026-11-01", "합계\t24,650.50\t2026-11-02"],
                uiFont, 12, scale, dark, false, true, 330);

        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        foreach (bool dark in new[] { false, true })
            Add("mixed-paragraph", ["Project Alpha 작업을 시작합니다.", "새 파일 12개를 local 폴더에 저장합니다.", "완료 후 Review 항목을 확인합니다."],
                uiFont, 12, scale, dark, false, false, 330);

        string[][] difficultTexts =
        [
            ["자동 저장 5분", "파일 이름: sample_01.png"],
            ["마지막 수정 2026-10-23", "총 1,250.50원"],
            ["연결 상태: Ready", "설정을 적용하시겠습니까?"],
        ];
        foreach (int fontSize in new[] { 9, 10 })
        foreach (string[] lines in difficultTexts)
        {
            Add("small-text", lines, uiFont, fontSize, 1, false, false, false, 240);
            Add("low-contrast", lines, uiFont, fontSize, 1, false, true, false, 240);
        }

        // One JPEG-70 derivative of each category, preserving exactly the same ground truth.
        foreach (Sample original in samples.GroupBy(s => s.Truth.Category).Select(g => g.First()).ToArray())
        {
            using var encoded = new MemoryStream();
            var jpeg = new JpegBitmapEncoder { QualityLevel = 70 };
            jpeg.Frames.Add(BitmapFrame.Create(original.Bitmap));
            jpeg.Save(encoded);
            encoded.Position = 0;
            BitmapSource compressed = BitmapDecoder.Create(encoded, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            compressed.Freeze();
            TruthImage truth = original.Truth with
            {
                Id = $"{samples.Count + 1:D3}-jpeg70-{original.Truth.Category}", Category = "jpeg70",
                SourceCategory = original.Truth.Category, JpegQuality = 70,
            };
            Save(compressed, truth);
        }
        WriteJson(Path.Combine(output, "truth.json"), new
        {
            schemaVersion = 1, rendering = "WPF FormattedText, TextFormattingMode.Display, grayscale antialiasing, 96 DPI output; em size multiplied by capture scale; no random data",
            fonts = new { uiFont, codeFont }, images = samples.Select(s => s.Truth).ToArray(),
        });
        return samples;

        void Add(string category, string[] lines, string font, double fontSize, double scale, bool dark,
            bool lowContrast, bool table, double logicalWidth)
        {
            int width = (int)Math.Ceiling(logicalWidth * scale);
            double lineHeight = Math.Ceiling(fontSize * 1.7);
            double logicalHeight = 20 + lines.Length * lineHeight;
            int height = (int)Math.Ceiling(logicalHeight * scale);
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);
            Brush background = new SolidColorBrush(dark ? Color.FromRgb(32, 34, 37) : lowContrast ? Color.FromRgb(238, 238, 238) : Colors.White);
            Brush foreground = new SolidColorBrush(dark ? Color.FromRgb(232, 234, 236) : lowContrast ? Color.FromRgb(174, 174, 174) : Color.FromRgb(30, 32, 34));
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                for (int row = 0; row < lines.Length; row++)
                {
                    string[] cells = table ? lines[row].Split('\t') : [lines[row]];
                    double[] columns = table ? [10, 94, 206] : [10];
                    for (int column = 0; column < cells.Length; column++)
                    {
                        var formatted = new FormattedText(cells[column], CultureInfo.GetCultureInfo("ko-KR"),
                            FlowDirection.LeftToRight, new Typeface(font), fontSize * scale, foreground,
                            null, TextFormattingMode.Display, 1.0);
                        Assert.True(formatted.WidthIncludingTrailingWhitespace <= (logicalWidth - columns[column] - 6) * scale,
                            "Corpus text must fit without clipping: " + cells[column]);
                        dc.DrawText(formatted, new Point(columns[column] * scale, (10 + row * lineHeight) * scale));
                    }
                }
                if (table)
                {
                    var grid = new Pen(new SolidColorBrush(dark ? Color.FromRgb(78, 80, 83) : Color.FromRgb(205, 210, 215)), 1);
                    for (int row = 0; row <= lines.Length; row++)
                        dc.DrawLine(grid, new Point(5 * scale, (7 + row * lineHeight) * scale),
                            new Point((logicalWidth - 5) * scale, (7 + row * lineHeight) * scale));
                    foreach (double x in new[] { 5.0, 88, 200, logicalWidth - 5 })
                        dc.DrawLine(grid, new Point(x * scale, 7 * scale), new Point(x * scale, (7 + lines.Length * lineHeight) * scale));
                }
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            Save(bitmap, new TruthImage
            {
                Id = $"{samples.Count + 1:D3}-{category}", Category = category, SourceCategory = category,
                Text = string.Join('\n', lines.Select(l => l.Replace('\t', ' '))), Lines = lines.Select(l => l.Replace('\t', ' ')).ToArray(),
                Font = font, FontSize = fontSize, Scale = scale, Theme = dark ? "dark" : "light", LowContrast = lowContrast,
                EnhancedSubset = category is "small-text" or "low-contrast", Width = width, Height = height,
            });
        }

        void Save(BitmapSource bitmap, TruthImage truth)
        {
            string imagePath = Path.Combine(images, truth.Id + ".png");
            ImageCodec.SavePng(bitmap, imagePath);
            using var stream = File.OpenRead(imagePath);
            truth = truth with { Png = "images/" + truth.Id + ".png", PngSha256 = Convert.ToHexString(SHA256.HashData(stream)) };
            samples.Add(new Sample(truth, bitmap));
        }
    }

    private static string SelectFont(params string[] candidates)
    {
        HashSet<string> installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(installed.Contains)
            ?? throw new InvalidOperationException("None of the required corpus fonts is installed: " + string.Join(", ", candidates));
    }

    private static string NormalizeLines(string value) => string.Join('\n', value.Normalize(NormalizationForm.FormC)
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
        .Select(line => Regex.Replace(line, @"[^\S\n]+", " ").Trim()).Where(line => line.Length > 0));

    private static TextMetrics Measure(string truth, string output)
    {
        string expected = NormalizeLines(truth), actual = NormalizeLines(output);
        string flatExpected = Regex.Replace(expected, @"\s+", " "), flatActual = Regex.Replace(actual, @"\s+", " ");
        string[] expectedLines = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] actualLines = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int[] previous = new int[actualLines.Length + 1], current = new int[actualLines.Length + 1];
        foreach (string line in expectedLines)
        {
            for (int j = 1; j <= actualLines.Length; j++)
                current[j] = line == actualLines[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            (previous, current) = (current, previous);
            Array.Clear(current);
        }
        return new TextMetrics
        {
            Edits = Levenshtein(expected, actual), TruthCharacters = expected.Length,
            LineInsensitiveEdits = Levenshtein(flatExpected, flatActual), LineInsensitiveTruthCharacters = flatExpected.Length,
            MatchedLines = previous[actualLines.Length], TruthLines = expectedLines.Length, OutputLines = actualLines.Length,
        };
    }

    private static int Levenshtein(string expected, string actual)
    {
        int[] previous = Enumerable.Range(0, actual.Length + 1).ToArray(), current = new int[actual.Length + 1];
        for (int i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[actual.Length];
    }

    private static void VerifyMetricExamples()
    {
        Assert.Equal(3, Levenshtein("kitten", "sitting"));
        Assert.Equal(0, Measure("한글", "한글").Edits);
        Assert.Equal(0, Measure("a  b\r\nc", "a b\nc").Edits);
        Assert.Equal(1, Measure("a\nb", "a b").Edits);
        Assert.Equal(0, Measure("a\nb", "a b").LineInsensitiveEdits);
        Assert.Equal(2, Measure("a\nb\nc", "a\nc").MatchedLines);
        Assert.Equal(1, Measure("a", "a\na").MatchedLines);
    }

    private static AggregateMetrics Aggregate(List<ImageMeasurement> images)
    {
        double[] times = images.SelectMany(i => i.WarmMs).OrderBy(t => t).ToArray();
        int edits = images.Sum(i => i.Metrics.Edits), chars = images.Sum(i => i.Metrics.TruthCharacters);
        int flatEdits = images.Sum(i => i.Metrics.LineInsensitiveEdits), flatChars = images.Sum(i => i.Metrics.LineInsensitiveTruthCharacters);
        int matches = images.Sum(i => i.Metrics.MatchedLines), lines = images.Sum(i => i.Metrics.TruthLines);
        return new AggregateMetrics
        {
            Images = images.Count, Edits = edits, TruthCharacters = chars, Cer = edits / (double)Math.Max(1, chars),
            LineInsensitiveCer = flatEdits / (double)Math.Max(1, flatChars),
            ExactLineMatchRate = matches / (double)Math.Max(1, lines),
            ExtraLinePenalizedMatchRate = matches / (double)Math.Max(1, images.Sum(i => Math.Max(i.Metrics.TruthLines, i.Metrics.OutputLines))),
            WarmMedianMs = Percentile(times, 0.5), WarmP95Ms = Percentile(times, 0.95),
            WarmMeasurements = times.Length, DifferentRepeatedOutputs = images.Sum(i => i.DifferentRepeatedOutputs),
        };
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        double position = (sorted.Length - 1) * fraction;
        int left = (int)Math.Floor(position), right = (int)Math.Ceiling(position);
        return sorted[left] + (sorted[right] - sorted[left]) * (position - left);
    }

    private sealed class WorkingSetProbe : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly object _sync = new();
        private readonly Timer _timer;
        private readonly long _initialLifetimePeak;
        public long Baseline { get; }
        public long Peak { get; private set; }
        public long LifetimeIncrease { get; private set; }
        public WorkingSetProbe()
        {
            _process.Refresh();
            Baseline = Peak = _process.WorkingSet64;
            _initialLifetimePeak = _process.PeakWorkingSet64;
            _timer = new Timer(_ => Sample(), null, 20, 20);
        }
        public void Sample()
        {
            lock (_sync)
            {
                _process.Refresh();
                Peak = Math.Max(Peak, _process.WorkingSet64);
                LifetimeIncrease = Math.Max(0, _process.PeakWorkingSet64 - _initialLifetimePeak);
            }
        }
        public void Dispose()
        {
            using var finished = new ManualResetEvent(false);
            if (_timer.Dispose(finished)) finished.WaitOne();
            Sample();
            _process.Dispose();
        }
    }

    private static ModelFile ModelEvidence(string path)
    {
        using var stream = File.OpenRead(path);
        return new ModelFile(path, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string SafeOutputRoot(string requested)
    {
        string result = Path.GetFullPath(requested);
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            string profile = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(folder), "MyCapture"));
            if (string.Equals(result.TrimEnd(Path.DirectorySeparatorChar), profile, StringComparison.OrdinalIgnoreCase)
                || result.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Benchmark output must not use the real MyCapture profile.");
        }
        return result;
    }

    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));

    private static void SaveReport(string output, BenchmarkReport report)
    {
        WriteJson(Path.Combine(output, "results.json"), report);
        var markdown = new StringBuilder("# Local OCR model benchmark\n\n");
        markdown.AppendLine($"State: **{report.Status}**. Images: {report.CorpusCount}. Enhanced detector: {report.EnhancedDetector ?? "pending"}.");
        markdown.AppendLine($"\nStarted UTC: {report.StartedUtc:O}. Finished UTC: {report.FinishedUtc:O}.");
        markdown.AppendLine("\n## Method\n");
        foreach (string note in report.Notes) markdown.AppendLine("- " + note);
        markdown.AppendLine("\n## Overall\n\n| Pipeline | State | Images | CER | Line-insensitive CER | Exact lines | Extra-line-penalized |\n|---|---|---:|---:|---:|---:|---:|");
        foreach (PipelineReport pipeline in report.Pipelines)
        {
            AggregateMetrics? a = pipeline.Overall;
            markdown.AppendLine($"| {pipeline.Name} | {pipeline.Status} | {a?.Images ?? 0} | {Pct(a?.Cer)} | {Pct(a?.LineInsensitiveCer)} | {Pct(a?.ExactLineMatchRate)} | {Pct(a?.ExtraLinePenalizedMatchRate)} |");
        }
        markdown.AppendLine("\n## Category accuracy and warm latency\n\n| Pipeline | Category | Images | CER | Line-insensitive CER | Exact lines | Median ms | p95 ms |\n|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (PipelineReport pipeline in report.Pipelines)
        foreach ((string category, AggregateMetrics a) in pipeline.Categories.OrderBy(p => p.Key, StringComparer.Ordinal))
            markdown.AppendLine($"| {pipeline.Name} | {category} | {a.Images} | {Pct(a.Cer)} | {Pct(a.LineInsensitiveCer)} | {Pct(a.ExactLineMatchRate)} | {Num(a.WarmMedianMs)} | {Num(a.WarmP95Ms)} |");
        markdown.AppendLine("\n## Latency\n\n| Pipeline | Init ms | First inference ms | Warm median ms | Warm p95 ms | Warm runs | Changed repeats |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (PipelineReport p in report.Pipelines)
            markdown.AppendLine($"| {p.Name} | {Num(p.InitMs)} | {Num(p.FirstInferenceMs)} | {Num(p.Overall?.WarmMedianMs)} | {Num(p.Overall?.WarmP95Ms)} | {p.Overall?.WarmMeasurements ?? 0} | {p.Overall?.DifferentRepeatedOutputs ?? 0} |");
        markdown.AppendLine("\n## Memory and disk (MiB)\n\n| Pipeline | Pre-init working set | Sampled peak | Sampled peak delta | Lifetime high-water increase | Model files |\n|---|---:|---:|---:|---:|---:|");
        foreach (PipelineReport p in report.Pipelines)
            markdown.AppendLine($"| {p.Name} | {MiB(p.BaselineWorkingSetBytes)} | {MiB(p.SampledPeakWorkingSetBytes)} | {MiB(p.SampledPeakDeltaBytes)} | {MiB(p.LifetimePeakIncreaseBytes)} | {(p.Name == "W" ? "OS-managed, unknown" : MiB(p.ModelBytes))} |");
        markdown.AppendLine("\n## Difficult-subset comparison\n\nThe same image IDs are used below, including the two matching JPEG derivatives.\n\n| Pipeline | Images | CER | Line-insensitive CER | Exact lines | Warm median ms | Warm p95 ms |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (PipelineReport p in report.Pipelines.Where(p => p.Images.Any(i => i.EnhancedSubset)))
        {
            AggregateMetrics a = Aggregate(p.Images.Where(i => i.EnhancedSubset).ToList());
            markdown.AppendLine($"| {p.Name} | {a.Images} | {Pct(a.Cer)} | {Pct(a.LineInsensitiveCer)} | {Pct(a.ExactLineMatchRate)} | {Num(a.WarmMedianMs)} | {Num(a.WarmP95Ms)} |");
        }
        foreach (PipelineReport p in report.Pipelines)
        {
            markdown.AppendLine($"\n## {p.Name}: ten worst images\n");
            if (p.Note is not null) markdown.AppendLine("```text\n" + p.Note + "\n```\n");
            foreach (ImageMeasurement i in p.Images.OrderByDescending(i => i.Metrics.Cer).ThenBy(i => i.Id, StringComparer.Ordinal).Take(10))
            {
                markdown.AppendLine($"### {i.Id} — CER {Pct(i.Metrics.Cer)}\n\n[Image](images/{i.Id}.png)\n\nTruth:\n```text\n{i.Truth}\n```\n\nOutput:\n```text\n{i.Output}\n```\n");
            }
        }
        File.WriteAllText(Path.Combine(output, "summary.md"), markdown.ToString(), new UTF8Encoding(false));
    }

    private static string Pct(double? value) => value is null ? "—" : (value.Value * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
    private static string Num(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    private static string MiB(long value) => (value / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

    private sealed record Sample(TruthImage Truth, BitmapSource Bitmap);
    private sealed record TruthImage
    {
        public string Id { get; init; } = "";
        public string Category { get; init; } = "";
        public string SourceCategory { get; init; } = "";
        public string Text { get; init; } = "";
        public string[] Lines { get; init; } = [];
        public string Font { get; init; } = "";
        public double FontSize { get; init; }
        public double Scale { get; init; }
        public string Theme { get; init; } = "";
        public bool LowContrast { get; init; }
        public bool EnhancedSubset { get; init; }
        public int? JpegQuality { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string Png { get; init; } = "";
        public string PngSha256 { get; init; } = "";
    }
    private sealed class BenchmarkReport
    {
        public int SchemaVersion => 1;
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public string OutputDirectory { get; init; } = "";
        public string ModelsDirectory { get; init; } = "";
        public string Runtime { get; init; } = "";
        public string OperatingSystem { get; init; } = "";
        public int ProcessorCount { get; init; }
        public string RapidOcrAssembly { get; init; } = "";
        public string OnnxRuntimeAssembly { get; init; } = "";
        public int CorpusCount { get; init; }
        public int WarmRepetitionsPerImage => Repetitions;
        public bool V6PresetAvailable { get; set; }
        public string? EnhancedDetector { get; set; }
        public List<string> Notes { get; } = [];
        public List<PipelineReport> Pipelines { get; } = [];
    }
    private sealed class PipelineReport
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Status { get; set; } = "running";
        public string? Note { get; set; }
        public Dictionary<string, string>? Options { get; init; }
        public List<ModelFile> Models { get; set; } = [];
        public long ModelBytes { get; set; }
        public double? InitMs { get; set; }
        public double? FirstInferenceMs { get; set; }
        public string? FirstInferenceImage { get; set; }
        public string? FirstInferenceStatus { get; set; }
        public long BaselineWorkingSetBytes { get; set; }
        public long SampledPeakWorkingSetBytes { get; set; }
        public long SampledPeakDeltaBytes { get; set; }
        public long LifetimePeakIncreaseBytes { get; set; }
        public AggregateMetrics? Overall { get; set; }
        public Dictionary<string, AggregateMetrics> Categories { get; set; } = [];
        public List<ImageMeasurement> Images { get; } = [];
    }
    private sealed record ModelFile(string Path, long Bytes, string Sha256);
    private sealed class ImageMeasurement
    {
        public string Id { get; init; } = "";
        public string Category { get; init; } = "";
        public bool EnhancedSubset { get; init; }
        public string Truth { get; init; } = "";
        public string Output { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Language { get; set; }
        public List<double> WarmMs { get; } = [];
        public int DifferentRepeatedOutputs { get; set; }
        public TextMetrics Metrics { get; set; } = new();
    }
    private sealed class TextMetrics
    {
        public int Edits { get; init; }
        public int TruthCharacters { get; init; }
        public double Cer => Edits / (double)Math.Max(1, TruthCharacters);
        public int LineInsensitiveEdits { get; init; }
        public int LineInsensitiveTruthCharacters { get; init; }
        public int MatchedLines { get; init; }
        public int TruthLines { get; init; }
        public int OutputLines { get; init; }
    }
    private sealed class AggregateMetrics
    {
        public int Images { get; init; }
        public int Edits { get; init; }
        public int TruthCharacters { get; init; }
        public double Cer { get; init; }
        public double LineInsensitiveCer { get; init; }
        public double ExactLineMatchRate { get; init; }
        public double ExtraLinePenalizedMatchRate { get; init; }
        public double WarmMedianMs { get; init; }
        public double WarmP95Ms { get; init; }
        public int WarmMeasurements { get; init; }
        public int DifferentRepeatedOutputs { get; init; }
    }
}
