using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Store;

/// <summary>
/// The inline Store rating invitation, followed only by a chosen review. It shows on an explicit return
/// to the library or settings window, or on a capture card from its first appearance, in one place at a
/// time and at most <see cref="StoreRatingPolicy.MaxInvitations"/> times, at least 24 hours apart.
/// Cards may predict their pending capture's success and persist only after showing. An untouched
/// card offer gives its showing back through a deferred write only when its card never showed or its
/// capture's own save or copy failed; once that capture is delivered or the card closes, it stands.
/// Deferred writes hand a snapshot to the background saver; a choice the user makes, and a library or
/// settings showing, save at once. Exit queues the deferred writes still waiting (<see cref="FlushDeferredWrites"/>).
/// </summary>
internal sealed class StoreRatingCoordinator
{
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(6);
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action<AppSettings> _saveInBackground;
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
    private CardOffer? _cardOffer;
    private int _queuedWrites;

    /// <param name="save">Saves at once, on the caller's thread: the choices the user makes and a window's showing.</param>
    /// <param name="deferWrite">Runs a write later, once the UI is idle.</param>
    /// <param name="isBusy">Anything on screen a Store dialog must not interrupt, the capture card included.</param>
    /// <param name="showInvitation">Shows the invitation in the active library or settings window; false when it could not.</param>
    /// <param name="isInvitationVisible">The invitation is on screen somewhere already.</param>
    /// <param name="isCardBusy">As <paramref name="isBusy"/>, without the capture card itself; defaults to it.</param>
    /// <param name="saveInBackground">
    /// What a deferred write runs: takes its own snapshot of the settings and saves it off the UI thread,
    /// logging a failure itself; defaults to <paramref name="save"/>.
    /// </param>
    internal StoreRatingCoordinator(Func<AppSettings> settings, Action<AppSettings> save,
        Action<Action> deferWrite, Func<bool> isBusy, IStoreRatingRequester requester, ILogger log,
        bool isPackaged, TimeProvider? clock = null, Func<bool>? isUserActive = null, Func<bool>? showInvitation = null,
        Func<bool>? isInvitationVisible = null, Func<bool>? isCardBusy = null, Action<AppSettings>? saveInBackground = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _saveInBackground = saveInBackground ?? save;
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
        if (StoreRatingPolicy.RecordCapture(settings.StoreRating, succeeded)) QueueWrite();
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow());
        if (!IsPending) _settleTimer?.Stop();
    }

    /// <summary>
    /// Exit: hands the deferred writes still waiting for the idle UI to the background saver now, as one
    /// save of the live state, so waiting for pending saves covers them. Those writes then add nothing.
    /// </summary>
    internal void FlushDeferredWrites()
    {
        if (_queuedWrites == 0) return;
        _queuedWrites = 0;
        PersistLater();
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
        // An invitation still on screen is neither shown nor counted a second time.
        if (!_userReturned || !_isUserActive() || _isBusy() || _isInvitationVisible()) return false;
        AppSettings settings = _settings();
        if (!CanOffer(settings) || !CountShowing(settings, out Showing before)) return false;
        IsPending = false;
        if (_showInvitation())
        {
            _cardOffer = null;
            return true;
        }
        // Not on screen after all (an update notice took the window first), so not counted either.
        Restore(settings, before);
        Persist(settings);
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow());
        return false;
    }

    /// <summary>
    /// Counts a card's showing in memory before construction. A pending capture is evaluated with one
    /// predicted success. Practice, first run, busy UI and an invitation elsewhere exclude the offer.
    /// The returned token identifies this showing until it is answered, withdrawn or superseded, its
    /// pending capture is delivered (<see cref="NoteCardCaptureDelivered"/>), or its card closes with no
    /// capture outcome still to come (<see cref="NoteCardClosed"/>).
    /// </summary>
    internal CardOffer? TryOfferOnCard(bool practice, bool pendingCapture = false)
    {
        if (practice || _isCardBusy() || _isInvitationVisible()) return null;
        AppSettings settings = _settings();
        if (!CanOffer(settings, pendingCapture)) return null;
        StoreRatingState state = settings.StoreRating;
        _cardOffer = new CardOffer(state.InvitationShownCount, state.InvitationShownUtc) { AwaitsCapture = pendingCapture };
        StoreRatingPolicy.MarkInvitationShown(state, _clock.GetUtcNow());
        IsPending = false;
        _settleTimer?.Stop();
        return _cardOffer;
    }

    /// <summary>Called after the card's Show returns; queues the counted showing once.</summary>
    internal void NoteCardOfferShown(CardOffer offer)
    {
        if (!ReferenceEquals(_cardOffer, offer) || offer.WriteQueued) return;
        offer.WriteQueued = true;
        QueueWrite();
    }

    /// <summary>A Review or Close press keeps this showing, even when the review request is refused.</summary>
    internal void NoteCardOfferAnswered(CardOffer offer) => Retire(offer);

    /// <summary>
    /// The card's pending capture was saved and copied. Once the card has shown, its showing stands: a
    /// failure of the card's own actions afterwards never gives it back.
    /// </summary>
    internal void NoteCardCaptureDelivered(CardOffer offer)
    {
        offer.AwaitsCapture = false;
        if (offer.WriteQueued) Retire(offer);
    }

    /// <summary>
    /// The card closed. A showing it made stands, except while its capture's own save or copy may still fail:
    /// that outcome resolves it (<see cref="NoteCardCaptureDelivered"/> or <see cref="WithdrawCardOffer"/>).
    /// </summary>
    internal void NoteCardClosed(CardOffer offer)
    {
        if (offer.WriteQueued && !offer.AwaitsCapture) Retire(offer);
    }

    /// <summary>
    /// Restores only this untouched showing, once, and defers saving the restored live state: for a card that
    /// never showed, or whose capture's own save or copy failed before the showing was answered or settled.
    /// </summary>
    internal bool WithdrawCardOffer(CardOffer offer)
    {
        if (!ReferenceEquals(_cardOffer, offer)) return false;
        _cardOffer = null;
        AppSettings settings = _settings();
        Restore(settings, new Showing(offer.PreviousCount, offer.PreviousShownUtc));
        QueueWrite();
        IsPending = StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow());
        return true;
    }

    /// <summary>This showing can no longer be given back.</summary>
    private void Retire(CardOffer offer)
    {
        if (ReferenceEquals(_cardOffer, offer)) _cardOffer = null;
    }

    /// <summary>Identity and rollback snapshot for one card's showing.</summary>
    internal sealed class CardOffer(int count, DateTimeOffset? shownUtc)
    {
        internal int PreviousCount { get; } = count;
        internal DateTimeOffset? PreviousShownUtc { get; } = shownUtc;
        internal bool WriteQueued { get; set; }

        /// <summary>Offered predicting its capture's success, whose save and copy have not reported yet.</summary>
        internal bool AwaitsCapture { get; set; }
    }

    /// <summary>
    /// Only the invitation's explicit Review button in a library or settings window may call this. An error
    /// recorded since the invitation showed refuses it.
    /// </summary>
    internal bool AcceptInvitation() => Accept(owner: null, userActive: _isUserActive(), busy: _isBusy(), refuseAfterError: true);

    /// <summary>
    /// Only the Review button on a capture card's invitation may call this. The Store dialog opens over
    /// <paramref name="card"/>, which the user has just clicked, so no library or settings window has to
    /// be active, and the attempt is recorded so the invitation never returns. No error refuses it: a card
    /// may have predicted the success of its capture while the live state still holds the previous use's
    /// error, and its save and copy may not have reported yet. Returns the request, or null when it is
    /// refused (exit, an update being installed, or an attempt that could not be saved) and nothing opens.
    /// </summary>
    internal Task? AcceptInvitationOnCard(Window card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return Accept(card, userActive: true, busy: _isCardBusy(), refuseAfterError: false) ? LastRequest : null;
    }

    /// <summary>The invitation's Close button, in any window or on a card: it is not offered again.</summary>
    internal void DismissInvitation()
    {
        if (!_isPackaged) return;
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (state.InvitationShownUtc is null || StoreRatingPolicy.IsAnswered(state)) return;
        _cardOffer = null;
        IsPending = false;
        _settleTimer?.Stop();
        StoreRatingPolicy.MarkInvitationDismissed(state, _clock.GetUtcNow());
        Persist(settings);
    }

    internal void NoteManualReviewOpened()
    {
        _cardOffer = null;
        IsPending = false;
        _settleTimer?.Stop();
        AppSettings settings = _settings();
        StoreRatingPolicy.MarkAttempted(settings.StoreRating, _clock.GetUtcNow());
        Persist(settings);
    }

    private bool Accept(Window? owner, bool userActive, bool busy, bool refuseAfterError)
    {
        AppSettings settings = _settings();
        StoreRatingState state = settings.StoreRating;
        if (!_isPackaged || !userActive || busy || state.InvitationShownUtc is null
            || StoreRatingPolicy.IsAnswered(state) || (refuseAfterError && state.LastCaptureFailed)) return false;
        StoreRatingPolicy.MarkAttempted(state, _clock.GetUtcNow());
        if (!Persist(settings))
        {
            // Nothing opened, so it was no attempt: Review can be pressed again.
            state.RequestAttemptedUtc = null;
            return false;
        }
        LastRequest = RequestAsync(owner);
        _cardOffer = null;
        return true;
    }

    /// <summary>The rules every showing shares. Nothing is offered, or counted, while the first run is under way.</summary>
    private bool CanOffer(AppSettings settings, bool pendingCapture = false) =>
        !settings.General.IsFirstRun && (pendingCapture
            ? StoreRatingPolicy.ShouldRequestCountingSuccess(settings.StoreRating, _isPackaged, _clock.GetUtcNow())
            : StoreRatingPolicy.ShouldRequest(settings.StoreRating, _isPackaged, _clock.GetUtcNow()));

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

    /// <summary>
    /// Saves the live state once the UI is idle, off the UI thread. Each queued write saves once, unless
    /// <see cref="FlushDeferredWrites"/> has already saved for it.
    /// </summary>
    private void QueueWrite()
    {
        _queuedWrites++;
        _deferWrite(() =>
        {
            if (_queuedWrites == 0) return;
            _queuedWrites--;
            PersistLater();
        });
    }

    /// <summary>Hands the live state to the background saver; nothing is written during the first run.</summary>
    private void PersistLater()
    {
        AppSettings settings = _settings();
        if (settings.General.IsFirstRun) return;
        try { _saveInBackground(settings); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save Store rating state"); }
    }
}
