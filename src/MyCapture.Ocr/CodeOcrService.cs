using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace MyCapture.Ocr;

/// <summary>Shares one decoded image with OCR and a bounded, background-only barcode reader.</summary>
internal sealed class CodeOcrService(IOcrService textService,
    Func<BitmapSource, CancellationToken, IReadOnlyList<DecodedImageCode>>? decode = null,
    TimeSpan? timeLimit = null) : IOcrService
{
    internal static readonly TimeSpan DecodeTimeLimit = TimeSpan.FromMilliseconds(200);
    private readonly Func<BitmapSource, CancellationToken, IReadOnlyList<DecodedImageCode>> _decode = decode ?? ImageCodeDecoder.Decode;
    private readonly TimeSpan _limit = timeLimit ?? DecodeTimeLimit;
    // Number of scans whose request already stopped waiting while the reader was still running.
    private int _overdueScans;

    /// <summary>True while a scan that outlived its request is still unwinding; new requests skip codes.</summary>
    internal bool HasOverdueScan => Volatile.Read(ref _overdueScans) > 0;

    public bool IsAvailable => textService.IsAvailable;
    public IReadOnlyList<string> SupportedLanguages => textService.SupportedLanguages;

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return OcrResult.Cancelled();
        BitmapSource? image;
        try
        {
            image = await Task.Run(() => Load(request), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return OcrResult.Cancelled(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or FormatException)
        {
            // Let the existing recognizer retain its failure/status contract.
            return await textService.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        if (image is null) return await textService.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);

        // A timed-out non-interruptible reader may still be unwinding. Never queue another
        // scan behind it or let repeated recognition requests retain a pile of images.
        // Scans that are still within their own limit do not block each other.
        if (HasOverdueScan)
            return await textService.RecognizeAsync(request.WithBitmap(image, request.BeforeLatinRecovery), cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_limit);
        CancellationToken decodeToken = deadline.Token;
        var watch = Stopwatch.StartNew();
        var scan = new ScanState();
        Task<IReadOnlyList<DecodedImageCode>> codes = Task.Run(() =>
        {
            try
            {
                var found = _decode(image, decodeToken);
                return watch.Elapsed <= _limit && !decodeToken.IsCancellationRequested ? found : [];
            }
            catch (OperationCanceledException) { return []; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
                or IndexOutOfRangeException) { return []; }
            finally
            {
                if (Interlocked.Exchange(ref scan.State, ScanState.Finished) == ScanState.Overdue)
                    Interlocked.Decrement(ref _overdueScans);
            }
        }, CancellationToken.None);
        // Observe completion even if a reader ignores cancellation. Its late result is never published.
        Task<IReadOnlyList<DecodedImageCode>> bounded = WithinLimitAsync(codes, scan, deadline.Token);
        Action<OcrResult>? beforeRecovery = request.BeforeLatinRecovery is null ? null : interim =>
        {
            // Do not pretranslate unfiltered OCR noise while the code scan is still pending.
            if (bounded.IsCompletedSuccessfully)
                request.BeforeLatinRecovery(CodeOcrResult.Merge(interim, bounded.Result));
        };
        OcrResult text = await textService.RecognizeAsync(request.WithBitmap(image, beforeRecovery), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DecodedImageCode> completed = await bounded.ConfigureAwait(false);
        return cancellationToken.IsCancellationRequested ? OcrResult.Cancelled() : CodeOcrResult.Merge(text, completed);
    }

    private async Task<IReadOnlyList<DecodedImageCode>> WithinLimitAsync(
        Task<IReadOnlyList<DecodedImageCode>> task, ScanState scan, CancellationToken token)
    {
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // The request stops waiting; if the reader is still running it becomes overdue and
            // blocks new scans until it returns. Count first so the reader's finally always sees it.
            Interlocked.Increment(ref _overdueScans);
            if (Interlocked.CompareExchange(ref scan.State, ScanState.Overdue, ScanState.Running) != ScanState.Running)
                Interlocked.Decrement(ref _overdueScans);
            return [];
        }
    }

    private sealed class ScanState
    {
        internal const int Running = 0, Overdue = 1, Finished = 2;
        internal int State;
    }

    private static BitmapSource? Load(OcrRequest request)
    {
        if (request.Bitmap is { } supplied)
        {
            // Caller-owned mutable pixels cannot be read from a worker. Existing callers freeze.
            if (!supplied.CheckAccess() || !supplied.IsFrozen) return null;
            return CaptureOcrService.PrepareDualRequest(request).Bitmap;
        }
        using Stream stream = request.EncodedImage is { } bytes
            ? new MemoryStream(bytes, writable: false) : File.OpenRead(request.FilePath!);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
