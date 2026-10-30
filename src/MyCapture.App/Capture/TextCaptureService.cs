using System.Globalization;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Capture;

/// <summary>What a text capture produced, for the notification the shell shows afterwards.</summary>
internal enum TextCaptureStatus
{
    /// <summary>Text was recognised and is now on the clipboard.</summary>
    Copied,

    /// <summary>Recognition ran but the region holds no text.</summary>
    NoText,

    /// <summary>Text was recognised but the clipboard rejected it.</summary>
    ClipboardFailed,

    /// <summary>No OCR engine is usable on this PC.</summary>
    Unavailable,

    /// <summary>Recognition failed for another reason.</summary>
    Failed,

    /// <summary>A newer text capture or shutdown superseded this one; show nothing.</summary>
    Cancelled,
}

/// <param name="Status">The user-visible outcome.</param>
/// <param name="Result">The OCR result, kept so the advanced extraction window can open without re-running.</param>
/// <param name="CharacterCount">Non-whitespace characters copied; zero unless <see cref="TextCaptureStatus.Copied"/>.</param>
internal sealed record TextCaptureOutcome(TextCaptureStatus Status, OcrResult? Result, int CharacterCount);

/// <summary>
/// Recognises a selected screen region locally and copies only its text.
/// </summary>
/// <remarks>
/// <para>
/// Text capture is the "I only want the words" path: the frozen selection is recognised with
/// the user's current OCR quality stage, the text goes to the clipboard, and the image is never
/// written to the library. Recognition runs through the shared <see cref="IOcrService"/>, which
/// decodes and recognises on worker threads, so the dispatcher stays responsive.
/// </para>
/// <para>
/// Recognised text is user content and may be sensitive, so it is never logged: diagnostics
/// record only the outcome, character count and elapsed time.
/// </para>
/// </remarks>
internal sealed class TextCaptureService
{
    private readonly IOcrService _ocr;
    private readonly Func<OcrSettings> _settings;
    private readonly Func<string, Task<bool>> _copyText;
    private readonly ILogger _log;

    internal TextCaptureService(
        IOcrService ocr,
        Func<OcrSettings> settings,
        Func<string, Task<bool>> copyText,
        ILogger log)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _copyText = copyText ?? throw new ArgumentNullException(nameof(copyText));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Recognises <paramref name="image"/> and copies its text. Never throws for recognition or
    /// clipboard failures; cancellation before the clipboard write leaves the clipboard untouched.
    /// </summary>
    internal async Task<TextCaptureOutcome> RecognizeAndCopyAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        OcrResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrRequest request = OcrRequestFactory.FromBitmap(Freeze(image), _settings());
            result = await _ocr.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
            result = result.WithQuality(OcrQualityProfile.FromUpscale(request.UpscaleFactor));
        }
        catch (OperationCanceledException)
        {
            result = OcrResult.Cancelled();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Text capture recognition threw unexpectedly");
            result = OcrResult.Failed(UiText.Get("TextCapture_Failed"));
        }

        TextCaptureStatus status = cancellationToken.IsCancellationRequested ? TextCaptureStatus.Cancelled : Classify(result);
        int characters = 0;
        if (status == TextCaptureStatus.Copied)
        {
            characters = CountCharacters(result.Text);
            bool copied;
            try
            {
                copied = await _copyText(result.Text).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The exception type is enough; a clipboard failure never needs the text itself.
                _log.LogWarning("Text capture clipboard write threw {ExceptionType}", ex.GetType().Name);
                copied = false;
            }

            if (!copied) status = TextCaptureStatus.ClipboardFailed;
        }

        _log.LogInformation(
            "Text capture finished: {Status}, {Characters} character(s), recognition {Elapsed:0}ms",
            status,
            characters,
            result.Elapsed.TotalMilliseconds);
        return new TextCaptureOutcome(status, result, status == TextCaptureStatus.Copied ? characters : 0);
    }

    /// <summary>Maps an OCR result to the text capture outcome before any clipboard write.</summary>
    internal static TextCaptureStatus Classify(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            OcrStatus.Success when !string.IsNullOrWhiteSpace(result.Text) => TextCaptureStatus.Copied,
            OcrStatus.Success or OcrStatus.NoText => TextCaptureStatus.NoText,
            OcrStatus.Unavailable => TextCaptureStatus.Unavailable,
            OcrStatus.Cancelled => TextCaptureStatus.Cancelled,
            _ => TextCaptureStatus.Failed,
        };
    }

    /// <summary>User-perceived characters, excluding whitespace and line breaks.</summary>
    internal static int CountCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element)) count++;
        }

        return count;
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.IsFrozen) return image;
        BitmapSource snapshot = image.CloneCurrentValue();
        snapshot.Freeze();
        return snapshot;
    }
}
