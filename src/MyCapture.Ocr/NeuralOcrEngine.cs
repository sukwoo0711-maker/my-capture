using MyCapture.Core.Platform;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Platform.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace MyCapture.Ocr;

/// <summary>
/// PP-OCRv5 text recognizer for captures and imported images. Detection uses the RapidAI
/// server detector; the Korean mobile recognizer also covers digits and Latin text.
/// </summary>
internal sealed class NeuralOcrEngine : IDisposable
{
    private readonly OcrModelStore _store;
    private readonly ILogger _log;
    private readonly OnnxRuntimeInitialization _runtime;
    private readonly object _sync = new();
    private RapidOcr? _engine;
    private bool _initFailed;

    internal NeuralOcrEngine(
        OcrModelStore store,
        ILogger<NeuralOcrEngine> log,
        OnnxRuntimeInitialization? runtime = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _runtime = runtime ?? OnnxRuntimeInitialization.Shared;
        ModelVerificationTask = _store.PreVerifyInstalledModelsAsync();
    }

    /// <summary>
    /// Installed-model verification runs independently of construction and never downloads weights.
    /// </summary>
    internal Task ModelVerificationTask { get; }

    internal bool CanRun => AppArchitecture.SupportsNeuralOcr && _store.HasRequiredModels;

