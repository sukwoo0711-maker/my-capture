using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Card first: the quick access card shows on the turn after the selector closes, before the capture
/// is saved and copied. Its actions wait for the saved capture, its status for the copy, and a failed
/// save or copy is reported on the card instead of a second notification.
/// </summary>
public sealed class QuickAccessCardFirstTests : KoreanCaptionTest
{
    // ---- The card on its own -------------------------------------------------------------------

    [Fact]
    public void APendingCardDisablesEveryActionThatNeedsTheSavedCaptureUntilItIsBound() => RunCard(router =>
    {
        int edits = 0, pins = 0, saves = 0, copies = 0, library = 0, texts = 0;
        QuickAccessWindow card = Pending(router, copied: true);
        try
        {
            card.Show();
            Assert.True(card.IsPending);
            Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
            Assert.False(card.LibraryButtonForTest.IsEnabled);
            Assert.Equal((UiText.Get("QuickAccess.Copied"), FeedbackKind.Confirmation, true, false), card.StatusForTest);

            // Neither their keys nor a click do anything yet: Edit and Library do not close the card.
            foreach (string action in QuickAccessWindow.ActionOrder) card.ShortcutActions[action]();
            card.LibraryButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert.True(card.IsVisible);
            Assert.Contains("busy=False", card.TimeoutStateForTest, StringComparison.Ordinal);
            Assert.Equal(UiText.Get("QuickAccess.Copied"), card.StatusForTest.Text);

            card.BindCapture(new QuickAccessCardActions(
                () => { copies++; return Task.FromResult(true); }, () => edits++,
                () => { pins++; return Task.CompletedTask; }, () => { saves++; return Task.FromResult(true); },
                () => library++, () => Task.FromResult("unused.png"),
                () => { texts++; return Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Copied, null, 3)); }));
            Assert.False(card.IsPending);
            Assert.All(card.ActionButtonsForTest, button => Assert.True(button.IsEnabled));
            Assert.True(card.LibraryButtonForTest.IsEnabled);
            foreach (string action in new[] { "Copy", "Pin", "Save", "CopyText" }) card.ShortcutActions[action]();
            Pump();
            Assert.Equal((1, 1, 1, 1), (copies, pins, saves, texts));
            card.ShortcutActions["Edit"]();
            Assert.Equal(1, edits);
            Assert.False(card.IsVisible);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ACopyStillRunningSaysCopyingWithoutAnnouncingItAndThenAnnouncesOnlyItsResult() => RunCard(router =>
    {
        QuickAccessWindow card = Pending(router, copied: null);
        try
        {
            card.Show();
            Assert.Equal(("복사 중…", FeedbackKind.Progress, true, false), card.StatusForTest);
            Assert.Null(card.LastAnnouncementForTest);
            Assert.Contains("pending=True", card.TimeoutStateForTest, StringComparison.Ordinal);

            card.ReportInitialCopy(true);
            Assert.Equal(("복사됨", FeedbackKind.Confirmation, true, false), card.StatusForTest);
            Assert.Equal("복사됨", card.LastAnnouncementForTest);
            // The copy reports once.
            card.ReportInitialCopy(false);
            Assert.Equal(("복사됨", FeedbackKind.Confirmation, true, false), card.StatusForTest);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ACopyFailureHoldsTheCardAndIsRetriedOnceTheCaptureIsSaved() => RunCard(router =>
    {
        int copies = 0, closed = 0;
        QuickAccessWindow card = Pending(router, copied: null);
        card.Closed += (_, _) => closed++;
        try
        {
            card.Show();
            card.ReportInitialCopy(false);
            Assert.Equal((QuickAccessWindow.FailureText("Copy"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Equal(QuickAccessWindow.FailureText("Copy"), card.LastAnnouncementForTest);
            Assert.False(CopyButton(card).IsEnabled);
            card.Advance(TimeSpan.FromHours(1), interacting: false);
            Assert.Equal(0, closed);

            card.BindCapture(Actions(copy: () => { copies++; return Task.FromResult(true); }));
            Assert.True(CopyButton(card).IsEnabled);
            card.ShortcutActions["Copy"]();
            Pump();
            Assert.Equal(1, copies);
            Assert.Equal((UiText.Get("QuickAccess.Copied"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
            Assert.Contains("held=False", card.TimeoutStateForTest, StringComparison.Ordinal);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Fact]
    public void AFailedCopyKnownWhenTheCardShowsIsItsFirstLine() => RunCard(router =>
    {
        QuickAccessWindow card = Pending(router, copied: false);
        try
        {
            card.Show();
            Assert.Equal((QuickAccessWindow.FailureText("Copy"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Contains("held=True", card.TimeoutStateForTest, StringComparison.Ordinal);
            Assert.Contains("pending=True", card.TimeoutStateForTest, StringComparison.Ordinal);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ASaveFailureHoldsTheCardWithItsReasonAndItsActionsStayUnavailable() => RunCard(router =>
    {
        const string title = "캡처를 저장할 수 없습니다";
        const string detail = "라이브러리에 저장하지 못했습니다. 디스크 공간이 부족합니다.";
        int closed = 0;
        string tip = UiText.Get("Onboard_FirstCaptureTip");
        QuickAccessWindow card = QuickAccessWindow.ForPendingCapture(Image(), copiedToClipboard: null,
            targetMonitor: Offscreen(), firstCaptureTip: tip, keyboardRouter: router);
        card.Closed += (_, _) => closed++;
        try
        {
            card.Show();
            Assert.Equal(Visibility.Visible, TipText(card, tip).Visibility);
            card.ShowCaptureFailure(title, detail);

            Assert.True(card.HasCaptureFailed);
            Assert.False(card.IsPending);
            Assert.Equal((title, FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Equal((detail, true), card.DetailForTest);
            Assert.Equal(title + " " + detail, card.LastAnnouncementForTest);
            // The tip names P, which the card no longer offers.
            Assert.Equal(Visibility.Collapsed, TipText(card, tip).Visibility);
            Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
            card.Advance(TimeSpan.FromHours(1), interacting: false);
            Assert.Equal(0, closed);

            // The copy finishing afterwards is announced without replacing the reason.
            card.ReportInitialCopy(true);
            Assert.Equal((title, FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Equal("복사됨", card.LastAnnouncementForTest);
            // A capture that was not saved never gets actions.
            card.BindCapture(Actions());
            Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
            Assert.True(card.HasCaptureFailed);

            card.ShortcutActions["Close"]();
            Assert.Equal(1, closed);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Fact]
    public void TheCountdownStartsWhenTheCardShowsButCannotCloseItBeforeTheCaptureIsSaved() => RunCard(router =>
    {
        int closed = 0;
        QuickAccessWindow card = Pending(router, copied: true);
        card.Closed += (_, _) => closed++;
        try
        {
            card.Show();
            card.Advance(TimeSpan.FromSeconds(2), interacting: false);
            Assert.Equal(QuickAccessWindow.Lifetime - TimeSpan.FromSeconds(2), card.RemainingForTest);
            card.Advance(TimeSpan.FromSeconds(30), interacting: false);
            Assert.Equal(0, closed);
            Assert.Equal(TimeSpan.Zero, card.RemainingForTest);

            // Once saved, the actions get at least the hold after a result.
            card.BindCapture(Actions());
            Assert.Equal(FeedbackTiming.HoldAfterResult, card.RemainingForTest);
            card.Advance(FeedbackTiming.HoldAfterResult - TimeSpan.FromMilliseconds(100), interacting: false);
            Assert.Equal(0, closed);
            card.Advance(TimeSpan.FromMilliseconds(200), interacting: false);
            Assert.Equal(1, closed);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Fact]
    public void ACaptureSavedEarlyKeepsTheRestOfTheCountdown() => RunCard(router =>
    {
        QuickAccessWindow card = Pending(router, copied: true);
        try
        {
            card.Show();
            card.Advance(TimeSpan.FromMilliseconds(500), interacting: false);
            card.BindCapture(Actions());
            Assert.Equal(QuickAccessWindow.Lifetime - TimeSpan.FromMilliseconds(500), card.RemainingForTest);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ACopyStillRunningAfterTheSaveAlsoKeepsTheCard() => RunCard(router =>
    {
        int closed = 0;
        QuickAccessWindow card = Pending(router, copied: null);
        card.Closed += (_, _) => closed++;
        try
        {
            card.Show();
            card.BindCapture(Actions());
            card.Advance(TimeSpan.FromSeconds(30), interacting: false);
            Assert.Equal(0, closed);
            card.ReportInitialCopy(true);
            Assert.Equal(FeedbackTiming.HoldAfterResult, card.RemainingForTest);
            card.Advance(FeedbackTiming.HoldAfterResult, interacting: false);
            Assert.Equal(1, closed);
        }
        finally { if (closed == 0) card.Close(); }
    });

    // ---- The App's capture flow ------------------------------------------------------------------

    [Fact]
    public void TheCardShowsRightAfterTheSelectorAndBindsTheCaptureOnceSaved() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();

        // On screen before anything is saved or copied, with the captured image and the tip.
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.True(card.IsOpen);
        Assert.True(card.IsPending);
        Assert.Null(shell.CurrentRecord);
        Assert.Null(shell.LatestReceipt);
        Assert.Same(shell.Selection.SelectedBitmap, card.PreviewForTest.Source);
        Assert.Equal(("복사 중…", FeedbackKind.Progress, true, false), card.StatusForTest);
        Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
        Assert.False(card.LibraryButtonForTest.IsEnabled);
        Assert.Equal(Visibility.Visible, TipText(card, UiText.Get("Onboard_FirstCaptureTip")).Visibility);
        Assert.False(shell.Settings.General.FirstCaptureTipShown);
        Assert.Equal(0, shell.Settings.StoreRating.SuccessfulCaptureCount);

        // Saved: the record, the latest receipt and the actions are bound; the copy still runs.
        shell.PumpUntil(() => !card.IsPending);
        CaptureRecord saved = Assert.IsType<CaptureRecord>(shell.CurrentRecord);
        Assert.Equal(saved.Id, shell.LatestReceipt);
        Assert.All(card.ActionButtonsForTest, button => Assert.True(button.IsEnabled));
        Assert.True(card.LibraryButtonForTest.IsEnabled);
        Assert.Equal("복사 중…", card.StatusForTest.Text);
        Assert.True(shell.Settings.General.FirstCaptureTipShown);
        Assert.Equal(0, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.False(handler.IsCompleted);

        copy.SetResult(true);
        shell.Wait(handler);
        Assert.Equal(("복사됨", FeedbackKind.Confirmation, true, false), card.StatusForTest);
        Assert.Equal("복사됨", card.LastAnnouncementForTest);
        Assert.Equal(1, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.False(shell.Settings.StoreRating.LastCaptureFailed);
        Assert.Equal(1, shell.CopyCalls);
        Assert.Single(shell.Queue.Records);
        Assert.Empty(shell.Notices);
        // No second card follows once saved and copied.
        shell.RunIdleTurns();
        Assert.Same(card, shell.Card);
    });

    [Fact]
    public void ACopyFinishedBeforeTheCardShowsStartsItAtCopied() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal((UiText.Get("QuickAccess.Copied"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
        shell.Wait(handler);
        Assert.False(card.IsPending);
        Assert.Equal(UiText.Get("QuickAccess.Copied"), card.StatusForTest.Text);
    });

    [Fact]
    public void TheCardHoldsTheSavedCaptureInTheLibraryUntilItCloses() => StaTestHost.Run(() =>
    {
        using var shell = new Shell(new QueueSettings { MaxItems = 1 });
        shell.Wait(shell.Select());
        CaptureRecord saved = Assert.IsType<CaptureRecord>(shell.CurrentRecord);
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.IsPending);
        var evicted = new List<Guid>();
        shell.Queue.Evicted += (_, e) => evicted.Add(e.Record.Id);
        // As the capture session's end does.
        shell.ReleaseEditSession();

        shell.Wait(shell.Persistence.PersistOriginalAsync(shell.Frame.Bitmap, 1, "next capture", string.Empty));
        Assert.DoesNotContain(saved.Id, evicted);
        card.Close();
        shell.Wait(shell.Persistence.PersistOriginalAsync(shell.Frame.Bitmap, 1, "later capture", string.Empty));
        Assert.Contains(saved.Id, evicted);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosingTheCardReleasesItsCaptureStateWhileSavingCopyingAndAfterCompletion(bool savedBeforeClose, bool copiedBeforeClose) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        object first = shell.CardFirstState!;
        Assert.NotNull(first);
        Assert.Null(shell.Flight);
        if (savedBeforeClose) shell.PumpUntil(() => !card.IsPending);
        else Assert.True(card.IsPending);
        if (copiedBeforeClose)
        {
            copy.SetResult(true);
            shell.Wait(handler);
        }

        card.CloseNow();

        Assert.Null(shell.Card);
        Assert.Null(shell.CardFirstState);
        Assert.Null(first.GetType().GetProperty("Card", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first));
        // Any unfinished save and copy finish without restoring the dismissed card or its root.
        if (!copiedBeforeClose) copy.SetResult(true);
        shell.Wait(handler);
        shell.RunIdleTurns();
        Assert.Null(shell.Card);
        Assert.Null(shell.CardFirstState);
        Assert.Single(shell.Queue.Records);
        Assert.Equal(1, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.Empty(shell.Notices);
    });

    [Fact]
    public void ClosingThePreviousCardPreservesItsReplacementCaptureState() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.Wait(shell.Select());
        QuickAccessWindow previous = Assert.IsType<QuickAccessWindow>(shell.Card);
        object first = shell.CardFirstState!;
        Assert.NotNull(first);

        Task handler = shell.Select();
        shell.RunSendTurn();

        QuickAccessWindow replacement = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.NotSame(previous, replacement);
        Assert.False(previous.IsOpen);
        Assert.NotNull(shell.CardFirstState);
        Assert.NotSame(first, shell.CardFirstState);
        Assert.Null(first.GetType().GetProperty("Card", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first));
        shell.Wait(handler);
        Assert.Same(replacement, shell.Card);
        Assert.False(replacement.IsPending);
        Assert.Equal(Assert.IsType<CaptureRecord>(shell.CurrentRecord).Id, shell.LatestReceipt);
    });

    [Fact]
    public void ASaveFailureTurnsTheCardIntoTheFailureWithoutASecondNotification() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var failure = new IOException("synthetic library save failure");
        shell.Persistence.BeforeRecordMetadataCommit = _ => throw failure;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.True(card.IsPending);
        shell.Wait(handler);

        Assert.True(card.IsOpen);
        Assert.True(card.HasCaptureFailed);
        Assert.Equal((UiText.Get("Text_4033027EAEDB"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
        Assert.Equal((App.LibrarySaveFailureMessage(AfterCaptureAction.QuickAccess, failure), true), card.DetailForTest);
        Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
        Assert.Empty(shell.Notices);
        // The capture is kept for the recovery editor, as before; nothing counts as a success.
        Assert.Null(shell.CurrentRecord);
        Assert.NotNull(shell.PendingRecord);
        Assert.Null(shell.LatestReceipt);
        Assert.False(shell.Settings.General.FirstCaptureTipShown);
        Assert.Equal(0, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
        shell.RunIdleTurns();
        Assert.Same(card, shell.Card);
    });

    [Fact]
    public void ASaveFailureBeforeTheCardsTurnShowsTheNotificationAndNoCard() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        // The library is unavailable: the save fails before the handler first yields.
        shell.SetPersistence(null);
        Task handler = shell.Select();
        shell.RunSendTurn();
        shell.Wait(handler);
        shell.RunIdleTurns();

        Assert.Null(shell.Card);
        (string title, string message) = Assert.Single(shell.Notices);
        Assert.Equal(UiText.Get("Text_4033027EAEDB"), title);
        Assert.StartsWith(UiText.Get("Capture.LibrarySaveFailedNoEditor")[..10], message, StringComparison.Ordinal);
    });

    [Fact]
    public void ASaveFailureAfterTheUserClosedTheCardIsStillReported() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic library save failure");
        Task handler = shell.Select();
        shell.RunSendTurn();
        Assert.IsType<QuickAccessWindow>(shell.Card).ShortcutActions["Close"]();
        shell.Wait(handler);

        Assert.Null(shell.Card);
        Assert.Equal(UiText.Get("Text_4033027EAEDB"), Assert.Single(shell.Notices).Title);
    });

    [Fact]
    public void ACopyFailureIsTheCardsOwnFailureToRetryWithoutTheTrayNotice() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        shell.PumpUntil(() => !card.IsPending);
        copy.SetResult(false);
        shell.Wait(handler);

        Assert.Equal((QuickAccessWindow.FailureText("Copy"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
        // C retries it on the saved capture.
        Assert.True(CopyButton(card).IsEnabled);
        Assert.Empty(shell.Notices);
        Assert.Equal(0, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
    });

    [Fact]
    public void ACopyFailureKnownWhenTheCardShowsIsOnTheCardOnly() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.CopyResult = Task.FromResult(false);
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(QuickAccessWindow.FailureText("Copy"), card.StatusForTest.Text);
        shell.Wait(handler);

        Assert.False(card.IsPending);
        Assert.Equal((QuickAccessWindow.FailureText("Copy"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
        Assert.Empty(shell.Notices);
    });

    [Fact]
    public void ACopyFailureTheCardCannotRetryKeepsTodaysNotification() => StaTestHost.Run(() =>
    {
        // Saved, but the card was closed before the copy failed.
        using (var shell = new Shell())
        {
            var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.CopyResult = copy.Task;
            Task handler = shell.Select();
            shell.RunSendTurn();
            QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
            shell.PumpUntil(() => !card.IsPending);
            card.ShortcutActions["Close"]();
            copy.SetResult(false);
            shell.Wait(handler);
            Assert.Equal((UiText.Get("Text_422F61DDCB7C"), UiText.Get("Text_9E0860CEAEC2")), Assert.Single(shell.Notices));
        }

        // Neither saved nor copied: the card shows the save failure, the notification the copy's retry in the editor.
        using (var shell = new Shell())
        {
            shell.CopyResult = Task.FromResult(false);
            shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic library save failure");
            Task handler = shell.Select();
            shell.RunSendTurn();
            QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
            shell.Wait(handler);
            Assert.True(card.HasCaptureFailed);
            Assert.Equal(UiText.Get("Text_4033027EAEDB"), card.StatusForTest.Text);
            Assert.Equal((UiText.Get("Text_422F61DDCB7C"), UiText.Get("Text_4048C8E18C61")), Assert.Single(shell.Notices));
        }
    });

    [Fact]
    public void ACaptureCommandWhileTheCaptureIsSavedIsRefusedAndKeepsItsCard() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        using CaptureOverlayCoordinator coordinator = shell.UseCoordinator();
        QuickAccessWindow? atCommand = null;
        bool refusedWithItsCard = false;
        shell.Persistence.BeforeRecordMetadataCommit = _ =>
        {
            // Mid-save, the card already in front: the region capture command arrives.
            atCommand = shell.Card;
            shell.RequestRegionCapture();
            refusedWithItsCard = atCommand is { IsOpen: true, IsPending: true } && ReferenceEquals(shell.Card, atCommand)
                && !coordinator.IsSelecting;
        };
        Assert.True(coordinator.StartWithSelection(shell.Frame, shell.Region, "Source - App", recordForRepeat: false));
        shell.Wait(coordinator.LastTransitionForTest);

        Assert.True(refusedWithItsCard);
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Same(atCommand, card);
        Assert.False(card.IsPending);
        Assert.Single(shell.Queue.Records);
        Assert.False(coordinator.IsActive);

        // Once saved and copied, the next capture command closes it at once, as before.
        shell.Dismiss();
        Assert.False(card.IsVisible);
        Assert.Null(shell.Card);
        Assert.Null(shell.LatestReceipt);
    });

    [Fact]
    public void ACardClosedForANewCaptureBeforeTheSaveFinishesNeverComesBack() => StaTestHost.Run(() =>
    {
        using var shell = new Shell(new QueueSettings { MaxItems = 1 });
        QuickAccessWindow? first = null;
        bool closedAtOnce = false;
        shell.Persistence.BeforeRecordMetadataCommit = _ =>
        {
            if (first is not null) return;
            first = shell.Card;
            // Nothing refuses it here (no capture session holds the command), so the card goes at once.
            shell.Dismiss();
            closedAtOnce = first is { IsVisible: false } && shell.Card is null;
        };
        shell.Wait(shell.Select());

        Assert.True(closedAtOnce);
        CaptureRecord saved = Assert.IsType<CaptureRecord>(shell.CurrentRecord);
        Assert.Single(shell.Queue.Records);
        Assert.Null(shell.Card);
        Assert.Null(shell.LatestReceipt);
        shell.RunIdleTurns();
        Assert.Null(shell.Card);
        Assert.Empty(shell.Notices);
        // Saved and copied: it still counts.
        Assert.Equal(1, shell.Settings.StoreRating.SuccessfulCaptureCount);

        // No receipt holds it: once the session ends, the next capture's card replaces it in the library.
        shell.ReleaseEditSession();
        var evicted = new List<Guid>();
        shell.Queue.Evicted += (_, e) => evicted.Add(e.Record.Id);
        shell.Wait(shell.Select());
        QuickAccessWindow second = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.NotSame(first, second);
        Assert.False(second.IsPending);
        Assert.Equal(Assert.IsType<CaptureRecord>(shell.CurrentRecord).Id, shell.LatestReceipt);
        Assert.Contains(saved.Id, evicted);
    });

    [Theory]
    [InlineData(AfterCaptureAction.Editor)]
    [InlineData(AfterCaptureAction.ClipboardOnly)]
    public void OtherAfterCaptureModesShowNoCard(AfterCaptureAction mode) => StaTestHost.Run(() =>
    {
        using var shell = new Shell(mode: mode);
        Task handler = shell.Select();
        shell.RunSendTurn();
        Assert.Null(shell.Card);
        shell.Wait(handler);
        shell.RunIdleTurns();
        Assert.Null(shell.Card);
        Assert.Equal(1, shell.CopyCalls);
        Assert.Equal(1, shell.Settings.StoreRating.SuccessfulCaptureCount);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TextCaptureAndTheTestSwitchShowTheCardOnlyOnceSavedAndCopied(bool textCapture, bool afterSave) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.ShowsCardAfterSave = afterSave;
        Task handler = shell.Select(textCapture);
        shell.RunSendTurn();
        Assert.Null(shell.Card);
        shell.Wait(handler);
        Assert.Null(shell.Card);
        shell.RunIdleTurns();

        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.IsPending);
        Assert.All(card.ActionButtonsForTest, button => Assert.True(button.IsEnabled));
        Assert.Equal(UiText.Get("QuickAccess.Copied"), card.StatusForTest.Text);
        Assert.Equal(Assert.IsType<CaptureRecord>(shell.CurrentRecord).Id, shell.LatestReceipt);
        Assert.True(shell.Settings.General.FirstCaptureTipShown);
    });

    [Fact]
    public void TheLiftedCaptureFliesIntoTheCardShownFirst() => StaTestHost.Run(() =>
    {
        using var shell = new Shell(motion: true);
        Task handler = shell.Select();
        shell.RunSendTurn();

        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        CaptureFlight flight = Assert.IsType<CaptureFlight>(shell.Flight);
        Assert.Same(shell.Selection.SelectedBitmap, flight.Image);
        Assert.True(card.IsPending);
        Assert.True(card.IsLanding);
        shell.Wait(handler);
        Assert.True(shell.PumpUntil(() => flight.IsDone, throwOnTimeout: false), "The flight did not land.");
        Assert.False(card.IsLanding);
        Assert.Equal(1, card.PreviewForTest.Opacity);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedLocationReceiptFollowsACardClosedBeforeOrAfterDurableSave(bool savedBeforeClose) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        using var presenter = new ThemedShellPresenter(() => false) { NotificationMonitor = Offscreen };
        shell.UseReceiptPresenter(presenter);
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        if (savedBeforeClose) shell.PumpUntil(() => !card.IsPending);
        else Assert.True(card.IsPending);
        card.Close();
        if (!savedBeforeClose) Assert.False(presenter.HasNotification);
        shell.Wait(handler);
        shell.RunIdleTurns();
        Assert.True(presenter.HasNotification);
        Assert.Equal(1, shell.Settings.General.SavedLocationTipCount);
    });

    [Fact]
    public void SavedLocationReceiptNeverFollowsAFailedPendingSave() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        using var presenter = new ThemedShellPresenter(() => false) { NotificationMonitor = Offscreen };
        shell.UseReceiptPresenter(presenter);
        shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("Synthetic save failure");
        Task handler = shell.Select();
        shell.RunSendTurn();
        Assert.IsType<QuickAccessWindow>(shell.Card).Close();
        shell.Wait(handler);
        shell.RunIdleTurns();
        Assert.False(presenter.HasNotification);
        Assert.Equal(0, shell.Settings.General.SavedLocationTipCount);
    });

    [Fact]
    public void SavedLocationReceiptFollowsTheCardShownAfterSaveToo() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        using var presenter = new ThemedShellPresenter(() => false) { NotificationMonitor = Offscreen };
        shell.UseReceiptPresenter(presenter);
        shell.ShowsCardAfterSave = true;
        shell.Wait(shell.Select());
        shell.RunIdleTurns();
        Assert.IsType<QuickAccessWindow>(shell.Card).Close();
        Assert.True(presenter.HasNotification);
        Assert.Equal(1, shell.Settings.General.SavedLocationTipCount);
    });

    // ---- Helpers ---------------------------------------------------------------------------------

    private static Button CopyButton(QuickAccessWindow card) => card.ActionButtonsForTest[QuickAccessWindow.ActionOrder.ToList().IndexOf("Copy")];

    private static TextBlock TipText(QuickAccessWindow card, string tip) =>
        QuickAccessDetachedFixture.Descendants(card).OfType<TextBlock>().First(text => text.Text == tip);

    private static QuickAccessWindow Pending(ReceiptKeyboardRouter router, bool? copied) =>
        QuickAccessWindow.ForPendingCapture(Image(), copied, targetMonitor: Offscreen(), keyboardRouter: router);

    private static QuickAccessCardActions Actions(Func<Task<bool>>? copy = null) => new(
        copy ?? (() => Task.FromResult(true)), () => { }, () => Task.CompletedTask, () => Task.FromResult(true),
        () => { }, () => Task.FromResult("unused.png"), null);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Off the visible desktop, at the primary display's scale.
    private static MonitorInfo Offscreen()
    {
        var bounds = new RectD(-32000, -32000, 1600, 1000);
        return new MonitorInfo("TEST-CARD-FIRST", bounds, bounds, MonitorEnumerator.GetPrimary().Dpi, false);
    }

    private static BitmapSource Image()
    {
        byte[] pixels = Enumerable.Repeat((byte)200, 64 * 48 * 4).ToArray();
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        image.Freeze();
        return image;
    }

    private static void RunCard(Action<ReceiptKeyboardRouter> body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            body(router);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id)
        {
        }
    }

    private sealed class NoRequester : IStoreRatingRequester
    {
        public Task<string> RequestRateAndReviewAsync() => Task.FromResult("not requested in tests");
    }

    /// <summary>
    /// The App's own capture handler without its shell (as <c>CaptureLibraryFirstTests</c> builds it) over a
    /// temporary library, with the services, card keyboard router and Store rating a card needs. The capture
    /// lies on a test display at -32000; nothing is activated, no hotkey is registered, and the automatic copy
    /// never touches the clipboard.
    /// </summary>
    private sealed class Shell : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = OwnedTestDirectory.Create("mycapture-card-first-");
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        private readonly ReceiptKeyboardRouter _router = new(_ => new NoHotkeys());
        private readonly bool? _previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly Func<bool> _previousRemote = CaptureFlight.ReadRemoteSession;
        private readonly Func<bool> _previousLayered = CaptureFlight.ReadLayeredSupport;
        private readonly Func<IReadOnlyList<MonitorInfo>> _previousMonitors = CaptureFlight.ReadMonitors;
        private readonly Func<Window, bool> _previousExclusion = CaptureFlightDecoration.ApplyCaptureExclusion;
        private CaptureOverlayCoordinator? _coordinator;

        internal Shell(QueueSettings? queue = null, AfterCaptureAction mode = AfterCaptureAction.QuickAccess, bool motion = false)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            typeof(System.Windows.Threading.DispatcherObject).GetField("_dispatcher", Flags)!.SetValue(_app, Dispatcher.CurrentDispatcher);
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            MonitorInfo monitor = Offscreen();
            CaptureFlight.ReadRemoteSession = () => false;
            CaptureFlight.ReadLayeredSupport = () => true;
            CaptureFlight.ReadMonitors = () => [monitor];
            CaptureFlightDecoration.ApplyCaptureExclusion = _ => true;

            Paths = AppPaths.CreateForRoot(_root);
            Settings.General.AfterCapture = mode;
            Settings.General.PlayCaptureSound = false;
            if (queue is not null) Settings.Queue = queue;
            Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(Persistence, () => Settings, () => Paths, NullLogger<CaptureCommitService>.Instance,
                (_, _) => { CopyCalls++; return CopyResult ?? Task.FromResult(true); });
            var storeRating = new StoreRatingCoordinator(() => Settings, _ => { }, write => write(), () => true, new NoRequester(),
                NullLogger.Instance, isPackaged: true);
            Set("_queue", Queue);
            Set("_settings", Settings);
            Set("_persistence", Persistence);
            Set("_commit", commit);
            Set("_capturePaths", Paths);
            Set("_services", _services);
            Set("_receiptKeyboard", _router);
            Set("_storeRating", storeRating);
            _app.CaptureNoticeForTest = (title, message) => Notices.Add((title, message));

            // A desktop-sized frame on the test display; the selection lies well inside it.
            byte[] pixels = new byte[320 * 200 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 % 251);
            BitmapSource bitmap = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
            bitmap.Freeze();
            Frame = new FrozenFrame(bitmap, new RectD(-31800, -31800, 320, 200), monitor, 0);
            Selection = new CaptureSelectionCompletedEventArgs(Frame, Region, ScreenCaptureEngine.Crop(Frame, Region),
                "CaptureService.cs - my-capture - Visual Studio Code", recordForRepeat: false);
        }

        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal AppSettings Settings { get; } = new();
        internal FrozenFrame Frame { get; }
        internal RectD Region { get; } = new(100, 60, 64, 48);
        internal CaptureSelectionCompletedEventArgs Selection { get; }
        internal Task<bool>? CopyResult { get; set; }
        internal int CopyCalls { get; private set; }
        internal List<(string Title, string Message)> Notices { get; } = [];
        internal QuickAccessWindow? Card => (QuickAccessWindow?)Get("_quickAccess");
        internal object? CardFirstState => Get("_quickAccessFirst");
        internal CaptureFlight? Flight => (CaptureFlight?)Get("_captureFlight");
        internal CaptureRecord? CurrentRecord => (CaptureRecord?)Get("_currentRecord");
        internal CaptureRecord? PendingRecord => (CaptureRecord?)Get("_pendingRecord");
        internal Guid? LatestReceipt => (Guid?)Get("_latestReceipt");

        internal bool ShowsCardAfterSave
        {
            get => _app.ShowsCardAfterSaveForTest;
            set => _app.ShowsCardAfterSaveForTest = value;
        }

        internal Task Select(bool textCapture = false) => (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Flags)!
            .Invoke(_app, [textCapture
                ? new CaptureSelectionCompletedEventArgs(Frame, Region, Selection.SelectedBitmap, Selection.SourceTitle, recordForRepeat: false) { TextCapture = true }
                : Selection])!;

        /// <summary>The App's DismissQuickAccessForCapture, as each capture command runs it first.</summary>
        internal void Dismiss() => typeof(App).GetMethod("DismissQuickAccessForCapture", Flags)!.Invoke(_app, null);

        /// <summary>The region capture command (the hotkey's handler).</summary>
        internal void RequestRegionCapture() => typeof(App).GetMethod("HandleCaptureRequested", Flags)!.Invoke(_app, null);

        internal void SetPersistence(CapturePersistenceService? persistence) => Set("_persistence", persistence);

        internal void UseReceiptPresenter(ThemedShellPresenter presenter) => Set("_shellPresenter", presenter);

        /// <summary>
        /// The real coordinator around the handler, as App.InitializeShell wires it; the recovery editor never opens here.
        /// </summary>
        internal CaptureOverlayCoordinator UseCoordinator()
        {
            _coordinator = new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance)
            {
                SelectionPersistRequested = selection => (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Flags)!.Invoke(_app, [selection])!,
                OpenEditorAfterCapture = () => false,
            };
            _coordinator.OverlayClosed += (_, _) => ReleaseEditSession();
            Set("_overlay", _coordinator);
            return _coordinator;
        }

        /// <summary>Runs what is already posted at Send priority (the lift, then the card), nothing below it.</summary>
        internal void RunSendTurn()
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Runs everything posted down to ContextIdle (the card scheduled after the save and copy).</summary>
        internal void RunIdleTurns()
        {
            for (int turn = 0; turn < 3; turn++)
            {
                var frame = new DispatcherFrame();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }

        internal bool PumpUntil(Func<bool> done, bool throwOnTimeout = true)
        {
            var timeout = Stopwatch.StartNew();
            while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                var frame = new DispatcherFrame();
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (!done()) Thread.Sleep(1);
            }

            if (throwOnTimeout) Assert.True(done(), "The capture did not settle.");
            return done();
        }

        internal void Wait(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        internal T Wait<T>(Task<T> task)
        {
            Wait((Task)task);
            return task.GetAwaiter().GetResult();
        }

        internal void ReleaseEditSession()
        {
            (Get("_currentEditSession") as IDisposable)?.Dispose();
            Set("_currentEditSession", null);
        }

        public void Dispose()
        {
            Card?.CloseNow();
            Flight?.Finish();
            _coordinator?.Dispose();
            ReleaseEditSession();
            _router.Dispose();
            _services.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = _previousMotion;
            CaptureFlight.ReadRemoteSession = _previousRemote;
            CaptureFlight.ReadLayeredSupport = _previousLayered;
            CaptureFlight.ReadMonitors = _previousMonitors;
            CaptureFlightDecoration.ApplyCaptureExclusion = _previousExclusion;
            SynchronizationContext.SetSynchronizationContext(_previous);
            if (Directory.Exists(_root)) TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }

        private object? Get(string name) => typeof(App).GetField(name, Flags)!.GetValue(_app);

        private void Set(string name, object? value) => typeof(App).GetField(name, Flags)!.SetValue(_app, value);
    }
}
