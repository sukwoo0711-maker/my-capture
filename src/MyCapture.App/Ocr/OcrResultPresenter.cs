using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

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

    private CancellationTokenSource? _inFlight;
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
        ITextTranslator? translator = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _window = new OcrResultWindow { Translator = translator };
        _window.RerunRequested += (_, _) => Rerun();
        _window.GeometryRequested += (_, _) => Rerun();
        _window.Dismissed += (_, _) =>
        {
            // Dismissing a busy reusable window is a real user cancellation, not merely a visual
            // hide. Clearing ownership before signalling cancellation also prevents a late result
            // from showing and activating the window again.
            CancelInFlight();
            _setBusy(false);
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
        Run();
    }

    private void Rerun()
    {
        if (_currentRequestFactory is not null)
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

        OcrRequest request;
        try
        {
            request = _currentRequestFactory();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not build the OCR request");
            // Run() may just have cancelled a previous owner. Its late completion is intentionally
            // ignored, so this boundary must release the tray busy state itself.
            _setBusy(false);
            _window.ShowResult(OcrResult.Failed(UiText.Get("Text_F09A6C5A6A10")), _currentContext);
            return;
        }

        var cts = new CancellationTokenSource();
        _inFlight = cts;

        _window.ShowBusy(_currentContext, preserveSelection);
        _setBusy(true);

        _ = RecognizeAsync(request, cts, preserveSelection);
    }

    private async Task RecognizeAsync(OcrRequest request, CancellationTokenSource cts, bool preserveSelection)
    {
        OcrResult result;
        BitmapSource? source = null;
        try
        {
            source = await OcrSourceSnapshot.LoadAsync(request, cts.Token).ConfigureAwait(true);
            result = source is null ? OcrResult.Failed(UiText.Get("Text_475D185DC33B"))
                : await _service.RecognizeAsync(OcrSourceSnapshot.WindowsRequest(request, source), cts.Token)
                    .ConfigureAwait(true);
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

            if (result.Status != OcrStatus.Cancelled)
            {
                _window.ShowResult(result, _currentContext, source, preserveSelection);

                if (result.Status == OcrStatus.Success && _mutationSuspensions == 0 && ReferenceEquals(_inFlight, cts))
                {
                    _onFreshResult?.Invoke(result);
                }
            }

            _inFlight = null;
            cts.Dispose();
        }));
    }

    private async Task LoadCachedImageAsync(Func<OcrRequest> requestFactory, long version)
    {
        try
        {
            OcrRequest request = requestFactory();
            BitmapSource? source = await OcrSourceSnapshot.LoadAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (!_disposed && version == _presentationVersion) _window.SetSourceImage(source);
            }));
        }
        catch (Exception error)
        {
            // The cached text remains usable if its source was moved or removed.
            _log.LogWarning(error, "Could not load the cached OCR image");
        }
    }

    private void CancelInFlight()
    {
        _presentationVersion++;
        CancellationTokenSource? previous = _inFlight;
        _inFlight = null;
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
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
        CancelInFlight();

        try
        {
            _window.CloseForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
