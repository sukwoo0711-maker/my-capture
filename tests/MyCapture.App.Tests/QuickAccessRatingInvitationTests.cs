using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;
using Shell = MyCapture.App.Tests.QuickAccessCardFirstTests.Shell;

namespace MyCapture.App.Tests;

public sealed class QuickAccessRatingInvitationTests
{
    [Theory]
    [InlineData("ko", 1.0, false)]
    [InlineData("ko", 1.5, false)]
    [InlineData("en", 1.0, false)]
    [InlineData("en", 1.5, false)]
    [InlineData("ko", 1.0, true)]
    [InlineData("ko", 1.5, true)]
    [InlineData("en", 1.0, true)]
    [InlineData("en", 1.5, true)]
    public void AppendedInvitationAndHiddenRowNeverMoveExistingControls(string language, double scale, bool review) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var plain = Card(scale);
        var offered = Card(scale);
        var completion = new TaskCompletionSource();
        try
        {
            Assert.Equal(Visibility.Collapsed, plain.RatingInvitationForTest.Row.Visibility);
            offered.OfferRatingInvitation(() => completion.Task, () => { }, () => { });
            Border plainHost = QuickAccessDetachedFixture.Layout(plain, scale);
            Border host = QuickAccessDetachedFixture.Layout(offered, scale);
            Rect[] before = ControlBounds(offered);
            Assert.Equal(ControlBounds(plain), before);
            Assert.True(host.ActualHeight > plainHost.ActualHeight);
            var row = offered.RatingInvitationForTest;
            AssertRowFits(offered, host);
            Assert.Equal(UiText.Get("StoreRating_InviteAction"), AutomationProperties.GetName(row.Review));
            Assert.Equal(UiText.Get("StoreRating_InviteCloseName"), AutomationProperties.GetName(row.Close));
            Assert.Equal("QuickAccess.RatingReview", AutomationProperties.GetAutomationId(row.Review));
            Assert.Equal("QuickAccess.RatingClose", AutomationProperties.GetAutomationId(row.Close));
            Assert.Equal("QuickAccess.RatingMessage", AutomationProperties.GetAutomationId(row.Message));
            Assert.Equal(KoreanWordWrap.Strip(row.Message.Text), AutomationProperties.GetName(row.Message));
            Assert.Equal(UiText.Get("Dialog.Close"), row.Close.Content);
            Assert.Equal(6, row.Row.Margin.Top);
            var panel = Assert.IsType<StackPanel>(row.Row.Parent);
            Assert.Same(row.Row, panel.Children[panel.Children.Count - 2]);
            Assert.Same(offered.RemainingBarForTest, panel.Children[panel.Children.Count - 1]);
            Assert.All(new[] { row.Review, row.Close }, button =>
            {
                Assert.Same(button.FindResource("Button.Secondary"), button.Style);
                Assert.Equal(8, button.Margin.Left);
                Assert.False(button.Focusable);
                Assert.NotNull(button.ToolTip);
                Assert.Equal(100, ToolTipService.GetInitialShowDelay(button));
            });
            double height = host.ActualHeight;
            Click(review ? row.Review : row.Close);
            Relayout(host, offered.Width);
            Assert.Equal(Visibility.Hidden, row.Row.Visibility);
            Assert.Equal(height, host.ActualHeight);
            Assert.Equal(before, ControlBounds(offered));
            Assert.Equal(review, offered.IsRatingRequestRunningForTest);
        }
        finally { completion.TrySetResult(); plain.Close(); offered.Close(); Pump(); }
    });

    [Fact]
    public void OfferingTwiceOrAfterShowingCreatingAHandleOrClosingThrows() => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        var card = Card();
        try
        {
            card.OfferRatingInvitation(() => null, () => { }, () => { });
            Assert.Throws<InvalidOperationException>(() => card.OfferRatingInvitation(() => null, () => { }, () => { }));
        }
        finally { card.Close(); }
        Assert.Throws<InvalidOperationException>(() => card.OfferRatingInvitation(() => null, () => { }, () => { }));
        QuickAccessWindow shown = Card();
        try
        {
            ShowWithoutHotkey(shown);
            Assert.Throws<InvalidOperationException>(() => shown.OfferRatingInvitation(() => null, () => { }, () => { }));
            shown.Hide();
            Assert.Throws<InvalidOperationException>(() => shown.OfferRatingInvitation(() => null, () => { }, () => { }));
        }
        finally { shown.Close(); }
        var withHandle = Card();
        try
        {
            new WindowInteropHelper(withHandle).EnsureHandle();
            Assert.Throws<InvalidOperationException>(() => withHandle.OfferRatingInvitation(() => null, () => { }, () => { }));
        }
        finally { withHandle.Close(); }
    });

    /// <summary>
    /// A card whose window is not at its monitor's scale cannot reach the bounds placement asks for.
    /// That used to throw out of the card's Loaded handler (at 125% it ended this test host, issue 281).
    /// </summary>
    [Fact]
    public void ACardThatMissesItsBoundsStaysOpenAndReportsItOnce() => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        // At half the window's scale the monitor asks for half the card's minimum width in pixels, as a
        // display does whose scale rose while the card was on it. Windows keeps the minimum width, so the
        // read-back misses on every attempt, on any display.
        QuickAccessWindow card = Card(scale: 0.5);
        var reported = new List<Exception>();
        card.PlacementMissed = reported.Add;
        try
        {
            ShowAt96Dpi(card);
            var miss = Assert.IsType<InvalidOperationException>(card.PlacementMiss);
            Assert.Contains("did not reach its requested physical-pixel bounds", miss.Message);
            Assert.Same(miss, Assert.Single(reported));
            Assert.True(card.IsVisible);
            Assert.True(card.IsOpen);

            // A size change places the card again and misses again: the first miss stays, told once.
            int sizeChanges = 0;
            card.SizeChanged += (_, _) => sizeChanges++;
            card.MinHeight = card.ActualHeight + 24;
            card.UpdateLayout();
            Pump();
            Assert.True(sizeChanges > 0, "The taller minimum did not change the card's size.");
            Assert.Same(miss, card.PlacementMiss);
            Assert.Single(reported);

            // It is still the card: its close button closes it.
            Click(CloseButton(card));
            Assert.False(card.IsOpen);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewRunsOnceHoldsCountdownAndReleasesAfterSuccessOrFault(bool fault) => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var completion = new TaskCompletionSource();
        var offered = Card();
        int calls = 0;
        bool keyboardBeforeCallback = false;
        offered.OfferRatingInvitation(() =>
        {
            calls++;
            keyboardBeforeCallback = offered.RatingInvitationForTest.Review.Focusable
                && offered.RatingInvitationForTest.Close.Focusable && offered.ActionButtonsForTest.All(button => button.Focusable);
            return completion.Task;
        }, () => throw new InvalidOperationException("Close must not run"), () => { });
        try
        {
            ShowWithoutHotkey(offered);
            offered.Advance(QuickAccessWindow.Lifetime - TimeSpan.FromMilliseconds(100), false);
            Click(offered.RatingInvitationForTest.Review);
            Click(offered.RatingInvitationForTest.Review);
            Assert.True(keyboardBeforeCallback);
            Assert.Equal(1, calls);
            Assert.True(offered.IsRatingRequestRunningForTest);
            offered.Advance(QuickAccessWindow.Lifetime * 3, false);
            Assert.True(offered.IsVisible);
            if (fault) completion.SetException(new InvalidOperationException("Synthetic Store failure"));
            else completion.SetResult();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (offered.IsRatingRequestRunningForTest && timeout.Elapsed < TimeSpan.FromSeconds(5)) Pump();
            Assert.False(offered.IsRatingRequestRunningForTest);
            Assert.True(offered.RemainingForTest >= FeedbackTiming.HoldAfterResult);
            Assert.Equal(Visibility.Hidden, offered.RatingInvitationForTest.Row.Visibility);
            offered.Advance(QuickAccessWindow.Lifetime * 3, false);
            Assert.False(offered.IsOpen);
        }
        finally { completion.TrySetResult(); offered.CloseNow(); SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Fact]
    public void RefusedReviewHidesTheRowAndCloseRunsOnlyOnce() => StaTestHost.Run(() =>
    {
        var refused = Card();
        var closing = Card();
        int reviews = 0, closes = 0;
        refused.OfferRatingInvitation(() => { reviews++; return null; }, () => closes++, () => { });
        closing.OfferRatingInvitation(() => { reviews++; return null; }, () => closes++, () => { });
        try
        {
            // A refused Review opens nothing: the row leaves, keeping its space, rather than keep a button
            // that does nothing, and neither button calls anything afterwards.
            Click(refused.RatingInvitationForTest.Review);
            Assert.Equal(1, reviews);
            Assert.Equal(Visibility.Hidden, refused.RatingInvitationForTest.Row.Visibility);
            Assert.False(refused.IsRatingRequestRunningForTest);
            Click(refused.RatingInvitationForTest.Review);
            Click(refused.RatingInvitationForTest.Close);
            Assert.Equal(1, reviews);
            Assert.Equal(0, closes);

            Click(closing.RatingInvitationForTest.Close);
            Click(closing.RatingInvitationForTest.Close);
            Click(closing.RatingInvitationForTest.Review);
            Assert.Equal(1, closes);
            Assert.Equal(1, reviews);
            Assert.Equal(Visibility.Hidden, closing.RatingInvitationForTest.Row.Visibility);
        }
        finally { refused.Close(); closing.Close(); }
    });

    [Theory]
    [InlineData("capture")]
    [InlineData("copy")]
    [InlineData("action")]
    public void AnyFailureOnTheCardHidesAndDisablesInvitation(string failure) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        QuickAccessWindow card = failure == "action" ? Card() : QuickAccessWindow.ForPendingCapture(Image(), null, targetMonitor: Monitor());
        int callbacks = 0;
        int withdrawals = 0;
        card.OfferRatingInvitation(() => { callbacks++; return Task.CompletedTask; }, () => callbacks++, () => withdrawals++);
        try
        {
            if (failure == "copy") card.ReportInitialCopy(false);
            else if (failure == "capture") card.ShowCaptureFailure("저장 실패", "저장할 수 없습니다.");
            else
            {
                // An action of the card that fails, as a pin that could not open: the row leaves like after
                // the capture's own failure, but the showing it made stays counted.
                Task pin = card.RunAsync("Pin", () => Task.FromResult(false), "QuickAccess.Copying", "QuickAccess.Copied");
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!pin.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) Pump();
                Assert.True(pin.IsCompleted);
            }
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Assert.False(card.ShowsRatingInvitation);
            // Only the capture's own save or copy failure gives the showing back.
            int given = failure == "action" ? 0 : 1;
            Assert.Equal(given, withdrawals);
            Task anotherFailure = card.RunAsync("Pin", () => Task.FromResult(false), "QuickAccess.Pinning", "QuickAccess.Pinned");
            Assert.True(anotherFailure.IsCompletedSuccessfully);
            Assert.Equal(given, withdrawals);
            Click(card.RatingInvitationForTest.Review);
            Click(card.RatingInvitationForTest.Close);
            Assert.Equal(0, callbacks);
        }
        finally { card.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Theory]
    [InlineData("accepted")]
    [InlineData("refused")]
    [InlineData("close")]
    public void AFailureAfterReviewOrCloseNeverWithdrawsTheInvitation(string answer) => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = QuickAccessWindow.ForPendingCapture(Image(), null, targetMonitor: Monitor());
        int withdrawals = 0;
        card.OfferRatingInvitation(() => answer == "accepted" ? Task.CompletedTask : null, () => { }, () => withdrawals++);
        try
        {
            Click(answer == "close" ? card.RatingInvitationForTest.Close : card.RatingInvitationForTest.Review);
            card.ReportInitialCopy(false);
            card.ShowCaptureFailure("저장 실패", "저장할 수 없습니다.");
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(0, withdrawals);
            Assert.False(card.WithdrawRatingInvitation());
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("capture")]
    [InlineData("copy")]
    public void AFailureKnownBeforeOfferingWithdrawsAtTheOfferOnlyOnce(string failure) => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = QuickAccessWindow.ForPendingCapture(Image(), failure == "copy" ? false : null, targetMonitor: Monitor());
        int withdrawals = 0;
        try
        {
            if (failure == "capture") card.ShowCaptureFailure("저장 실패", "저장할 수 없습니다.");
            Assert.Equal(0, withdrawals);
            card.OfferRatingInvitation(() => throw new InvalidOperationException("Review must not run"),
                () => throw new InvalidOperationException("Close must not run"), () => withdrawals++);
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, withdrawals);
            card.ShowCaptureFailure("저장 실패", "저장할 수 없습니다.");
            card.ReportInitialCopy(false);
            Assert.False(card.WithdrawRatingInvitation());
            Assert.Equal(1, withdrawals);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void AppWithdrawalKeepsTheRowSpaceAndNeverCallsBack() => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = Card();
        int withdrawals = 0;
        card.OfferRatingInvitation(() => Task.CompletedTask, () => { }, () => withdrawals++);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, 1);
            double height = host.ActualHeight;
            Assert.True(card.WithdrawRatingInvitation());
            Assert.False(card.WithdrawRatingInvitation());
            Relayout(host, card.Width);
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(height, host.ActualHeight);
            Assert.Equal(0, withdrawals);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvitationIsVisibleAndCountedFromFirstAppearance(bool afterSave, bool coordinatorPath) => RunWithShell(afterSave, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        CaptureOverlayCoordinator? coordinator = coordinatorPath ? shell.UseCoordinator() : null;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!afterSave) shell.CopyResult = copy.Task;
        var observations = new List<(Visibility Visibility, int Count, int SuccessfulCaptures, bool Active)>();
        shell.CardFirstVisible = card => observations.Add((card.RatingInvitationForTest.Row.Visibility,
            shell.Settings.StoreRating.InvitationShownCount, shell.Settings.StoreRating.SuccessfulCaptureCount, coordinator?.IsActive == true));
        Task handler;
        if (coordinator is not null)
        {
            Assert.True(coordinator.StartWithSelection(shell.Frame, shell.Region, "Source - App", recordForRepeat: false));
            handler = coordinator.LastTransitionForTest;
        }
        else handler = shell.Select();
        if (!afterSave)
        {
            shell.RunSendTurn();
            copy.SetResult(true);
        }
        shell.Wait(handler);
        shell.RunIdleTurns();
        Assert.True(shell.Card is not null, "Capture produced no card. " + string.Join(" | ", shell.Notices.Select(notice => notice.Message)));
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.HasCaptureFailed, "Capture failed before rating assertions. " + card.DetailForTest.Text);
        var first = Assert.Single(observations);
        Assert.Equal(Visibility.Visible, first.Visibility);
        Assert.Equal(1, first.Count);
        Assert.Equal(afterSave ? 2 : 1, first.SuccessfulCaptures);
        Assert.Equal(2, shell.Settings.StoreRating.SuccessfulCaptureCount);
        if (coordinatorPath && !afterSave) Assert.True(first.Active);
    });

    [Theory]
    [InlineData("save")]
    [InlineData("copy")]
    public void AFailedPredictedCaptureWithdrawsItsShowingAndALaterSuccessCanOfferAgain(string failure) => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        if (failure == "save") shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("Synthetic capture save failure");
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(1, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.True(shell.IsRatingInvitationVisible);

        copy.SetResult(failure != "copy");
        shell.Wait(handler);
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
        Assert.False(shell.IsRatingInvitationVisible);
        Assert.False(StoreRatingPolicy.ShouldRequest(shell.Settings.StoreRating, true, DateTimeOffset.UtcNow));

        shell.Persistence.BeforeRecordMetadataCommit = null;
        shell.CopyResult = null;
        shell.Dismiss();
        QuickAccessWindow next = Capture(shell);
        Assert.Equal(Visibility.Visible, next.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.NotNull(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(2, shell.Settings.StoreRating.SuccessfulCaptureCount);
        Assert.False(shell.Settings.StoreRating.LastCaptureFailed);
    });

    [Theory]
    [InlineData("save", "accepted")]
    [InlineData("save", "refused")]
    [InlineData("save", "close")]
    [InlineData("copy", "accepted")]
    [InlineData("copy", "refused")]
    [InlineData("copy", "close")]
    public void AnsweringBeforeThePredictedCaptureFailsKeepsItsShowing(string failure, string answer) => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        if (failure == "save") shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("Synthetic capture save failure");
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        DateTimeOffset shown = Assert.IsType<DateTimeOffset>(shell.Settings.StoreRating.InvitationShownUtc);
        shell.SetBusy("_preparingExit", answer == "refused");
        Click(answer == "close" ? card.RatingInvitationForTest.Close : card.RatingInvitationForTest.Review);
        shell.SetBusy("_preparingExit", false);
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.False(shell.IsRatingInvitationVisible);
        Assert.Equal(answer == "accepted" ? 1 : 0, shell.RatingOwners.Count);
        if (answer == "refused") Assert.Null(shell.Settings.StoreRating.RequestAttemptedUtc);

        copy.SetResult(failure != "copy");
        shell.Wait(handler);
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(shown, shell.Settings.StoreRating.InvitationShownUtc);
        Assert.False(card.WithdrawRatingInvitation());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReviewOnAPredictedCardAfterAnErrorOpensTheStoreBeforeTheCaptureReports(bool captureSucceeds) => RunWithShell(false, shell =>
    {
        // The previous capture failed: only this capture's predicted success puts the invitation on its card.
        shell.StoreRating.RecordCapture(false);
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);

        // Review before the save and copy report: the Store dialog opens over the card and the answer is saved.
        Click(card.RatingInvitationForTest.Review);
        Assert.Same(card, Assert.Single(shell.RatingOwners));
        Assert.NotNull(shell.Settings.StoreRating.RequestAttemptedUtc);
        Assert.Contains(shell.Settings.StoreRating.RequestAttemptedUtc, shell.SavedRatings);
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.False(shell.IsRatingInvitationVisible);

        copy.SetResult(captureSucceeds);
        shell.Wait(handler);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.False(card.WithdrawRatingInvitation());

        // Answered: no later card carries the invitation.
        shell.Dismiss();
        shell.CopyResult = null;
        Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
        Assert.Single(shell.RatingOwners);
    });

    [Fact]
    public void AFailedPredictedCaptureWithdrawsAfterItsCardWasClosed() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        card.CloseNow();
        Assert.Null(shell.Card);
        copy.SetResult(false);
        shell.Wait(handler);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.False(shell.IsRatingInvitationVisible);
    });

    [Theory]
    [InlineData("save")]
    [InlineData("copy")]
    public void PreservedWindowCaptureWithdrawsAfterItsCardClosesBeforeFailure(string failure) => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var preserved = new Window { Left = -32000, Top = -32000, Width = 120, Height = 80, ShowActivated = false, ShowInTaskbar = false };
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        if (failure == "save") shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("Synthetic separate capture save failure");
        try
        {
            preserved.Show();
            Task handler = shell.SelectSeparate();
            shell.RunSendTurn();
            QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            card.CloseNow();
            Assert.Null(shell.Card);
            copy.SetResult(failure != "copy");
            shell.Wait(handler);
            Assert.True(preserved.IsVisible);
            Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
            Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
            Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
            Assert.False(shell.IsRatingInvitationVisible);
        }
        finally { copy.TrySetResult(false); preserved.Close(); }
    });

    [Fact]
    public void PreservedWindowSaveFailureBeforeTheCardsTurnNeverOffers() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        shell.SetPersistence(null);
        Task handler = shell.SelectSeparate();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.True(card.HasCaptureFailed);
        Assert.Equal(Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        copy.SetResult(true);
        shell.Wait(handler);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
    });

    [Fact]
    public void PreservedWindowCaptureSuccessKeepsItsShowingAndEndsItsPrediction() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var preserved = new Window { Left = -32000, Top = -32000, Width = 120, Height = 80, ShowActivated = false, ShowInTaskbar = false };
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        try
        {
            preserved.Show();
            Task handler = shell.SelectSeparate();
            shell.RunSendTurn();
            QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
            object first = Assert.IsAssignableFrom<object>(shell.CardFirstState);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            copy.SetResult(true);
            shell.Wait(handler);
            Assert.True(preserved.IsVisible);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            Assert.Equal(2, shell.Settings.StoreRating.SuccessfulCaptureCount);
            Assert.False(shell.Settings.StoreRating.LastCaptureFailed);
            // The recorded success ends the prediction, so no later report for this capture can withdraw its showing.
            const BindingFlags state = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.True((bool)first.GetType().GetProperty("RatingOutcomeRecorded", state)!.GetValue(first)!);
            Assert.Null(first.GetType().GetProperty("PendingRatingOffer", state)!.GetValue(first));
        }
        finally { copy.TrySetResult(false); preserved.Close(); }
    });

    [Theory]
    [InlineData("SaveFailed")]
    [InlineData("DeferredSaveFailure")]
    public void AKnownCaptureSaveFailureNeverOffersBeforeTheCardIsBuilt(string failureState) => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        Task handler = shell.Select();
        object first = Assert.IsAssignableFrom<object>(shell.CardFirstState);
        PropertyInfo property = first.GetType().GetProperty(failureState, BindingFlags.Instance | BindingFlags.NonPublic)!;
        property.SetValue(first, failureState == "SaveFailed" ? true : ("저장 실패", "저장할 수 없습니다."));
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        shell.Wait(handler);
    });

    [Fact]
    public void AnAlreadyRecordedCaptureNeverGetsAPredictedExtraSuccess() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        Task handler = shell.Select();
        object first = Assert.IsAssignableFrom<object>(shell.CardFirstState);
        // Exercise the recorded-before-build decision with one total success, including this capture.
        first.GetType().GetProperty("RatingOutcomeRecorded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(first, true);
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        shell.Wait(handler);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACardShowingIsSavedOnlyByTheWriteQueuedAfterItsFirstAppearance(bool afterSave)
    {
        var writes = new Queue<Action>();
        RunWithShell(() => new Shell(ratingDeferWrite: writes.Enqueue) { ShowsCardAfterSave = afterSave }, shell =>
        {
            shell.Settings.StoreRating.SuccessfulCaptureCount = 2;
            var visible = new List<(int Saves, int Writes)>();
            var shown = new List<(int Saves, int Writes)>();
            shell.CardFirstVisible = _ => visible.Add((shell.SavedRatings.Count, writes.Count));
            shell.CardFirstShown = _ => shown.Add((shell.SavedRatings.Count, writes.Count));
            QuickAccessWindow card = Capture(shell);
            Assert.Equal((0, 0), Assert.Single(visible));
            Assert.Equal((0, 1), Assert.Single(shown));
            Assert.Empty(shell.SavedRatings);
            Assert.Single(writes);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            writes.Dequeue()();
            Assert.Equal((1, shell.Settings.StoreRating.InvitationShownUtc), Assert.Single(shell.SavedRatingShowings));
            Assert.Single(shell.SavedRatings);
            Assert.Empty(writes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitSavesACardShowingStillWaitingForTheIdleWriteOnce(bool afterSave)
    {
        var writes = new Queue<Action>();
        RunWithShell(() => new Shell(ratingDeferWrite: writes.Enqueue) { ShowsCardAfterSave = afterSave }, shell =>
        {
            shell.Settings.StoreRating.SuccessfulCaptureCount = 2;
            QuickAccessWindow card = Capture(shell);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Single(writes);
            Assert.Empty(shell.SavedRatingShowings);

            // The app exits before the UI is idle again: the showing is saved anyway, and only once.
            shell.QueueSettingsSavesForExit();
            Assert.Equal((1, shell.Settings.StoreRating.InvitationShownUtc), Assert.Single(shell.SavedRatingShowings));
            writes.Dequeue()();
            Assert.Single(shell.SavedRatingShowings);
        });
    }

    [Fact]
    public void ACopyFailureKnownBeforeTheCardIsBuiltNeverOffers() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        shell.CopyResult = Task.FromResult(false);
        int saves = shell.SavedRatingShowings.Count;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        // Neither shown nor counted, rather than shown and withdrawn as the card reports the copy.
        Assert.Equal(Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        shell.Wait(handler);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
        Assert.All(shell.SavedRatingShowings.Skip(saves), saved => Assert.Equal(0, saved.Count));
        Assert.False(shell.IsRatingInvitationVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACardStillCarriesItsInvitationWhenTheDeferredSettingsSaveFails(bool afterSave)
    {
        var writes = new Queue<Action>();
        var logger = new RatingLogger();
        var failure = new IOException("Synthetic rating settings failure");
        RunWithShell(() => new Shell(ratingDeferWrite: writes.Enqueue, ratingSave: _ => throw failure, ratingLog: logger)
            { ShowsCardAfterSave = afterSave }, shell =>
        {
            shell.Settings.StoreRating.SuccessfulCaptureCount = 2;
            QuickAccessWindow card = Capture(shell);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Empty(logger.Warnings);
            Assert.Single(writes);
            Assert.Null(Record.Exception(writes.Dequeue()));
            Assert.Same(failure, Assert.Single(logger.Warnings).Exception);
            Assert.Contains("Could not save Store rating state", logger.Warnings[0].Message, StringComparison.Ordinal);
            Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            Assert.NotNull(shell.Settings.StoreRating.InvitationShownUtc);
            Assert.True(shell.IsRatingInvitationVisible);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnActionFailureOnAnInvitationCardHidesItsRowAndKeepsItsShowing(bool afterSave) => RunWithShell(afterSave, shell =>
    {
        QuickAccessWindow card = Capture(shell);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        DateTimeOffset shown = Assert.IsType<DateTimeOffset>(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.True(shell.IsRatingInvitationVisible);
        int saves = shell.SavedRatingShowings.Count;
        for (int failure = 0; failure < 2; failure++)
        {
            shell.Wait(card.RunAsync("Pin", () => Task.FromResult(false), "QuickAccess.Pinning", "QuickAccess.Pinned"));
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            Assert.Equal(shown, shell.Settings.StoreRating.InvitationShownUtc);
            Assert.False(shell.IsRatingInvitationVisible);
        }
        // Seen before the failure, the showing stays counted: nothing is given back or saved again.
        Assert.Equal(saves, shell.SavedRatingShowings.Count);
        Assert.False(StoreRatingPolicy.ShouldRequest(shell.Settings.StoreRating, true, DateTimeOffset.UtcNow));
        Click(card.RatingInvitationForTest.Review);
        Click(card.RatingInvitationForTest.Close);
        Assert.Empty(shell.RatingOwners);
        Assert.Null(shell.Settings.StoreRating.InvitationDismissedUtc);
    });

    [Theory]
    [InlineData(false, "escape")]
    [InlineData(false, "replacement")]
    [InlineData(true, "escape")]
    [InlineData(true, "replacement")]
    public void AnActionThatFailsAfterItsCardClosedKeepsTheShowing(bool afterSave, string closedBy) => RunWithShell(afterSave, shell =>
    {
        QuickAccessWindow card = Capture(shell);
        DateTimeOffset shown = Assert.IsType<DateTimeOffset>(shell.Settings.StoreRating.InvitationShownUtc);
        var pin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task action = card.RunAsync("Pin", () => pin.Task, "QuickAccess.Pinning", "QuickAccess.Pinned");
        // Esc, or the next capture's command, closes the card while its action still runs.
        if (closedBy == "escape") card.ShortcutActions["Close"]();
        else shell.Dismiss();
        Assert.False(card.IsOpen);
        int saves = shell.SavedRatingShowings.Count;

        pin.SetResult(false);
        shell.Wait(action);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Equal(shown, shell.Settings.StoreRating.InvitationShownUtc);
        Assert.Equal(saves, shell.SavedRatingShowings.Count);
        Assert.False(shell.IsRatingInvitationVisible);
        // The next card does not carry it a third time from the user's point of view.
        Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
    });

    [Theory]
    [InlineData("close")]
    [InlineData("escape")]
    public void ACaptureSaveFailureAfterItsCardClosedStillGivesTheShowingBack(string closedBy) => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("Synthetic capture save failure");
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        if (closedBy == "escape") card.ShortcutActions["Close"]();
        else card.CloseNow();
        Assert.False(card.IsOpen);

        copy.SetResult(true);
        shell.Wait(handler);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
        Assert.Equal((0, (DateTimeOffset?)null), shell.SavedRatingShowings[^1]);
        Assert.False(shell.IsRatingInvitationVisible);
    });

    [Fact]
    public void ACaptureCopyFailureAfterAnActionFailedStillGivesTheShowingBack() => RunWithShell(false, shell =>
    {
        shell.Settings.StoreRating.SuccessfulCaptureCount = 1;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task handler = shell.Select();
        shell.RunSendTurn();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.Equal(Visibility.Visible, card.RatingInvitationForTest.Row.Visibility);
        // Saved and bound while the copy still runs: an action fails before the capture has reported.
        shell.PumpUntil(() => card.LibraryButtonForTest.IsEnabled);
        shell.Wait(card.RunAsync("Pin", () => Task.FromResult(false), "QuickAccess.Pinning", "QuickAccess.Pinned"));
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
        Assert.False(shell.IsRatingInvitationVisible);

        // The capture's own copy then fails: its showing is given back after all.
        copy.SetResult(false);
        shell.Wait(handler);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.True(shell.Settings.StoreRating.LastCaptureFailed);
        Assert.False(card.WithdrawRatingInvitation());
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ShownCardKeepsScreenCoordinatesAfterReviewOrClose(bool afterSave, bool review) => RunWithShell(afterSave, shell =>
    {
        Point[]? initial = null;
        double top = 0, height = 0;
        shell.CardFirstShown = card =>
        {
            card.UpdateLayout();
            initial = Controls(card).Select(control => control.PointToScreen(new Point())).ToArray();
            top = card.Top;
            height = card.ActualHeight;
        };
        QuickAccessWindow card = Capture(shell);
        Assert.NotNull(initial);
        Click(review ? card.RatingInvitationForTest.Review : card.RatingInvitationForTest.Close);
        card.UpdateLayout();
        Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(top, card.Top);
        Assert.Equal(height, card.ActualHeight);
        Assert.Equal(initial, Controls(card).Select(control => control.PointToScreen(new Point())).ToArray());
    });

    public static IEnumerable<object[]> IneligibleCases()
    {
        foreach (bool afterSave in new[] { false, true })
        foreach (string condition in new[] { "one-success", "failed", "dismissed", "attempted", "two-showings", "legacy", "first-run", "exit", "installation" })
            yield return [afterSave, condition];
    }

    [Theory]
    [MemberData(nameof(IneligibleCases))]
    public void EligibilityUsesCurrentOrPredictedCaptureState(bool afterSave, string condition) => RunWithShell(afterSave, shell =>
    {
        // The after-save route has already recorded this capture. Only card first predicts one success.
        if (afterSave) shell.Wait(shell.Select());
        var state = shell.Settings.StoreRating;
        switch (condition)
        {
            case "one-success": state.SuccessfulCaptureCount = 1; break;
            case "failed": state.LastCaptureFailed = true; break;
            case "dismissed": state.InvitationDismissedUtc = DateTimeOffset.UtcNow; break;
            case "attempted": state.RequestAttemptedUtc = DateTimeOffset.UtcNow; break;
            case "two-showings": state.InvitationShownCount = 2; break;
            case "legacy": state.InvitationShownUtc = DateTimeOffset.UtcNow; break;
            case "first-run": shell.Settings.General.IsFirstRun = true; break;
            case "exit": shell.SetBusy("_preparingExit", true); break;
            case "installation": shell.SetBusy("_storeInstallationPending", true); break;
        }
        int count = state.InvitationShownCount;
        if (afterSave) shell.RunIdleTurns();
        else Capture(shell);
        bool predictedSuccess = !afterSave && (condition is "one-success" or "failed");
        Assert.Equal(predictedSuccess ? Visibility.Visible : Visibility.Collapsed,
            Assert.IsType<QuickAccessWindow>(shell.Card).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(predictedSuccess ? count + 1 : count, state.InvitationShownCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PracticeCaptureLeavesInvitationForNextNormalCapture(bool afterSave) => RunWithShell(afterSave, shell =>
    {
        shell.Wait(shell.Select(shell.PracticeSelection()));
        shell.RunIdleTurns();
        Assert.Equal(Visibility.Collapsed, Assert.IsType<QuickAccessWindow>(shell.Card).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        shell.Dismiss();
        Assert.Equal(Visibility.Visible, Capture(shell).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservedWindowPracticeFallbackCardLeavesInvitationForNextCapture(bool copySlowerThanSave) => RunWithShell(() =>
    {
        // The editor-preserving path shows its card after the save when no card showed first: none was
        // scheduled, or its turn had not come when the capture was saved (a copy slower than the save).
        var shell = new Shell(copyWait: copySlowerThanSave ? TimeSpan.FromMinutes(1) : null) { ShowsCardAfterSave = !copySlowerThanSave };
        shell.StoreRating.RecordCapture(true);
        shell.StoreRating.RecordCapture(true);
        return shell;
    }, shell =>
    {
        QuickAccessWindow Deliver(CaptureSelectionCompletedEventArgs selection)
        {
            var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.CopyResult = copy.Task;
            Task handler = shell.SelectSeparate(selection);
            if (copySlowerThanSave)
            {
                object first = Assert.IsAssignableFrom<object>(shell.CardFirstState);
                PropertyInfo phase = first.GetType().GetProperty("Phase", BindingFlags.Instance | BindingFlags.NonPublic)!;
                shell.PumpUntil(() => phase.GetValue(first)!.ToString() == "Cancelled");
            }
            copy.SetResult(true);
            shell.Wait(handler);
            shell.RunIdleTurns();
            QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
            Assert.False(card.HasCaptureFailed, "Capture failed before rating assertions. " + card.DetailForTest.Text);
            return card;
        }

        QuickAccessWindow practice = Deliver(shell.PracticeSelection());
        Assert.Equal(Visibility.Collapsed, practice.RatingInvitationForTest.Row.Visibility);
        Assert.Equal(0, shell.Settings.StoreRating.InvitationShownCount);
        Assert.Null(shell.Settings.StoreRating.InvitationShownUtc);
        Assert.False(shell.IsRatingInvitationVisible);
        shell.Dismiss();
        Assert.Equal(Visibility.Visible, Deliver(shell.Selection).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnsweringInvitationPersistsAndPreventsLaterOffers(bool afterSave, bool review) => RunWithShell(afterSave, shell =>
    {
        QuickAccessWindow card = Capture(shell);
        var invitation = card.RatingInvitationForTest;
        Click(review ? invitation.Review : invitation.Close);
        Click(review ? invitation.Review : invitation.Close);
        Assert.Equal(Visibility.Hidden, invitation.Row.Visibility);
        Assert.False(shell.IsRatingInvitationVisible);
        if (review)
        {
            Assert.Same(card, Assert.Single(shell.RatingOwners));
            Assert.NotNull(shell.Settings.StoreRating.RequestAttemptedUtc);
            Assert.Contains(shell.Settings.StoreRating.RequestAttemptedUtc, shell.SavedRatings);
        }
        else
        {
            Assert.NotNull(shell.Settings.StoreRating.InvitationDismissedUtc);
            Assert.Empty(shell.RatingOwners);
        }
        shell.Dismiss();
        Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IgnoredInvitationReturnsOnlyOnceAfterTimeoutOrReplacement(bool afterSave, bool replaced)
    {
        var clock = new StoreRatingTestClock(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        RunWithShell(afterSave, shell =>
        {
            for (int showing = 1; showing <= 3; showing++)
            {
                QuickAccessWindow card = Capture(shell);
                Assert.Equal(showing <= 2 ? Visibility.Visible : Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
                Assert.Equal(Math.Min(showing, 2), shell.Settings.StoreRating.InvitationShownCount);
                if (replaced) shell.Dismiss();
                else card.Advance(QuickAccessWindow.Lifetime * 2, false);
                Assert.False(card.IsOpen);
                clock.Now += StoreRatingPolicy.SecondInvitationDelay;
            }
        }, clock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardAndVisibleWindowNeverOfferTogetherAndHiddenNoticeGivesWay(bool afterSave)
    {
        var clock = new StoreRatingTestClock(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        RunWithShell(afterSave, shell =>
        {
            QuickAccessWindow card = Capture(shell);
            Assert.True(shell.IsRatingInvitationVisible);
            Assert.False(shell.StoreRating.NotifyUserReturned());
            Assert.False(shell.StoreRating.TryRequestNow());
            Assert.Equal(0, shell.WindowInvitations);
            shell.Dismiss();
            var window = new Window { Left = -32000, Top = -32000, Width = 120, Height = 80, ShowActivated = false, ShowInTaskbar = false };
            var notice = new RatingNotice();
            try
            {
                window.Show();
                shell.EngagementNotices.Add(window, notice);
                Assert.True(shell.IsRatingInvitationVisible);
                clock.Now += StoreRatingPolicy.SecondInvitationDelay;
                Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
                Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
                Assert.Equal(0, notice.HideCalls);
                shell.Dismiss();
                window.Hide();
                Assert.False(shell.IsRatingInvitationVisible);
                Assert.Equal(Visibility.Visible, Capture(shell).RatingInvitationForTest.Row.Visibility);
                Assert.Equal(2, shell.Settings.StoreRating.InvitationShownCount);
                Assert.Equal(1, notice.HideCalls);
            }
            finally { window.Close(); }
        }, clock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IgnoredCardInvitationWaitsTwentyFourHoursBeforeTheNextCard(bool afterSave)
    {
        var clock = new StoreRatingTestClock(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero));
        RunWithShell(afterSave, shell =>
        {
            Assert.Equal(Visibility.Visible, Capture(shell).RatingInvitationForTest.Row.Visibility);
            DateTimeOffset shown = clock.Now;
            shell.Dismiss();
            Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            Assert.Equal(shown, shell.Settings.StoreRating.InvitationShownUtc);
            shell.Dismiss();
            clock.Now = shown + StoreRatingPolicy.SecondInvitationDelay - TimeSpan.FromSeconds(1);
            Assert.Equal(Visibility.Collapsed, Capture(shell).RatingInvitationForTest.Row.Visibility);
            Assert.Equal(1, shell.Settings.StoreRating.InvitationShownCount);
            shell.Dismiss();
            clock.Now = shown + StoreRatingPolicy.SecondInvitationDelay;
            Assert.Equal(Visibility.Visible, Capture(shell).RatingInvitationForTest.Row.Visibility);
            Assert.Equal(2, shell.Settings.StoreRating.InvitationShownCount);
        }, clock);
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("close")]
    [InlineData("replacement")]
    public void ExplicitCloseStillClosesWhileReviewIsPending(string method) => StaTestHost.Run(() =>
    {
        using TestDpiContext dpi = TestDpiContext.At96Dpi();
        var card = Card();
        var completion = new TaskCompletionSource();
        card.OfferRatingInvitation(() => completion.Task, () => { }, () => { });
        try
        {
            ShowWithoutHotkey(card);
            Click(card.RatingInvitationForTest.Review);
            Assert.True(card.IsRatingRequestRunningForTest);
            if (method == "escape") card.ShortcutActions["Close"]();
            else if (method == "close") Click(CloseButton(card));
            else card.CloseNow();
            Assert.False(card.IsOpen);
        }
        finally { completion.TrySetResult(); card.CloseNow(); Pump(); }
    });

    private static Shell EligibleShell(bool afterSave, TimeProvider? ratingClock = null)
    {
        var shell = new Shell(ratingClock: ratingClock) { ShowsCardAfterSave = afterSave };
        shell.StoreRating.RecordCapture(true);
        shell.StoreRating.RecordCapture(true);
        return shell;
    }

    private static void RunWithShell(bool afterSave, Action<Shell> body, TimeProvider? ratingClock = null) =>
        RunWithShell(() => EligibleShell(afterSave, ratingClock), body);

    private static void RunWithShell(Func<Shell> create, Action<Shell> body) => StaTestHost.Run(() =>
    {
        Shell shell = create();
        Exception? failure = null;
        try { body(shell); }
        catch (Exception error) { failure = error; }
        try { shell.Dispose(); }
        catch (Exception cleanup)
        {
            // Preserve assertion failures as well when the OS refuses fixture recycling.
            if (failure is not null) throw new AggregateException(failure, cleanup);
            throw;
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    });

    private static QuickAccessWindow Capture(Shell shell)
    {
        shell.Wait(shell.Select());
        shell.RunIdleTurns();
        Assert.True(shell.Card is not null, "Capture produced no card. " + string.Join(" | ", shell.Notices.Select(notice => notice.Message)));
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.HasCaptureFailed, "Capture failed before rating assertions. " + card.DetailForTest.Text);
        return card;
    }

    internal static QuickAccessWindow Card(double scale = 1)
    {
        var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        var card = new QuickAccessWindow(Image(), () => Task.FromResult(true), () => { },
            () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"),
            targetMonitor: Monitor(scale), keyboardRouter: router);
        card.Closed += (_, _) => router.Dispose();
        return card;
    }

    private static MonitorInfo Monitor(double scale = 1) => new("RATING-TEST", new RectD(-32000, -32000, 1366, 768),
        new RectD(-32000, -32000, 1366, 768), (uint)(96 * scale), false);

    internal static BitmapSource Image()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.AliceBlue, null, new Rect(0, 0, 320, 120));
            drawing.DrawRectangle(Brushes.SteelBlue, null, new Rect(20, 20, 100, 80));
            drawing.DrawRectangle(Brushes.LightSlateGray, null, new Rect(140, 24, 160, 12));
            drawing.DrawRectangle(Brushes.LightSlateGray, null, new Rect(140, 50, 130, 12));
        }
        var bitmap = new RenderTargetBitmap(320, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void ShowWithoutHotkey(QuickAccessWindow card)
    {
        ShowAt96Dpi(card);
        // Placement runs as the card loads. A miss there means the fixture's monitor and the card's
        // window do not share their pixels, and nothing asserted after it could be trusted.
        Assert.True(card.PlacementMiss is null, "The card missed its bounds on the fixture's monitor: " + card.PlacementMiss?.Message);
    }

    // The fixture's monitor is a 96 DPI one that no display backs, so the card's window is made at 96 DPI
    // on any display: the caller holds TestDpiContext.At96Dpi for as long as the card is open. With the
    // window at the display's scale instead, the card's minimum width in pixels is wider than the width
    // placement asks for on that monitor (328 against 410 at 125%).
    private static void ShowAt96Dpi(QuickAccessWindow card)
    {
        Ocr410Fixture.LoadTheme(card);
        FluidMotion.SetWindowEntrance(card, false);
        TestDpiContext.Initialize96DpiWindow(card);
        card.Show();
        card.UpdateLayout();
        TestDpiContext.Assert96Dpi(card);
        Assert.True(card.IsLoaded);
    }

    private static Button CloseButton(QuickAccessWindow card) => QuickAccessDetachedFixture.Descendants(card.SurfaceForTest)
        .OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "Dialog.Close");

    private static FrameworkElement[] Controls(QuickAccessWindow card) =>
        [.. card.ActionButtonsForTest, card.LibraryButtonForTest, card.KeepToggleForTest, CloseButton(card), card.PreviewForTest];

    private static Rect[] ControlBounds(QuickAccessWindow card) => Controls(card).Select(control => Bounds(control, card.SurfaceForTest)).ToArray();
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    internal static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    internal static void Relayout(Border host, double width)
    {
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    private static void AssertRowFits(QuickAccessWindow card, Border host)
    {
        var row = card.RatingInvitationForTest;
        foreach (FrameworkElement element in new FrameworkElement[] { row.Row, row.Message, row.Review, row.Close })
        {
            Rect bounds = Bounds(element, host);
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= host.ActualWidth + 0.01 && bounds.Bottom <= host.ActualHeight + 0.01);
        }
        Assert.True(Bounds(row.Message, host).Right <= Bounds(row.Review, host).Left);
        Assert.True(Bounds(row.Review, host).Right <= Bounds(row.Close, host).Left);
        Assert.All(new[] { row.Review, row.Close }, button =>
        {
            Assert.True(button.ActualWidth >= 28 && button.ActualHeight >= 28);
            var text = Assert.Single(QuickAccessDetachedFixture.Descendants(button).OfType<TextBlock>());
            Assert.True(text.DesiredSize.Width <= text.ActualWidth + 0.01);
        });
        Assert.True(row.Message.DesiredSize.Height <= row.Message.ActualHeight + 0.01);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class NoHotkeys : MyCapture.Platform.Shell.IHotkeyRegistrar
    {
        public bool TryRegister(int id, MyCapture.Core.Settings.Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }

    private sealed class RatingNotice : IEngagementNotice
    {
        public Action? ReviewChosen { get; set; }
        public Action? UpdateChosen { get; set; }
        public Action<EngagementNoticeKind>? ClosedByUser { get; set; }
        public EngagementNoticeKind Kind => IsShown ? EngagementNoticeKind.Rating : EngagementNoticeKind.None;
        public bool IsShown { get; private set; } = true;
        internal int HideCalls { get; private set; }
        public void ShowInvitation() => IsShown = true;
        public bool ShowUpdateCompleted(string version) => false;
        public void ShowUpdateAvailable(string? version, bool busy = false, string? status = null, bool failed = false) { }
        public void Hide() { HideCalls++; IsShown = false; }
    }

    private sealed class RatingLogger : ILogger
    {
        internal List<(string Message, Exception? Exception)> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add((formatter(state, exception), exception));
        }
    }
}
