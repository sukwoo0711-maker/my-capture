using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Notification cards are gone at once for everyone who asks, and their pixels leave toward the
/// right edge they are docked at. A newer card of the same kind and a starting capture end it at once.
/// </summary>
public sealed class NotificationExitTests
{
    [Fact]
    public void DismissedCardIsGoneAtOnceAndSlidesRight() => Run(motion: true, presenter =>
    {
        int dismissed = 0;
        presenter.NotificationDismissed += () => dismissed++;
        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window card = Shown(presenter, drain: true);
        int closed = 0;
        card.Closed += (_, _) => closed++;

        presenter.Dismiss();

        Assert.False(presenter.HasNotification);
        Assert.False(presenter.NotificationForTest.Open);
        Assert.Equal(1, dismissed);
        Assert.Equal(0, closed);
        Assert.True(FluidMotion.IsExiting(card));
        Assert.True(card.IsVisible);
        Assert.False(card.IsHitTestVisible);
        Assert.IsType<TranslateTransform>(((FrameworkElement)card.Content).RenderTransform);
        Assert.Equal([card], presenter.LeavingCardsForTest);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (closed == 0 && DateTime.UtcNow < deadline) Pump(card.Dispatcher, 20);

        Assert.Equal(1, closed);
        Assert.Empty(presenter.LeavingCardsForTest);
        Assert.Equal(1, dismissed);
    });

    [Fact]
    public void TimeoutCloseButtonEscAndActionAllLeave() => Run(motion: true, presenter =>
    {
        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window timedOut = Shown(presenter);
        presenter.AdvanceNotificationForTest(FeedbackTiming.Result);
        Assert.True(FluidMotion.IsExiting(timedOut));

        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window closedByButton = Shown(presenter);
        Buttons(closedByButton).Single(button => Equals(button.Content, UiText.Get("Shell.Close")))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(FluidMotion.IsExiting(closedByButton));

        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window closedByEsc = Shown(presenter);
        closedByEsc.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(closedByEsc)!, 0, Key.Escape)
            { RoutedEvent = Keyboard.KeyDownEvent });
        Assert.True(FluidMotion.IsExiting(closedByEsc));

        int runs = 0;
        presenter.ShowActionNotification("MyCapture", "준비됨", "열기", () => runs++);
        Window acted = Shown(presenter);
        Buttons(acted).Single(button => Equals(button.Content, "열기")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, runs);
        Assert.True(FluidMotion.IsExiting(acted));
        Assert.False(presenter.HasNotification);
        Assert.Contains(acted, presenter.LeavingCardsForTest);
    });

    [Fact]
    public void ANewerCardOfTheSameKindReplacesAtOnceAndAnotherKindLetsTheOldOneLeave() => Run(motion: true, presenter =>
    {
        presenter.ShowColorCopy(new ColorCopyFeedback("#604020", 96, 64, 32));
        Window first = Shown(presenter, drain: true);
        presenter.ShowColorCopy(new ColorCopyFeedback("#204060", 32, 64, 96));
        Window second = Shown(presenter, drain: true);

        Assert.NotSame(first, second);
        Assert.False(FluidMotion.IsExiting(first));
        Assert.Null(PresentationSource.FromVisual(first));
        Assert.Empty(presenter.LeavingCardsForTest);

        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);

        Assert.True(FluidMotion.IsExiting(second));
        Assert.Equal([second], presenter.LeavingCardsForTest);
        Window notice = Assert.IsType<Window>(presenter.NotificationWindowForTest);
        Assert.NotSame(second, notice);
        Assert.True(presenter.NotificationForTest.Open);
    });

    [Fact]
    public void ACaptureEndsEveryExitAtOnce() => Run(motion: true, presenter =>
    {
        int foreground = 0;
        presenter.ForegroundDismissed += () => foreground++;
        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window leaving = Shown(presenter);
        presenter.Dismiss();
        presenter.ShowCaptureReceipt(UiText.Get("Capture.Saved"), () => { });
        Window receipt = Shown(presenter);
        Assert.True(FluidMotion.IsExiting(leaving));

        presenter.DismissForCapture();

        Assert.Null(PresentationSource.FromVisual(leaving));
        Assert.Null(PresentationSource.FromVisual(receipt));
        Assert.False(FluidMotion.IsExiting(receipt));
        Assert.Empty(presenter.LeavingCardsForTest);
        Assert.Equal(1, foreground);
    });

    [Fact]
    public void WithoutMotionACardClosesAtOnce() => Run(motion: false, presenter =>
    {
        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Window card = Shown(presenter);

        presenter.Dismiss();

        Assert.False(FluidMotion.IsExiting(card));
        Assert.Null(PresentationSource.FromVisual(card));
        Assert.Empty(presenter.LeavingCardsForTest);
    });

    private static void Run(bool motion, Action<ThemedShellPresenter> body) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(true);
        using var language = UiText.UseLanguage("ko-KR");
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        var monitor = new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920), new RectD(-20000, -20000, 1080, 1880), 96, false);
        var presenter = new ThemedShellPresenter(() => false, () => false) { NotificationMonitor = () => monitor };
        try
        {
            body(presenter);
        }
        finally
        {
            presenter.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    // Draining lets layout settle, but also lets an earlier card's exit end; drain only before the
    // first dismissal of a test.
    private static Window Shown(ThemedShellPresenter presenter, bool drain = false)
    {
        Window card = Assert.IsType<Window>(presenter.NotificationWindowForTest);
        if (drain) ControlStateMotionTests.Drain(card);
        Assert.True(card.IsVisible);
        Assert.True(card.AllowsTransparency);
        return card;
    }

    private static IEnumerable<Button> Buttons(Window window) => FirstRunWelcomeTests.Descendants(window).OfType<Button>();

    private static void Pump(Dispatcher dispatcher, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
