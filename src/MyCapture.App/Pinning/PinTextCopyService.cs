using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static class PinTextCopyService
{
    private static readonly ConditionalWeakTable<BitmapSource, RecognitionMemory> Memories = new();

    internal static void Forget(BitmapSource image) => Memories.Remove(image);

    internal static Task<bool> CopyOriginalAsync(string originalText, Func<string, Task<bool>> copyText) =>
        copyText(originalText);

    internal static Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        double upscale,
        IReadOnlyList<string> languages) =>
        CopyAsync(image, ocr, copyText, OcrRequest.FromBitmap(image, upscale, languages));

    internal static Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        OcrSettings settings) =>
        CopyAsync(image, ocr, copyText, OcrRequestFactory.FromBitmap(image, settings));

    private static async Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        OcrRequest request)
    {
        OcrResult result = await RecognizeAsync(image, ocr, request);
        return result.HasText && await copyText(result.Text);
    }

    internal static Task<OcrResult> RecognizeAsync(BitmapSource image, IOcrService ocr,
        OcrSettings settings, CancellationToken cancellationToken = default) =>
        RecognizeAsync(image, ocr, OcrRequestFactory.FromBitmap(image, settings), cancellationToken);

    internal static Task<OcrResult> RecognizeAsync(BitmapSource image, IOcrService ocr,
        OcrRequest request, CancellationToken cancellationToken = default) =>
        Memories.GetValue(image, static _ => new RecognitionMemory())
            .GetResultAsync(image, ocr, request, cancellationToken);

    private sealed class RecognitionMemory
    {
        private readonly object _gate = new();
        private readonly List<Recognition> _pending = [];
        private Recognition? _current;

        internal Task<OcrResult> GetResultAsync(BitmapSource image, IOcrService ocr, OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Recognition recognition;
            bool start;
            lock (_gate)
            {
                if (_current is not null && _current.Key.Matches(request))
                    recognition = _current;
                else
                    recognition = _pending.Find(item => item.Key.Matches(request))
                        ?? new Recognition(new RecognitionKey(request));
                _current = recognition;
                start = !recognition.Started;
                if (start)
                {
                    recognition.Started = true;
                    _pending.Add(recognition);
                }
                recognition.Readers++;
            }

            // The optional translation preview callback is request-local, never retained in a
            // completed cache entry. Recovery itself is part of the key, not the callback.
            // Register cancellation before calling an engine which may prepare synchronously.
            Task<OcrResult> waiting = AwaitResultAsync(recognition, cancellationToken);
            if (start) _ = RecognizeAsync(recognition, ocr,
                recognition.Key.CreateRequest(image, request.BeforeLatinRecovery));
            return waiting;
        }

        private async Task<OcrResult> AwaitResultAsync(Recognition recognition, CancellationToken token)
        {
            try { return await recognition.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    if (--recognition.Readers == 0 && !recognition.Finished)
                    {
                        _pending.Remove(recognition);
                        if (ReferenceEquals(_current, recognition)) _current = null;
                        // Cancel only when the last consumer leaves. Copy/translation may still
                        // need a recognition whose selection view was dismissed.
                        recognition.Cancellation.Cancel();
                    }
                }
            }
        }

        private async Task RecognizeAsync(Recognition recognition, IOcrService ocr, OcrRequest request)
        {
            try
            {
                recognition.Cancellation.Token.ThrowIfCancellationRequested();
                OcrResult result = await ocr.RecognizeAsync(request, recognition.Cancellation.Token).ConfigureAwait(false);
                Finish(recognition, result.HasText);
                recognition.Completion.SetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                Finish(recognition, remember: false);
                recognition.Completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                Finish(recognition, remember: false);
                recognition.Completion.SetException(ex);
            }
        }

        private void Finish(Recognition recognition, bool remember)
        {
            lock (_gate)
            {
                recognition.Finished = true;
                _pending.Remove(recognition);
                if (!remember && ReferenceEquals(_current, recognition)) _current = null;
                recognition.Cancellation.Dispose();
            }
        }
    }

    private sealed class Recognition(RecognitionKey key)
    {
        internal RecognitionKey Key { get; } = key;
        internal bool Started { get; set; }
        internal bool Finished { get; set; }
        internal int Readers { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();
        internal TaskCompletionSource<OcrResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Snapshot effective options. Completed entries retain text/boxes, never pixels or engines.
    private sealed class RecognitionKey(OcrRequest request)
    {
        private readonly string[] _languages = request.PreferredLanguages.ToArray();
        private readonly double _upscale = request.UpscaleFactor;
        private readonly bool _searchRotated = request.SearchRotatedOrientations;
        private readonly bool _enhanceContrast = request.EnhanceContrast;
        private readonly bool _useNeuralModel = request.UseNeuralModel;
        private readonly bool _localCorrection = request.LocalCorrection;
        private readonly bool _useSuperResolution = request.UseSuperResolution;
        private readonly bool _recoverLatinIntrusions = request.RecoverLatinIntrusions;

        internal bool Matches(OcrRequest other) =>
            _upscale.Equals(other.UpscaleFactor)
            && _searchRotated == other.SearchRotatedOrientations
            && _enhanceContrast == other.EnhanceContrast
            && _useNeuralModel == other.UseNeuralModel
            && _localCorrection == other.LocalCorrection
            && _useSuperResolution == other.UseSuperResolution
            && _recoverLatinIntrusions == other.RecoverLatinIntrusions
            && _languages.SequenceEqual(other.PreferredLanguages, StringComparer.Ordinal);

        internal OcrRequest CreateRequest(BitmapSource image, Action<OcrResult>? beforeRecovery)
        {
            OcrRequest snapshot = OcrRequest.FromBitmap(image, _upscale, _languages, _searchRotated,
                _enhanceContrast, _useNeuralModel, _localCorrection, _useSuperResolution);
            return _recoverLatinIntrusions ? snapshot.WithLatinRecovery(beforeRecovery) : snapshot;
        }
    }
}
