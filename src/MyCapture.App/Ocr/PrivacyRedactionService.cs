using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;
using MyCapture.Core.Privacy;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal enum PrivacyRedactionStatus
{
    Success,
    NoMatches,
    Unavailable,
    Failed,
    Cancelled,
}

internal sealed record PrivacyRedactionResult(
    PrivacyRedactionStatus Status,
    IReadOnlyList<RectD> Regions,
    string? Message = null);

internal interface IPrivacyRedactionService
{
    bool IsAvailable { get; }

    Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bridges local Windows OCR to the pure privacy detector. Recognised text is kept inside this
/// method only; callers receive padded image rectangles without matched plaintext.
/// </summary>
internal sealed class PrivacyRedactionService : IPrivacyRedactionService
{
    private const double PaddingPixels = 3;
    private readonly IOcrService _ocr;
    private readonly IPrivacyDetector _detector;
    private readonly Func<OcrSettings> _settings;

    internal PrivacyRedactionService(
        IOcrService ocr,
        IPrivacyDetector detector,
        Func<OcrSettings> settings)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool IsAvailable => _ocr.IsAvailable;

    public async Task<PrivacyRedactionResult> FindAsync(
        BitmapSource bitmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!_ocr.IsAvailable)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.Unavailable, []);
        }

        OcrSettings settings = _settings();
        OcrResult result;
        try
        {
            result = await _ocr.RecognizeAsync(
                    OcrRequest.FromBitmap(
                        bitmap,
                        settings.UpscaleFactor,
                        settings.PreferredLanguages),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.Cancelled, []);
        }

        if (result.Status == OcrStatus.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.Cancelled, []);
        }

        if (result.Status == OcrStatus.Unavailable)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.Unavailable, [], result.Message);
        }

        if (result.Status == OcrStatus.Failed)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.Failed, [], result.Message);
        }

        if (result.Status == OcrStatus.NoText)
        {
            return new PrivacyRedactionResult(PrivacyRedactionStatus.NoMatches, []);
        }

        var tokens = new List<PrivacyToken>();
        for (int lineIndex = 0; lineIndex < result.Lines.Count; lineIndex++)
        {
            OcrLine line = result.Lines[lineIndex];
            for (int tokenIndex = 0; tokenIndex < line.Words.Count; tokenIndex++)
            {
                OcrWord word = line.Words[tokenIndex];
                tokens.Add(new PrivacyToken(
                    word.Text,
                    lineIndex,
                    tokenIndex,
                    new RectD(word.Bounds.X, word.Bounds.Y, word.Bounds.Width, word.Bounds.Height)));
            }
        }

        RectD imageBounds = new(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        IReadOnlyList<RectD> regions = _detector.Detect(tokens)
            .Select(match => match.Bounds.Inflate(PaddingPixels).ClampTo(imageBounds).ToPixelBounds())
            .Where(static region => !region.IsEmpty)
            .Distinct()
            .ToList();

        return regions.Count == 0
            ? new PrivacyRedactionResult(PrivacyRedactionStatus.NoMatches, [])
            : new PrivacyRedactionResult(PrivacyRedactionStatus.Success, regions);
    }
}
