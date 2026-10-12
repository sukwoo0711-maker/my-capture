using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FeatureTutorialTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CatalogHas38SeparateLocalizedWorkflowsAnd42CategoryQualifiedHelpMappings(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(38, TutorialCatalog.All.Count);
        Assert.Equal(38, TutorialCatalog.All.Select(feature => feature.Id).Distinct().Count());
        Assert.Equal(42, TutorialCatalog.All.Sum(feature => feature.HelpActions.Count));
        foreach (FeatureTutorial feature in TutorialCatalog.All)
        {
            Assert.NotEqual(feature.TitleKey, UiText.Get(feature.TitleKey));
            Assert.Equal(3, feature.Steps.Count);
            Assert.All(feature.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step) || step.StartsWith("Tutorial.", StringComparison.Ordinal)));
            Assert.Contains(" · ", feature.DisplayName, StringComparison.Ordinal);
            if (language == "ko-KR")
            {
                Assert.All(feature.Steps, step =>
                {
                    Assert.Contains(step, character => character is >= '\uAC00' and <= '\uD7A3');
                    Assert.DoesNotContain("?", step, StringComparison.Ordinal);
                    Assert.DoesNotContain("\uFFFD", step, StringComparison.Ordinal);
                });
                Assert.Equal("기능 선택", UiText.Get("Tutorial.Select"));
                Assert.Equal("단축키: Ctrl+C", UiText.Format("Tutorial.CurrentKey", "Ctrl+C"));
                if (feature.UnassignedKey is not null)
                {
                    string fallback = UiText.Get(feature.UnassignedKey);
                    Assert.Contains(fallback, character => character is >= '\uAC00' and <= '\uD7A3');
                    Assert.DoesNotContain("?", fallback, StringComparison.Ordinal);
                }
            }
            foreach (string action in feature.HelpActions)
                Assert.Same(feature, TutorialCatalog.FindHelp(feature.CategoryKey, action));
        }
        Assert.Equal("image-copy", TutorialCatalog.FindHelp("Help.Annotation", "QuickAccess.Copy")!.Id);
        Assert.Equal("pin-copy", TutorialCatalog.FindHelp("Text_33EFD44366A7", "QuickAccess.Copy")!.Id);
        Assert.Equal("image-save", TutorialCatalog.FindHelp("Help.Annotation", "QuickAccess.Save")!.Id);
        Assert.Equal("pin-save", TutorialCatalog.FindHelp("Text_33EFD44366A7", "QuickAccess.Save")!.Id);
        Assert.Equal("pin-clipboard", TutorialCatalog.FindHelp("Text_33EFD44366A7", "Dialog.Close")!.Id);
        Assert.Equal("text-copy", TutorialCatalog.FindHelp("Help.Capture", "TextCapture_HelpToggle")!.Id);
        Assert.NotEqual(TutorialCatalog.Get("image-blur").Id, TutorialCatalog.Get("image-mosaic").Id);
    });

    [Fact]
    public void EveryTabRowKeepsDirectTextAndKeycapsAndHasItsOwnTutorialButton() => StaTestHost.Run(() =>
    {
        var help = new ShortcutHelpWindow();
        try
        {
            TabControl tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
            UniformGrid[] rows = tabs.Items.Cast<TabItem>().SelectMany(tab =>
                ((StackPanel)((ScrollViewer)tab.Content).Content).Children.OfType<UniformGrid>()).ToArray();
            Assert.Equal(42, rows.Length);
            foreach (UniformGrid row in rows)
            {
                Assert.Single(row.Children.OfType<TextBlock>());
                WrapPanel keycaps = Assert.Single(row.Children.OfType<WrapPanel>());
                Button tutorial = Assert.Single(keycaps.Children.OfType<Button>());
                Assert.Contains(TutorialCatalog.All, feature => feature.Id == Assert.IsType<string>(tutorial.Tag));
                Assert.True(tutorial.IsTabStop && tutorial.Focusable);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tutorial)));
            }
        }
        finally { help.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void GlobalShortcutsUseCurrentValuesAndUnassignedPinCommandsExplainAssignment(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var keys = new HotkeySettings { Capture = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4A),
            PasteToScreen = Hotkey.None, HideAllPins = Hotkey.None };
        Assert.Equal(UiText.Format("Tutorial.CurrentKey", "Ctrl+Alt+J"), TutorialCatalog.Get("capture-region").EntryText(keys));
        Assert.Equal(UiText.Get("Tutorial.pin-clipboard.Unassigned"), TutorialCatalog.Get("pin-clipboard").EntryText(keys));
        Assert.Equal(UiText.Get("Tutorial.pin-hide-all.Unassigned"), TutorialCatalog.Get("pin-hide-all").EntryText(keys));
        keys.PasteToScreen = new(HotkeyModifiers.Shift, 0x71);
        Assert.Equal(UiText.Format("Tutorial.CurrentKey", "Shift+F2"), TutorialCatalog.Get("pin-clipboard").EntryText(keys));
        Assert.Equal(UiText.Format("Tutorial.CurrentKey", ShortcutCatalog.Get(ShortcutScope.Annotation, "Copy").Hint),
            TutorialCatalog.Get("image-copy").EntryText(keys));
    });

    [Fact]
    public void ConstructorListsAllFeaturesWithoutLoadingAndSwitchLoadsOnlyTheSelectedGif() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("en-US");
        var paths = new ConcurrentQueue<string>();
        FeatureTutorialWindow window = Offscreen("image-blur", (path, _) => { paths.Enqueue(path); return Frames(); });
        try
        {
            Assert.Empty(paths);
            ComboBox choices = FirstRunWelcomeTests.Descendants(window).OfType<ComboBox>().Single();
            Assert.Equal(38, choices.Items.Count);
            Show(window);
            Assert.EndsWith("image-blur.en.gif", Assert.Single(paths), StringComparison.Ordinal);
            window.TogglePlayback();
            window.SelectFeature("pin-copy");
            WaitLoaded(window);
            Assert.Equal(2, paths.Count);
            Assert.EndsWith("pin-copy.en.gif", paths.Last(), StringComparison.Ordinal);
            Assert.Equal("pin-copy", window.SelectedFeatureId);
            Assert.True(window.HasFrames);
            window.Hide();
            Assert.False(window.HasFrames);
            Assert.False(window.IsPlaying);
            Assert.Null(FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single().Source);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RapidSwitchKeepsOneDecoderAndSkipsCanceledQueuedFeatures() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        using var enteredFirst = new ManualResetEventSlim();
        using var finishStale = new ManualResetEventSlim();
        using var staleReturned = new ManualResetEventSlim();
        var tokens = new ConcurrentQueue<CancellationToken>();
        var paths = new ConcurrentQueue<string>();
        int concurrent = 0, maximum = 0;
        FeatureTutorialWindow window = Offscreen("image-rectangle", (path, token) =>
        {
            int count = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, count);
            paths.Enqueue(path);
            try
            {
                if (path.Contains("image-mosaic", StringComparison.Ordinal)) return Frames();
                tokens.Enqueue(token);
                enteredFirst.Set();
                Assert.True(finishStale.Wait(TimeSpan.FromSeconds(5)));
                GifSampleFrames frames = Frames();
                staleReturned.Set();
                return frames;
            }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        try
        {
            window.Show(); Drain(window);
            Assert.True(enteredFirst.Wait(TimeSpan.FromSeconds(5)));
            window.SelectFeature("image-blur");
            window.SelectFeature("image-mosaic");
            Assert.Equal("image-mosaic", window.SelectedFeatureId);
            Assert.True(window.IsLoading);
            Assert.False(window.HasFrames);
            Assert.Single(paths);
            Assert.Single(tokens);
            Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
            finishStale.Set();
            Assert.True(staleReturned.Wait(TimeSpan.FromSeconds(5)));
            WaitLoaded(window);
            Assert.True(window.HasFrames);
            Assert.Equal(1, maximum);
            Assert.Equal(0, concurrent);
            Assert.Equal(2, paths.Count);
            Assert.DoesNotContain(paths, path => path.Contains("image-blur", StringComparison.Ordinal));
            Assert.EndsWith("image-mosaic.ko.gif", paths.Last(), StringComparison.Ordinal);
            Image preview = FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single();
            Assert.NotNull(preview.Source);
            window.Close();
            Drain(window);
            Assert.False(window.HasFrames);
            Assert.False(window.IsLoading);
            Assert.False(window.IsPlaying);
            Assert.Null(preview.Source);
        }
        finally { finishStale.Set(); window.Close(); }
    });

    [Fact]
    public void ClosingAndReopeningStillShareOneActualDecoder() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        int concurrent = 0, maximum = 0;
        FeatureTutorialWindow old = Offscreen("image-text", (_, _) =>
        {
            int count = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, count);
            try { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(5))); return Frames(); }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        int freshReads = 0;
        FeatureTutorialWindow fresh = Offscreen("pin-copy", (_, _) =>
        {
            int count = Interlocked.Increment(ref concurrent);
            maximum = Math.Max(maximum, count);
            try { Interlocked.Increment(ref freshReads); return Frames(); }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        try
        {
            old.Show(); Drain(old);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            old.Close();
            fresh.Show(); Drain(fresh);
            Assert.True(fresh.IsLoading);
            Assert.Equal(0, freshReads);
            finish.Set();
            WaitLoaded(fresh);
            Assert.Equal(1, maximum);
            Assert.Equal(1, freshReads);
            Assert.True(fresh.HasFrames);
            Assert.False(old.HasFrames);
            fresh.Close();
            Assert.False(fresh.HasFrames);
        }
        finally { finish.Set(); old.Close(); fresh.Close(); }
    });

    [Fact]
    public void ReusedTutorialTransfersOwnerAndOnlyNewOwnerVisibilityClosesIt() => StaTestHost.Run(() =>
    {
        static Window Owner() => new() { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        Window first = Owner(), second = Owner();
        try
        {
            first.Show(); second.Show();
            FeatureTutorialWindow.ShowTutorial(first, new HotkeySettings(), "image-text");
            FeatureTutorialWindow tutorial = Assert.Single(first.OwnedWindows.OfType<FeatureTutorialWindow>());
            FeatureTutorialWindow.ShowTutorial(second, new HotkeySettings(), "pin-copy");
            Assert.Same(tutorial, Assert.Single(second.OwnedWindows.OfType<FeatureTutorialWindow>()));
            Assert.Empty(first.OwnedWindows.OfType<FeatureTutorialWindow>());
            Assert.Same(second, tutorial.Owner);
            first.Hide();
            Assert.True(tutorial.IsVisible);
            second.Hide();
            Assert.False(tutorial.IsVisible);
            Assert.False(tutorial.HasFrames);
            Assert.False(tutorial.IsLoading);
        }
        finally { FeatureTutorialWindow.DismissForCapture(); first.Close(); second.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ClickThroughRecoveryUsesToggleThenHideTwiceThenTray(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        FeatureTutorial feature = TutorialCatalog.Get("pin-click-through");
        var keys = new HotkeySettings { ToggleClickThrough = new(HotkeyModifiers.Control, 0x4A),
            HideAllPins = new(HotkeyModifiers.Alt, 0x48) };
        Assert.Equal(UiText.Format("Tutorial.pin-click-through.Exit", "Ctrl+J"), feature.StepsFor(keys)[2]);
        keys.ToggleClickThrough = Hotkey.None;
        Assert.Equal(UiText.Format("Tutorial.pin-click-through.Exit", UiText.Format("Hotkey.Twice", "Alt+H")), feature.StepsFor(keys)[2]);
        keys.HideAllPins = Hotkey.None;
        Assert.Equal(UiText.Format("Tutorial.pin-click-through.Exit", UiText.Get("Hotkey.TrayRestore")), feature.StepsFor(keys)[2]);
    });

    [Fact]
    public void MissingGifAfterSwitchKeepsTheSelectedStaticStepsAndDropsPreviousFrames() => StaTestHost.Run(() =>
    {
        FeatureTutorialWindow window = Offscreen("image-text", (path, _) => path.Contains("pin-save", StringComparison.Ordinal)
            ? throw new IOException("fixture missing") : Frames());
        try
        {
            Show(window);
            Assert.True(window.HasFrames);
            window.SelectFeature("pin-save");
            WaitLoaded(window);
            Assert.False(window.HasFrames);
            Assert.False(window.IsPlaying);
            TextBlock[] texts = FirstRunWelcomeTests.Descendants(window).OfType<TextBlock>().ToArray();
            Assert.All(TutorialCatalog.Get("pin-save").Steps, step => Assert.Contains(texts, text => text.Text == step));
            Assert.Contains(texts, text => text.Text == UiText.Get("Tutorial.Unavailable") && text.IsVisible);
            Assert.Null(FirstRunWelcomeTests.Descendants(window).OfType<Image>().Single().Source);
        }
        finally { window.Close(); }
    });

    private static FeatureTutorialWindow Offscreen(string id, Func<string, CancellationToken, GifSampleFrames> decode) =>
        new(new HotkeySettings(), id, decode) { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
    private static void Show(FeatureTutorialWindow window) { window.Show(); Drain(window); WaitLoaded(window); }
    private static void Drain(FeatureTutorialWindow window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void WaitLoaded(FeatureTutorialWindow window)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (window.IsLoading && timeout.Elapsed < TimeSpan.FromSeconds(5)) { Drain(window); Thread.Sleep(1); }
        Assert.False(window.IsLoading, "Background STA decoder did not finish.");
    }
    private static GifSampleFrames Frames()
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        image.Freeze();
        return new GifSampleFrames([image, image], [100, 100], 1, 1);
    }
}
