using MyCapture.Core.Platform;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace MyCapture.Ocr;

/// <summary>
/// Runs Accurate requests through PP-OCR and Windows OCR in parallel when the
/// downloaded Korean model is present, and merges their lines; Windows OCR serves
/// Fast/Balanced requests, with a targeted second reading for short Hangul intrusions
/// in otherwise Latin lines, and remains the fallback when the model is missing.
/// </summary>
public sealed class CaptureOcrService : IOcrService
{
    private readonly IOcrService _windows;
    private readonly Func<OcrRequest, CancellationToken, Task<OcrResult>> _recognizeNeural;
    private readonly Func<OcrRequest, IReadOnlyList<OcrLine>, CancellationToken, Task<OcrResult>>? _readNeuralRows;
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
            log,
            readNeuralRows: neural.ReadRowsAsync)
    {
    }

    // On by default since the 2026-10-23 benchmark gates passed on the original, validation2
    // and held-out validation3 corpora: every category CER within 0.5 pp of the better single
    // engine, overall CER below both, and median wall time within 30 ms of PP-OCR alone
    // (OcrModelBenchmarkTests, A-dual). The switch belongs to the instance, so tests cannot
    // alter another request's routing through process-wide state. Without a row reader the
    // Latin recovery of upright Fast requests is off.
    internal CaptureOcrService(
        IOcrService windows,
        Func<OcrRequest, CancellationToken, Task<OcrResult>> recognizeNeural,
        Func<bool> neuralCanRun,
        Func<bool> neuralIsReady,
        ILogger<CaptureOcrService> log,
        bool enableDualEngine = true,
        Func<OcrRequest, IReadOnlyList<OcrLine>, CancellationToken, Task<OcrResult>>? readNeuralRows = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _recognizeNeural = recognizeNeural ?? throw new ArgumentNullException(nameof(recognizeNeural));
        _readNeuralRows = readNeuralRows;
        _neuralCanRun = neuralCanRun ?? throw new ArgumentNullException(nameof(neuralCanRun));
        _neuralIsReady = neuralIsReady ?? throw new ArgumentNullException(nameof(neuralIsReady));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enableDualEngine = enableDualEngine;
    }

    public bool IsAvailable => _windows.IsAvailable || NeuralCanRunSafely();

    public IReadOnlyList<string> SupportedLanguages
    {
        get
        {
            if (!AppArchitecture.SupportsNeuralOcr || !_neuralIsReady())
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
        if (!AppArchitecture.SupportsNeuralOcr)
            return await _windows.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);

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

        OcrResult windows = await _windows.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
        // Fast is the default, so these requests never reach the dual-engine merge above.
        // The Korean Windows pack can read English function words as Hangul. When the caller
        // asks for it (image translation), consult the installed Latin-capable model only for
        // that pattern, retaining every other word and all Windows geometry; a missing model
        // keeps the ordinary Fast result. Plain recognition never pays for the second reading.
        // Windows OCR has already found the rows, so the recognizer alone reads only those that
        // hold a suspect, each bounded to its own visual row with up to three Latin anchors either side.
        // Row crops are horizontal source strips, so a rotated reading keeps the whole-image pass,
        // whose source-axis boxes Recover maps through the reading orientation.
        bool rotated = windows.ReadingOrientation.IsRotated;
        if (!request.RecoverLatinIntrusions || !_enableDualEngine || !rotated && _readNeuralRows is null
            || !OcrLatinWordRecovery.HasSuspects(windows) || !NeuralCanRunSafely())
            return windows;
        request.BeforeLatinRecovery?.Invoke(windows);
        var recoveryWatch = Stopwatch.StartNew();
        OcrLine[] rows = rotated ? [] : [.. OcrLatinWordRecovery.SuspectRows(windows)];
        OcrResult second = await RecognizeSafelyAsync((input, token) => rotated
                ? _recognizeNeural(PrepareDualRequest(input), token) : _readNeuralRows!(input, rows, token),
            "PP-OCR Latin recovery", request, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || second.Status == OcrStatus.Cancelled)
            return OcrResult.Cancelled();
        TimeSpan elapsed = windows.Elapsed + recoveryWatch.Elapsed;
        OcrResult recovered = OcrLatinWordRecovery.Recover(windows, second, elapsed);
        // A failed or inconclusive second reading still took time. Preserve the source
        // text/geometry, but include all attempted work in the result's elapsed metadata.
        return OcrResult.Success(recovered.Text, recovered.LanguageTag ?? string.Empty, recovered.Lines,
            elapsed, recovered.ReadingOrientation, recovered.TextAngle);
    }

    private async Task<OcrResult> RecognizeDualAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return OcrResult.Cancelled();
        }

        // A missing or unreadable model keeps the original Windows fallback, including the
        // caller's preprocessing. CanRun is an installed-model check; it does not fetch models.
        if (!NeuralCanRunSafely())
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
        // the benchmark, while Accurate preprocessing stays on PP-OCR.
        OcrRequest fastWindows = CreateFastWindowsRequest(prepared);
        Task<OcrResult> windowsTask = RecognizeSafelyAsync(
            _windows.RecognizeAsync, "Windows OCR", fastWindows, cancellationToken);
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

        // Without a PP-OCR reading the merge would return only the reduced Fast pass. Read the
        // same snapshot again with the caller's own preprocessing, as the missing-model path
        // does. A healthy PP-OCR result, including NoText, keeps the parallel merge.
        if (neural.Status is OcrStatus.Failed or OcrStatus.Unavailable &&
            !SameWindowsPreprocessing(prepared, fastWindows))
        {
            _log.LogInformation("PP-OCR returned {Status}; repeating Windows OCR with the original preprocessing",
                neural.Status);
            OcrResult original = await RecognizeSafelyAsync(
                _windows.RecognizeAsync, "Windows OCR", prepared, cancellationToken).ConfigureAwait(false);
            // Text the Fast pass already found is never replaced by an empty or failed retry.
            return original.Status == OcrStatus.Cancelled || original.HasText || !windows.HasText
                ? original : windows;
        }

        return OcrResultMerger.Merge(windows, neural, stopwatch.Elapsed);
    }

    private bool NeuralCanRunSafely()
    {
        try
        {
            return AppArchitecture.SupportsNeuralOcr && _neuralCanRun();
        }
        catch (Exception ex)
        {
            // Model verification hashes the installed files; a locked or unreadable file
            // (IOException, UnauthorizedAccessException) must not escape recognition.
            _log.LogWarning(ex, "PP-OCR availability check failed; using Windows OCR");
            return false;
        }
    }

    private static bool SameWindowsPreprocessing(OcrRequest left, OcrRequest right) =>
        left.SearchRotatedOrientations == right.SearchRotatedOrientations &&
        left.UpscaleFactor.Equals(right.UpscaleFactor) &&
        left.EnhanceContrast == right.EnhanceContrast;

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
