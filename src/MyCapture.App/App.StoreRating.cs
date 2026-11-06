using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Shell glue for the Store rating invitation and the Print Screen key guidance.
/// The capture, pin, OCR and recording paths only report outcomes and flow ends; the decisions live in
/// <see cref="StoreRatingCoordinator"/>, <see cref="StoreRatingPolicy"/>, <see cref="StoreRatingOutcomes"/> and
/// <see cref="PrintScreenKeyGuide"/>.
/// </summary>
public partial class App
{
    private StoreRatingCoordinator? _storeRating;

    private void InitializeStoreRating()
    {
        if (_services is null) return;
        ServiceProvider services = _services;
        _storeRating = new StoreRatingCoordinator(
            () => _settings!,
            settings => services.GetRequiredService<SettingsStore>().Save(settings),
            write => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, write),
            IsStoreRatingDeferred,
            new StoreContextRatingRequester(),
            services.GetRequiredService<ILogger<StoreRatingCoordinator>>(),
            AppPackage.IsPackaged,
            isUserActive: () => ActiveEngagementWindow() is not null,
            showInvitation: ShowStoreInvitation,
            isInvitationVisible: IsStoreInvitationVisible,
            isCardBusy: IsStoreRatingDeferredOnCard);
        InitializeStoreUpdateObservation();
        if (_settingsApply is not null)
            _settingsApply.PrintScreenCaptureKeyEnabled = PrintScreenKeyGuide.IsWindowsCaptureKeyEnabled;
    }

    /// <summary>The capture card carrying the rating invitation, from its first appearance until it is answered or closes.</summary>
    private QuickAccessWindow? _storeRatingCard;

    /// <summary>
    /// The rating invitation is on screen: on the capture card, or on a library or settings window that is
    /// showing. One left in a hidden window was seen and left unanswered; the next showing replaces it.
    /// </summary>
    private bool IsStoreInvitationVisible() =>
        _storeRatingCard is { IsOpen: true }
        || _engagementNotices.Any(pair => pair.Key.IsVisible && pair.Value.IsShown && pair.Value.Kind == EngagementNoticeKind.Rating);

    /// <summary>
    /// Offers the invitation before a card is built, counting its showing in memory. A pending capture
    /// contributes a predicted success. The caller saves the showing after Show returns, or withdraws it.
    /// </summary>
    private StoreRatingCoordinator.CardOffer? OfferStoreRatingOnCard(bool practice, bool pendingCapture = false)
    {
        StoreRatingCoordinator.CardOffer? offer = _storeRating?.TryOfferOnCard(practice, pendingCapture);
        if (offer is null) return null;
        // One place at a time: an invitation left in a hidden library or settings window gives way to the card.
        foreach (IEngagementNotice notice in _engagementNotices.Values)
            if (notice.IsShown && notice.Kind == EngagementNoticeKind.Rating) notice.Hide();
        return offer;
    }

    /// <summary>
    /// The card offered the invitation shows it from its first appearance (<see cref="QuickAccessWindow.OfferRatingInvitation"/>).
    /// Review opens the Store dialog over the card; Close ends the invitation; a card that closes with neither
    /// pressed leaves it unanswered, eligible once more after 24 hours. Failures withdraw an untouched offer.
    /// </summary>
    private void AttachStoreRatingInvitation(QuickAccessWindow card, StoreRatingCoordinator.CardOffer offer)
    {
        _storeRatingCard = card;
        card.Closed += (_, _) => { if (ReferenceEquals(_storeRatingCard, card)) _storeRatingCard = null; };
        card.OfferRatingInvitation(
            review: () =>
            {
                _storeRating?.NoteCardOfferAnswered(offer);
                if (ReferenceEquals(_storeRatingCard, card)) _storeRatingCard = null;
                return _storeRating?.AcceptInvitationOnCard(card);
            },
            close: () =>
            {
                _storeRating?.NoteCardOfferAnswered(offer);
                if (ReferenceEquals(_storeRatingCard, card)) _storeRatingCard = null;
                _storeRating?.DismissInvitation();
            },
            withdraw: () => WithdrawStoreRatingCardOffer(offer, card));
    }

    /// <summary>Restores this untouched offer's showing and defers its save, hiding its row if still open.</summary>
    private void WithdrawStoreRatingCardOffer(StoreRatingCoordinator.CardOffer offer, QuickAccessWindow? card)
    {
        if (card is not null && ReferenceEquals(_storeRatingCard, card)) _storeRatingCard = null;
        if (_storeRating?.WithdrawCardOffer(offer) == true && card is { IsOpen: true })
            card.WithdrawRatingInvitation();
    }

    /// <summary>Resolves this capture's prediction once its actual save and copy outcome is recorded.</summary>
    private void ResolveStoreRatingCapture(QuickAccessFirst? first, bool delivered)
    {
        if (first is null) return;
        first.RatingOutcomeRecorded = true;
        StoreRatingCoordinator.CardOffer? offer = first.PendingRatingOffer;
        first.PendingRatingOffer = null;
        if (!delivered && offer is not null) WithdrawStoreRatingCardOffer(offer, first.Card);
    }

    /// <summary>Records a success or an error toward the Store rating invitation; null records nothing.</summary>
    private void RecordStoreRatingOutcome(bool? succeeded)
    {
        if (succeeded is { } outcome) _storeRating?.RecordCapture(outcome);
    }

    /// <summary>A still capture that was saved and copied, or failed (<see cref="StoreRatingOutcomes.FromStillCapture"/>).</summary>
    private void RecordStoreRatingCapture(bool delivered, bool practice, bool pinRequested) =>
        RecordStoreRatingOutcome(StoreRatingOutcomes.FromStillCapture(delivered, practice, pinRequested));

    /// <summary>Text copied by text capture or the capture card (<see cref="StoreRatingOutcomes.FromTextCapture"/>).</summary>
    private void RecordStoreRatingTextCopy(TextCaptureStatus status) =>
        RecordStoreRatingOutcome(StoreRatingOutcomes.FromTextCapture(status));

    /// <summary>
    /// A new pin is a successful use, except the one the practice window asked for. A capture taken as a
    /// pin is counted here, by its pin, and not by the capture (<see cref="StoreRatingOutcomes.FromStillCapture"/>).
    /// </summary>
    private void OnPinCreatedForStoreRating()
    {
        if (StoreRatingOutcomes.IsPracticePin(_onboardingWindow?.Session)) return;
        _storeRating?.RecordCapture(succeeded: true);
    }

    /// <summary>Anything on screen that a Store dialog must not interrupt, the capture card and a capture still being saved included.</summary>
    private bool IsStoreRatingDeferred() => _quickAccess is not null || _overlay?.IsActive == true || IsStoreRatingDeferredOnCard();

    /// <summary>
    /// As <see cref="IsStoreRatingDeferred"/>, without what belongs to the card's own capture: the card, which may
    /// carry the invitation itself, and the capture session still saving that capture (a card shown first appears
    /// while the overlay coordinator is active). A selector or an editor still counts, as do a recording, a
    /// countdown, a scrolling capture and an update being installed.
    /// </summary>
    private bool IsStoreRatingDeferredOnCard() =>
        _storeInstallationPending || _preparingExit
        || _overlay?.IsSelecting == true || _overlay?.CurrentEditor is not null
        || _recorder?.IsActive == true || _activeCountdown is not null
        || _scrollCancellation is not null || _preparingAdvancedCapture
        || _pasteToScreenInFlight || HotkeyRecorder.Active is not null
        || Windows.OfType<Window>().Any(window => window.IsVisible
            && window is AnnotationEditorWindow or MyCapture.App.Recording.VideoEditorWindow or ShortcutHelpWindow);

    /// <summary>
    /// A line for the startup shortcut-failure notice when a failed shortcut uses Print Screen
    /// while Windows claims that key for its own capture tool; otherwise empty.
    /// </summary>
    private static string PrintScreenFailureLine(IEnumerable<HotkeyRegistrationFailure> failures)
    {
        string notice = PrintScreenKeyGuide.FailureNotice(
            failures.Select(failure => failure.Hotkey),
            PrintScreenKeyGuide.IsWindowsCaptureKeyEnabled());
        return notice.Length == 0 ? string.Empty : "\n" + notice;
    }
}
