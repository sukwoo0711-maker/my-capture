using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TextCopyTutorialTests
{
    [Fact]
    public void ConstructingTheWindowDoesNotReadTheGif_LoadingUsesTheAppAssemblyFolder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        int reads = 0;
        string? readPath = null;
        ApartmentState? apartment = null;
        var window = Offscreen(new HotkeySettings(), (path, _) =>
        { reads++; readPath = path; apartment = Thread.CurrentThread.GetApartmentState(); return Frames(); });
        try
        {
            Assert.Equal(0, reads);
            Assert.False(window.HasFrames);
            Show(window);
            Assert.Equal(1, reads);
            Assert.Equal(ApartmentState.STA, apartment);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(TextCopyTutorialWindow).Assembly.Location)!,
                "Assets", "Tutorials", "text-copy.en.gif"), readPath);
            Assert.True(window.HasFrames);
            Assert.Equal(UiText.Get("TextCapture_TutorialAnimation"), AutomationProperties.GetName(
                FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single()));
        }
        finally { window.Close(); }
        Assert.False(window.HasFrames);
    });

    [Fact]
    public void ReducedMotionStartsPausedButExplicitPlayPauseAndReplayWork() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var window = Offscreen(new HotkeySettings());
        try
        {
            Show(window);
            Assert.False(window.IsPlaying);
            Assert.All(new[] { "TextCapture_TutorialStep1", "TextCapture_TutorialStep2", "TextCapture_TutorialStep3" },
                key => Assert.Contains(Texts(window), text => text.Text == UiText.Get(key)));
            Click(window, UiText.Get("GifPreview_Play"));
            Assert.True(window.IsPlaying);
            Click(window, UiText.Get("GifPreview_Pause"));
            Assert.False(window.IsPlaying);
            Click(window, UiText.Get("TextCapture_TutorialReplay"));
            Assert.True(window.IsPlaying);
            Assert.Equal(0, window.FrameIndex);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void DeactivationMinimizeAndHideStopPlayback_HideAndCloseReleaseFrames() => StaTestHost.Run(() =>
    {
        var window = Offscreen(new HotkeySettings());
        try
        {
            Show(window);
            window.TogglePlayback();
            Assert.True(window.IsPlaying);
            typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
            Assert.False(window.IsPlaying);
            window.TogglePlayback();
            window.WindowState = WindowState.Minimized;
            Assert.False(window.IsPlaying);
            window.WindowState = WindowState.Normal;
            window.TogglePlayback();
            window.Hide();
            Assert.False(window.IsPlaying);
            Assert.False(window.HasFrames);
            Assert.Null(FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single().Source);
        }
        finally { window.Close(); }
        Assert.False(window.IsPlaying);
        Assert.False(window.HasFrames);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedGifKeepsTheStepsAndDisablesPlayback(bool malformed) => StaTestHost.Run(() =>
    {
        var window = Offscreen(new HotkeySettings(), (_, _) => throw (malformed
            ? new FileFormatException("fixture malformed") : new IOException("fixture missing")));
        try
        {
            Show(window);
            Assert.False(window.HasFrames);
            Assert.False(window.IsPlaying);
            Assert.Contains(Texts(window), text => text.Text == UiText.Get("TextCapture_TutorialUnavailable") && text.IsVisible);
            Button play = FirstRunWelcomeTests.Descendants(window).OfType<Button>().Single(button =>
                Equals(button.Content, UiText.Get("GifPreview_Play")));
            Assert.False(play.IsEnabled);
            Assert.Contains(Texts(window), text => text.Text == UiText.Get("TextCapture_TutorialStep3"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TurningOffMotionWhilePlayingPausesTheTutorial() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var window = Offscreen(new HotkeySettings());
        try
        {
            Show(window);
            window.TogglePlayback();
            Assert.True(window.IsPlaying);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            typeof(TextCopyTutorialWindow).GetMethod("OnMotionPreferenceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new System.ComponentModel.PropertyChangedEventArgs(nameof(SystemParameters.ClientAreaAnimation))]);
            Assert.False(window.IsPlaying);
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void ClosingDuringDecodeCancelsAndNeverReattachesFramesOrStartsThePlayer() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        bool sawCancellation = false;
        var window = Offscreen(new HotkeySettings(), (_, token) =>
        {
            entered.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
            sawCancellation = token.IsCancellationRequested;
            GifSampleFrames frames = Frames();
            returned.Set();
            return frames;
        });
        try
        {
            window.Show(); window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(window.IsLoading);
            Assert.False(window.HasFrames);
            window.Close();
            finish.Set();
            Assert.True(returned.Wait(TimeSpan.FromSeconds(5)));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(sawCancellation);
            Assert.False(window.IsLoading);
            Assert.False(window.HasFrames);
            Assert.False(window.IsPlaying);
            Assert.Null(FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single().Source);
        }
        finally { finish.Set(); window.Close(); }
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
        var configured = Offscreen(keys);
        var unassigned = Offscreen(new HotkeySettings { Capture = Hotkey.None });
        try
        {
            Assert.Contains(Texts(configured), text => text.Text == UiText.Format("TextCapture_TutorialCaptureKey", "Ctrl+Shift+J"));
            Assert.Contains(Texts(configured), text => text.Text == UiText.Format("TextCapture_TutorialDirect", "Ctrl+Alt+T"));
            Assert.Contains(Texts(unassigned), text => text.Text == UiText.Get("TextCapture_TutorialCaptureMenu"));
            Assert.DoesNotContain(Texts(unassigned), text => text.Text.Contains(UiText.Get("Hotkey.Unassigned"), StringComparison.Ordinal));
            Assert.Equal(language.StartsWith("ko", StringComparison.Ordinal) ? "text-copy.ko.gif" : "text-copy.en.gif",
                Path.GetFileName(configured.AssetPath));
        }
        finally { configured.Close(); unassigned.Close(); }
    });

    [Fact]
    public void ShortcutSearchHasBothTutorialEntrypoints_AndCaptureDismissesTheTutorial() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings();
        var help = new ShortcutHelpWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            help.Show();
            help.UpdateLayout();
            var view = ShortcutHelpSearchTests.View(help);
            view.Search.Text = UiText.Get("TextCapture_Title");
            Border[] rows = view.VisibleResults();
            Assert.Equal(2, rows.Length);
            Button[] tutorials = rows.SelectMany(FirstRunWelcomeTests.Descendants).OfType<Button>().ToArray();
            Assert.Equal(2, tutorials.Length);
            Assert.All(tutorials, button =>
            {
                Assert.Equal(UiText.Get("TextCapture_TutorialOpen"), button.Content);
                Assert.Equal(UiText.Get("TextCapture_TutorialTitle"), AutomationProperties.GetName(button));
                Assert.True(button.Focusable && button.IsTabStop);
            });
            tutorials[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            TextCopyTutorialWindow tutorial = Assert.Single(help.OwnedWindows.OfType<TextCopyTutorialWindow>());
            Assert.True(tutorial.IsVisible);
            Assert.True(ShortcutHelpWindow.DismissForCapture());
            Assert.False(tutorial.IsVisible);
            Assert.False(tutorial.HasFrames);
            Assert.False(tutorial.IsPlaying);
        }
        finally { help.Close(); ShortcutHelpWindow.ReadHotkeys = previous; }
    });

    private static TextCopyTutorialWindow Offscreen(HotkeySettings keys, Func<string, CancellationToken, GifSampleFrames>? decode = null) =>
        new(keys, decode ?? ((_, _) => Frames())) { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };

    private static void Show(TextCopyTutorialWindow window)
    {
        window.Show(); window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (window.IsLoading && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(1);
        }
        Assert.False(window.IsLoading, "Background STA decoder did not finish.");
    }

    private static void Click(TextCopyTutorialWindow window, string caption) =>
        FirstRunWelcomeTests.Descendants(window).OfType<Button>().Single(button => Equals(button.Content, caption))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static TextBlock[] Texts(DependencyObject root) => FirstRunWelcomeTests.Descendants(root).OfType<TextBlock>().ToArray();

    private static GifSampleFrames Frames()
    {
        BitmapSource red = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        BitmapSource blue = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
        red.Freeze(); blue.Freeze();
        return new GifSampleFrames([red, blue], [100, 100], 1, 1);
    }
}
