using System.Diagnostics;
using System.IO;
using System.Windows.Media;
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
    private const int MaximumLanguagePasses = 2;
    private const int WeakMeaningfulCharacterThreshold = 4;
    private static readonly int[] AlternativeRotations = [90, 180, 270];

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
                UiText.Get("Text_D6580A6EEFB2"));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Cancelled();
        }

        var stopwatch = Stopwatch.StartNew();

        BitmapSource? source;
        try
        {
            // File/byte decoding can materialise a full-resolution image. Keep that work off the
            // caller's dispatcher; ConfigureAwait(false) also keeps every subsequent resize,
            // rotation and recognizer conversion on a worker thread.
            source = await Task.Run(() => DecodeSource(request), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Cancelled();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            _log.LogWarning(ex, "OCR could not decode the requested image");
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), stopwatch.Elapsed);
        }

        if (source is null)
        {
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), stopwatch.Elapsed);
        }

        IReadOnlyList<string> selectedLanguages = OcrPlanner.SelectLanguages(
            request.PreferredLanguages,
            _recognizer.SupportedLanguages,
            MaximumLanguagePasses);
        string?[] languagePasses = selectedLanguages.Count == 0
            ? [null]
            : selectedLanguages.Select(language => (string?)language).ToArray();

        double scale = OcrPlanner.ResolveAdaptiveScale(
            source.PixelWidth,
            source.PixelHeight,
            request.UpscaleFactor,
            _recognizer.MaxImageDimension);

        try
        {
            OrientationResult upright = await RecognizeOrientationAsync(
                    source,
                    scale,
                    rotation: 0,
                    languagePasses,
                    request.EnhanceContrast,
                    request.LocalCorrection,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!upright.RecognizerWasAvailable)
            {
                stopwatch.Stop();
                return OcrResult.Unavailable(UiText.Get("Text_FB68C3A34978"));
            }

            OrientationResult best = upright;
            if (request.SearchRotatedOrientations && IsWeak(upright.Lines))
            {
                foreach (int rotation in AlternativeRotations)
                {
                    OrientationResult candidate = await RecognizeOrientationAsync(
                            source,
                            scale,
                            rotation,
                            languagePasses,
                            request.EnhanceContrast,
                            request.LocalCorrection,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (candidate.RecognizerWasAvailable && candidate.Score > best.Score)
                    {
                        best = candidate;
                    }
                }
            }

            stopwatch.Stop();
            return BuildResult(best, stopwatch.Elapsed, source.PixelWidth, source.PixelHeight);
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
            return OcrResult.Failed(UiText.Get("Text_AA254F35F02E"), stopwatch.Elapsed);
        }
    }

    private BitmapSource? DecodeSource(OcrRequest request)
    {
        BitmapSource? source;
        if (request.Bitmap is not null)
        {
            source = request.Bitmap;
        }
        else if (request.EncodedImage is not null)
        {
            source = _decodeBytes(request.EncodedImage);
        }
        else if (!string.IsNullOrWhiteSpace(request.FilePath))
        {
            source = _decodeFile(request.FilePath);
        }
        else
        {
            return null;
        }

        if (source is not null && !source.IsFrozen)
        {
            // OcrRequest documents bitmap inputs as frozen. Decoders created on this worker may
            // still return a mutable Freezable, so freeze it here; a dispatcher-owned bitmap from
            // another thread fails non-fatally through the service's decode error path.
            if (!source.CanFreeze)
            {
                throw new InvalidOperationException("The OCR bitmap must be freezable.");
            }

            source.Freeze();
        }

        return source;
    }

    /// <summary>
    /// Applies the effective scale: nearest-neighbour upscale for small UI text (crisp glyph
    /// edges), a plain resize when shrinking an oversized source to fit the engine. Accurate
    /// mode stretches contrast first so faint receipt ink survives the later scale.
    /// </summary>
    private static BitmapSource Prepare(
        BitmapSource source,
        double scale,
        bool enhanceContrast,
        bool localCorrection)
    {
        BitmapSource current = localCorrection
            ? ImageCodec.CorrectImageForRecognition(source)
            : enhanceContrast
                ? ImageCodec.StretchContrastForRecognition(source)
                : source;

        if (Math.Abs(scale - 1.0) < 1e-6)
        {
            return current;
        }

        return scale > 1.0
            ? ImageCodec.UpscaleForRecognition(current, scale)
            : ImageCodec.Resize(current, scale);
    }

    private async Task<OrientationResult> RecognizeOrientationAsync(
        BitmapSource source,
        double scale,
        int rotation,
        IReadOnlyList<string?> languagePasses,
        bool enhanceContrast,
        bool localCorrection,
        CancellationToken cancellationToken)
    {
        BitmapSource prepared = Prepare(Rotate(source, rotation), scale, enhanceContrast, localCorrection);
        var passes = new List<LanguageResult>(languagePasses.Count);
        bool recognizerWasAvailable = false;

        foreach (string? language in languagePasses)
        {
            RecognizedText? recognized = await _recognizer
                .RecognizeAsync(prepared, language, cancellationToken)
                .ConfigureAwait(false);

            if (recognized is null)
            {
                continue;
            }

            recognizerWasAvailable = true;
            IReadOnlyList<OcrLine> lines = OcrPlanner.MergeLines(
                [],
                BuildLines(
                    recognized,
                    scale,
                    rotation,
                    source.PixelWidth,
                    source.PixelHeight));
            passes.Add(new LanguageResult(
                recognized.LanguageTag,
                lines,
                OcrPlanner.ScoreLines(lines)));
        }

        if (passes.Count == 0)
        {
            return new OrientationResult(
                rotation,
                string.Empty,
                [],
                0,
                recognizerWasAvailable);
        }

        LanguageResult dominant = passes
            .OrderByDescending(pass => pass.Score)
            .First();
        IReadOnlyList<OcrLine> merged = dominant.Lines;
        foreach (LanguageResult supplementary in passes
                     .Where(pass => !ReferenceEquals(pass, dominant))
                     .OrderByDescending(pass => pass.Score))
        {
            merged = OcrPlanner.MergeLines(merged, supplementary.Lines);
        }

        return new OrientationResult(
            rotation,
            dominant.LanguageTag,
            merged,
            OcrPlanner.ScoreLines(merged),
            recognizerWasAvailable);
    }

    private static IReadOnlyList<OcrLine> BuildLines(
        RecognizedText recognized,
        double scale,
        int rotation,
        int originalWidth,
        int originalHeight)
    {
        var lines = new List<OcrLine>(recognized.Lines.Count);
        // Language merging must use the recognizer's reading axes. Map only the winning
        // result back to the source image after row/word merging has finished.
        int readingWidth = rotation is 90 or 270 ? originalHeight : originalWidth;
        int readingHeight = rotation is 90 or 270 ? originalWidth : originalHeight;

        foreach (RecognizedLine line in recognized.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            var wordTexts = new List<string>(line.Words.Count);
            OcrRect lineBounds = default;

            foreach (RecognizedWord word in line.Words)
            {
                string wordText = OcrPlanner.NormalizeWord(word.Text);
                if (wordText.Length == 0)
                {
                    continue;
                }

                OcrRect bounds = word.Bounds
                    .Unscale(scale)
                    .MapFromClockwiseRotation(0, readingWidth, readingHeight);
                words.Add(new OcrWord(wordText, bounds));
                wordTexts.Add(wordText);
                lineBounds = OcrRect.Union(lineBounds, bounds);
            }

            // Prefer the engine's own line text when present; otherwise rebuild from words.
            string lineText = string.IsNullOrWhiteSpace(line.Text)
                ? OcrPlanner.BuildLineText(wordTexts)
                : OcrPlanner.NormalizeWord(line.Text);

            if (lineText.Length > 0)
            {
                lines.Add(new OcrLine(lineText, lineBounds, words));
            }
        }

        return lines;
    }

    private static OcrResult BuildResult(OrientationResult recognized, TimeSpan elapsed,
        int originalWidth, int originalHeight)
    {
        string blockText = OcrPlanner.BuildBlockText(recognized.Lines.Select(line => line.Text));
        if (blockText.Length == 0) return OcrResult.NoText(recognized.LanguageTag, elapsed);

        var orientation = new OcrReadingOrientation(recognized.Rotation, originalWidth, originalHeight);
        var lines = recognized.Lines.Select(line =>
        {
            // Clipped word boxes still need source axes even when one dimension is zero.
            OcrWord[] words = line.Words.Select(word => word with
            {
                Bounds = word.Bounds.MapFromClockwiseRotation(recognized.Rotation, originalWidth, originalHeight),
            }).ToArray();
            return line with
            {
                Words = words,
                Bounds = words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union),
            };
        }).ToArray();
        return OcrResult.Success(blockText, recognized.LanguageTag, lines, elapsed, orientation);
    }

    private static BitmapSource Rotate(BitmapSource source, int clockwiseDegrees)
    {
        if (clockwiseDegrees == 0)
        {
            return source;
        }

        var rotated = new TransformedBitmap(source, new RotateTransform(clockwiseDegrees));
        if (rotated.CanFreeze)
        {
            rotated.Freeze();
        }

        return rotated;
    }

    private static bool IsWeak(IEnumerable<OcrLine> lines)
    {
        string text = OcrPlanner.BuildBlockText(lines.Select(line => line.Text));
        return text.Count(char.IsLetterOrDigit) < WeakMeaningfulCharacterThreshold;
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

    private sealed record LanguageResult(
        string LanguageTag,
        IReadOnlyList<OcrLine> Lines,
        int Score);

    private sealed record OrientationResult(
        int Rotation,
        string LanguageTag,
        IReadOnlyList<OcrLine> Lines,
        int Score,
        bool RecognizerWasAvailable);
}
