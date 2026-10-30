using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using MyCapture.Core.Settings;

namespace MyCapture.App.Ocr;

/// <summary>
/// Owns the one reusable <see cref="OcrResultWindow"/> and drives recognition for the whole app.
/// </summary>
/// <remarks>
/// <para>
/// Both the gallery and pinned windows recognise through this single presenter, so there is one
/// result window, one in-flight cancellation, and one place that toggles the tray to Busy during
/// recognition and restores it afterwards. A new request cancels any previous in-flight one.
/// </para>
/// <para>
/// The presenter is deliberately UI-thread bound (it touches a WPF window and the tray). It does
/// not know how a request produced its bytes — the gallery caches results on the capture record,
/// a pin does not — so it takes a <see cref="Func{TResult}"/> that rebuilds the request for the
/// rerun button and an optional callback invoked with a fresh successful result so the caller can
/// persist it.
/// </para>
/// </remarks>
internal sealed class OcrResultPresenter : IDisposable
{
    private readonly IOcrService _service;
    private readonly Dispatcher _dispatcher;
    private readonly Action<bool> _setBusy;
    private readonly ILogger _log;
    private readonly OcrResultWindow _window;
    private readonly OcrAccuracyOptions? _accuracy;
    private CancellationTokenSource? _qualityCancellation;
    private BitmapSource? _currentSnapshot;
    private OcrRequest? _currentSourceRequest;

    internal OcrResultWindow Window => _window;

    private CancellationTokenSource? _inFlight;
    private CancellationTokenSource? _cachedImageLoad;
    private Func<OcrRequest>? _currentRequestFactory;
    private string _currentContext = string.Empty;
    private Action<OcrResult>? _onFreshResult;
    private bool _disposed;
    private long _presentationVersion;
    private int _mutationSuspensions;