    internal bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _engine is not null;
            }
        }
    }

    internal async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AppArchitecture.SupportsNeuralOcr) return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        if (!await _store.EnsureAsync(cancellationToken).ConfigureAwait(false))
        {
            return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        }

        // ONNX init + detect must not run on the WPF dispatcher. A large receipt otherwise
        // freezes the shell long enough to look like a hang or failed launch.
        return await Task.Run(() => RecognizeOnWorker(request, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private OcrResult RecognizeOnWorker(OcrRequest request, CancellationToken cancellationToken)
    {
        if (!TryInitialize())
        {
            return OcrResult.Unavailable(UiText.Get("Text_D6580A6EEFB2"));
        }

        BitmapSource? source = Decode(request);
        if (source is null)
        {
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), TimeSpan.Zero);
        }

        if (request.EnhanceContrast)
        {
            source = ImageCodec.StretchContrastForRecognition(source);
        }

        byte[] png = ImageCodec.EncodePng(source);
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), TimeSpan.Zero);
        }

        try
        {
            lock (_sync)
            {
                RapidOcr engine = _engine ?? throw new InvalidOperationException("PP-OCR session was not initialized.");
                return ToResult(engine.Detect(bitmap, CreateOptions(), cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PP-OCR detect failed");
            return OcrResult.Failed(UiText.Get("Text_AA254F35F02E"), TimeSpan.Zero);
        }
    }

    internal static RapidOcrOptions CreateOptions() => RapidOcrOptions.Default with
    {
        ReturnWordBox = true,
        // Most capture text is upright. Requiring stronger evidence avoids flipping short
        // Korean UI lines, while retaining angle correction for imported/edited images and
        // pins. SearchRotatedOrientations controls Windows whole-image search; it does not
        // establish that an input is upright (background indexing disables that search).
        DoAngle = true,
        ClsThresh = 0.99f,
        RecMaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
    };

    internal bool TryInitialize()
    {
        if (!AppArchitecture.SupportsNeuralOcr) return false;
        lock (_sync)
        {
            if (_engine is not null)
            {
                return true;
            }

            if (_initFailed || !_store.HasRequiredModels)
            {
                return false;
            }

            string? cls = Bundled(RapidOcr.DefaultClsModelPath);
            string? bundledDet = Bundled(RapidOcr.DefaultDetModelPath);
            string det = _store.IsPresent(OcrModelCatalog.Detection)
                ? _store.PathFor(OcrModelCatalog.Detection)
                : bundledDet ?? string.Empty;
            string rec = _store.PathFor(OcrModelCatalog.Recognition);
            string keys = _store.PathFor(OcrModelCatalog.Dictionary);
            if (cls is null || string.IsNullOrEmpty(det))
            {
                _log.LogWarning(
                    "Bundled PP-OCR detector or classifier was not found. BaseDirectory={BaseDirectory}; Process={Process}",
                    AppContext.BaseDirectory,
                    Environment.ProcessPath);
                _initFailed = true;
                return false;
            }

            try
            {
                _runtime.EnsureInitialized();
                var engine = new RapidOcr();
                engine.InitModels(det, cls, rec, keys);
                _engine = engine;
                _log.LogInformation("PP-OCR session ready det={Det} rec={Rec}", det, rec);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PP-OCR session could not be created det={Det} rec={Rec} keys={Keys}", det, rec, keys);
                _initFailed = true;
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }

    private static string? Bundled(string fileName)
    {
        foreach (string root in CandidateRoots())
        {
            string path = Path.Combine(root, RapidOcr.ModelsFolderName, RapidOcr.ModelsVersion, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return AppContext.BaseDirectory;
        string? process = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(process))
        {
            yield return process;
        }

        string? assembly = Path.GetDirectoryName(typeof(NeuralOcrEngine).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assembly))
        {
            yield return assembly;
        }
    }

    private static BitmapSource? Decode(OcrRequest request)
    {
        if (request.Bitmap is not null)
        {
            return request.Bitmap;
        }

        if (request.EncodedImage is not null)
        {
            try
            {
                using var stream = new MemoryStream(request.EncodedImage, writable: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (NotSupportedException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            return ImageCodec.TryLoad(request.FilePath);
        }

        return null;
    }

    private static OcrResult ToResult(RapidOcrNet.OcrResult raw, double scale = 1.0)
    {
        if (raw.TextBlocks is null || raw.TextBlocks.Length == 0)
        {
            return OcrResult.NoText("ko-KR", TimeSpan.FromMilliseconds(raw.DetectTime));
        }

        var lines = new List<OcrLine>(raw.TextBlocks.Length);
        var angles = new List<double?>();
        foreach (TextBlock block in raw.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            OcrRect lineBox = ToRect(block.BoxPoints, scale);
            IReadOnlyList<OcrWord> words;
            if (block.WordResults is { Length: > 0 })
            {
                words = block.WordResults
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new OcrWord(word.Text.Trim(), ToRect(word.BoxPoints, scale)))
                    .ToArray();
            }
            else
            {
                words = [new OcrWord(block.Text.Trim(), lineBox)];
            }

            if (words.Count == 0)
            {
                continue;
            }

            lines.Add(new OcrLine(block.Text.Trim(), lineBox, words));
            angles.Add(TextAngle(block.BoxPoints, block.AngleIndex));
        }

        if (lines.Count == 0)
        {
            return OcrResult.NoText("ko-KR", TimeSpan.FromMilliseconds(raw.DetectTime));
        }

        string text = OcrPlanner.BuildBlockText(lines.Select(line => line.Text));
        return OcrResult.Success(text, "ko-KR", lines, TimeSpan.FromMilliseconds(raw.DetectTime), OcrResult.MostTilted(angles));
    }

    /// <summary>PP-OCR quadrilaterals run top-left to top-right in the source image.</summary>
    internal static double? TextAngle(SKPointI[]? points, int angleIndex = 0)
    {
        if (points is null || points.Length < 2) return null;
        double x = points[1].X - points[0].X, y = points[1].Y - points[0].Y;
        return x * x + y * y < 4 ? null : Math.Atan2(y, x) * 180 / Math.PI + (angleIndex == 1 ? 180 : 0);
    }

    private static OcrRect ToRect(SKPointI[]? points, double scale = 1.0)
    {
        if (points is null || points.Length == 0)
        {
            return new OcrRect(0, 0, 1, 1);
        }

        int minX = points[0].X;
        int minY = points[0].Y;
        int maxX = points[0].X;
        int maxY = points[0].Y;
        for (int i = 1; i < points.Length; i++)
        {
            minX = Math.Min(minX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxX = Math.Max(maxX, points[i].X);
            maxY = Math.Max(maxY, points[i].Y);
        }

        if (scale > 1.0)
        {
            int unscaledX = (int)Math.Round(minX / scale);
            int unscaledY = (int)Math.Round(minY / scale);
            int unscaledWidth = Math.Max(1, (int)Math.Round((maxX - minX) / scale));
            int unscaledHeight = Math.Max(1, (int)Math.Round((maxY - minY) / scale));
            return new OcrRect(unscaledX, unscaledY, unscaledWidth, unscaledHeight);
        }

        return new OcrRect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }
}
