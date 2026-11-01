using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeJourneyPlaybackTests
{
    [Theory]
    [InlineData("ko-KR", "방금 캡처한 그림을 두 번 누릅니다.", "같은 그림이 편집기에 다시 열립니다.")]
    [InlineData("en-US", "Double-click the picture you just captured.", "The same picture opens in the editor again.")]
    public void LibraryClipAndStepDescribeReopeningTheSamePictureInTheEditor(string language, string action, string result) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var feature = TutorialCatalog.Get("practice-library-find");
        Assert.Equal(action, feature.Steps[1]);
        Assert.Equal(result, feature.Steps[2]);
        Assert.Equal(action + " " + result, UiText.Get("Journey.C2.Body"));
    });

    [Fact]
    public void ReducedMotionStartsStillAndExplicitReplayPlaysOnce() => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        using var preview = new WelcomeTutorialPreview(new WelcomeOptions { ReducedMotion = true, MediaFactory = () => media });
        var window = Offscreen(preview);
        try
        {
            preview.SelectFeature("image-arrow"); window.Show(); Drain(window);
            Assert.True(preview.HasPoster); Assert.Empty(media.OpenedPaths);
            preview.Replay(); Drain(window);
            Assert.True(media.IsPlaying); Assert.Single(media.OpenedPaths);
            media.RaiseEnded(); Pump(window, 1200);
            Assert.False(media.IsPlaying); Assert.Equal(0, media.RewindCount);
            preview.Replay(); Assert.True(media.IsPlaying); Assert.Equal(1, media.RewindCount);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WaitingRepeatsAfterRestAndPauseHideAndFailureKeepTheStill() => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        using var preview = new WelcomeTutorialPreview(new WelcomeOptions { ReducedMotion = false, MediaFactory = () => media });
        var window = Offscreen(preview);
        try
        {
            preview.SelectFeature("image-arrow"); window.Show(); Drain(window);
            Assert.True(media.IsPlaying);
            media.RaiseEnded(); Assert.Equal(0, media.RewindCount);
            Pump(window, 1200); Assert.Equal(1, media.RewindCount); Assert.True(media.IsPlaying);
            preview.Pause(); media.RaiseEnded(); Pump(window, 1200);
            Assert.False(media.IsPlaying); Assert.Equal(1, media.RewindCount);
            preview.Replay(); preview.Suspend(true); Assert.False(media.IsPlaying);
            preview.Suspend(false); Assert.True(media.IsPlaying);
            media.ThrowOnRewind = new InvalidOperationException("decoder failed");
            preview.Replay(); Assert.True(preview.IsFallback && preview.HasPoster);
            window.Hide(); Assert.False(media.IsOpen);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void SevenMissingClipsHaveTheSameThreeScenesAndAnImage(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string emptyClips = System.IO.Path.Combine(AppContext.BaseDirectory, "missing-practice-clips", Guid.NewGuid().ToString("N"));
        Assert.False(System.IO.Directory.Exists(emptyClips));
        foreach (string id in new[]
        {
            "pin-resize", "pin-opacity", "practice-image-copy", "practice-image-pin",
            "practice-editor-open", "practice-pin-close", "practice-library-find",
        })
        {
            var media = new FakeTutorialMedia();
            using var preview = new WelcomeTutorialPreview(new WelcomeOptions { ReducedMotion = true, MediaFactory = () => media }) { AssetDirectory = emptyClips };
            preview.SelectFeature(id);
            Assert.True(preview.HasPoster);
            Assert.True(preview.IsFallback);
            string[] text = FirstRunWelcomeTests.Descendants(preview).OfType<System.Windows.Controls.TextBlock>().Select(t => KoreanWordWrap.Strip(t.Text)).ToArray();
            foreach (string scene in TutorialCatalog.Get(id).Steps)
            {
                Assert.DoesNotContain("?", scene);
                Assert.Contains(text, line => line.EndsWith(scene, StringComparison.Ordinal));
            }
            Assert.Empty(media.OpenedPaths);
            var help = new FeatureTutorialWindow(new HotkeySettings(), id, () => new FakeTutorialMedia())
            { AssetDirectory = emptyClips, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            // Every practice clip now ships, so a missing file in help falls back like any other tutorial.
            try { help.Show(); Drain(help); Assert.True(help.IsShowingFallback); }
            finally { help.Close(); }
        }
    });

    private static Window Offscreen(UIElement content) => new()
    {
        Content = content, Width = 420, Height = 300, Left = -10000, Top = -10000,
        WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
    };
    private static void Drain(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Pump(Window window, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
