using Microsoft.Extensions.Logging;

namespace MyCapture.Ocr;

/// <summary>
/// Routes Accurate and Enhanced receipt requests through PP-OCR when the downloaded Korean model is
/// present, and keeps Windows OCR as the Fast/Balanced engine and as a fallback.
/// </summary>
public sealed class CaptureOcrService : IOcrService
{
    private readonly WindowsOcrService _windows;
    private readonly NeuralOcrEngine _neural;
    private readonly ILogger _log;

    internal CaptureOcrService(
        WindowsOcrService windows,
        NeuralOcrEngine neural,
        ILogger<CaptureOcrService> log)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _neural = neural ?? throw new ArgumentNullException(nameof(neural));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsAvailable => _windows.IsAvailable || _neural.CanRun;

    public IReadOnlyList<string> SupportedLanguages
    {
        get
        {
            if (!_neural.IsReady)
            {
                return _windows.SupportedLanguages;
            }

            var tags = new HashSet<string>(_windows.SupportedLanguages, StringComparer.OrdinalIgnoreCase)
            {
                "ko-KR",
                "en-US",
            };
            return [.. tags];
        }
    }

    public async Task<OcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UseNeuralModel)
        {
            try
            {
                OcrResult neural = await _neural.RecognizeAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (neural.Status == OcrStatus.Success && neural.HasText)
                {
                    return neural;
                }

                if (!_windows.IsAvailable)
                {
                    return neural;
                }

                _log.LogInformation(
                    "PP-OCR returned {Status}; falling back to Windows OCR",
                    neural.Status);
            }
            catch (OperationCanceledException)
            {
                return OcrResult.Cancelled();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PP-OCR recognition failed; falling back to Windows OCR");
                if (!_windows.IsAvailable)
                {
                    return OcrResult.Failed(UiText.Get("Text_AA254F35F02E"), TimeSpan.Zero);
                }
            }
        }

        return await _windows.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
