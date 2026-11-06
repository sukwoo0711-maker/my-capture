using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
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
            offered.OfferRatingInvitation(() => completion.Task, () => { });
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
        var card = Card();
        try
        {
            card.OfferRatingInvitation(() => null, () => { });
            Assert.Throws<InvalidOperationException>(() => card.OfferRatingInvitation(() => null, () => { }));
        }
        finally { card.Close(); }
        Assert.Throws<InvalidOperationException>(() => card.OfferRatingInvitation(() => null, () => { }));
        QuickAccessWindow shown = Card();
        try
        {
            ShowWithoutHotkey(shown);
            Assert.Throws<InvalidOperationException>(() => shown.OfferRatingInvitation(() => null, () => { }));
            shown.Hide();
            Assert.Throws<InvalidOperationException>(() => shown.OfferRatingInvitation(() => null, () => { }));
        }
        finally { shown.Close(); }
        var withHandle = Card();
        try
        {
            new WindowInteropHelper(withHandle).EnsureHandle();
            Assert.Throws<InvalidOperationException>(() => withHandle.OfferRatingInvitation(() => null, () => { }));
        }
        finally { withHandle.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewRunsOnceHoldsCountdownAndReleasesAfterSuccessOrFault(bool fault) => StaTestHost.Run(() =>
    {
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
        }, () => throw new InvalidOperationException("Close must not run"));
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
        refused.OfferRatingInvitation(() => { reviews++; return null; }, () => closes++);
        closing.OfferRatingInvitation(() => { reviews++; return null; }, () => closes++);
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
        card.OfferRatingInvitation(() => { callbacks++; return Task.CompletedTask; }, () => callbacks++);
        try
        {
            if (failure == "copy") card.ReportInitialCopy(false);
            else if (failure == "capture") card.ShowCaptureFailure("저장 실패", "저장할 수 없습니다.");
            else
            {
                // An action of the card that fails, as a pin that could not open: held like the capture's own failure.
                Task pin = card.RunAsync("Pin", () => Task.FromResult(false), "QuickAccess.Copying", "QuickAccess.Copied");
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!pin.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(5)) Pump();
                Assert.True(pin.IsCompleted);
            }
            Assert.Equal(Visibility.Hidden, card.RatingInvitationForTest.Row.Visibility);
            Click(card.RatingInvitationForTest.Review);
            Click(card.RatingInvitationForTest.Close);
            Assert.Equal(0, callbacks);
        }
        finally { card.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvitationIsVisibleAndCountedFromFirstAppearance(bool afterSave, bool coordinatorPath) => RunWithShell(afterSave, shell =>
    {
        CaptureOverlayCoordinator? coordinator = coordinatorPath ? shell.UseCoordinator() : null;
        var observations = new List<(Visibility Visibility, int Count, bool Active)>();
        shell.CardFirstVisible = card => observations.Add((card.RatingInvitationForTest.Row.Visibility,
            shell.Settings.StoreRating.InvitationShownCount, coordinator?.IsActive == true));
        if (coordinator is not null)
        {
            Assert.True(coordinator.StartWithSelection(shell.Frame, shell.Region, "Source - App", recordForRepeat: false));
            shell.Wait(coordinator.LastTransitionForTest);
            shell.RunIdleTurns();
        }
        else Capture(shell);
        var first = Assert.Single(observations);
        Assert.Equal(Visibility.Visible, first.Visibility);
        Assert.Equal(1, first.Count);
        if (coordinatorPath && !afterSave) Assert.True(first.Active);
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
    public void FailedEligibilityNeverShowsOrCountsInvitation(bool afterSave, string condition) => RunWithShell(afterSave, shell =>
    {
        // The after-save route evaluates after the outcome was recorded. Set the state at that
        // boundary so both routes exercise exactly the same policy inputs.
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
        Assert.Equal(Visibility.Collapsed, Assert.IsType<QuickAccessWindow>(shell.Card).RatingInvitationForTest.Row.Visibility);
        Assert.Equal(count, state.InvitationShownCount);
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
    public void IgnoredInvitationReturnsOnlyOnceAfterTimeoutOrReplacement(bool afterSave, bool replaced) => RunWithShell(afterSave, shell =>
    {
        for (int showing = 1; showing <= 3; showing++)
        {
            QuickAccessWindow card = Capture(shell);
            Assert.Equal(showing <= 2 ? Visibility.Visible : Visibility.Collapsed, card.RatingInvitationForTest.Row.Visibility);
            Assert.Equal(Math.Min(showing, 2), shell.Settings.StoreRating.InvitationShownCount);
            if (replaced) shell.Dismiss();
            else card.Advance(QuickAccessWindow.Lifetime * 2, false);
            Assert.False(card.IsOpen);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardAndVisibleWindowNeverOfferTogetherAndHiddenNoticeGivesWay(bool afterSave) => RunWithShell(afterSave, shell =>
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
    });

    [Theory]
    [InlineData("escape")]
    [InlineData("close")]
    [InlineData("replacement")]
    public void ExplicitCloseStillClosesWhileReviewIsPending(string method) => StaTestHost.Run(() =>
    {
        var card = Card();
        var completion = new TaskCompletionSource();
        card.OfferRatingInvitation(() => completion.Task, () => { });
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

    private static Shell EligibleShell(bool afterSave)
    {
        var shell = new Shell { ShowsCardAfterSave = afterSave };
        shell.StoreRating.RecordCapture(true);
        shell.StoreRating.RecordCapture(true);
        return shell;
    }

    private static void RunWithShell(bool afterSave, Action<Shell> body) => StaTestHost.Run(() =>
    {
        Shell shell = EligibleShell(afterSave);
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
        Ocr410Fixture.LoadTheme(card);
        FluidMotion.SetWindowEntrance(card, false);
        card.Show();
        card.UpdateLayout();
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
}
