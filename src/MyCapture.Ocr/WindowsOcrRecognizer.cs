using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinRtOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinRtOcrResult = Windows.Media.Ocr.OcrResult;
using WinRtOcrLine = Windows.Media.Ocr.OcrLine;
using WinRtOcrWord = Windows.Media.Ocr.OcrWord;

namespace MyCapture.Ocr;

/// <summary>
/// The real recognizer over <c>Windows.Media.Ocr.OcrEngine</c>.
/// </summary>
/// <remarks>
/// <para>
/// Isolates every <c>Windows.Media.Ocr</c> / <c>Windows.Graphics.Imaging</c> touch so the
/// service around it stays testable. Engine creation is attempted (in order) for an explicit
/// supported language, then the user-profile languages; a machine with no installed OCR
/// language pack — or a desktop process without package identity, where the API can throw —
/// yields <see cref="IsAvailable"/> = <see langword="false"/> and a <see langword="null"/>
/// recognition result instead of crashing.
/// </para>
/// <para>
/// All WinRT streams and <see cref="SoftwareBitmap"/> instances are disposed on every path.
/// </para>
/// </remarks>
internal sealed class WindowsOcrRecognizer : IOcrRecognizer
{
    private readonly ILogger _log;
    private readonly IReadOnlyList<string> _supported;
    private readonly int _maxImageDimension;
    private readonly bool _available;

    public WindowsOcrRecognizer(ILogger log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var supported = new List<string>();
        int maxDimension = 0;
        bool available = false;

        try
        {
            foreach (Language language in WinRtOcrEngine.AvailableRecognizerLanguages)
            {
                supported.Add(language.LanguageTag);
            }

            // MaxImageDimension is a static engine property; reading it also proves the WinRT
            // surface is reachable at all.
            maxDimension = (int)WinRtOcrEngine.MaxImageDimension;
            available = supported.Count > 0 && maxDimension > 0;
        }
        catch (Exception ex) when (ex is TypeInitializationException
            or InvalidOperationException
            or COMException
            or DllNotFoundException
            or NotSupportedException)
        {
            // No OCR component / no package identity / API surface missing. Non-fatal.
            _log.LogWarning(ex, "Windows OCR is unavailable on this system");
        }

        _supported = supported;
        _maxImageDimension = maxDimension > 0 ? maxDimension : 4096;
        _available = available;
    }

    public bool IsAvailable => _available;

    public IReadOnlyList<string> SupportedLanguages => _supported;

    public int MaxImageDimension => _maxImageDimension;

    public async Task<RecognizedText?> RecognizeAsync(
        BitmapSource preparedBitmap,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedBitmap);

        WinRtOcrEngine? engine = TryCreateEngine(languageTag);
        if (engine is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        SoftwareBitmap? softwareBitmap = null;
        try
        {
            softwareBitmap = await WindowsOcrBitmapBridge.CreateAsync(preparedBitmap, cancellationToken)
                .ConfigureAwait(false);
            if (softwareBitmap is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            WinRtOcrResult winrt = await engine.RecognizeAsync(softwareBitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return Convert(winrt, engine, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
        }
        finally
        {
            softwareBitmap?.Dispose();
        }
    }

    private WinRtOcrEngine? TryCreateEngine(string? languageTag)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(languageTag))
            {
                var language = new Language(languageTag);
                WinRtOcrEngine? fromLanguage = WinRtOcrEngine.TryCreateFromLanguage(language);
                if (fromLanguage is not null)
                {
                    return fromLanguage;
                }
            }

            // Fall back to whatever the user's profile languages resolve to.
            return WinRtOcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is COMException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException)
        {
            // A bad tag or a missing component surfaces here; treat as unavailable.
            _log.LogWarning(ex, "Could not create an OCR engine for {Language}", languageTag ?? "<profile>");
            return null;
        }
    }

    private static RecognizedText Convert(WinRtOcrResult winrt, WinRtOcrEngine engine,
        int preparedWidth, int preparedHeight)
    {
        var lines = new List<RecognizedLine>(winrt.Lines.Count);
        foreach (WinRtOcrLine line in winrt.Lines)
        {
            var words = new List<RecognizedWord>(line.Words.Count);
            foreach (WinRtOcrWord word in line.Words)
            {
                var rect = new OcrRect(
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height);
                words.Add(new RecognizedWord(word.Text,
                    MapTextAngleBounds(rect, winrt.TextAngle, preparedWidth, preparedHeight)));
            }

            lines.Add(new RecognizedLine(line.Text, words));
        }

        string tag = engine.RecognizerLanguage?.LanguageTag ?? string.Empty;
        return new RecognizedText(tag, lines) { TextAngle = winrt.TextAngle };
    }


    /// <summary>Maps WinRT's text-angle axes into the actual prepared bitmap's pixel axes.</summary>
    /// <remarks>
    /// Windows.Media.Ocr.TextAngle specifies a clockwise rotation around the image center
    /// for word overlays. Normalize at the WinRT boundary, before language passes, unscaling
    /// or whole-image right-angle restoration. TextAngle metadata remains unchanged.
    /// https://learn.microsoft.com/uwp/api/windows.media.ocr.ocrresult.textangle
    /// </remarks>
    internal static OcrRect MapTextAngleBounds(OcrRect bounds, double? textAngle,
        int preparedWidth, int preparedHeight)
    {
        // Preserve the previous geometry for absent/zero or unusable metadata. In particular,
        // a nonfinite angle remains in TextAngle for downstream validity checks, not in boxes.
        if (textAngle is not { } angle || angle == 0 || !double.IsFinite(angle)
            || preparedWidth <= 0 || preparedHeight <= 0)
            return bounds;

        double centerX = preparedWidth / 2.0, centerY = preparedHeight / 2.0;
        (double sin, double cos) = Math.SinCos(angle * (Math.PI / 180.0));
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        Include(bounds.X, bounds.Y);
        Include(bounds.Right, bounds.Y);
        Include(bounds.X, bounds.Bottom);
        Include(bounds.Right, bounds.Bottom);
        return new OcrRect(left, top, right - left, bottom - top);

        void Include(double x, double y)
        {
            double dx = x - centerX, dy = y - centerY;
            double mappedX = centerX + cos * dx - sin * dy;
            double mappedY = centerY + sin * dx + cos * dy;
            left = Math.Min(left, mappedX);
            top = Math.Min(top, mappedY);
            right = Math.Max(right, mappedX);
            bottom = Math.Max(bottom, mappedY);
        }
    }
}
