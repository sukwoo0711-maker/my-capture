using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Platform.Imaging;

namespace MyCapture.Ocr;

/// <summary>
/// Recognises text with the operating system's OCR engine.
/// </summary>
/// <remarks>
/// <para>
/// The service owns the pipeline: decode the requested source (bytes, file, or bitmap) →
/// choose a language and an effective scale via <see cref="OcrPlanner"/> → prepare a
/// nearest-neighbour upscaled bitmap for small UI text → recognise → map every box back to
/// original image pixels → normalise text → return a typed <see cref="OcrResult"/>. The WinRT
/// engine itself lives behind <see cref="IOcrRecognizer"/>, so this whole pipeline is
/// exercisable in tests with a fake recognizer.
/// </para>
/// <para>
/// Every failure mode is non-fatal: an unavailable engine, an undecodable image, or an API
/// fault becomes <see cref="OcrStatus.Unavailable"/>/<see cref="OcrStatus.Failed"/> rather than
/// an exception. Cancellation surfaces as <see cref="OcrStatus.Cancelled"/>.
/// </para>
/// </remarks>
public sealed class WindowsOcrService : IOcrService
{
    private readonly IOcrRecognizer _recognizer;
    private readonly Func<byte[], BitmapSource?> _decodeBytes;
    private readonly Func<string, BitmapSource?> _decodeFile;
    private readonly ILogger _log;

    /// <summary>Production constructor: wraps the real Windows OCR engine.</summary>
    public WindowsOcrService(ILogger<WindowsOcrService> log)
        : this(new WindowsOcrRecognizer(log), DecodeBytesDefault, ImageCodec.TryLoad, log)
    {
    }

    /// <summary>Test/DI constructor: inject a recognizer and decoders.</summary>
    internal WindowsOcrService(
        IOcrRecognizer recognizer,
        Func<byte[], BitmapSource?> decodeBytes,
        Func<string, BitmapSource?> decodeFile,
        ILogger log)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _decodeBytes = decodeBytes ?? throw new ArgumentNullException(nameof(decodeBytes));
        _decodeFile = decodeFile ?? throw new ArgumentNullException(nameof(decodeFile));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsAvailable => _recognizer.IsAvailable;

    public IReadOnlyList<string> SupportedLanguages => _recognizer.SupportedLanguages;

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_recognizer.IsAvailable)
        {
            return OcrResult.Unavailable(
                "이 시스템에서 OCR 언어 팩을 사용할 수 없습니다.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Cancelled();
        }

        var stopwatch = Stopwatch.StartNew();

        BitmapSource? source;
        try
        {
            source = DecodeSource(request);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            _log.LogWarning(ex, "OCR could not decode the requested image");
            return OcrResult.Failed("이미지를 불러올 수 없습니다.", stopwatch.Elapsed);
        }

        if (source is null)
        {
            return OcrResult.Failed("이미지를 불러올 수 없습니다.", stopwatch.Elapsed);
        }

        string? language = OcrPlanner.SelectLanguage(
            request.PreferredLanguages,
            _recognizer.SupportedLanguages);

        double scale = OcrPlanner.ResolveScale(
            source.PixelWidth,
            source.PixelHeight,
            request.UpscaleFactor,
            _recognizer.MaxImageDimension);

        BitmapSource prepared = Prepare(source, scale);

        try
        {
            RecognizedText? recognized = await _recognizer
                .RecognizeAsync(prepared, language, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            if (recognized is null)
            {
                return OcrResult.Unavailable("OCR 엔진을 만들 수 없습니다.");
            }

            return BuildResult(recognized, scale, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Cancelled();
        }
        catch (Exception ex)
        {
            // The recognizer isolates WinRT, but a decode/COM fault can still surface. Keep OCR
            // strictly non-fatal for the caller.
            stopwatch.Stop();
            _log.LogWarning(ex, "OCR recognition failed");
            return OcrResult.Failed("텍스트 인식에 실패했습니다.", stopwatch.Elapsed);
        }
    }

    private BitmapSource? DecodeSource(OcrRequest request)
    {
        if (request.Bitmap is not null)
        {
            return request.Bitmap;
        }

        if (request.EncodedImage is not null)
        {
            return _decodeBytes(request.EncodedImage);
        }

        if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            return _decodeFile(request.FilePath);
        }

        return null;
    }

    /// <summary>
    /// Applies the effective scale: nearest-neighbour upscale for small UI text (crisp glyph
    /// edges), a plain resize when shrinking an oversized source to fit the engine.
    /// </summary>
    private static BitmapSource Prepare(BitmapSource source, double scale)
    {
        if (Math.Abs(scale - 1.0) < 1e-6)
        {
            return source;
        }

        return scale > 1.0
            ? ImageCodec.UpscaleForRecognition(source, scale)
            : ImageCodec.Resize(source, scale);
    }

    private static OcrResult BuildResult(RecognizedText recognized, double scale, TimeSpan elapsed)
    {
        var lines = new List<OcrLine>(recognized.Lines.Count);
        var lineTexts = new List<string>(recognized.Lines.Count);

        foreach (RecognizedLine line in recognized.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            var wordTexts = new List<string>(line.Words.Count);
            OcrRect lineBounds = default;

            foreach (RecognizedWord word in line.Words)
            {
                string wordText = OcrPlanner.NormalizeWord(word.Text);
                OcrRect bounds = word.Bounds.Unscale(scale);
                words.Add(new OcrWord(wordText, bounds));
                wordTexts.Add(wordText);
                lineBounds = OcrRect.Union(lineBounds, bounds);
            }

            // Prefer the engine's own line text when present; otherwise rebuild from words.
            string lineText = string.IsNullOrWhiteSpace(line.Text)
                ? OcrPlanner.BuildLineText(wordTexts)
                : OcrPlanner.NormalizeWord(line.Text);

            lineTexts.Add(lineText);
            lines.Add(new OcrLine(lineText, lineBounds, words));
        }

        string blockText = OcrPlanner.BuildBlockText(lineTexts);
        string tag = recognized.LanguageTag ?? string.Empty;

        return blockText.Length == 0
            ? OcrResult.NoText(tag, elapsed)
            : OcrResult.Success(blockText, tag, lines, elapsed);
    }

    private static BitmapSource? DecodeBytesDefault(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
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
}