    /// <summary>Invalidates both running and already queued record-writing callbacks.</summary>
    internal async Task<IDisposable> AcquireMutationLeaseAsync(CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        _mutationSuspensions++;
        CancelInFlight();
        _currentRequestFactory = null;
        _onFreshResult = null;
        _setBusy(false);
        var lease = new MutationLease(this);
        try
        {
            // Earlier Normal callbacks drain; later completions retain their invalidated owner.
            await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, cancellationToken);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed class MutationLease(OcrResultPresenter owner) : IDisposable
    {
        private OcrResultPresenter? _owner = owner;
        public void Dispose()
        {
            OcrResultPresenter? presenter = Interlocked.Exchange(ref _owner, null);
            if (presenter is null) return;
            presenter._dispatcher.VerifyAccess();
            presenter._mutationSuspensions--;
        }
    }

    internal OcrResultPresenter(
        IOcrService service,
        Dispatcher dispatcher,
        Action<bool> setBusy,
        ILogger log,
        ITextTranslator? translator = null,
        OcrAccuracyOptions? accuracy = null,
        OcrResultWindow? window = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _accuracy = accuracy;
        _window = window ?? new OcrResultWindow();
        _window.Translator = translator;
        if (accuracy is not null) _window.ConfigureAccuracy(accuracy, ChangeQualityAsync, CancelQualityChange);
        _window.RerunRequested += (_, _) => Rerun();
        _window.GeometryRequested += (_, _) => Rerun();
        _window.Dismissed += (_, _) =>
        {
            // Dismissing a busy reusable window is a real user cancellation, not merely a visual
            // hide. Clearing ownership before signalling cancellation also prevents a late result
            // from showing and activating the window again.
            ReleasePresentation();
        };
    }

    /// <summary>
    /// Shows an already-cached result immediately and offers a rerun that rebuilds the request.
    /// </summary>
    /// <param name="cachedResult">The result to display at once (from a cached record).</param>
    /// <param name="contextLabel">Short label for the status line (e.g. the capture title).</param>
    /// <param name="requestFactory">Rebuilds the request when the user reruns.</param>
    /// <param name="onFreshResult">Invoked on the UI thread with a fresh result from a rerun.</param>
    internal void ShowCached(
        OcrResult cachedResult,
        string contextLabel,
        Func<OcrRequest> requestFactory,
        Action<OcrResult>? onFreshResult = null)
    {
        if (_disposed || _mutationSuspensions != 0) return;
        // A cached result replaces the current request just as decisively as a new recognition.
        // Clear ownership before changing callbacks so a late result from A can never be rendered
        // under B's label or persisted through B's callback.
        CancelInFlight();
        _setBusy(false);
        _currentRequestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _currentContext = contextLabel ?? string.Empty;
        _onFreshResult = onFreshResult;
        _currentSnapshot = null;
        _currentSourceRequest = null;
        long version = _presentationVersion;
        _window.ShowResult(cachedResult, _currentContext);
        if (version == _presentationVersion) _ = LoadCachedImageAsync(requestFactory, version);
    }

    /// <summary>
    /// Runs recognition for a request and shows the result. Cancels any prior in-flight request,
    /// flips the tray to Busy for the duration, and remembers the request so the rerun button
    /// works. Never throws: the service reports failures as a typed result.
    /// </summary>
    internal void ShowRecognized(
        Func<OcrRequest> requestFactory,
        string contextLabel,
        Action<OcrResult>? onFreshResult = null)
    {
        if (_disposed || _mutationSuspensions != 0) return;
        _currentRequestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _currentContext = contextLabel ?? string.Empty;
        _onFreshResult = onFreshResult;
        _currentSnapshot = null;
        _currentSourceRequest = null;
        Run();
    }

    private void Rerun()
    {
        long version = _presentationVersion;
        if (_currentRequestFactory is not null && _window.ConfirmReplacement()
            && version == _presentationVersion && !_disposed)
        {
            Run(preserveSelection: true);
        }
    }

    private void Run(bool preserveSelection = false)
    {
        if (_disposed || _mutationSuspensions != 0 || _currentRequestFactory is null)
        {
            return;
        }

        // Cancel any prior request before starting a new one.
        CancelInFlight();

        long version = _presentationVersion;
        OcrRequest request;
        try
        {
            request = preserveSelection && _currentSnapshot is not null && _currentSourceRequest is not null
                ? OcrSourceSnapshot.WindowsRequest(_currentSourceRequest, _currentSnapshot)
                : _currentRequestFactory();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not build the OCR request");
            if (_disposed || version != _presentationVersion) return;
            // Run() may just have cancelled a previous owner. Its late completion is intentionally
            // ignored, so this boundary must release the tray busy state itself.
            _setBusy(false);
            if (preserveSelection) _window.CompletePreservedRerun(UiText.Get("Text_F09A6C5A6A10"));
            else _window.ShowResult(OcrResult.Failed(UiText.Get("Text_F09A6C5A6A10")), _currentContext);
            return;
        }

        // A request factory can synchronously dismiss or replace this presentation.
        if (_disposed || version != _presentationVersion) return;
        var cts = new CancellationTokenSource();
        _inFlight = cts;
        string? acceptedText = preserveSelection ? _window.ResultTextBox.Text : null;

        _window.ShowBusy(_currentContext, preserveSelection);
        if (!ReferenceEquals(_inFlight, cts))
        {
            cts.Dispose();
            return;
        }
        _setBusy(true);

        _ = RecognizeAsync(request, cts, preserveSelection, acceptedText);
    }

    private async Task RecognizeAsync(OcrRequest request, CancellationTokenSource cts, bool preserveSelection, string? acceptedText)
    {
        OcrResult result;
        BitmapSource? source = null;
        try
        {
            source = await OcrSourceSnapshot.LoadAsync(request, cts.Token).ConfigureAwait(true);
            result = source is null ? OcrResult.Failed(UiText.Get("Text_475D185DC33B"))
                : await _service.RecognizeAsync(OcrSourceSnapshot.WindowsRequest(request, source), cts.Token)
                    .ConfigureAwait(true);
            result = result.WithQuality(OcrQualityProfile.FromUpscale(request.UpscaleFactor));
        }
        catch (OperationCanceledException)
        {
            result = OcrResult.Cancelled();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "OCR recognition threw unexpectedly");
            result = OcrResult.Failed(UiText.Get("Text_AA254F35F02E"));
        }

        // Marshal back to the UI thread for all window/tray updates.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            // Only the newest request updates the UI; a superseded/cancelled one is ignored.
            if (_disposed || _mutationSuspensions != 0 || !ReferenceEquals(_inFlight, cts))
            {
                cts.Dispose();
                return;
            }

            _setBusy(false);

            if (preserveSelection && result.Status is OcrStatus.Success or OcrStatus.NoText
                && acceptedText != _window.ResultTextBox.Text)
            {
                bool replace = _window.ConfirmReplacement();
                if (_disposed || _mutationSuspensions != 0 || !ReferenceEquals(_inFlight, cts))
                {
                    cts.Dispose();
                    return;
                }
                if (!replace)
                {
                    _window.CompletePreservedRerun();
                    _inFlight = null;
                    cts.Dispose();
                    return;
                }
            }

            if (preserveSelection && result.Status is OcrStatus.Failed or OcrStatus.Unavailable or OcrStatus.Cancelled)
            {
                _window.CompletePreservedRerun(result.Status == OcrStatus.Cancelled
                    ? UiText.Format("Text_5626DD9C2F6A", _currentContext)
                    : result.Message ?? UiText.Get("Text_AA254F35F02E"));
            }
            else
            {
                _currentSnapshot = source;
                _currentSourceRequest = request;
                _window.ShowResult(result, _currentContext, source, preserveSelection);

                if (result.Status == OcrStatus.Success && _mutationSuspensions == 0 && ReferenceEquals(_inFlight, cts))
                {
                    _onFreshResult?.Invoke(result);
                }
            }

            // Showing a result can synchronously request geometry and start a new run.
            if (ReferenceEquals(_inFlight, cts)) _inFlight = null;
            cts.Dispose();
        }));
    }

    private async Task LoadCachedImageAsync(Func<OcrRequest> requestFactory, long version)
    {
        var cancellation = new CancellationTokenSource();
        _cachedImageLoad = cancellation;
        CancellationToken token = cancellation.Token;
        try
        {
            OcrRequest request = requestFactory();
            BitmapSource? source = await OcrSourceSnapshot.LoadAsync(request, token)
                .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed && version == _presentationVersion && !token.IsCancellationRequested)
                {
                    _currentSnapshot = source;
                    _currentSourceRequest = request;
                    _window.SetSourceImage(source);
                }
            }, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            // The cached text remains usable if its source was moved or removed.
            _log.LogWarning(error, "Could not load the cached OCR image");
        }
        finally
        {
            // This method can finish on a decoder thread; ownership stays on the dispatcher.
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (ReferenceEquals(_cachedImageLoad, cancellation)) _cachedImageLoad = null;
                cancellation.Dispose();
            }));
        }
    }

    private void ReleasePresentation()
    {
        _currentRequestFactory = null;
        _currentContext = string.Empty;
        _onFreshResult = null;
        _currentSnapshot = null;
        _currentSourceRequest = null;
        CancelInFlight();
        _setBusy(false);
    }

    private void CancelInFlight()
    {
        _presentationVersion++;
        CancellationTokenSource? quality = _qualityCancellation;
        _qualityCancellation = null;
        quality?.Cancel();
        if (quality is not null) _window.EndQualityOperation();
        CancellationTokenSource? previous = _inFlight;
        _inFlight = null;
        CancellationTokenSource? cachedImage = _cachedImageLoad;
        _cachedImageLoad = null;
        cachedImage?.Cancel();
        if (previous is not null)
        {
            _window.CompletePreservedRerun();
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal void CancelQualityChange()
    {
        if (_qualityCancellation is null) return;
        CancelInFlight();
        _setBusy(false);
    }

    internal async Task ChangeQualityAsync(OcrQuality quality)
    {
        _dispatcher.VerifyAccess();
        quality = OcrQualityNames.Normalize(quality);
        if (_disposed || _mutationSuspensions != 0 || _currentRequestFactory is null || _accuracy is null
            || _inFlight is not null || _qualityCancellation is not null || quality == _window.ResultQuality)
        {
            _window.RestoreQualitySelection();
            return;
        }
        long confirmationVersion = _presentationVersion;
        if (!_window.ConfirmReplacement() || confirmationVersion != _presentationVersion || _disposed)
        {
            _window.RestoreQualitySelection();
            return;
        }

        string acceptedText = _window.ResultTextBox.Text;
        CancelInFlight();
        long version = _presentationVersion;
        using var cancellation = new CancellationTokenSource();
        _qualityCancellation = cancellation;
        bool OwnsOperation() => !_disposed && _mutationSuspensions == 0 && version == _presentationVersion
            && ReferenceEquals(_qualityCancellation, cancellation) && !cancellation.IsCancellationRequested;
        string? failure = null;
        bool download = false;
        try
        {
            _window.BeginQualityOperation(downloading: false);
            _setBusy(true);
            bool neural = _accuracy.Model.IsSupported && OcrQualityProfile.UseNeuralModel(quality);
            if (neural)
            {
                download = !await Task.Run(() => _accuracy.Model.IsReady, cancellation.Token);
                if (!OwnsOperation()) return;
                if (download) _window.BeginQualityOperation(downloading: true);
            }
            OcrRequest original = _currentSourceRequest ?? _currentRequestFactory();
            BitmapSource? source = _currentSnapshot ?? await OcrSourceSnapshot.LoadAsync(original, cancellation.Token);
            if (!OwnsOperation()) return;
            if (source is null)
            {
                failure = UiText.Get("Text_475D185DC33B");
                return;
            }
            if (download)
            {
                // Render the inline size/cost notice before the explicit download starts.
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (!OwnsOperation()) return;
                var progress = new Progress<double>(value => { if (OwnsOperation() && download) _window.ReportQualityProgress(value); });
                bool ready = await _accuracy.Model.DownloadAsync(progress, cancellation.Token);
                if (!OwnsOperation()) return;
                if (!ready)
                {
                    failure = UiText.Get("Translate_DownloadFailed");
                    return;
                }
                download = false;
                _window.BeginQualityOperation(downloading: false);
            }

            OcrRequest request = OcrRequest.FromBitmap(source, OcrQualityProfile.UpscaleFactor(quality),
                original.PreferredLanguages.ToArray(), OcrQualityProfile.SearchRotatedOrientations(quality),
                OcrQualityProfile.EnhanceContrast(quality), neural);
            OcrResult result = await _service.RecognizeAsync(request, cancellation.Token);
            if (!OwnsOperation()) return;
            if (result.Status == OcrStatus.Cancelled) return;
            if (result.Status is not (OcrStatus.Success or OcrStatus.NoText))
            {
                failure = result.Message ?? UiText.Get("Text_AA254F35F02E");
                return;
            }
            if (_window.ResultTextBox.Text != acceptedText && !_window.ConfirmReplacement()) return;
            if (!OwnsOperation()) return;

            OcrSettings settings = _accuracy.ReadSettings();
            (OcrQuality previousQuality, double previousUpscale, bool previousDismissed) =
                (settings.Quality, settings.UpscaleFactor, settings.AccuracySuggestionDismissed);
            try
            {
                settings.Quality = quality;
                OcrQualityProfile.Synchronize(settings);
                if (quality == OcrQuality.Accurate) settings.AccuracySuggestionDismissed = true;
                _accuracy.SaveSettings();
            }
            catch (Exception error)
            {
                settings.Quality = previousQuality;
                settings.UpscaleFactor = previousUpscale;
                settings.AccuracySuggestionDismissed = previousDismissed;
                _log.LogWarning(error, "Could not save OCR accuracy");
                failure = UiText.Get("Ocr_AccuracySaveFailed");
                return;
            }
            if (!OwnsOperation()) return;
            result = result.WithQuality(quality);
            _currentSnapshot = source;
            _currentSourceRequest = request;
            _window.ShowResult(result, _currentContext, source, preserveSelection: true);
            if (OwnsOperation() && result.Status == OcrStatus.Success) _onFreshResult?.Invoke(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            _log.LogWarning(error, "Could not change OCR accuracy");
            failure = UiText.Get(download ? "Translate_DownloadFailed" : "Text_AA254F35F02E");
        }
        finally
        {
            if (ReferenceEquals(_qualityCancellation, cancellation))
            {
                _qualityCancellation = null;
                _setBusy(false);
                if (failure is null) _window.EndQualityOperation();
                else _window.ShowAccuracyFailure(failure);
            }
        }
    }

    /// <summary>Cancels any in-flight recognition and closes the window; used on app exit.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleasePresentation();
        _window.ReleaseResult();

        try
        {
            _window.CloseForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
