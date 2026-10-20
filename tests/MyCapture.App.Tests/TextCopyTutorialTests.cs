using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TextCopyTutorialTests
{
    [Fact]
    public void ConstructingTheWindowOpensNothing_ShowingOpensTheVideoBesideTheAppAndShowsItsFirstFrame() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var media = new FakeTutorialMedia();
        var window = new TextCopyTutorialWindow(new HotkeySettings(), () => media) { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Assert.Empty(media.OpenedPaths);
            Assert.False(window.HasMedia);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(TextCopyTutorialWindow).Assembly.Location)!,
                "Assets", "Tutorials", "text-copy.en.mp4"), window.AssetPath);
            Assert.Equal(UiText.Get("TextCapture_TutorialAnimation"), AutomationProperties.GetName(media.View));
        }
        finally { window.Close(); }
        Assert.True(media.Disposed);

        media = new FakeTutorialMedia();
        window = Offscreen(new HotkeySettings(), media);
        try
        {
            Assert.Empty(media.OpenedPaths);
            Show(window);
            Assert.Equal(Path.Combine(window.AssetDirectory, "text-copy.en.mp4"), Assert.Single(media.OpenedPaths));
            Assert.True(window.HasFirstFrame);
            Assert.DoesNotContain(Texts(window), text => text.Text == UiText.Get("TextCapture_TutorialLoading") && text.IsVisible);
        }
        finally { window.Close(); }
        Assert.False(window.HasMedia);
        Assert.True(media.Disposed);
    });

    [Fact]
    public void ReducedMotionShowsTheFirstFrameStillButExplicitPlayPauseAndReplayWork() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var media = new FakeTutorialMedia();
        var window = Offscreen(new HotkeySettings(), media);
        try
        {
            Show(window);
            window.Activate();
            Drain(window);
            Assert.True(window.HasFirstFrame);
            Assert.False(window.IsPlaying);
            Assert.Equal(0, media.PlayCount);
            Assert.All(new[] { "TextCapture_TutorialStep1", "TextCapture_TutorialStep2", "TextCapture_TutorialStep3" },
                key => Assert.Contains(Texts(window), text => text.Text == UiText.Get(key)));
            Click(window, UiText.Get("GifPreview_Play"));
            Assert.True(window.IsPlaying && media.IsPlaying);
            Click(window, UiText.Get("GifPreview_Pause"));
            Assert.False(window.IsPlaying || media.IsPlaying);
            Click(window, UiText.Get("TextCapture_TutorialReplay"));
            Assert.True(window.IsPlaying && media.IsPlaying);
            Assert.Equal(1, media.RewindCount);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void WithMotionAnActiveWindowStartsPlayingAsSoonAsTheVideoOpens() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var media = new FakeTutorialMedia { AutoOpen = false };
        var window = new TextCopyTutorialWindow(new HotkeySettings(), () => media)
        {
            AssetDirectory = TutorialTestAssets.Create("text-copy"), WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowInTaskbar = false,
        };
        try
        {
            Show(window);
            window.Activate();
            Drain(window);
            Assert.True(window.HasMedia);
            Assert.False(window.IsPlaying);
            media.RaiseOpened();
            Assert.True(window.HasFirstFrame);
            Assert.Equal(window.IsActive, window.IsPlaying);
            Assert.Equal(window.IsActive, media.IsPlaying);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void DeactivationMinimizeAndHideStopPlayback_HideReleasesAndShowReopens() => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        var window = Offscreen(new HotkeySettings(), media);
        try
        {
            Show(window);
            window.TogglePlayback();
            Assert.True(window.IsPlaying);
            typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
            Assert.False(window.IsPlaying || media.IsPlaying);
            window.TogglePlayback();
            window.WindowState = WindowState.Minimized;
            Assert.False(window.IsPlaying || media.IsPlaying);
            window.WindowState = WindowState.Normal;
            window.TogglePlayback();
            window.Hide();
            Assert.False(window.IsPlaying);
            Assert.False(window.HasMedia || window.HasFirstFrame);
            Assert.False(media.IsOpen);
            Show(window);
            Assert.Equal(2, media.OpenedPaths.Count);
            Assert.True(window.HasFirstFrame);
            Assert.False(window.IsPlaying);
        }
        finally { window.Close(); }
        Assert.False(window.IsPlaying || window.HasMedia);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("throws")]
    public void MissingOrFailingVideoKeepsTheStepsAndDisablesPlayback(string kind) => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia
        {
            AutoOpen = kind != "failed",
            ThrowOnOpen = kind == "throws" ? new FileFormatException("fixture malformed") : null,
        };
        var window = Offscreen(new HotkeySettings(), media, kind == "missing" ? TutorialTestAssets.Create() : null);
        try
        {
            Show(window);
            if (kind == "failed") media.RaiseFailed(new FileFormatException("fixture malformed"));
            Assert.True(window.IsShowingFallback);
            Assert.False(window.HasMedia || window.IsPlaying);
            Assert.Contains(Texts(window), text => text.Text == UiText.Get("TextCapture_TutorialUnavailable") && text.IsVisible);
            Button play = FirstRunWelcomeTests.Descendants(window).OfType<Button>().Single(button =>
                Equals(button.Content, UiText.Get("GifPreview_Play")));
            Assert.False(play.IsEnabled);
            window.TogglePlayback();
            Assert.False(window.IsPlaying);
            Assert.Contains(Texts(window), text => text.Text == UiText.Get("TextCapture_TutorialStep3") && text.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TurningOffMotionWhilePlayingPausesTheTutorial() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var media = new FakeTutorialMedia();
        var window = Offscreen(new HotkeySettings(), media);
        try
        {
            Show(window);
            if (!window.IsPlaying) window.TogglePlayback();
            Assert.True(window.IsPlaying);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            typeof(TextCopyTutorialWindow).GetMethod("OnMotionPreferenceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new System.ComponentModel.PropertyChangedEventArgs(nameof(SystemParameters.ClientAreaAnimation))]);
            Assert.False(window.IsPlaying || media.IsPlaying);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void ClosingWhileTheVideoOpensNeverReattachesOrStartsIt() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var media = new FakeTutorialMedia { AutoOpen = false };
        var window = Offscreen(new HotkeySettings(), media);
        try
        {
            Show(window);
            Assert.True(window.HasMedia);
            Assert.False(window.HasFirstFrame);
            window.Close();
            media.RaiseOpened();
            media.RaiseEnded();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(window.HasMedia || window.HasFirstFrame || window.IsPlaying);
            Assert.Equal(0, media.PlayCount);
            Assert.True(media.Disposed);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CurrentShortcutsAndUnassignedCaptureHaveAccurateEntryInstructions(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var keys = new HotkeySettings
        {
            Capture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4A),
            CaptureText = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54),
        };
        var configured = Offscreen(keys, new FakeTutorialMedia());
        var unassigned = Offscreen(new HotkeySettings { Capture = Hotkey.None }, new FakeTutorialMedia());
        try
        {
            Assert.Contains(Texts(configured), text => text.Text == UiText.Format("TextCapture_TutorialCaptureKey", "Ctrl+Shift+J"));
            Assert.Contains(Texts(configured), text => text.Text == UiText.Format("TextCapture_TutorialDirect", "Ctrl+Alt+T"));
            Assert.Contains(Texts(unassigned), text => text.Text == UiText.Get("TextCapture_TutorialCaptureMenu"));
            Assert.DoesNotContain(Texts(unassigned), text => text.Text.Contains(UiText.Get("Hotkey.Unassigned"), StringComparison.Ordinal));
            Assert.Equal(language.StartsWith("ko", StringComparison.Ordinal) ? "text-copy.ko.mp4" : "text-copy.en.mp4",
                Path.GetFileName(configured.AssetPath));
        }
        finally { configured.Close(); unassigned.Close(); }
    });

    [Fact]
    public void ShortcutSearchHasBothTutorialEntrypoints_AndCaptureDismissesTheTutorial() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        Func<ITutorialMedia>? previousMedia = FeatureTutorialWindow.MediaFactoryOverrideForTest;
        ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings();
        FeatureTutorialWindow.MediaFactoryOverrideForTest = () => new FakeTutorialMedia();
        var help = new ShortcutHelpWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            help.Show();
            help.UpdateLayout();
            var view = ShortcutHelpSearchTests.View(help);
            view.Search.Text = UiText.Get("TextCapture_Title");
            Border[] rows = view.VisibleResults();
            Assert.Equal(3, rows.Length);
            Border originalText = Assert.Single(rows, row => Texts(row)[0].Text == UiText.Get("Text_976A6C92B870"));
            Assert.Equal(UiText.Get("Text_33EFD44366A7"), Texts(originalText)[1].Text);
            Assert.Contains(FirstRunWelcomeTests.Descendants(originalText).OfType<WrapPanel>(),
                keys => AutomationProperties.GetName(keys) == "Ctrl+C / " + UiText.Get("Text_C7011B8D345C"));
            Assert.Empty(FirstRunWelcomeTests.Descendants(originalText).OfType<Button>());
            Button[] tutorials = rows.SelectMany(FirstRunWelcomeTests.Descendants).OfType<Button>().ToArray();
            Assert.Equal(2, tutorials.Length);
            Assert.All(rows.Where(row => !ReferenceEquals(row, originalText)),
                row => Assert.Single(FirstRunWelcomeTests.Descendants(row).OfType<Button>()));
            Assert.All(tutorials, button =>
            {
                Assert.Null(button.Content);
                Assert.Equal("텍스트 복사 사용법 보기", AutomationProperties.GetName(button));
                Assert.True(button.Focusable && button.IsTabStop);
            });
            tutorials[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            TextCopyTutorialWindow tutorial = Assert.Single(help.OwnedWindows.OfType<TextCopyTutorialWindow>());
            Assert.True(tutorial.IsVisible);
            Assert.True(ShortcutHelpWindow.DismissForCapture());
            Assert.False(tutorial.IsVisible);
            Assert.False(tutorial.HasMedia);
            Assert.False(tutorial.IsPlaying);
        }
        finally
        {
            help.Close();
            ShortcutHelpWindow.ReadHotkeys = previous;
            FeatureTutorialWindow.MediaFactoryOverrideForTest = previousMedia;
        }
    });

    private static TextCopyTutorialWindow Offscreen(HotkeySettings keys, FakeTutorialMedia media, string? assets = null) =>
        new(keys, () => media) { AssetDirectory = assets ?? TutorialTestAssets.Create("text-copy"),
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };

    private static void Show(TextCopyTutorialWindow window)
    {
        window.Show(); window.UpdateLayout();
        Drain(window);
    }

    private static void Drain(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Click(TextCopyTutorialWindow window, string caption) =>
        FirstRunWelcomeTests.Descendants(window).OfType<Button>().Single(button => Equals(button.Content, caption))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static TextBlock[] Texts(DependencyObject root) => FirstRunWelcomeTests.Descendants(root).OfType<TextBlock>().ToArray();
}
