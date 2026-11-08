using System.Windows.Automation;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureRatioAnnouncementTests
{
    [Theory]
    [InlineData(false, "ko-KR")]
    [InlineData(false, "en-US")]
    [InlineData(true, "ko-KR")]
    [InlineData(true, "en-US")]
    public void RatioActivityReplacesUnreadMessagesAndStopsWhenTheSelectorCloses(bool native, string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        var view = new RatioSelector(native, CaptureCueEvidenceTests.Desktop(), () => new PointD(300, 300), false,
            CaptureCueEvidenceTests.Monitors);
        var notifications = new List<(string Message, FeedbackKind Kind, string Activity)>();
        Action<string, FeedbackKind, string>? previous = FeedbackTiming.AnnouncementRequestedForTest;
        FeedbackTiming.AnnouncementRequestedForTest = (message, kind, activity) => notifications.Add((message, kind, activity));
        try
        {
            view.SelectRatio(SelectionRatio.Ratio16x9);
            Assert.Equal((UiText.Format("CaptureRatio_Changed", CaptureModeBar.RatioName(SelectionRatio.Ratio16x9)),
                FeedbackKind.Echo, "MyCapture.Capture.Ratio"), Assert.Single(notifications));
            Assert.True(view.PlaceFixedSize(new FixedSelectionSize(1280, 720)));
            Assert.Equal((UiText.Get("CaptureRatio_FixedHint"), FeedbackKind.Result, "MyCapture.Capture.Ratio"), notifications[1]);
            view.SelectRatio(SelectionRatio.Ratio4x3);
            Assert.Equal((UiText.Format("CaptureRatio_Changed", CaptureModeBar.RatioName(SelectionRatio.Ratio4x3)),
                FeedbackKind.Echo, "MyCapture.Capture.Ratio"), notifications[2]);
            Assert.Equal(3, notifications.Count);
            Assert.All(notifications, notification =>
            {
                Assert.Equal("MyCapture.Capture.Ratio", notification.Activity);
                Assert.Equal(AutomationNotificationProcessing.MostRecent, FeedbackTiming.NotificationProcessing(notification.Kind));
            });
            Assert.Equal(AutomationNotificationKind.Other, FeedbackTiming.NotificationKind(notifications[0].Kind));
            Assert.Equal(AutomationNotificationKind.ActionCompleted, FeedbackTiming.NotificationKind(notifications[1].Kind));
            view.ReleaseResources();
            view.SelectRatio(SelectionRatio.Ratio1x1);
            Assert.False(view.PlaceFixedSize(new FixedSelectionSize(1280, 720)));
            Assert.Equal(3, notifications.Count);
        }
        finally
        {
            view.ReleaseResources();
            FeedbackTiming.AnnouncementRequestedForTest = previous;
        }
    });
}
