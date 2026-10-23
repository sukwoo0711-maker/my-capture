using System.Diagnostics;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace MyCapture.Ocr;

/// <summary>
/// Routes Accurate and Enhanced requests through PP-OCR when the downloaded Korean model is
/// present, and keeps Windows OCR as the Fast/Balanced engine and as a fallback.
/// </summary>
public sealed class CaptureOcrService : IOcrService
{
    private readonly IOcrService _windows;
    private readonly Func<OcrRequest, CancellationToken, Task<OcrResult>> _recognizeNeural;
    private readonly Func<bool> _neuralCanRun;
    private readonly Func<bool> _neuralIsReady;
    private readonly ILogger _log;
    private readonly bool _enableDualEngine;

    internal CaptureOcrService(
        WindowsOcrService windows,
        NeuralOcrEngine neural,
        ILogger<CaptureOcrService> log)
        : this(
            windows,
            (neural ?? throw new ArgumentNullException(nameof(neural))).RecognizeAsync,
            () => neural.CanRun,
            () => neural.IsReady,
            log)
    {
    }

    // Keep the production default off until the per-category accuracy and wall-time
    // benchmark gates pass. The switch belongs to the instance, so tests cannot alter
    // another request's routing through process-wide state.
    internal CaptureOcrService(
        IOcrService windows,
        Func<OcrRequest, CancellationToken, Task<OcrResult>> recognizeNeural,
        Func<bool> neuralCanRun,
        Func<bool> neuralIsReady,
        ILogger<CaptureOcrService> log,
        bool enableDualEngine = false)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _recognizeNeural = recognizeNeural ?? throw new ArgumentNullException(nameof(recognizeNeural));
        _neuralCanRun = neuralCanRun ?? throw new ArgumentNullException(nameof(neuralCanRun));
        _neuralIsReady = neuralIsReady ?? throw new ArgumentNullException(nameof(neuralIsReady));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enableDualEngine = enableDualEngine;
    }

    public bool IsAvailable => _windows.IsAvailable || _neuralCanRun();

    public IReadOnlyList<string> SupportedLanguages
    {
        get
        {
            if (!_neuralIsReady())
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

        if (_enableDualEngine && request.UseNeuralModel)
        {
            return await RecognizeDualAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.UseNeuralModel)
        {
            try
            {
                OcrResult neural = await _recognizeNeural(request, cancellationToken)
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

    private async Task<OcrResult> RecognizeDualAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Cancelled();
        }

        // A missing model keeps the original Windows fallback, including the caller's
        // preprocessing. CanRun is an installed-model check; it does not fetch models.
        if (!_neuralCanRun())
        {
            return await RecognizeSafelyAsync(
                _windows.RecognizeAsync, "Windows OCR", request, cancellationToken).ConfigureAwait(false);
        }

        // Do not add an unrelated user-profile language to a neural-only request when
        // the caller explicitly asks for languages Windows does not have installed.
        bool windowsSupportsRequest = _windows.IsAvailable &&
            (!request.PreferredLanguages.Any(tag => !string.IsNullOrWhiteSpace(tag)) ||
                OcrPlanner.SelectLanguages(request.PreferredLanguages, _windows.SupportedLanguages).Count > 0);
        if (!windowsSupportsRequest)
        {
            return await RecognizeSafelyAsync(
                _recognizeNeural, "PP-OCR", request, cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        OcrRequest prepared;
        try
        {
            prepared = PrepareDualRequest(request);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Cancelled();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OCR could not prepare the shared image");
            return OcrResult.Failed(UiText.Get("Text_475D185DC33B"), stopwatch.Elapsed);
        }

        // Start both before awaiting either. Windows sees the same Fast pipeline as
        // the benchmark, while Accurate/Enhanced preprocessing stays on PP-OCR.
        Task<OcrResult> windowsTask = RecognizeSafelyAsync(
            _windows.RecognizeAsync, "Windows OCR", CreateFastWindowsRequest(prepared), cancellationToken);
        Task<OcrResult> neuralTask = RecognizeSafelyAsync(
            _recognizeNeural, "PP-OCR", prepared, cancellationToken);
        await Task.WhenAll(windowsTask, neuralTask).ConfigureAwait(false);
        stopwatch.Stop();

        OcrResult windows = await windowsTask.ConfigureAwait(false);
        OcrResult neural = await neuralTask.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested ||
            windows.Status == OcrStatus.Cancelled || neural.Status == OcrStatus.Cancelled)
        {
            return OcrResult.Cancelled();
        }

        return OcrResultMerger.Merge(windows, neural, stopwatch.Elapsed);
    }

    private async Task<OcrResult> RecognizeSafelyAsync(
        Func<OcrRequest, CancellationToken, Task<OcrResult>> recognize,
        string engineName,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            OcrResult result = await recognize(request, cancellationToken).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested ? OcrResult.Cancelled() : result;
        }
        catch (OperationCanceledException)
        {
            return OcrResult.Cancelled();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Engine} recognition failed", engineName);
            return OcrResult.Failed(UiText.Get("Text_AA254F35F02E"), stopwatch.Elapsed);
        }
    }

    internal static OcrRequest PrepareDualRequest(OcrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Bitmap is not BitmapFrame { IsFrozen: true } frame)
        {
            return request;
        }

        // A frozen decoder frame still retains its thread-affine BitmapDecoder. A
        // FormatConvertedBitmap's CanFreeze can traverse that decoder on an OCR worker
        // and throw. Copy decoded pixels once before either worker uses the image; a
        // frozen BitmapSource created from those pixels has no decoder dependency.
        int stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
        frame.CopyPixels(pixels, stride, 0);
        BitmapSource snapshot = BitmapSource.Create(
            frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY,
            frame.Format, frame.Palette, pixels, stride);
        snapshot.Freeze();
        return OcrRequest.FromBitmap(
            snapshot, request.UpscaleFactor, request.PreferredLanguages,
            request.SearchRotatedOrientations, request.EnhanceContrast,
            request.UseNeuralModel, request.LocalCorrection, request.UseSuperResolution);
    }

    // Match the product Fast profile, including no rotated-orientation search. Only
    // the source and language preferences carry over from the neural request.
    internal static OcrRequest CreateFastWindowsRequest(OcrRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Bitmap is not null)
        {
            return OcrRequest.FromBitmap(
                request.Bitmap,
                preferredLanguages: request.PreferredLanguages,
                searchRotatedOrientations: false);
        }

        if (request.EncodedImage is not null)
        {
            return OcrRequest.FromBytes(
                request.EncodedImage,
                preferredLanguages: request.PreferredLanguages,
                searchRotatedOrientations: false);
        }

        return OcrRequest.FromFile(
            request.FilePath!,
            preferredLanguages: request.PreferredLanguages,
            searchRotatedOrientations: false);
    }
}
