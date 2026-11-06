using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Store;

/// <summary>
/// The inline Store rating invitation, followed only by a chosen review. It shows on an explicit return
/// to the library or settings window, or on a capture card from its first appearance, in one place at a
/// time and at most <see cref="StoreRatingPolicy.MaxInvitations"/> times across all of them.
/// </summary>
internal sealed class StoreRatingCoordinator
{
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(6);
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action<Action> _deferWrite;
    private readonly Func<bool> _isBusy;
    private readonly Func<bool> _isCardBusy;
    private readonly IStoreRatingRequester _requester;
    private readonly ILogger _log;
    private readonly bool _isPackaged;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _isUserActive;
    private readonly Func<bool> _showInvitation;
    private readonly Func<bool> _isInvitationVisible;
    private DispatcherTimer? _settleTimer;
    private bool _userReturned;
    private Showing? _cardOffer;

    /// <param name="isBusy">Anything on screen a Store dialog must not interrupt, the capture card included.</param>
    /// <param name="showInvitation">Shows the invitation in the active library or settings window; false when it could not.</param>
    /// <param name="isInvitationVisible">The invitation is on screen somewhere already.</param>
    /// <param name="isCardBusy">As <paramref name="isBusy"/>, without the capture card itself; defaults to it.</param>
    internal StoreRatingCoordinator(Func<AppSettings> settings, Action<AppSettings> save,
        Action<Action> deferWrite, Func<bool> isBusy, IStoreRatingRequester requester, ILogger log,
        bool isPackaged, TimeProvider? clock = null, Func<bool>? isUserActive = null, Func<bool>? showInvitation = null,
        Func<bool>? isInvitationVisible = null, Func<bool>? isCardBusy = null)
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
        _showInvitation = showInvitation ?? (() => true);
        _isInvitationVisible = isInvitationVisible ?? (() => false);
        _isCardBusy = isCardBusy ?? _isBusy;
    }

    internal bool IsPending { get; private set; }
    internal bool IsSettleTimerRunning => _settleTimer?.IsEnabled == true;
    internal Task? LastRequest { get; private set; }

    /// <summary>One use's outcome: a delivered capture, a new pin, copied OCR text or a saved recording.</summary>
    internal void RecordCapture(bool succeeded)
    {
        if (!_isPackaged) return;
        AppSettings settings = _settings();
        if (StoreRatingPolicy.IsAnswered(settings.StoreRating)) return;
        if (StoreRatingPolicy.RecordCapture(settings.StoreRating, succeeded)) _deferWrite(() => Persist(_settings()));
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged);
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
        IsPending = StoreRatingPolicy.ShouldRequest(_settings().StoreRating, _isPackaged);
        return TryRequestNow();
    }

    /// <summary>Offers inline UI; this method never invokes the native Store requester.</summary>
    internal bool TryRequestNow()
    {
        _settleTimer?.Stop();
        // An invitation still on screen is neither shown nor counted a second time.
        if (!_userReturned || !_isUserActive() || _isBusy() || _isInvitationVisible()) return false;
        AppSettings settings = _settings();
        if (!CanOffer(settings) || !CountShowing(settings, out Showing before)) return false;
        IsPending = false;
        if (_showInvitation()) return true;
        // Not on screen after all (an update notice took the window first), so not counted either.
        Restore(settings, before);
        Persist(settings);
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged);
        return false;
    }

    /// <summary>
    /// Asked once, just before a capture card is built: whether the card carries the invitation from its
    /// first appearance. The rules are those of a return (<see cref="StoreRatingPolicy.ShouldRequest"/>,
    /// never during the first run), and the card adds its own: never for a capture the practice window
    /// asked for, never while anything else a Store dialog must not interrupt is on screen, and never
    /// while the invitation is on screen elsewhere. True counts the showing and saves it first;
    /// <see cref="WithdrawCardOffer"/> takes it back when that card cannot show after all.
    /// </summary>
    internal bool TryOfferOnCard(bool practice)
    {
        _cardOffer = null;
        if (practice || _isCardBusy() || _isInvitationVisible()) return false;
        AppSettings settings = _settings();
        if (!CanOffer(settings) || !CountShowing(settings, out Showing before)) return false;
        _cardOffer = before;
        IsPending = false;
        _settleTimer?.Stop();
        return true;
    }

    /// <summary>
    /// The card a successful <see cref="TryOfferOnCard"/> was for could not show: its showing is not
    /// counted. Call it only for that card, before anything else records an outcome or a showing.
    /// </summary>
    internal void WithdrawCardOffer()
    {
        if (_cardOffer is not { } before) return;
        _cardOffer = null;
        AppSettings settings = _settings();
        Restore(settings, before);
        Persist(settings);
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged);
    }

    /// <summary>Only the invitation's explicit Review button in a library or settings window may call this.</summary>
    internal bool AcceptInvitation() => Accept(owner: null, userActive: _isUserActive(), busy: _isBusy());

    /// <summary>
    /// Only the Review button on a capture card's invitation may call this. The Store dialog opens over
    /// <paramref name="card"/>, which the user has just clicked, so no library or settings window has to
    /// be active. Returns the request, or null when it is refused and nothing opens.
    /// </summary>
    internal Task? AcceptInvitationOnCard(Window card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return Accept(card, userActive: true, busy: _isCardBusy()) ? LastRequest : null;
    }

    /// <summary>The invitation's Close button, in any window or on a card: it is not offered again.</summary>
    internal void DismissInvitation()
    {
        if (!_isPackaged) return;
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (state.InvitationShownUtc is null || StoreRatingPolicy.IsAnswered(state)) return;
        IsPending = false;
        _settleTimer?.Stop();
        StoreRatingPolicy.MarkInvitationDismissed(state, _clock.GetUtcNow());
        Persist(settings);
    }

    internal void NoteManualReviewOpened()
    {
        IsPending = false;
        _settleTimer?.Stop();
        AppSettings settings = _settings();
        StoreRatingPolicy.MarkAttempted(settings.StoreRating, _clock.GetUtcNow());
        Persist(settings);
    }

    private bool Accept(Window? owner, bool userActive, bool busy)
    {
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (!_isPackaged || !userActive || busy || state.InvitationShownUtc is null
            || StoreRatingPolicy.IsAnswered(state) || state.LastCaptureFailed) return false;
        StoreRatingPolicy.MarkAttempted(state, _clock.GetUtcNow());
        if (!Persist(settings))
        {
            // Nothing opened, so it was no attempt: Review can be pressed again.
            state.RequestAttemptedUtc = null;
            return false;
        }
        LastRequest = RequestAsync(owner);
        return true;
    }

    /// <summary>The rules every showing shares. Nothing is offered, or counted, while the first run is under way.</summary>
    private bool CanOffer(AppSettings settings) =>
        !settings.General.IsFirstRun && StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged);

    /// <summary>
    /// Counts one showing and saves it before anything shows; false, with nothing counted, when it cannot
    /// be saved. <paramref name="before"/> is the count and time it replaced.
    /// </summary>
    private bool CountShowing(AppSettings settings, out Showing before)
    {
        StoreRatingState state = settings.StoreRating;
        before = new Showing(state.InvitationShownCount, state.InvitationShownUtc);
        StoreRatingPolicy.MarkInvitationShown(state, _clock.GetUtcNow());
        if (Persist(settings)) return true;
        Restore(settings, before);
        return false;
    }

    private static void Restore(AppSettings settings, Showing before)
    {
        settings.StoreRating.InvitationShownCount = before.Count;
        settings.StoreRating.InvitationShownUtc = before.ShownUtc;
    }

    /// <summary>The showings counted so far, and when the last one was.</summary>
    private readonly record struct Showing(int Count, DateTimeOffset? ShownUtc);

    private async Task RequestAsync(Window? owner)
    {
        try { _log.LogInformation("Store rating request finished: {Outcome}", await _requester.RequestRateAndReviewAsync(owner)); }
        catch (Exception ex) { _log.LogWarning(ex, "Store rating request could not be shown"); }
    }

    private bool Persist(AppSettings settings)
    {
        if (settings.General.IsFirstRun) return false;
        try { _save(settings); return true; }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save Store rating state"); return false; }
    }
}
