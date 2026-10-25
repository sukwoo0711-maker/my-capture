using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinFeedbackLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultBeforeRevealNeverShowsProgress(bool copied) => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        var announced = new List<FeedbackKind>();
        pin.AnnounceFeedback = (_, _, kind, _) => announced.Add(kind);
        try
        {
            pin.Show();
            pin.SimulateCtrlDoubleClickForTest();
            Assert.True(pin.IsProgressPendingForTest);
            Assert.False(pin.IsFeedbackVisible);
            pin.AdvanceProgressForTest(TimeSpan.FromMilliseconds(199));
            Assert.False(pin.IsFeedbackVisible);
            Assert.Empty(announced);
            pin.ReportOriginalTextCopyResult(copied);
            var result = pin.FeedbackForTest;
            Assert.Equal(copied ? FeedbackKind.Confirmation : FeedbackKind.ActionRequired, result.Kind);
            Assert.True(pin.IsFeedbackVisible);
            Assert.False(pin.IsProgressPendingForTest);
            pin.AdvanceProgressForTest(TimeSpan.FromSeconds(1));
            Assert.Equal(result, pin.FeedbackForTest);
            Assert.Equal([result.Kind], announced);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ResultAfterRevealReplacesVisibleProgress() => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        var announced = new List<FeedbackKind>();
        pin.AnnounceFeedback = (_, _, kind, _) => announced.Add(kind);
        try
        {
            pin.Show();
            pin.SimulateCtrlDoubleClickForTest();
            pin.AdvanceProgressForTest(TimeSpan.FromMilliseconds(199));
            Assert.False(pin.IsFeedbackVisible);
            pin.AdvanceProgressForTest(TimeSpan.FromMilliseconds(1));
            Assert.True(pin.IsFeedbackVisible);
            Assert.Equal(FeedbackKind.Progress, pin.FeedbackForTest.Kind);
            Assert.False(pin.IsFeedbackTimerRunning);
            pin.ReportOriginalTextCopyResult(true);
            Assert.Equal(FeedbackKind.Confirmation, pin.FeedbackForTest.Kind);
            Assert.Equal(FeedbackTiming.ConfirmationOverContent, pin.FeedbackRemainingForTest);
            Assert.Equal([FeedbackKind.Progress, FeedbackKind.Confirmation], announced);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("close")]
    [InlineData("hide")]
    [InlineData("selection-exit")]
    [InlineData("message")]
    public void PendingProgressIsCancelledByLifetimeOrNewMessage(string action) => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        try
        {
            pin.Show();
            if (action == "selection-exit") Press(pin, Key.F2);
            pin.ForceFeedbackTimeoutForTest();
            // Copy without a selection requests the image through the same progress path.
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(pin.IsProgressPendingForTest);
            switch (action)
            {
                case "close": pin.Close(); break;
                case "hide": pin.HidePin(); break;
                case "selection-exit": Press(pin, Key.Escape); break;
                case "message": Press(pin, Key.OemPlus); break;
            }
            Assert.False(pin.IsProgressPendingForTest);
            var feedback = pin.FeedbackForTest;
            pin.AdvanceProgressForTest(TimeSpan.FromSeconds(1));
            Assert.Equal(feedback, pin.FeedbackForTest);
            Assert.NotEqual(FeedbackKind.Progress, pin.FeedbackForTest.Kind);
            if (action is "close" or "hide") Assert.False(pin.IsFeedbackTimerRunning);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void FirstSelectionEntryShowsSixSecondsAndLaterPinsShowTwoSeconds() => StaTestHost.Run(() =>
    {
        int previous = PinWindow.SelectionHintShownForTest;
        PinWindow.SelectionHintShownForTest = 0;
        var first = NewTextPin();
        var second = NewTextPin();
        try
        {
            first.Show();
            second.Show();
            Press(first, Key.F2);
            Assert.Equal(TimeSpan.FromSeconds(6), first.FeedbackRemainingForTest);
            Press(first, Key.Escape);
            Press(first, Key.F2);
            Assert.Equal(TimeSpan.FromSeconds(2), first.FeedbackRemainingForTest);
            Press(second, Key.F2);
            Assert.Equal(TimeSpan.FromSeconds(2), second.FeedbackRemainingForTest);
        }
        finally
        {
            first.Close();
            second.Close();
            PinWindow.SelectionHintShownForTest = previous;
        }
    });

    private static PinWindow NewTextPin()
    {
        var image = BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 180 * 4], 480 * 4);
        image.Freeze();
        return new PinWindow(PinContent.FromText(image, "exact source", false),
            new PinViewState(480, 180, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
            { ShowActivated = false };
    }

    [Fact]
    public void ReadoutDuringPendingProgressReturnsToProgressWhenItExpires() => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        try
        {
            pin.Show();
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(pin.IsProgressPendingForTest);
            Press(pin, Key.OemPlus);
            Assert.Equal(FeedbackKind.Echo, pin.FeedbackForTest.Kind);
            pin.AdvanceFeedbackForTest(FeedbackTiming.Echo);
            Assert.Equal((MyCapture.Core.Localization.UiText.Get("Text_4C160257F02C"), FeedbackKind.Progress, false), pin.FeedbackForTest);
            pin.ReportCopyResult(true);
            Assert.Equal(FeedbackKind.Confirmation, pin.FeedbackForTest.Kind);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void FailureReportedWhileHiddenIsShownWhenThePinReturns() => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        try
        {
            pin.Show();
            Press(pin, Key.C, ModifierKeys.Control);
            pin.HidePin();
            pin.ReportCopyResult(false);
            pin.ShowPin();
            Assert.True(pin.IsFeedbackVisible);
            Assert.Equal(FeedbackKind.ActionRequired, pin.FeedbackForTest.Kind);
            Assert.True(pin.FeedbackForTest.Dismissible);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void HiddenPinDoesNotAnnounceLateResults() => StaTestHost.Run(() =>
    {
        var pin = NewTextPin();
        int announcements = 0;
        pin.AnnounceFeedback = (_, _, _, _) => announcements++;
        try
        {
            pin.Show();
            pin.Hide();
            pin.ReportCopyResult(true);
            Assert.Equal(0, announcements);
            pin.Show();
            pin.ReportCopyResult(true);
            Assert.Equal(1, announcements);
        }
        finally { pin.Close(); }
    });

    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DelayedClipboardOrOcrCompletion_AfterClose_DoesNotRestartFeedbackTimer(bool textFeedback) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 64);
        image.Freeze();
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(16, 16, 1, 1, 0.1),
            0, 0, () => new PinSettings());
        var timer = (FeedbackCountdown)typeof(PinWindow).GetField("_feedbackCountdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!;
        try
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task feedback = CompleteLaterAsync();
            pin.Close();
            Assert.True(pin.IsClosed);
            Assert.False(timer.IsRunning);
            completed.SetResult(true);
            long started = Stopwatch.GetTimestamp();
            while (!feedback.IsCompleted)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) throw new TimeoutException();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            feedback.GetAwaiter().GetResult();
            Assert.False(timer.IsRunning);

            async Task CompleteLaterAsync()
            {
                bool copied = await completed.Task;
                if (textFeedback) pin.ReportOriginalTextCopyResult(copied);
                else pin.ReportCopyResult(copied);
            }
        }
        finally
        {
            // Also contain the timer if an unfixed implementation fails the assertion.
            timer.Stop();
            pin.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });
}
