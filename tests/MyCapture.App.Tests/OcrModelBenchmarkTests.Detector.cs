using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCapture.Ocr;
using RapidOcrNet;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    // One process per configuration gives every detector the same memory baseline semantics.
    // The caller runs this opt-in test serially and selects candidates using tuning results only.
    private static void RunDetector(string output, string models)
    {
        VerifyMetricExamples();
        string corpusName = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH_CORPUS") ?? "original";
        string split = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH_SPLIT") ?? "tuning";
        Assert.Contains(corpusName, new[] { "original", "validation2", "validation3", "rotated" });
        Assert.Contains(split, new[] { "tuning", "validation", "all" });
        Assert.False(corpusName != "original" && split != "all", "Additional corpora must run in full.");
        List<Sample> all = corpusName switch
        {
            "validation2" => CreateValidationCorpus(output),
            "validation3" => CreateValidation3Corpus(output),
            "rotated" => CreateRotatedCorpus(output),
            _ => CreateCorpus(output),
        };
        List<Sample> samples = all.Where(s => split == "all" || IsTuningImage(s.Truth.Id) == (split == "tuning")).ToList();
        Assert.Equal(corpusName == "rotated" ? all.Count : split == "all" ? 60 : 30, samples.Count);
        WriteJson(Path.Combine(output, "split.json"), new
        {
            corpus = corpusName, split,
            method = "Fixed balanced 30/30 IDs; JPEG derivatives stay with their original image. Both themes and all scales occur in both halves.",
            selectedIds = samples.Select(s => s.Truth.Id),
        });
        var configuration = JsonSerializer.Deserialize<DetectorConfiguration>(
            Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH_OPTIONS") ?? "{}", Json)!;
        Assert.Contains(configuration.Detector, new[] { "current", "v6" });
        RapidOcrOptions options = ProductOverrides(RapidOcrOptions.Default) with
        {
            BoxThresh = configuration.BoxThresh, BoxScoreThresh = configuration.BoxScoreThresh,
            UnClipRatio = configuration.UnClipRatio, Padding = configuration.Padding,
            LimitSideLen = configuration.LimitSideLen, ImgResize = configuration.ImgResize,
            TextScore = configuration.TextScore,
            DoAngle = configuration.DoAngle, ClsThresh = configuration.ClsThresh,
        };
        string current = Path.Combine(models, "current");
        string detector = configuration.Detector == "current"
            ? Path.Combine(current, "ch_PP-OCRv5_det_server.onnx")
            : Path.Combine(models, "new", "PP-OCRv6_det_small.onnx");
        string classifier = Bundled(RapidOcr.DefaultClsModelPath);
        string recognizer = Path.Combine(current, "korean_PP-OCRv5_rec_mobile.onnx");
        string dictionary = Path.Combine(current, "ppocrv5_korean_dict.txt");
        var pipeline = new PipelineReport
        {
            Name = configuration.Name, Options = DescribeOptions(options),
            Models = new[] { detector, classifier, recognizer, dictionary }.Select(ModelEvidence).ToList(),
        };
        pipeline.ModelBytes = pipeline.Models.Sum(m => m.Bytes);
        if (configuration.Detector == "v6")
        {
            Assert.Equal(9_929_594, pipeline.Models[0].Bytes);
            Assert.Equal("090F04ABCD9D9A7498BC4EBF677E4CB9BDCE1FE4197DDB7E529F1EF44E1FF94F", pipeline.Models[0].Sha256);
        }
        OnnxRuntimeInitialization.Shared.EnsureInitialized();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var memory = new WorkingSetProbe();
        pipeline.BaselineWorkingSetBytes = memory.Baseline;
        var timer = Stopwatch.StartNew();
        using var runner = new NeuralRunner(detector, classifier, recognizer, dictionary, options,
            MyCapture.Core.Settings.OcrQuality.Accurate);
        pipeline.InitMs = timer.Elapsed.TotalMilliseconds;
        memory.Sample();
        timer.Restart();
        var cold = runner.Recognize(samples[0].Bitmap);
        pipeline.FirstInferenceMs = timer.Elapsed.TotalMilliseconds;
        pipeline.FirstInferenceImage = samples[0].Truth.Id;
        pipeline.FirstInferenceStatus = cold.Status.ToString();
        CheckResult(cold);
        memory.Sample();
        foreach (Sample sample in samples)
        {
            WriteJson(Path.Combine(output, "progress.json"), new
            {
                state = "running", configuration.Name, corpus = corpusName, split,
                image = sample.Truth.Id, completed = pipeline.Images.Count, total = samples.Count,
            });
            var image = new ImageMeasurement
            {
                Id = sample.Truth.Id, Category = sample.Truth.SourceCategory, Truth = sample.Truth.Text,
            };
            for (int repeat = 0; repeat < Repetitions; repeat++)
            {
                timer.Restart();
                var result = runner.Recognize(sample.Bitmap);
                double elapsed = timer.Elapsed.TotalMilliseconds;
                CheckResult(result);
                image.WarmMs.Add(elapsed);
                if (repeat == 0)
                {
                    image.Output = result.Text;
                    image.Status = result.Status.ToString();
                    image.Language = result.LanguageTag;
                }
                else if (image.Output != result.Text) image.DifferentRepeatedOutputs++;
                memory.Sample();
            }
            image.Metrics = Measure(image.Truth, image.Output);
            pipeline.Images.Add(image);
        }
        memory.Sample();
        pipeline.SampledPeakWorkingSetBytes = memory.Peak;
        pipeline.SampledPeakDeltaBytes = memory.Peak - memory.Baseline;
        pipeline.LifetimePeakIncreaseBytes = memory.LifetimeIncrease;
        pipeline.Overall = Aggregate(pipeline.Images);
        pipeline.Categories = pipeline.Images.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => Aggregate(g.ToList()));
        pipeline.Status = "completed";
        // Freeze the timed/memory result BEFORE optional extra diagnostic inference.
        WriteJson(Path.Combine(output, "detector-results.json"), new
        {
            schemaVersion = 1, completedUtc = DateTimeOffset.UtcNow, corpus = corpusName, split,
            processId = Environment.ProcessId, processorCount = Environment.ProcessorCount,
            configuration, pipeline,
            falsePositiveControls = pipeline.Images.Where(i => i.Category is "code" or "spreadsheet").Select(i => new
            {
                i.Id, i.Category, i.Truth, i.Output,
                extraLines = Math.Max(0, i.Metrics.OutputLines - i.Metrics.TruthLines),
                // Audit aid, not a calibrated false-positive classifier. Retain full strings for inspection.
                distantLines = NormalizeLines(i.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(line => !NormalizeLines(i.Truth).Split('\n').Any(truth =>
                        Levenshtein(Regex.Replace(truth, @"\s+", ""), Regex.Replace(line, @"\s+", ""))
                        <= Math.Max(truth.Length, line.Length) * 0.5)),
            }),
        });
        if (Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BENCH_DIAGNOSTICS") == "1")
        {
            var diagnostics = samples.Select(s => new
            {
                s.Truth.Id, s.Truth.Text, s.Truth.Width, s.Truth.Height,
                diagnostic = runner.Diagnose(s.Bitmap),
            }).ToArray();
            WriteJson(Path.Combine(output, "diagnostics.json"), new
            {
                pipeline = configuration.Name, corpus = corpusName, split,
                note = "Extra untimed inference after memory measurement; pinned RapidOcrNet prefilter path includes blanks and verifies every surviving line against public Detect.",
                images = diagnostics,
            });
        }
        WriteJson(Path.Combine(output, "progress.json"), new { state = "completed", configuration.Name });
    }

    private static bool IsTuningImage(string id)
    {
        int index = int.Parse(id.AsSpan(0, 3), CultureInfo.InvariantCulture);
        return index is 1 or 4 or 5 or 8 or 9 or 12 or 13 or 16 or 17 or 20 or 21 or 24
            or 26 or 27 or 30 or 31 or 34 or 35 or 38 or 39 or 42
            or 43 or 46 or 49 or 50 or 51 or 54 or 55 or 57 or 59;
    }

    [Fact]
    public void DetectorBenchmarkMetricsAndHoldout_DoNotHideSpacesOrLeakJpegPairs()
    {
        Assert.True(Measure("알림 설정", "알림설정").Edits > 0);
        Assert.Equal(0, Measure("알림 설정", "알림설정").SpaceInsensitiveEdits);
        Assert.Equal(0, Measure("가\n나", "가 나").SpaceInsensitiveEdits);
        Assert.Equal(30, Enumerable.Range(1, 60).Count(i => IsTuningImage($"{i:D3}-test")));
        foreach ((int original, int jpeg) in new[] { (1, 55), (25, 56), (31, 57), (37, 58), (43, 59), (44, 60) })
            Assert.Equal(IsTuningImage($"{original:D3}-original"), IsTuningImage($"{jpeg:D3}-jpeg"));
    }

    private sealed record DetectorConfiguration
    {
        public string Name { get; init; } = "A-v6";
        public string Detector { get; init; } = "v6";
        public float BoxThresh { get; init; } = 0.3f;
        public float BoxScoreThresh { get; init; } = 0.5f;
        public float UnClipRatio { get; init; } = 1.6f;
        public int Padding { get; init; } = 50;
        public int LimitSideLen { get; init; } = 736;
        public int ImgResize { get; init; } = 1024;
        public float TextScore { get; init; } = 0.5f;
        public bool DoAngle { get; init; } = NeuralOcrEngine.CreateOptions().DoAngle;
        public float ClsThresh { get; init; } = NeuralOcrEngine.CreateOptions().ClsThresh;
    }
}
