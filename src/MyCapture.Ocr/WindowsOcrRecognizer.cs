using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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
            softwareBitmap = await DecodeToSoftwareBitmapAsync(preparedBitmap, cancellationToken)
                .ConfigureAwait(false);
            if (softwareBitmap is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            WinRtOcrResult winrt = await engine.RecognizeAsync(softwareBitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return Convert(winrt, engine);
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

    private static RecognizedText Convert(WinRtOcrResult winrt, WinRtOcrEngine engine)
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
                words.Add(new RecognizedWord(word.Text, rect));
            }

            lines.Add(new RecognizedLine(line.Text, words));
        }

        string tag = engine.RecognizerLanguage?.LanguageTag ?? string.Empty;
        return new RecognizedText(tag, lines);
    }

    /// <summary>
    /// Encodes the WPF bitmap to PNG in memory, then decodes it into a BGRA8 SoftwareBitmap the
    /// OCR engine accepts. Every WinRT stream is disposed.
    /// </summary>
    private static async Task<SoftwareBitmap?> DecodeToSoftwareBitmapAsync(
        BitmapSource bitmap,
        CancellationToken cancellationToken)
    {
        byte[] png = EncodePng(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);

        Windows.Graphics.Imaging.BitmapDecoder decoder =
            await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

        SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        return software;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}
