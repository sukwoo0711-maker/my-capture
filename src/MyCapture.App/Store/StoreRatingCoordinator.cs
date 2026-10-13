using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Store;

/// <summary>A single inline invitation on an explicit return, followed only by a chosen review.</summary>
internal sealed class StoreRatingCoordinator
{
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(6);
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action<Action> _deferWrite;
    private readonly Func<bool> _isBusy;
    private readonly IStoreRatingRequester _requester;
    private readonly ILogger _log;
    private readonly bool _isPackaged;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _isUserActive;
    private readonly Action _showInvitation;
    private DispatcherTimer? _settleTimer;
    private bool _userReturned;

    internal StoreRatingCoordinator(Func<AppSettings> settings, Action<AppSettings> save,
        Action<Action> deferWrite, Func<bool> isBusy, IStoreRatingRequester requester, ILogger log,
        bool isPackaged, TimeProvider? clock = null, Func<bool>? isUserActive = null, Action? showInvitation = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _deferWrite = deferWrite ?? throw new ArgumentNullException(nameof(deferWrite));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _requester = requester ?? throw new ArgumentNullException(nameof(requester));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isPackaged = isPackaged;
        _clock = clock ?? TimeProvider.System;
        _isUserActive = isUserActive ?? (() => false);
        _showInvitation = showInvitation ?? (() => { });
    }

    internal bool IsPending { get; private set; }
    internal bool IsSettleTimerRunning => _settleTimer?.IsEnabled == true;
    internal Task? LastRequest { get; private set; }

    internal void RecordCapture(bool succeeded)
    {
        if (!_isPackaged) return;
        AppSettings settings = _settings();
        if (settings.StoreRating.RequestAttemptedUtc is not null) return;
        if (StoreRatingPolicy.RecordCapture(settings.StoreRating, succeeded)) _deferWrite(() => Persist(_settings()));
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow());
        if (!IsPending) _settleTimer?.Stop();
    }

    internal void NotifyFlowEnded()
    {
        _userReturned = false;
        if (!IsPending) return;
        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = SettleDelay };
            _settleTimer.Tick += (_, _) => TryRequestNow();
        }
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    /// <summary>Only explicit shell return or input in a library/settings window calls this.</summary>
    internal bool NotifyUserReturned()
    {
        _userReturned = true;
        IsPending = StoreRatingPolicy.ShouldRequest(_settings().StoreRating, _isPackaged, _clock.GetUtcNow());
        return TryRequestNow();
    }

    /// <summary>Offers inline UI; this method never invokes the native Store requester.</summary>
    internal bool TryRequestNow()
    {
        _settleTimer?.Stop();
        if (!_userReturned || !_isUserActive() || _isBusy()) return false;
        AppSettings settings = _settings();
        if (!StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow())) return false;
        IsPending = false;
        settings.StoreRating.InvitationShownUtc = _clock.GetUtcNow();
        if (!Persist(settings)) return false;
        _showInvitation();
        return true;
    }

    /// <summary>Only the invitation's explicit Review button may call this.</summary>
    internal bool AcceptInvitation()
    {
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (!_isPackaged || !_isUserActive() || _isBusy() || state.InvitationShownUtc is null
            || state.RequestAttemptedUtc is not null || state.LastCaptureFailed) return false;
        StoreRatingPolicy.MarkAttempted(state, _clock.GetUtcNow());
        if (!Persist(settings)) return false;
        LastRequest = RequestAsync();
        return true;
    }

    internal void NoteManualReviewOpened()
    {
        IsPending = false;
        _settleTimer?.Stop();
        AppSettings settings = _settings();
        StoreRatingPolicy.MarkAttempted(settings.StoreRating, _clock.GetUtcNow());
        Persist(settings);
    }

    private async Task RequestAsync()
    {
        try { _log.LogInformation("Store rating request finished: {Outcome}", await _requester.RequestRateAndReviewAsync()); }
        catch (Exception ex) { _log.LogWarning(ex, "Store rating request could not be shown"); }
    }

    private bool Persist(AppSettings settings)
    {
        if (settings.General.IsFirstRun) return false;
        try { _save(settings); return true; }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save Store rating state"); return false; }
    }
}
