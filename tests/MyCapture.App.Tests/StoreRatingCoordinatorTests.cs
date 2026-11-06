using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

internal sealed class StoreRatingTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class StoreRatingCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public readonly AppSettings Settings = new() { StoreRating = { FirstSeenUtc = Start } };
        public readonly StoreRatingTestClock Clock = new(Start.AddDays(1));
        public readonly List<string> Events = [];
        public readonly List<string> Warnings = [];
        public readonly List<int> SavedShowingCounts = [];
        public Action<Action> DeferWrite = write => write();
        /// <summary>The background saver the deferred writes use; null leaves them on the synchronous save.</summary>
        public Action<AppSettings>? SaveInBackground;
        public int SaveCalls;
        public bool Busy;
        public bool CardBusy;
        public bool Active = true;
        public bool InvitationVisible;
        public bool ShowSucceeds = true;
        public bool FailSave;
        public Func<Task<string>> Request = () => Task.FromResult("Succeeded");
        public readonly List<Window?> Owners = [];

        public StoreRatingCoordinator Create(bool isPackaged = true) => new(
            () => Settings,
            saved =>
            {
                SaveCalls++;
                if (FailSave) throw new IOException("The settings file is locked.");
                SavedShowingCounts.Add(saved.StoreRating.InvitationShownCount);
                Events.Add($"save count={saved.StoreRating.SuccessfulCaptureCount} attempted={saved.StoreRating.RequestAttemptedUtc is not null}");
            },
            write => DeferWrite(write),
            () => Busy,
            new Requester(this),
            new WarningLogger(Warnings),
            isPackaged,
            Clock, () => Active,
            () =>
            {
                Events.Add("invite");
                return ShowSucceeds;
            },
            () => InvitationVisible,
            () => CardBusy,
            SaveInBackground);

        public int Requests => Events.Count(e => e == "request");

        public int Invites => Events.Count(e => e == "invite");

        private sealed class WarningLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Add(formatter(state, exception));
            }
        }

        private sealed class Requester(Harness harness) : IStoreRatingRequester
        {
            public Task<string> RequestRateAndReviewAsync(Window? owner)
            {
                harness.Events.Add("request");
                harness.Owners.Add(owner);
                return harness.Request();
            }
        }
    }

    private static void Succeed(StoreRatingCoordinator coordinator, int times)
    {
        for (int i = 0; i < times; i++) coordinator.RecordCapture(succeeded: true);
    }

    [Fact]
    public void StoreRating_UnpackagedBuild_NeverCountsSavesOrAsks()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create(isPackaged: false);
        harness.Clock.Now = Start.AddYears(1);
        Succeed(coordinator, 25);
        coordinator.NotifyFlowEnded();
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        coordinator.DismissInvitation();
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
    }

    [Fact]
    public void StoreRating_SecondSuccessOffersOnReturnAndExplicitReviewAsksExactlyOnce()
    {
        StaTestHost.Run(() =>
        {
            var harness = new Harness();
            StoreRatingCoordinator coordinator = harness.Create();

            Succeed(coordinator, 1);
            Assert.False(coordinator.IsPending);
            coordinator.NotifyFlowEnded();
            Assert.False(coordinator.IsSettleTimerRunning);

            coordinator.RecordCapture(succeeded: true);
            Assert.True(coordinator.IsPending);
            coordinator.NotifyFlowEnded();
            Assert.True(coordinator.IsSettleTimerRunning);

            // An overlay, editor, recording or card on screen defers it to the next flow end.
            harness.Busy = true;
            Assert.False(coordinator.NotifyUserReturned());
            Assert.True(coordinator.IsPending);
            Assert.Equal(0, harness.Requests);

            harness.Busy = false;
            Assert.True(coordinator.TryRequestNow());
            Assert.Equal(0, harness.Requests);
            Assert.Equal(1, harness.Events.Count(e => e == "invite"));
            Assert.True(coordinator.AcceptInvitation());
            Assert.Equal(1, harness.Requests);
            Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.RequestAttemptedUtc);
            // The attempt is persisted before the dialog is requested.
            int request = harness.Events.IndexOf("request");
            Assert.Equal("save count=2 attempted=True", harness.Events[request - 1]);

            int eventsAfterAttempt = harness.Events.Count;
            harness.Clock.Now = harness.Clock.Now.AddDays(30);
            Succeed(coordinator, 20);
            coordinator.NotifyFlowEnded();
            Assert.False(coordinator.IsPending);
            Assert.False(coordinator.TryRequestNow());
            Assert.Equal(1, harness.Requests);
            Assert.Equal(eventsAfterAttempt, harness.Events.Count);
        });
    }

    [Fact]
    public void StoreRating_OffersRightAfterTheSecondSuccessWithoutAWaitingDay()
    {
        var harness = new Harness();
        // The same moment the install was first seen.
        harness.Clock.Now = Start;
        StoreRatingCoordinator coordinator = harness.Create();
        coordinator.RecordCapture(succeeded: true);
        Assert.False(coordinator.IsPending);

        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(Start, harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(1, harness.Invites);
    }

    [Fact]
    public void StoreRating_AnErrorCancelsTheArmedRequestUntilTheNextSuccess()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.IsPending);

        coordinator.RecordCapture(succeeded: false);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.Equal(0, harness.Requests);

        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.True(coordinator.AcceptInvitation());
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public async Task StoreRating_AFailedRequestIsOnlyLoggedAndNeverRetried()
    {
        var harness = new Harness { Request = () => Task.FromException<string>(new InvalidOperationException("Store unavailable")) };
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.True(coordinator.AcceptInvitation());
        await coordinator.LastRequest!;
        Assert.NotNull(harness.Settings.StoreRating.RequestAttemptedUtc);

        Succeed(coordinator, 3);
        Assert.False(coordinator.TryRequestNow());
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public void StoreRating_FlowEndAndOtherAppFocusNeverShowNativeUiOrInvitation() => StaTestHost.Run(() =>
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        coordinator.NotifyFlowEnded();
        Assert.False(coordinator.TryRequestNow());
        harness.Active = false;
        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(coordinator.AcceptInvitation());
        Assert.DoesNotContain("invite", harness.Events);
        Assert.Equal(0, harness.Requests);
        harness.Active = true;
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(0, harness.Requests);
        Assert.NotNull(harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        harness.Clock.Now = harness.Clock.Now.AddYears(1);
        Succeed(coordinator, 20);
        // Left unanswered, it is offered once more after a restart, and never a third time.
        Assert.True(harness.Create().NotifyUserReturned());
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.Equal(2, harness.Invites);
        Assert.Equal(0, harness.Requests);
    });

    [Fact]
    public void StoreRating_FailureAfterInvitationPreventsChosenNativeRequest()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());
        coordinator.RecordCapture(false);
        Assert.False(coordinator.AcceptInvitation());
        Assert.Equal(0, harness.Requests);
    }

    [Fact]
    public void StoreRating_FirstRunCountsWithoutCreatingTheSettingsFile()
    {
        var harness = new Harness();
        harness.Settings.General.IsFirstRun = true;
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 3);
        Assert.Equal(2, harness.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.DoesNotContain(harness.Events, e => e.StartsWith("save", StringComparison.Ordinal));
    }

    [Fact]
    public void StoreRating_NeverOffersOrCountsAShowingDuringTheFirstRun()
    {
        var harness = new Harness();
        harness.Settings.General.IsFirstRun = true;
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.DoesNotContain("invite", harness.Events);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);

        harness.Settings.General.IsFirstRun = false;
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Invites);
    }

    [Fact]
    public void StoreRating_OpeningTheReviewPageManuallyRetiresTheAutomaticRequest()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 1);
        coordinator.NoteManualReviewOpened();
        Assert.NotNull(harness.Settings.StoreRating.RequestAttemptedUtc);
        Assert.Equal("save count=1 attempted=True", harness.Events[^1]);

        Succeed(coordinator, 5);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.TryRequestNow());
        Assert.False(coordinator.NotifyUserReturned());
        Assert.DoesNotContain("invite", harness.Events);
        Assert.Equal(0, harness.Requests);
    }

    [Fact]
    public void StoreRating_TheSettingsReviewLinkAfterAnIgnoredShowingEndsTheInvitation()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());
        coordinator.NoteManualReviewOpened();

        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.Equal(1, harness.Invites);
        Assert.Equal(0, harness.Requests);
    }

    [Fact]
    public void StoreRating_AnIgnoredInvitationIsOfferedOnceMoreOnALaterReturn()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        DateTimeOffset firstShown = harness.Clock.Now;
        Assert.Equal(firstShown, harness.Settings.StoreRating.InvitationShownUtc);

        // Still on screen: another return neither shows it again nor counts it.
        harness.InvitationVisible = true;
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(1, harness.Invites);

        // Its window closed untouched: the next showing still waits for 24 hours.
        harness.InvitationVisible = false;
        harness.Clock.Now = firstShown.AddMinutes(5);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        harness.Clock.Now = firstShown.AddDays(1);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(StoreRatingPolicy.MaxInvitations, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(firstShown.AddDays(1), harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(2, harness.Invites);

        // Ignored twice: never a third time, however many successes follow.
        Succeed(coordinator, 5);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.Equal(2, harness.Invites);
        Assert.Equal(0, harness.Requests);

        // The second showing still opens the Store dialog only from its Review button.
        Assert.True(coordinator.AcceptInvitation());
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public void StoreRating_AnErrorAfterAnIgnoredShowingHoldsBackTheSecondUntilTheNextSuccess()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());

        coordinator.RecordCapture(succeeded: false);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);

        coordinator.RecordCapture(succeeded: true);
        Assert.False(coordinator.NotifyUserReturned());
        harness.Clock.Now = harness.Clock.Now.AddDays(1);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(2, harness.Settings.StoreRating.InvitationShownCount);
    }

    [Fact]
    public void StoreRating_CloseEndsTheInvitation()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        // Nothing to close before the invitation was shown.
        coordinator.DismissInvitation();
        Assert.Null(harness.Settings.StoreRating.InvitationDismissedUtc);

        Assert.True(coordinator.NotifyUserReturned());
        harness.Clock.Now = harness.Clock.Now.AddMinutes(1);
        coordinator.DismissInvitation();
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationDismissedUtc);
        Assert.Equal("save count=2 attempted=False", harness.Events[^1]);
        // A Review click that arrives after Close never opens the Store dialog.
        Assert.False(coordinator.AcceptInvitation());

        Succeed(coordinator, 5);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.Equal(1, harness.Invites);
        Assert.Equal(0, harness.Requests);
    }

    [Fact]
    public void StoreRating_ReviewOnTheFirstShowingEndsTheInvitation()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.True(coordinator.AcceptInvitation());

        // The settings card closes itself after Review; that close records nothing more.
        int events = harness.Events.Count;
        coordinator.DismissInvitation();
        Assert.Equal(events, harness.Events.Count);
        Assert.Null(harness.Settings.StoreRating.InvitationDismissedUtc);

        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(harness.Create().NotifyUserReturned());
        Assert.False(coordinator.AcceptInvitation());
        Assert.Equal(1, harness.Invites);
        Assert.Equal(1, harness.Requests);
    }

    [Fact]
    public void StoreRating_AnInvitationShownByAnEarlierBuildIsNeverShownAgain()
    {
        var harness = new Harness();
        // Written before 2026-11-06: the single showing, stamped by time only.
        harness.Settings.StoreRating.SuccessfulCaptureCount = 5;
        harness.Settings.StoreRating.InvitationShownUtc = Start.AddDays(2);
        StoreRatingCoordinator coordinator = harness.Create();

        Succeed(coordinator, 3);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(coordinator.AcceptInvitation());
        coordinator.DismissInvitation();
        Assert.Empty(harness.Events);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationDismissedUtc);
    }

    [Fact]
    public void StoreRating_AShowingThatCouldNotBeSavedIsNeitherShownNorCounted()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);

        harness.FailSave = true;
        Assert.False(coordinator.NotifyUserReturned());
        Assert.DoesNotContain("invite", harness.Events);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);

        harness.FailSave = false;
        Assert.True(coordinator.NotifyUserReturned());
        DateTimeOffset firstShown = harness.Clock.Now;

        harness.FailSave = true;
        harness.Clock.Now = firstShown.AddDays(1);
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(firstShown, harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(1, harness.Invites);
    }

    [Fact]
    public void StoreRating_AnInvitationThatCouldNotBeShownIsNotCounted()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);

        // An update notice took the window first: the invitation did not reach the screen.
        harness.ShowSucceeds = false;
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Invites);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal("save count=2 attempted=False", harness.Events[^1]);
        Assert.True(coordinator.IsPending);

        harness.ShowSucceeds = true;
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(2, harness.Invites);
    }

    [Fact]
    public void StoreRating_ACardCarriesTheInvitationUnderTheRulesOfAReturn()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        coordinator.RecordCapture(succeeded: true);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));

        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
        // The card on screen holds back the library and settings invitation, never the card's own.
        harness.Busy = true;
        harness.Active = false;
        int saves = harness.SaveCalls;
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.False(coordinator.IsPending);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(saves, harness.SaveCalls);
        // The card is counted before construction and saved after it has shown.
        coordinator.NoteCardOfferShown(offer);
        Assert.Equal(saves + 1, harness.SaveCalls);
        Assert.Equal("save count=2 attempted=False", harness.Events[^1]);
        Assert.Equal(0, harness.Invites);
        Assert.Equal(0, harness.Requests);
    }

    [Fact]
    public void StoreRating_ACardNeverCarriesTheInvitationForThePracticeWhileBusyOrWhileItIsShownElsewhere()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);

        Assert.Null(coordinator.TryOfferOnCard(practice: true));
        harness.CardBusy = true;
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.CardBusy = false;
        harness.InvitationVisible = true;
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.InvitationVisible = false;
        harness.Settings.General.IsFirstRun = true;
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.Settings.General.IsFirstRun = false;
        coordinator.RecordCapture(succeeded: false);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.FailSave = true;
        coordinator.RecordCapture(succeeded: true);
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
        coordinator.NoteCardOfferShown(offer);
        Assert.True(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);

        harness.FailSave = false;
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
    }

    [Fact]
    public void StoreRating_CardsAndReturnsShareTheTwoShowings()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        // The first card closed with the invitation untouched: one more showing, wherever it comes.
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));
        Assert.False(coordinator.NotifyUserReturned());
        harness.Clock.Now = harness.Clock.Now.AddDays(1);
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(StoreRatingPolicy.MaxInvitations, harness.Settings.StoreRating.InvitationShownCount);
        Succeed(coordinator, 3);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.Null(harness.Create().TryOfferOnCard(practice: false));
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Invites);

        var other = new Harness();
        StoreRatingCoordinator returnFirst = other.Create();
        Succeed(returnFirst, 2);
        Assert.True(returnFirst.NotifyUserReturned());
        Assert.Null(returnFirst.TryOfferOnCard(practice: false));
        other.Clock.Now = other.Clock.Now.AddDays(1);
        Assert.NotNull(returnFirst.TryOfferOnCard(practice: false));
        Assert.Null(returnFirst.TryOfferOnCard(practice: false));
        Assert.False(returnFirst.NotifyUserReturned());
        Assert.Equal(StoreRatingPolicy.MaxInvitations, other.Settings.StoreRating.InvitationShownCount);
    }

    [Fact]
    public void StoreRating_ACardThatCouldNotShowGivesItsShowingBack()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        StoreRatingCoordinator.CardOffer? unshown = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(unshown);
        Assert.True(coordinator.WithdrawCardOffer(unshown));
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);
        Assert.True(coordinator.IsPending);
        Assert.Equal("save count=2 attempted=False", harness.Events[^1]);
        int events = harness.Events.Count;
        Assert.False(coordinator.WithdrawCardOffer(unshown));
        Assert.Equal(events, harness.Events.Count);

        // A second card that cannot show gives back only its own showing.
        StoreRatingCoordinator.CardOffer? first = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(first);
        DateTimeOffset firstShown = harness.Clock.Now;
        harness.Clock.Now = firstShown.AddMinutes(5);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.Clock.Now = firstShown.AddDays(1);
        StoreRatingCoordinator.CardOffer? second = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(second);
        Assert.Equal(2, harness.Settings.StoreRating.InvitationShownCount);
        Assert.False(coordinator.WithdrawCardOffer(first));
        Assert.True(coordinator.WithdrawCardOffer(second));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(firstShown, harness.Settings.StoreRating.InvitationShownUtc);

        // A refused offer returns no token, so it has nothing to give back, and it counts and saves nothing.
        harness.CardBusy = true;
        events = harness.Events.Count;
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.Null(coordinator.TryOfferOnCard(practice: false, pendingCapture: true));
        Assert.Equal(events, harness.Events.Count);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(firstShown, harness.Settings.StoreRating.InvitationShownUtc);

        // Nor does it touch an outstanding offer's withdrawal: refused only because the screen is busy,
        // 24 hours after that offer's showing, it leaves that offer to give back exactly its own showing.
        var other = new Harness();
        StoreRatingCoordinator refusing = other.Create();
        Succeed(refusing, 2);
        StoreRatingCoordinator.CardOffer? outstanding = refusing.TryOfferOnCard(practice: false);
        Assert.NotNull(outstanding);
        other.Clock.Now = other.Clock.Now.Add(StoreRatingPolicy.SecondInvitationDelay);
        Assert.True(StoreRatingPolicy.ShouldRequest(other.Settings.StoreRating, isPackaged: true, other.Clock.Now));
        other.CardBusy = true;
        events = other.Events.Count;
        Assert.Null(refusing.TryOfferOnCard(practice: false));
        Assert.Equal(events, other.Events.Count);
        Assert.Equal(1, other.Settings.StoreRating.InvitationShownCount);
        Assert.True(refusing.WithdrawCardOffer(outstanding));
        Assert.Equal(0, other.Settings.StoreRating.InvitationShownCount);
        Assert.Null(other.Settings.StoreRating.InvitationShownUtc);
        Assert.False(refusing.WithdrawCardOffer(outstanding));
    }

    [Fact]
    public void StoreRating_APendingCardPredictsItsOwnSuccessWithoutRecordingIt()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Assert.Null(coordinator.TryOfferOnCard(practice: false, pendingCapture: true));
        coordinator.RecordCapture(succeeded: true);
        coordinator.RecordCapture(succeeded: false);

        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.Null(coordinator.TryOfferOnCard(practice: true, pendingCapture: true));
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        Assert.Equal(1, harness.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.True(harness.Settings.StoreRating.LastCaptureFailed);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
    }

    [Fact]
    public void StoreRating_ACardShowingIsSavedOnlyAfterItShowsAndOnlyOnce()
    {
        var writes = new Queue<Action>();
        var harness = new Harness { DeferWrite = writes.Enqueue };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();

        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Empty(writes);
        Assert.Equal(0, harness.SaveCalls);

        coordinator.NoteCardOfferShown(offer);
        Assert.Single(writes);
        Assert.Equal(0, harness.SaveCalls);
        coordinator.NoteCardOfferShown(offer);
        Assert.Single(writes);
        writes.Dequeue()();
        Assert.Equal(1, harness.SaveCalls);
        Assert.Equal([1], harness.SavedShowingCounts);
    }

    [Fact]
    public void StoreRating_ACardShowingWhoseDeferredSaveFailsStaysCountedAndLogsTheFailure()
    {
        var writes = new Queue<Action>();
        var harness = new Harness { DeferWrite = writes.Enqueue, FailSave = true };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();

        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.Equal(0, harness.SaveCalls);
        Assert.Empty(writes);
        Assert.Empty(harness.Warnings);
        coordinator.NoteCardOfferShown(offer);
        Assert.Single(writes);
        writes.Dequeue()();

        Assert.Equal(1, harness.SaveCalls);
        Assert.Empty(harness.SavedShowingCounts);
        Assert.Equal("Could not save Store rating state", Assert.Single(harness.Warnings));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
    }

    [Fact]
    public void StoreRating_LibraryShowingSavesSynchronouslyBeforeTheInvitation()
    {
        var writes = new Queue<Action>();
        var harness = new Harness { DeferWrite = writes.Enqueue };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();

        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(["save count=2 attempted=False", "invite"], harness.Events);
        Assert.Equal([1], harness.SavedShowingCounts);
        Assert.Empty(writes);
    }

    [Fact]
    public void StoreRating_DeferredWritesUseTheBackgroundSaverAndExitQueuesTheWaitingOnesOnce()
    {
        var writes = new Queue<Action>();
        var background = new List<(int Count, int Successes)>();
        var harness = new Harness
        {
            DeferWrite = writes.Enqueue,
            SaveInBackground = settings => background.Add((settings.StoreRating.InvitationShownCount, settings.StoreRating.SuccessfulCaptureCount)),
        };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 1;
        StoreRatingCoordinator coordinator = harness.Create();

        // A counted success and a card's showing wait for the idle UI; nothing is saved on the spot.
        coordinator.RecordCapture(succeeded: true);
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        Assert.Equal(2, writes.Count);
        Assert.Empty(background);
        Assert.Equal(0, harness.SaveCalls);

        // Exit comes first: one background save of the live state covers both, and those writes add nothing.
        coordinator.FlushDeferredWrites();
        Assert.Equal([(1, 2)], background);
        coordinator.FlushDeferredWrites();
        while (writes.TryDequeue(out Action? write)) write();
        Assert.Equal([(1, 2)], background);

        // A write queued afterwards saves once more, in the background as well.
        Assert.True(coordinator.WithdrawCardOffer(offer));
        writes.Dequeue()();
        Assert.Equal([(1, 2), (0, 2)], background);
        coordinator.FlushDeferredWrites();
        Assert.Equal(2, background.Count);
        Assert.Equal(0, harness.SaveCalls);

        // A choice the user makes, and a window's showing, still save at once on the synchronous path.
        Assert.True(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.SaveCalls);
        coordinator.DismissInvitation();
        Assert.Equal(2, harness.SaveCalls);
        Assert.Equal(2, background.Count);
        Assert.Empty(writes);

        // Nothing reaches the disk during the first run.
        var firstRun = new Harness { DeferWrite = writes.Enqueue, SaveInBackground = _ => background.Add((-1, -1)) };
        firstRun.Settings.General.IsFirstRun = true;
        StoreRatingCoordinator duringFirstRun = firstRun.Create();
        duringFirstRun.RecordCapture(succeeded: true);
        duringFirstRun.FlushDeferredWrites();
        while (writes.TryDequeue(out Action? write)) write();
        Assert.Equal(2, background.Count);
        Assert.Equal(0, firstRun.SaveCalls);
    }

    [Fact]
    public void StoreRating_APendingCardFailureWithdrawsOnlyTheShowingAndDefersItsSave()
    {
        var writes = new Queue<Action>();
        var harness = new Harness { DeferWrite = writes.Enqueue };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 1;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        Assert.Single(writes);
        writes.Dequeue()();
        Assert.Equal([1], harness.SavedShowingCounts);

        coordinator.RecordCapture(succeeded: false);
        Assert.Single(writes);
        Assert.True(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(2, writes.Count);
        Assert.Equal(1, harness.SaveCalls);
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(1, harness.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.True(harness.Settings.StoreRating.LastCaptureFailed);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(2, writes.Count);
        while (writes.TryDequeue(out Action? write)) write();
        Assert.Equal([1, 0, 0], harness.SavedShowingCounts);

        coordinator.RecordCapture(succeeded: true);
        Assert.True(coordinator.IsPending);
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
    }

    [Fact]
    public void StoreRating_AWithdrawnUnshownCardQueuesOnlyItsRestoration()
    {
        var writes = new Queue<Action>();
        var harness = new Harness { DeferWrite = writes.Enqueue };
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.True(coordinator.WithdrawCardOffer(offer));
        coordinator.NoteCardOfferShown(offer);

        Assert.Single(writes);
        Assert.Equal(0, harness.SaveCalls);
        writes.Dequeue()();
        Assert.Equal([0], harness.SavedShowingCounts);
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Empty(writes);
    }

    [Fact]
    public void StoreRating_ARefusedNewOfferKeepsThePreviousCardWithdrawal()
    {
        var harness = new Harness();
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.True(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Null(harness.Settings.StoreRating.InvitationShownUtc);
    }

    [Fact]
    public void StoreRating_AShownCardShowingStandsOnceItsCaptureIsDeliveredOrItsCardCloses()
    {
        // Delivered while its card is open: nothing the card does afterwards gives the showing back.
        var open = new Harness();
        open.Settings.StoreRating.SuccessfulCaptureCount = 1;
        StoreRatingCoordinator coordinator = open.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        coordinator.RecordCapture(succeeded: true);
        coordinator.NoteCardCaptureDelivered(offer);
        int saves = open.SaveCalls;
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(saves, open.SaveCalls);
        Assert.Equal(1, open.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(open.Clock.Now, open.Settings.StoreRating.InvitationShownUtc);

        // Closed with its capture already recorded: the showing stands, as for a card left unanswered.
        var closed = new Harness();
        closed.Settings.StoreRating.SuccessfulCaptureCount = 2;
        coordinator = closed.Create();
        offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        coordinator.NoteCardClosed(offer);
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(1, closed.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(closed.Clock.Now, closed.Settings.StoreRating.InvitationShownUtc);

        // A card that never showed was never seen: it still gives its showing back, whatever came first.
        var unshown = new Harness();
        unshown.Settings.StoreRating.SuccessfulCaptureCount = 1;
        coordinator = unshown.Create();
        offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        coordinator.NoteCardCaptureDelivered(offer);
        coordinator.NoteCardClosed(offer);
        Assert.True(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(0, unshown.Settings.StoreRating.InvitationShownCount);
        Assert.Null(unshown.Settings.StoreRating.InvitationShownUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoreRating_ACardClosedBeforeItsCaptureReportsIsSettledByThatCapture(bool delivered)
    {
        var harness = new Harness();
        harness.Settings.StoreRating.SuccessfulCaptureCount = 1;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        coordinator.NoteCardClosed(offer);

        // Only the capture's own outcome decides: a failed save or copy gives the showing back.
        coordinator.RecordCapture(delivered);
        if (delivered) coordinator.NoteCardCaptureDelivered(offer);
        Assert.Equal(!delivered, coordinator.WithdrawCardOffer(offer));
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(delivered ? 1 : 0, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(delivered, harness.Settings.StoreRating.InvitationShownUtc is not null);
    }

    [Fact]
    public void StoreRating_ANewerLibraryShowingRetiresTheOldCardWithdrawal()
    {
        var harness = new Harness();
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        harness.Clock.Now = harness.Clock.Now.AddDays(1);
        Assert.True(coordinator.NotifyUserReturned());
        int saves = harness.SaveCalls;

        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(2, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(saves, harness.SaveCalls);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("refused")]
    [InlineData("close")]
    public void StoreRating_ACardButtonPressRetiresWithdrawalEvenWhenReviewIsRefused(string action) => StaTestHost.Run(() =>
    {
        var harness = new Harness();
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        coordinator.NoteCardOfferAnswered(offer);
        if (action == "close") coordinator.DismissInvitation();
        else
        {
            harness.CardBusy = action == "refused";
            Task? request = coordinator.AcceptInvitationOnCard(new Window());
            Assert.Equal(action == "accepted", request is not null);
        }

        coordinator.RecordCapture(succeeded: false);
        int saves = harness.SaveCalls;
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(saves, harness.SaveCalls);
    });

    [Fact]
    public void StoreRating_AnIgnoredCardWaitsTwentyFourHoursForEitherShowingPath()
    {
        var harness = new Harness();
        harness.Settings.StoreRating.SuccessfulCaptureCount = 2;
        StoreRatingCoordinator coordinator = harness.Create();
        StoreRatingCoordinator.CardOffer? first = coordinator.TryOfferOnCard(practice: false);
        Assert.NotNull(first);
        coordinator.NoteCardOfferShown(first);
        DateTimeOffset firstShown = harness.Clock.Now;

        harness.Clock.Now = firstShown.AddDays(1).AddSeconds(-1);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.Null(coordinator.TryOfferOnCard(practice: false, pendingCapture: true));
        Assert.False(coordinator.NotifyUserReturned());
        Assert.False(coordinator.IsPending);
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        harness.Clock.Now = firstShown.AddDays(1);
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));
        Assert.Equal(2, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationShownUtc);
    }

    [Fact]
    public void StoreRating_ResidentProbeMeasuresAnAnsweredRatingState()
    {
        var settings = new AppSettings();
        App.ApplyResidentProbeSettings(settings);
        Assert.True(StoreRatingPolicy.IsAnswered(settings.StoreRating));
    }

    [Fact]
    public void StoreRating_ReviewOnACardOpensTheStoreOverThatCardExactlyOnce() => StaTestHost.Run(() =>
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        var card = new Window();
        Succeed(coordinator, 2);
        // Nothing has been shown yet, so there is nothing to answer.
        Assert.Null(coordinator.AcceptInvitationOnCard(card));
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));

        // The card is clicked while another app keeps the focus and the card itself is on screen.
        harness.Active = false;
        harness.Busy = true;
        Assert.False(coordinator.AcceptInvitation());
        Task? request = coordinator.AcceptInvitationOnCard(card);
        Assert.NotNull(request);
        Assert.Same(coordinator.LastRequest, request);
        Assert.Equal(1, harness.Requests);
        Assert.Same(card, Assert.Single(harness.Owners));
        // The attempt is saved before the dialog is requested.
        Assert.Equal("save count=2 attempted=True", harness.Events[harness.Events.IndexOf("request") - 1]);

        Assert.Null(coordinator.AcceptInvitationOnCard(card));
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        harness.Active = true;
        harness.Busy = false;
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Equal(1, harness.Requests);
    });

    [Fact]
    public void StoreRating_ReviewOnACardIsRefusedOnlyWhileBusyOrWhenItsAttemptCannotBeSaved() => StaTestHost.Run(() =>
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        var card = new Window();
        Succeed(coordinator, 2);
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));

        harness.CardBusy = true;
        Assert.Null(coordinator.AcceptInvitationOnCard(card));
        harness.CardBusy = false;
        harness.FailSave = true;
        Assert.Null(coordinator.AcceptInvitationOnCard(card));
        Assert.Equal(0, harness.Requests);

        // An attempt that could not be saved is not one: Review can be pressed again.
        Assert.Null(harness.Settings.StoreRating.RequestAttemptedUtc);
        harness.FailSave = false;

        // An error recorded since the card showed refuses the library's Review, never the card's.
        coordinator.RecordCapture(succeeded: false);
        Assert.False(coordinator.AcceptInvitation());
        Assert.NotNull(coordinator.AcceptInvitationOnCard(card));
        Assert.Equal(1, harness.Requests);
        Assert.Same(card, Assert.Single(harness.Owners));
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.RequestAttemptedUtc);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StoreRating_ReviewOnAPredictedCardAfterAnErrorOpensTheStoreBeforeItsCaptureReports(bool captureSucceeds) => StaTestHost.Run(() =>
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        var card = new Window();
        // The previous use failed: only the success this card predicts for its capture makes it eligible.
        Succeed(coordinator, 2);
        coordinator.RecordCapture(succeeded: false);
        StoreRatingCoordinator.CardOffer? offer = coordinator.TryOfferOnCard(practice: false, pendingCapture: true);
        Assert.NotNull(offer);
        coordinator.NoteCardOfferShown(offer);
        Assert.True(harness.Settings.StoreRating.LastCaptureFailed);

        // Review before the capture's save and copy report: the Store opens and the answer is recorded.
        coordinator.NoteCardOfferAnswered(offer);
        Task? request = coordinator.AcceptInvitationOnCard(card);
        Assert.NotNull(request);
        Assert.Equal(1, harness.Requests);
        Assert.Same(card, Assert.Single(harness.Owners));
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.RequestAttemptedUtc);
        Assert.Equal("save count=2 attempted=True", harness.Events[harness.Events.IndexOf("request") - 1]);

        // Whatever the capture then reports, the answer stands, the showing stays, and nothing asks again.
        coordinator.RecordCapture(captureSucceeds);
        Assert.False(coordinator.WithdrawCardOffer(offer));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        harness.Clock.Now = harness.Clock.Now.AddDays(2);
        Succeed(coordinator, 2);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Null(coordinator.AcceptInvitationOnCard(card));
        Assert.Equal(1, harness.Requests);
        Assert.Equal(0, harness.Invites);
    });

    [Fact]
    public void StoreRating_CloseOnACardEndsTheInvitation()
    {
        var harness = new Harness();
        StoreRatingCoordinator coordinator = harness.Create();
        Succeed(coordinator, 2);
        Assert.NotNull(coordinator.TryOfferOnCard(practice: false));
        harness.Clock.Now = harness.Clock.Now.AddSeconds(3);
        coordinator.DismissInvitation();
        Assert.Equal(harness.Clock.Now, harness.Settings.StoreRating.InvitationDismissedUtc);

        Succeed(coordinator, 5);
        Assert.Null(coordinator.TryOfferOnCard(practice: false));
        Assert.False(coordinator.NotifyUserReturned());
        Assert.Null(harness.Create().TryOfferOnCard(practice: false));
        Assert.Equal(1, harness.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(0, harness.Invites);
        Assert.Equal(0, harness.Requests);
    }
}

public sealed class StoreRatingNoticeTests
{
    [Fact]
    public void ReviewOnTheSettingsCardIsNeverTakenForClose() => StaTestHost.Run(() =>
    {
        var notice = new EngagementNotice();
        var closed = new List<EngagementNoticeKind>();
        int reviews = 0;
        notice.ReviewChosen = () => reviews++;
        notice.ClosedByUser = closed.Add;

        // Review the shell had to refuse leaves the invitation unanswered, to be offered once more.
        notice.ShowInvitation();
        Click(ButtonNamed(notice, UiText.Get("StoreRating_InviteAction")));
        Assert.Equal(1, reviews);
        Assert.Empty(closed);
        Assert.False(notice.IsShown);
        Assert.Equal(EngagementNoticeKind.None, notice.Kind);

        notice.ShowInvitation();
        Click(ButtonNamed(notice, UiText.Get("Dialog.Close")));
        Assert.Equal([EngagementNoticeKind.Rating], closed);
        Assert.Equal(1, reviews);
    });

    private static Button ButtonNamed(DependencyObject root, string name) =>
        Descendants(root).OfType<Button>().First(button => AutomationProperties.GetName(button) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }
}

public sealed class StoreRatingOutcomesTests
{
    [Theory]
    [InlineData(nameof(TextCaptureStatus.Copied), true)]
    [InlineData(nameof(TextCaptureStatus.NoText), null)]
    [InlineData(nameof(TextCaptureStatus.ClipboardFailed), false)]
    [InlineData(nameof(TextCaptureStatus.Unavailable), false)]
    [InlineData(nameof(TextCaptureStatus.Failed), false)]
    [InlineData(nameof(TextCaptureStatus.Cancelled), null)]
    public void TextCapture_CopiedTextCountsAndErrorsBlock(string status, bool? expected) =>
        Assert.Equal(expected, StoreRatingOutcomes.FromTextCapture(Enum.Parse<TextCaptureStatus>(status)));

    [Fact]
    public void TextCapture_TheCasesAboveCoverEveryStatus() =>
        Assert.Equal(["Copied", "NoText", "ClipboardFailed", "Unavailable", "Failed", "Cancelled"], Enum.GetNames<TextCaptureStatus>());

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    // A practice capture's success is not counted; its error still blocks the invitation.
    [InlineData(true, true, false, null)]
    [InlineData(false, true, false, false)]
    // A capture taken as a pin is counted once, by its new pin.
    [InlineData(true, false, true, null)]
    [InlineData(false, false, true, false)]
    public void StillCapture_CountsOnceAndNeverInsideThePractice(bool delivered, bool practice, bool pinRequested, bool? expected) =>
        Assert.Equal(expected, StoreRatingOutcomes.FromStillCapture(delivered, practice, pinRequested));

    [Fact]
    public void PracticePin_IsOnlyThePinTheOpenPracticeStepAsksFor()
    {
        Assert.False(StoreRatingOutcomes.IsPracticePin(null));

        var session = new PracticeSession(pinOnly: true);
        Assert.Equal(PracticeStep.B0, session.Step);
        Assert.False(StoreRatingOutcomes.IsPracticePin(session));

        session.Skip();
        Assert.Equal(PracticeStep.B1, session.Step);
        Assert.True(StoreRatingOutcomes.IsPracticePin(session));

        session.Skip();
        Assert.Equal(PracticeStep.B2, session.Step);
        Assert.False(StoreRatingOutcomes.IsPracticePin(session));

        session.Back();
        Assert.Equal(PracticeStep.B1, session.Step);
        session.End();
        Assert.False(StoreRatingOutcomes.IsPracticePin(session));
    }
}

public sealed class StoreRatingAndPrtScGuideApplyTests : KoreanCaptionTest
{
    private sealed class BlockingRegistrar(params Hotkey[] blocked) : IHotkeyRegistrar
    {
        private readonly Dictionary<int, Hotkey> _held = [];

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            if (blocked.Contains(hotkey) || _held.ContainsValue(hotkey))
            {
                errorCode = 1409;
                errorMessage = "Hot key is already registered.";
                return false;
            }

            _held[id] = hotkey;
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }

    private sealed class MemoryRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    private static void WithApply(IHotkeyRegistrar registrar, Action<SettingsStore, Func<AppSettings>, SettingsApplyService> body)
    {
        StaTestHost.Run(() =>
        {
            string root = TestRecycleBin.CreateTempSubdirectory("mycapture-store-rating-apply-").FullName;
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            AppSettings published = store.Load();
            store.CompleteFirstRun(published);
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
            hotkeys.Initialize(published.Hotkeys);
            var startup = new StartupRegistrationService(new MemoryRunKeyStore(), @"C:\Apps\MyCapture.exe");
            var apply = new SettingsApplyService(store, () => published, next => published = next, null, hotkeys, startup, NullLogger.Instance);
            body(store, () => published, apply);
        });
    }

    [Fact]
    public void StoreRating_ApplyKeepsTheLiveStateRecordedWhileTheWindowWasOpen()
    {
        WithApply(new BlockingRegistrar(), (store, live, apply) =>
        {
            live().StoreRating.SuccessfulCaptureCount = 3;
            var draft = new SettingsDraft(live()) { MaxItems = "450" };

            // Captures taken while the window is open keep counting on the live object.
            DateTimeOffset attempted = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
            live().StoreRating.SuccessfulCaptureCount = 8;
            live().StoreRating.RequestAttemptedUtc = attempted;
            live().StoreUpdate.CompletionNoticeVersion = "4.1.2.0";

            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());
            Assert.True(result.Saved);
            Assert.Equal(450, live().Queue.MaxItems);
            Assert.Equal(8, live().StoreRating.SuccessfulCaptureCount);
            Assert.Equal(attempted, live().StoreRating.RequestAttemptedUtc);
            Assert.Equal("4.1.2.0", store.Load().StoreUpdate.CompletionNoticeVersion);
            StoreRatingState persisted = store.Load().StoreRating;
            Assert.Equal(8, persisted.SuccessfulCaptureCount);
            Assert.Equal(attempted, persisted.RequestAttemptedUtc);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrtScGuide_ApplyFailureExplainsTheWindowsPrintScreenSetting(bool windowsClaimsKey)
    {
        Assert.True(Hotkey.TryParse("PrintScreen", out Hotkey printScreen));
        WithApply(new BlockingRegistrar(printScreen), (_, live, apply) =>
        {
            apply.PrintScreenCaptureKeyEnabled = () => windowsClaimsKey;
            var draft = new SettingsDraft(live()) { CaptureFullScreenHotkey = "PrintScreen" };

            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());

            Assert.True(result.Saved);
            Assert.False(result.HotkeysApplied);
            Assert.Equal(Hotkey.None, live().Hotkeys.CaptureFullScreen);
            string notice = PrintScreenKeyGuide.FailureNotice([new Hotkey(HotkeyModifiers.None, PrintScreenKeyGuide.VkSnapshot)], true);
            if (windowsClaimsKey) Assert.Contains(notice, result.Messages);
            else Assert.DoesNotContain(notice, result.Messages);
        });
    }

    [Fact]
    public void PrtScGuide_DefaultApplyServiceNeverReadsTheMachineSetting()
    {
        Assert.True(Hotkey.TryParse("PrintScreen", out Hotkey printScreen));
        WithApply(new BlockingRegistrar(printScreen), (_, live, apply) =>
        {
            var draft = new SettingsDraft(live()) { CaptureFullScreenHotkey = "PrintScreen" };
            SettingsApplyResult result = apply.Apply(draft.ToAppSettings());
            Assert.DoesNotContain(PrintScreenKeyGuide.FailureNotice([new Hotkey(HotkeyModifiers.None, PrintScreenKeyGuide.VkSnapshot)], true), result.Messages);
            Assert.Equal(PrintScreenKeyGuide.VkSnapshot, printScreen.VirtualKey);
        });
    }
}
