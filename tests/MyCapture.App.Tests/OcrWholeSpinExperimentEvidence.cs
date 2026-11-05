using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

internal static class OcrWholeSpinExperimentEvidence
{
    internal const string CorpusSha256 = "EA2B700F8564651CB70DC375B48F9ED996C5186BE15D54A474A96E6FA90F06C8";
    internal sealed record FileReceipt(string Role, string Path, long Bytes, string Sha256);
    internal sealed record SessionReceipt(string Detector, string Classifier, string Recognizer, string Dictionary,
        int IntraThreads, int InterThreads, string ExecutionMode, string Optimization,
        bool CpuArena, bool MemoryPattern, int RecognitionDop, bool DisableSpinning);

    internal static FileReceipt File(string role, string path)
    {
        path = Path.GetFullPath(path);
        _ = DiagnosticOutputPaths.Child(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        return new(role, path, new FileInfo(path).Length, Data.FileHash(path));
    }

    internal static FileReceipt[] Catalog(string directory)
    {
        var results = new List<FileReceipt>();
        foreach (OcrModelFile model in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
        {
            string path = DiagnosticOutputPaths.Child(directory, model.FileName);
            if (!System.IO.File.Exists(path) && OcrModelCatalog.Optional.Contains(model)) continue;
            FileReceipt value = File("catalog:" + model.FileName, path);
            Assert.Equal(model.ExpectedBytes, value.Bytes);
            Assert.Equal(model.ExpectedSha256, value.Sha256, ignoreCase: true);
            results.Add(value);
        }
        return results.ToArray();
    }

    internal static SessionReceipt Session(SessionOptions options, string det, string cls, string rec, string keys, bool noSpin) =>
        new(det, cls, rec, keys, options.IntraOpNumThreads, options.InterOpNumThreads,
            options.ExecutionMode.ToString(), options.GraphOptimizationLevel.ToString(),
            options.EnableCpuMemArena, options.EnableMemoryPattern,
            NeuralOcrEngine.CreateOptions().RecMaxDegreeOfParallelism, noSpin);

    internal static FileReceipt[] Resolved(SessionReceipt value) =>
        [File("detector", value.Detector), File("classifier", value.Classifier),
            File("recognizer", value.Recognizer), File("dictionary", value.Dictionary)];

    internal static FileReceipt[] Sources()
    {
        string[] names = ["tests/MyCapture.App.Tests/OcrWholeSpinExperimentTests.cs",
            "tests/MyCapture.App.Tests/OcrWholeSpinExperimentEvidence.cs", "tests/MyCapture.App.Tests/OcrCommercialCorpusData.cs",
            "src/MyCapture.Ocr/NeuralOcrEngine.cs", "src/MyCapture.Ocr/OcrModelStore.cs", "src/MyCapture.Ocr/OcrModelCatalog.cs",
            "src/MyCapture.Ocr/CaptureOcrService.cs", "src/MyCapture.Ocr/WindowsOcrService.cs", "src/MyCapture.Ocr/WindowsOcrRecognizer.cs",
            "src/MyCapture.Ocr/WindowsOcrBitmapBridge.cs", "src/MyCapture.Ocr/OcrPlanner.cs", "src/MyCapture.Ocr/OcrRequestFactory.cs",
            "src/MyCapture.Ocr/OcrResultMerger.cs", "src/MyCapture.Ocr/IOcrService.cs", "src/MyCapture.Ocr/OnnxRuntimeInitialization.cs",
            "src/MyCapture.Core/Settings/OcrQuality.cs", "src/MyCapture.Platform/Imaging/ImageCodec.cs", "Directory.Packages.props"];
        return names.Select(name => File(name, Path.Combine(Data.RepositoryRoot(), name))).ToArray();
    }

    internal static FileReceipt[] Binaries()
    {
        string[] managed = [typeof(OcrWholeSpinExperimentTests).Assembly.Location, typeof(MyCapture.App.App).Assembly.Location,
            typeof(OcrRequestFactory).Assembly.Location, typeof(OcrSettings).Assembly.Location, typeof(ImageCodec).Assembly.Location,
            typeof(RapidOcrNet.RapidOcr).Assembly.Location, typeof(SessionOptions).Assembly.Location];
        string[] native = Directory.GetFiles(AppContext.BaseDirectory, "onnxruntime*.dll", SearchOption.TopDirectoryOnly);
        return managed.Concat(native).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(path => File(Path.GetFileName(path), path)).ToArray();
    }

    internal static FileReceipt[] LoadedNative()
    {
        using var process = Process.GetCurrentProcess();
        FileReceipt[] loaded = process.Modules.Cast<ProcessModule>()
            .Where(module => module.ModuleName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(module.ModuleName, "libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => File("loaded-native:" + module.ModuleName, module.FileName))
            .OrderBy(value => value.Role, StringComparer.Ordinal).ToArray();
        Assert.Contains(loaded, value => string.Equals(Path.GetFileName(value.Path), "onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded, value => string.Equals(Path.GetFileName(value.Path), "libSkiaSharp.dll", StringComparison.OrdinalIgnoreCase));
        return loaded;
    }

    internal static void Equal(FileReceipt[] before, FileReceipt[] after) =>
        Assert.Equal(JsonSerializer.Serialize(before, Data.Json), JsonSerializer.Serialize(after, Data.Json));

    internal static object Identity(FileReceipt[] sources, FileReceipt[] binaries, FileReceipt[] models, FileReceipt[] loadedNative) => new
    {
        corpus = CorpusSha256,
        sources = sources.Select(value => new { value.Role, value.Bytes, value.Sha256 }).ToArray(),
        binaries = binaries.Select(value => new { value.Role, value.Bytes, value.Sha256 }).ToArray(),
        models = models.Select(value => new { value.Role, value.Bytes, value.Sha256 }).ToArray(),
        loadedNative,
    };

    internal static object Result(OcrResult result) => new
    {
        status = result.Status.ToString(), result.Text, result.LanguageTag, result.Lines,
        result.ReadingOrientation, result.TextAngle, result.Message,
    };

    // Snapshot arrays inside the callback, before RapidOcr can start another call. Timing fields
    // are intentionally excluded from the exact quality oracle; scores/angles/boxes are retained.
    internal static object Raw(RapidOcrNet.OcrResult raw) => new
    {
        raw.StrRes,
        blocks = raw.TextBlocks?.Select(block => new
        {
            block.Text, points = Points(block.BoxPoints), block.BoxScore, block.AngleIndex, block.AngleScore,
            chars = block.Chars?.ToArray(), charScores = block.CharScores?.ToArray(),
            words = block.WordResults?.Select(word => new { word.Text, points = Points(word.BoxPoints), word.Score }).ToArray(),
        }).ToArray(),
    };

    // RapidOcrNet 4.2.0, commit 708cae2fcb88720e1d891a81b5ee3e8b2bcc139e:
    // OcrResult.cs and RapidOcr.cs DetectOnce expose these already-measured float milliseconds.
    // Returned blocks have passed the library text/score filter; omitted crop times are unavailable.
    internal sealed record RapidBlockTiming(int Index, float AngleTimeMs, float CrnnTimeMs, float BlockTimeMs);
    internal sealed record RapidTimingSnapshot(float DbNetTimeMs, float DetectTimeMs,
        int ReturnedBlockCount, RapidBlockTiming[] ReturnedBlocks)
    {
        public string Schema => "rapidocr-4.2.0-returned-wall-v1";
        public string Units => "ms";

        // Runs after the measured block, not inside the native-result callback.
        internal void RequireValid()
        {
            if (!Valid(DbNetTimeMs) || !Valid(DetectTimeMs) || DetectTimeMs < DbNetTimeMs ||
                ReturnedBlockCount < 0 || ReturnedBlocks.Length != ReturnedBlockCount)
                throw new InvalidDataException("Invalid same-call Rapid timing snapshot.");
            for (int index = 0; index < ReturnedBlocks.Length; index++)
            {
                RapidBlockTiming block = ReturnedBlocks[index];
                if (block.Index != index || !Valid(block.AngleTimeMs) || !Valid(block.CrnnTimeMs) ||
                    !Valid(block.BlockTimeMs) || block.BlockTimeMs != block.AngleTimeMs + block.CrnnTimeMs)
                    throw new InvalidDataException("Invalid returned-block Rapid timing observation.");
            }
        }
        private static bool Valid(float value) => float.IsFinite(value) && value >= 0;
    }

    internal static RapidTimingSnapshot CaptureRapidTimings(RapidOcrNet.OcrResult raw) =>
        new(raw.DbNetTime, raw.DetectTime, raw.TextBlocks.Length,
            raw.TextBlocks.Select((block, index) => new RapidBlockTiming(index,
                block.AngleTime, block.CrnnTime, block.BlockTime)).ToArray());

    private static object[]? Points(SkiaSharp.SKPointI[]? points) =>
        points?.Select(point => (object)new { point.X, point.Y }).ToArray();
}
