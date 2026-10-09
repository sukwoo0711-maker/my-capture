using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Store;

/// <summary>
/// Asks once, after a run of successful captures, for a Microsoft Store rating.
/// </summary>
/// <remarks>
/// <para>
/// The capture pipeline reports each outcome through <see cref="RecordCapture"/>; the rules
/// themselves live in <see cref="StoreRatingPolicy"/>. When they pass, the request is only
/// armed. It is attempted after the capture flow ends (editor or card closed) and a short
/// settle delay, and only if no overlay, editor, recording or card is in use at that moment,
/// so the system dialog never interrupts work in progress.
/// </para>
/// <para>
/// The attempt is recorded and persisted before the dialog is requested. Whatever the Store
/// returns, or if the request throws, the app never asks again. Failures are logged only.
/// </para>
/// </remarks>
internal sealed class StoreRatingCoordinator
{
    /// <summary>Outlasts the five-second capture receipt and tray notifications.</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(6);

    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action<Action> _deferWrite;
    private readonly Func<bool> _isBusy;
    private readonly IStoreRatingRequester _requester;
    private readonly ILogger _log;
    private readonly bool _isPackaged;
    private readonly TimeProvider _clock;
    private DispatcherTimer? _settleTimer;

    /// <param name="settings">Reads the live settings object.</param>
    /// <param name="save">Writes the settings file; called on the dispatcher.</param>
    /// <param name="deferWrite">Runs a count write off the capture's critical path.</param>
    /// <param name="isBusy">True while capture, editing, recording or a capture card is on screen.</param>
    internal StoreRatingCoordinator(
        Func<AppSettings> settings,
        Action<AppSettings> save,
        Action<Action> deferWrite,
        Func<bool> isBusy,
        IStoreRatingRequester requester,
        ILogger log,
        bool isPackaged,
        TimeProvider? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _deferWrite = deferWrite ?? throw new ArgumentNullException(nameof(deferWrite));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _requester = requester ?? throw new ArgumentNullException(nameof(requester));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isPackaged = isPackaged;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The rules passed on the last capture; waiting for the flow to end.</summary>
    internal bool IsPending { get; private set; }

    internal bool IsSettleTimerRunning => _settleTimer?.IsEnabled == true;

    /// <summary>The request started by the last attempt, for tests and shutdown diagnostics.</summary>
    internal Task? LastRequest { get; private set; }

    /// <summary>
    /// Records one capture outcome from the shared completion point. Does nothing outside the
    /// Store build or after the single attempt.
    /// </summary>
    internal void RecordCapture(bool succeeded)
    {
        if (!_isPackaged) return;
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (state.RequestAttemptedUtc is not null) return;

        if (StoreRatingPolicy.RecordCapture(state, succeeded))
            _deferWrite(() => Persist(_settings()));
        IsPending = StoreRatingPolicy.ShouldRequest(state, _isPackaged, _clock.GetUtcNow());
        if (!IsPending) _settleTimer?.Stop();
    }

    /// <summary>Called when the overlay/editor session or a capture card closes.</summary>
    internal void NotifyFlowEnded()
    {
        if (!IsPending) return;
        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = SettleDelay };
            _settleTimer.Tick += (_, _) => TryRequestNow();
        }

        _settleTimer.Stop();
        _settleTimer.Start();
    }

    /// <summary>
    /// Attempts the request if it is still due and nothing is on screen. A busy moment leaves it
    /// armed for the next flow end. Returns whether a request was started.
    /// </summary>
    internal bool TryRequestNow()
    {
        _settleTimer?.Stop();
        if (!IsPending || _isBusy()) return false;

        AppSettings settings = _settings();
        DateTimeOffset now = _clock.GetUtcNow();
        IsPending = false;
        if (!StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, now)) return false;

        StoreRatingPolicy.MarkAttempted(settings.StoreRating, now);
        Persist(settings);
        LastRequest = RequestAsync();
        return true;
    }

    /// <summary>
    /// The user opened the review page from Settings, so the automatic request is no longer
    /// needed.
    /// </summary>
    internal void NoteManualReviewOpened()
    {
        AppSettings settings = _settings();
        IsPending = false;
        _settleTimer?.Stop();
        if (settings.StoreRating.RequestAttemptedUtc is not null) return;
        StoreRatingPolicy.MarkAttempted(settings.StoreRating, _clock.GetUtcNow());
        Persist(settings);
    }

    private async Task RequestAsync()
    {
        try
        {
            string outcome = await _requester.RequestRateAndReviewAsync();
            _log.LogInformation("Store rating request finished: {Outcome}", outcome);
        }
        catch (Exception ex)
        {
            // Unsupported, offline or rejected: the user is never told, and never asked again.
            _log.LogWarning(ex, "Store rating request could not be shown");
        }
    }

    private void Persist(AppSettings settings)
    {
        // A first run writes the settings file only after the welcome card closes; the welcome
        // write carries this state with it.
        if (settings.General.IsFirstRun) return;
        try
        {
            _save(settings);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save Store rating state");
        }
    }
}
