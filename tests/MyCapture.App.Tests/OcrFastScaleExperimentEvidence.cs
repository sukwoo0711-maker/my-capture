using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

internal static class OcrFastScaleExperimentEvidence
{
    internal const string CorpusSha256 = "EA2B700F8564651CB70DC375B48F9ED996C5186BE15D54A474A96E6FA90F06C8";
    internal sealed record FileReceipt(string Role, string Path, bool Exists, long? Bytes, string? Sha256);

    internal static FileReceipt File(string role, string path, bool required = true)
    {
        path = Path.GetFullPath(path);
        _ = DiagnosticOutputPaths.Child(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        bool exists = System.IO.File.Exists(path);
        if (required && !exists) throw new FileNotFoundException("Missing experiment input: " + role, path);
        return new(role, path, exists, exists ? new FileInfo(path).Length : null,
            exists ? Data.FileHash(path) : null);
    }

    internal static FileReceipt[] Models(string directory)
    {
        directory = Data.ArtifactPath(directory);
        var result = new List<FileReceipt>();
        foreach (OcrModelFile model in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
        {
            FileReceipt entry = File("catalog:" + model.FileName,
                DiagnosticOutputPaths.Child(directory, model.FileName), OcrModelCatalog.Required.Contains(model));
            if (entry.Exists)
            {
                Assert.Equal(model.ExpectedBytes, entry.Bytes);
                Assert.Equal(model.ExpectedSha256, entry.Sha256, ignoreCase: true);
            }
            result.Add(entry);
        }
        result.Add(File("bundled-classifier-not-loaded", Path.Combine(AppContext.BaseDirectory, "models", "v5",
            Path.GetFileName(RapidOcrNet.RapidOcr.DefaultClsModelPath))));
        result.Add(File("bundled-detector-not-loaded", Path.Combine(AppContext.BaseDirectory, "models", "v5",
            Path.GetFileName(RapidOcrNet.RapidOcr.DefaultDetModelPath))));
        return result.ToArray();
    }

    internal static FileReceipt[] Binaries()
    {
        string[] managed = [typeof(OcrFastScaleExperimentTests).Assembly.Location, typeof(MyCapture.App.App).Assembly.Location,
            typeof(OcrRequestFactory).Assembly.Location, typeof(OcrSettings).Assembly.Location,
            typeof(ImageCodec).Assembly.Location, typeof(RapidOcrNet.RapidOcr).Assembly.Location,
            typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly.Location];
        string[] native = Directory.GetFiles(AppContext.BaseDirectory, "onnxruntime*.dll", SearchOption.TopDirectoryOnly);
        return managed.Concat(native).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(path => File("binary", path)).ToArray();
    }

    internal static FileReceipt[] Sources()
    {
        string[] paths = ["tests/MyCapture.App.Tests/OcrFastScaleExperimentTests.cs",
            "tests/MyCapture.App.Tests/OcrFastScaleExperimentEvidence.cs",
            "tests/MyCapture.App.Tests/OcrCommercialCorpusData.cs",
            "src/MyCapture.Ocr/WindowsOcrService.cs", "src/MyCapture.Ocr/WindowsOcrRecognizer.cs",
            "src/MyCapture.Ocr/WindowsOcrBitmapBridge.cs", "src/MyCapture.Ocr/CaptureOcrService.cs",
            "src/MyCapture.Ocr/OcrPlanner.cs", "src/MyCapture.Ocr/OcrRequestFactory.cs",
            "src/MyCapture.Ocr/IOcrService.cs", "src/MyCapture.Ocr/NeuralOcrEngine.cs",
            "src/MyCapture.Ocr/OcrModelStore.cs", "src/MyCapture.Ocr/OcrModelCatalog.cs",
            "src/MyCapture.Core/Settings/OcrQuality.cs", "src/MyCapture.Platform/Imaging/ImageCodec.cs",
            "Directory.Packages.props"];
        return paths.Select(path => File("source", Path.Combine(Data.RepositoryRoot(), path))).ToArray();
    }

    internal static void Equal(FileReceipt[] before, FileReceipt[] after) =>
        Assert.Equal(JsonSerializer.Serialize(before, Data.Json), JsonSerializer.Serialize(after, Data.Json));

    internal static object Result(OcrResult result) => new
    {
        status = result.Status.ToString(), result.Text, result.LanguageTag, result.Lines,
        result.TextAngle, result.ReadingOrientation, result.Message, elapsedMs = result.Elapsed.TotalMilliseconds,
    };

    internal readonly record struct PreparedPass(int Width, int Height, string PixelFormat, int BitsPerPixel,
        bool Frozen, string? RequestedLanguage, int MaxDimension, long EnteredTimestamp, double NativeWallMs,
        string? PixelSha256, RecognizedText? Raw, string? ExceptionType, int? HResult, bool CancellationRequested);

    /// <summary>Observes exactly the bitmap passed to the real WinRT recognizer, then delegates unchanged.</summary>
    internal sealed class PreparedInputObserver(IOcrRecognizer actual) : IOcrRecognizer
    {
        private PreparedPass[]? _buffer;
        private bool _hashPixels;
        internal int Count { get; private set; }
        internal int TotalCalls { get; private set; }
        public bool IsAvailable => actual.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => actual.SupportedLanguages;
        public int MaxImageDimension => actual.MaxImageDimension;

        internal void Begin(PreparedPass[] buffer, bool hashPixels)
        {
            _buffer = buffer;
            _hashPixels = hashPixels;
            Count = 0;
        }

        public async Task<RecognizedText?> RecognizeAsync(BitmapSource preparedBitmap, string? languageTag,
            CancellationToken cancellationToken)
        {
            PreparedPass[] buffer = _buffer ?? throw new InvalidOperationException("No owned observation buffer.");
            if (Count >= buffer.Length) throw new InvalidOperationException("Unexpected additional language/orientation pass.");
            int slot = Count++;
            TotalCalls++;
            long entered = Stopwatch.GetTimestamp();
            RecognizedText? raw = null;
            Exception? failure = null;
            string? pixelHash = null;
            long nativeStarted = 0;
            try
            {
                if (!preparedBitmap.IsFrozen || preparedBitmap.PixelWidth <= 0 || preparedBitmap.PixelHeight <= 0 ||
                    preparedBitmap.PixelWidth > MaxImageDimension || preparedBitmap.PixelHeight > MaxImageDimension)
                    throw new InvalidOperationException("Prepared image violates the real recognizer dimension/freeze contract.");
                if (_hashPixels) pixelHash = Data.PixelHash(preparedBitmap);
                nativeStarted = Stopwatch.GetTimestamp();
                raw = await actual.RecognizeAsync(preparedBitmap, languageTag, cancellationToken).ConfigureAwait(false);
                return raw;
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                buffer[slot] = new(preparedBitmap.PixelWidth, preparedBitmap.PixelHeight, preparedBitmap.Format.ToString(),
                    preparedBitmap.Format.BitsPerPixel, preparedBitmap.IsFrozen, languageTag, MaxImageDimension, entered,
                    nativeStarted == 0 ? 0 : Stopwatch.GetElapsedTime(nativeStarted).TotalMilliseconds,
                    pixelHash, raw, failure?.GetType().FullName, failure?.HResult, cancellationToken.IsCancellationRequested);
            }
        }
    }
}
