using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// One display-time policy for transient messages: confirmations leave quickly, results and
/// warnings stay long enough to read, and failures to act on stay until they are dismissed.
/// </summary>
public sealed class FeedbackTimingTests : KoreanCaptionTest
{
    [Fact]
    public void EachKindHasItsOwnDisplayTime()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), FeedbackTiming.DisplayTime(FeedbackKind.Echo));
        Assert.Equal(TimeSpan.FromSeconds(3), FeedbackTiming.DisplayTime(FeedbackKind.Confirmation));
        Assert.Equal(TimeSpan.FromSeconds(2), FeedbackTiming.DisplayTime(FeedbackKind.Confirmation, overContent: true));
        Assert.Equal(TimeSpan.FromSeconds(6), FeedbackTiming.DisplayTime(FeedbackKind.Result));
        Assert.Equal(TimeSpan.FromSeconds(6), FeedbackTiming.DisplayTime(FeedbackKind.Warning, overContent: true));
        Assert.Null(FeedbackTiming.DisplayTime(FeedbackKind.Progress));
        Assert.Null(FeedbackTiming.DisplayTime(FeedbackKind.ActionRequired, overContent: true));
        Assert.Equal(
            [FeedbackKind.ActionRequired],
            Enum.GetValues<FeedbackKind>().Where(FeedbackTiming.StaysUntilDismissed));
        Assert.Equal(TimeSpan.FromMilliseconds(200), FeedbackTiming.ProgressRevealDelay);
        Assert.Equal(TimeSpan.FromSeconds(4), FeedbackTiming.HoldAfterResult);
        Assert.Equal(AutomationNotificationKind.ActionCompleted, FeedbackTiming.NotificationKind(FeedbackKind.Confirmation));
        Assert.Equal(AutomationNotificationKind.ActionAborted, FeedbackTiming.NotificationKind(FeedbackKind.ActionRequired));
        Assert.Equal(AutomationNotificationProcessing.ImportantMostRecent, FeedbackTiming.NotificationProcessing(FeedbackKind.ActionRequired));
        Assert.Equal(AutomationNotificationProcessing.MostRecent, FeedbackTiming.NotificationProcessing(FeedbackKind.Echo));
        // The capture overlay's colour chip follows the same rules.
        Assert.Equal(TimeSpan.FromSeconds(2), CaptureOverlayView.ToastDuration(copied: true));
        Assert.Equal(TimeSpan.FromSeconds(6), CaptureOverlayView.ToastDuration(copied: false));
    }

    [Fact]
    public void FailedColorCopyUsesTheWarningTimeAndSuccessfulRetryEndsTheSelector() => StaTestHost.Run(() =>
    {
        bool copies = false;
        var view = new CaptureOverlayView(WindowDetectOverlayTests.Frame(), showMagnifier: false)
        {
            ReadCursor = () => new MyCapture.Core.Primitives.PointD(5, 5),
            CopyColorText = _ => Task.FromResult(copies),
        };
        try
        {
            view.InitializePointer();
            Assert.Null(view.CopyColorAtPointerAsync().Result);
            Assert.Equal(UiText.Get("ColorPick_CopyFailed"), view.ToastMessageForTest);
            Assert.Equal(FeedbackTiming.Result, view.ToastIntervalForTest);
            int cancelled = 0;
            view.CancelRequested += (_, _) => cancelled++;
            copies = true;
            Assert.NotNull(view.CopyColorAtPointerAsync().Result);
            Assert.Equal(1, cancelled);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void CountdownStandsStillWhileReadAndResumesWithTheTimeLeft() => StaTestHost.Run(() =>
    {
        bool reading = false;
        int expired = 0;
        var countdown = new FeedbackCountdown(Dispatcher.CurrentDispatcher, () => reading, () => expired++);
        countdown.Start(TimeSpan.FromSeconds(3));
        Assert.True(countdown.IsRunning);
        countdown.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(1), countdown.Remaining);
        reading = true;
        countdown.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(1), countdown.Remaining);
        reading = false;
        countdown.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, expired);
        Assert.False(countdown.IsRunning);
        Assert.Null(countdown.Remaining);

        // A message without a display time never expires on its own.
        countdown.Start(null);
        Assert.False(countdown.IsRunning);
        countdown.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, expired);
    });

    [Fact]
    public void PinFailureStaysUntilEscAndTheNextEscClosesThePin() => StaTestHost.Run(() =>
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        PinWindow pin = manager.PinImage(Image(160, 120));
        try
        {
            pin.ReportCopyResult(false);
            Assert.Equal((UiText.Get("Text_0642E2D15469"), FeedbackKind.ActionRequired, true), pin.FeedbackForTest);
            Assert.False(pin.IsFeedbackTimerRunning);
            pin.AdvanceFeedbackForTest(TimeSpan.FromHours(1));
            Assert.True(pin.IsFeedbackVisible);

            Press(pin, Key.Escape);
            Assert.False(pin.IsFeedbackVisible);
            Assert.False(pin.IsClosed);
            Press(pin, Key.Escape);
            Assert.True(pin.IsClosed);
        }
        finally
        {
            manager.CloseAll();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
        }
    });

    [Fact]
    public void PinMessagesFollowTheirKind() => StaTestHost.Run(() =>
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        PinWindow pin = manager.PinImage(Image(160, 120));
        try
        {
            // Over the pinned content a confirmation is shorter than on a card.
            pin.ReportCopyResult(true);
            Assert.Equal(FeedbackKind.Confirmation, pin.FeedbackForTest.Kind);
            pin.AdvanceFeedbackForTest(TimeSpan.FromSeconds(1.9));
            Assert.True(pin.IsFeedbackVisible);
            pin.AdvanceFeedbackForTest(TimeSpan.FromSeconds(0.2));
            Assert.False(pin.IsFeedbackVisible);

            // A saved file name and the click-through recovery key are results to read.
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Saved, @"C:\Captures\pin-2026.png"));
            Assert.Equal((UiText.Format("Text_E3F51EDC0F16", "pin-2026.png"), FeedbackKind.Result, false), pin.FeedbackForTest);
            pin.AdvanceFeedbackForTest(TimeSpan.FromSeconds(5.9));
            Assert.True(pin.IsFeedbackVisible);
            pin.ApplyClickThrough(true);
            Assert.Equal(FeedbackKind.Result, pin.FeedbackForTest.Kind);
            pin.ApplyClickThrough(false);
            Assert.Equal(FeedbackKind.Confirmation, pin.FeedbackForTest.Kind);

            // A zoom readout passes over a failure, which comes back when the readout expires.
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Failed));
            Press(pin, Key.Add);
            Assert.Equal(FeedbackKind.Echo, pin.FeedbackForTest.Kind);
            pin.AdvanceFeedbackForTest(FeedbackTiming.Echo);
            Assert.Equal((UiText.Get("Text_AE94EC51B2C2"), FeedbackKind.ActionRequired, true), pin.FeedbackForTest);
            Assert.True(pin.IsFeedbackVisible);

            // The next outcome replaces it; recognising no text in an image is only a warning.
            pin.ReportOriginalTextCopyResult(false);
            Assert.Equal((UiText.Get("Text_BCC2F939B733"), FeedbackKind.Warning, false), pin.FeedbackForTest);
            pin.AdvanceFeedbackForTest(FeedbackTiming.Result);
            Assert.False(pin.IsFeedbackVisible);
        }
        finally
        {
            manager.CloseAll();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
        }
    });

    [Fact]
    public void LibraryStatusLinesAreClassifiedByWhatTheyAsk()
    {
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            using var scope = UiText.UseLanguage(language);
            Assert.Equal(FeedbackKind.ActionRequired, GalleryWindow.ClassifyActionStatus(UiText.Get("Folder410.OperationFailed")));
            Assert.Equal(FeedbackKind.ActionRequired, GalleryWindow.ClassifyActionStatus(UiText.Get("Library.Archive.Failed")));
            Assert.Equal(FeedbackKind.ActionRequired, GalleryWindow.ClassifyActionStatus(UiText.Get("Library.Source.CopyPathFailed")));
            Assert.Equal(FeedbackKind.ActionRequired,
                GalleryWindow.ClassifyActionStatus(UiText.Get("Folder410.ExportFailed") + " " + UiText.Get("Folder410.ExportDiskFull")));
            Assert.Equal(FeedbackKind.Progress, GalleryWindow.ClassifyActionStatus(UiText.Format("Library.Archive.Progress", 3, 12)));
            Assert.Equal(FeedbackKind.Progress, GalleryWindow.ClassifyActionStatus(UiText.Format("Folder410.ExportProgress", 3, 12)));
            Assert.Equal(FeedbackKind.Progress, GalleryWindow.ClassifyActionStatus(UiText.Get("Folder410.ExportPreparing")));
            Assert.Equal(FeedbackKind.Confirmation, GalleryWindow.ClassifyActionStatus(UiText.Get("Library.Source.PathCopied")));
            Assert.Equal(FeedbackKind.Result, GalleryWindow.ClassifyActionStatus(UiText.Format("Library.Archive.Done", 12)));
            Assert.Equal(FeedbackKind.Result, GalleryWindow.ClassifyActionStatus(UiText.Format("LibraryUx_MovedTo", "제품리뷰", 2)));
            Assert.Equal(FeedbackKind.Result, GalleryWindow.ClassifyActionStatus(UiText.Get("Library.Source.FolderMissing")));
        }
    }

    [Fact]
    public void ShellNotificationsFollowTheirKind() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        presenter.ShowNotification("MyCapture", "저장하지 못했습니다.", TrayBalloonKind.Error);
        Assert.Equal((true, FeedbackKind.ActionRequired, (TimeSpan?)null), presenter.NotificationForTest);
        presenter.AdvanceNotificationForTest(TimeSpan.FromHours(1));
        Assert.True(presenter.NotificationForTest.Open);

        presenter.ShowNotification("MyCapture", "준비됨", TrayBalloonKind.Information);
        Assert.Equal((true, FeedbackKind.Result, (TimeSpan?)FeedbackTiming.Result), presenter.NotificationForTest);
        presenter.AdvanceNotificationForTest(FeedbackTiming.Result);
        Assert.False(presenter.NotificationForTest.Open);

        presenter.ShowCaptureReceipt(UiText.Get("Capture.Saved"), () => { });
        Assert.Equal((true, FeedbackKind.Confirmation, (TimeSpan?)FeedbackTiming.Confirmation), presenter.NotificationForTest);
        presenter.Dismiss();
        Assert.False(presenter.NotificationForTest.Open);
    });

    private static void Press(Window window, Key key) =>
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)200, width * height * 4).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
