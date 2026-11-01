using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static class PinTextCopyService
{
    private static readonly ConditionalWeakTable<BitmapSource, RecognitionMemory> Memories = new();

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
        RecognitionMemory memory = Memories.GetValue(image, static _ => new RecognitionMemory());
        string? text = await memory.GetTextAsync(image, ocr, request);
        return text is not null && await copyText(text);
    }

    private sealed class RecognitionMemory
    {
        private readonly object _gate = new();
        private readonly List<Recognition> _pending = [];
        private Recognition? _current;

        internal Task<string?> GetTextAsync(BitmapSource image, IOcrService ocr, OcrRequest request)
        {
            Recognition recognition;
            lock (_gate)
            {
                if (_current is not null && _current.Key.Matches(request))
                    return _current.Completion.Task;

                // A settings change can leave another recognition in flight. Reuse it if
                // the user switches back before it finishes, without retaining old results.
                recognition = _pending.Find(item => item.Key.Matches(request))
                    ?? new Recognition(new RecognitionKey(request));
                _current = recognition;
                if (_pending.Contains(recognition)) return recognition.Completion.Task;
                _pending.Add(recognition);
            }

            // Publish the task before starting OCR, including synchronously completing OCR.
            _ = RecognizeAsync(recognition, ocr, recognition.Key.CreateRequest(image));
            return recognition.Completion.Task;
        }

        private async Task RecognizeAsync(Recognition recognition, IOcrService ocr, OcrRequest request)
        {
            try
            {
                OcrResult result = await ocr.RecognizeAsync(request).ConfigureAwait(false);
                Finish(recognition, result.HasText);
                recognition.Completion.SetResult(result.HasText ? result.Text : null);
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
                _pending.Remove(recognition);
                if (!remember && ReferenceEquals(_current, recognition)) _current = null;
            }
        }
    }

    private sealed class Recognition(RecognitionKey key)
    {
        internal RecognitionKey Key { get; } = key;
        internal TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Snapshot the effective request, not the mutable settings object. Keep only text
    // and recognition options after completion; the weak-table value need not hold pixels.
    private sealed class RecognitionKey(OcrRequest request)
    {
        private readonly string[] _languages = request.PreferredLanguages.ToArray();
        private readonly double _upscale = request.UpscaleFactor;
        private readonly bool _searchRotated = request.SearchRotatedOrientations;
        private readonly bool _enhanceContrast = request.EnhanceContrast;
        private readonly bool _useNeuralModel = request.UseNeuralModel;
        private readonly bool _localCorrection = request.LocalCorrection;
        private readonly bool _useSuperResolution = request.UseSuperResolution;

        internal bool Matches(OcrRequest other) =>
            _upscale.Equals(other.UpscaleFactor)
            && _searchRotated == other.SearchRotatedOrientations
            && _enhanceContrast == other.EnhanceContrast
            && _useNeuralModel == other.UseNeuralModel
            && _localCorrection == other.LocalCorrection
            && _useSuperResolution == other.UseSuperResolution
            && _languages.SequenceEqual(other.PreferredLanguages, StringComparer.Ordinal);

        internal OcrRequest CreateRequest(BitmapSource image) =>
            OcrRequest.FromBitmap(image, _upscale, _languages, _searchRotated,
                _enhanceContrast, _useNeuralModel, _localCorrection, _useSuperResolution);
    }
}
