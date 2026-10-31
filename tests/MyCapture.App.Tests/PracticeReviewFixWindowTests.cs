using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeReviewFixWindowTests
{
    [Theory]
    [InlineData("A4")]
    [InlineData("A5")]
    [InlineData("A6")]
    [InlineData("B2")]
    [InlineData("B3")]
    [InlineData("B4")]
    [InlineData("B5")]
    public void ExternalActionsKeepADisabledWaitingSlotWithoutDefaultReplay(string stepName) => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
            options: new WelcomeOptions { ReducedMotion = true, MediaFactory = () => media });
        try
        {
            var step = Enum.Parse<PracticeStep>(stepName);
            PracticeJourneyDriver.AdvanceTo(guide.Session, step);
            Assert.Equal(Visibility.Visible, guide.TryNowButton.Visibility);
            Assert.False(guide.TryNowButton.IsEnabled);
            Assert.False(guide.TryNowButton.IsDefault);
            Assert.False(guide.TryNowButton.IsTabStop);
            Assert.Equal(UiText.Get(step <= PracticeStep.A6 ? "Journey.WaitEditor" : "Journey.WaitPin"), guide.TryNowButton.Content);
            Assert.Equal(Visibility.Visible, guide.ReplayButton.Visibility);
            Assert.True(guide.ReplayButton.IsEnabled && guide.ReplayButton.Focusable);
            int opened = media.OpenedPaths.Count, rewinds = media.RewindCount;
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(step, guide.Session.Step);
            Assert.Equal(opened, media.OpenedPaths.Count);
            Assert.Equal(rewinds, media.RewindCount);
            Assert.Equal(PracticeStepResult.Pending, guide.Session.Results[step]);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "연습 그림")]
    [InlineData("en-US", "Practice picture")]
    public void PracticeImageHasAnImageNameInBothLanguages(string language, string expected) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor);
        try
        {
            var picture = Assert.IsType<Image>(typeof(WelcomeWindow).GetField("_practiceTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guide));
            Assert.NotNull(picture.Source);
            Assert.Equal(expected, AutomationProperties.GetName(picture));
            Assert.NotEqual(UiText.Get("Onboarding.PracticeLabel"), AutomationProperties.GetName(picture));
        }
        finally { guide.Close(); }
    });
}
