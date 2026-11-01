using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    public void InContentActionsStayCompactAndPlaybackButtonsHaveEqualHeights(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        Action<string>? previous = FeatureTutorialWindow.OpenShortcutSetting;
        FeatureTutorialWindow.OpenShortcutSetting = _ => { };
        var window = new FeatureTutorialWindow(new HotkeySettings { ToggleClickThrough = Hotkey.None },
            "pin-click-through", () => new FakeTutorialMedia());
        try
        {
            window.Resources.MergedDictionaries.Add(SystemHighContrastThemeTests.LoadTheme());
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(672, 800));
            root.Arrange(new Rect(0, 0, 672, 800));
            Button[] buttons = FirstRunWelcomeTests.Descendants(root).OfType<Button>().ToArray();
            Button play = buttons.Single(button => Equals(button.Content, UiText.Get("GifPreview_Play")));
            Button replay = buttons.Single(button => Equals(button.Content, UiText.Get("TextCapture_TutorialReplay")));
            Button close = buttons.Single(button => Equals(button.Content, UiText.Get("Dialog.Close")));
            Assert.Contains(window.SetShortcutButton, buttons);
            // A compact bordered button is never taller than the 32-DIP plain button.
            Assert.Equal(32, window.SetShortcutButton.ActualHeight);
            Assert.Equal(32, play.ActualHeight);
            Assert.Equal(play.ActualHeight, replay.ActualHeight);
            Assert.Equal(play.TranslatePoint(default, root).Y, replay.TranslatePoint(default, root).Y);
            Assert.Equal(32, close.ActualHeight);
        }
        finally
        {
            window.Close();
            FeatureTutorialWindow.OpenShortcutSetting = previous;
        }
    });

    [Theory]
    [InlineData("ko-KR", "라이브러리")]
    [InlineData("en-US", "Library")]
    public void LibraryWorkflowsUseTheLibraryCategoryInHelpAndFeatureChoices(string language, string category) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(category, UiText.Get("Workspace.Library"));
        var window = new FeatureTutorialWindow(new HotkeySettings(), "folder-create", () => new FakeTutorialMedia());
        try
        {
            ComboBox choices = FirstRunWelcomeTests.Descendants(window).OfType<ComboBox>().Single();
            foreach (string id in new[] { "folder-create", "folder-rename", "library-move", "library-delete" })
            {
                FeatureTutorial feature = TutorialCatalog.Get(id);
                Assert.Equal("Workspace.Library", feature.CategoryKey);
                Assert.Equal($"{category} · {UiText.Get(feature.TitleKey)}", feature.DisplayName);
                Assert.All(feature.HelpActions, action =>
                    Assert.Same(feature, TutorialCatalog.FindHelp("Workspace.Library", action)));
                ComboBoxItem choice = Assert.Single(choices.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, id));
                Assert.Equal(feature.DisplayName, choice.Content);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CatalogHas45SeparateLocalizedWorkflowsAnd42CategoryQualifiedHelpMappings(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(45, TutorialCatalog.All.Count);
        Assert.Equal(45, TutorialCatalog.All.Select(feature => feature.Id).Distinct().Count());
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
                    Assert.Contains(step, character => character is >= '가' and <= '힣');
                    Assert.DoesNotContain("?", step, StringComparison.Ordinal);
                    Assert.DoesNotContain("�", step, StringComparison.Ordinal);
                });
                Assert.Equal("기능 선택", UiText.Get("Tutorial.Select"));
                Assert.Equal("단축키: Ctrl+C", UiText.Format("Tutorial.CurrentKey", "Ctrl+C"));
                if (feature.UnassignedKey is not null)
                {
                    string fallback = UiText.Get(feature.UnassignedKey);
                    Assert.Contains(fallback, character => character is >= '가' and <= '힣');
                    Assert.DoesNotContain("?", fallback, StringComparison.Ordinal);
                }
            }
            foreach (string action in feature.HelpActions)
                Assert.Same(feature, TutorialCatalog.FindHelp(feature.CategoryKey, action));
        }
        Assert.Equal("image-copy", TutorialCatalog.FindHelp("Help.Annotation", "EditorFlow_CopyAndClose")!.Id);
        Assert.Equal("pin-copy", TutorialCatalog.FindHelp("Text_33EFD44366A7", "QuickAccess.Copy")!.Id);
        Assert.Equal("image-save", TutorialCatalog.FindHelp("Help.Annotation", "QuickAccess.Save")!.Id);
        Assert.Equal("pin-save", TutorialCatalog.FindHelp("Text_33EFD44366A7", "QuickAccess.Save")!.Id);
        Assert.Equal("pin-clipboard", TutorialCatalog.FindHelp("Text_33EFD44366A7", "Dialog.Close")!.Id);
        Assert.Equal("text-copy", TutorialCatalog.FindHelp("Help.Capture", "TextCapture_HelpToggle")!.Id);
        Assert.NotEqual(TutorialCatalog.Get("image-blur").Id, TutorialCatalog.Get("image-mosaic").Id);
        Assert.Equal("image-mosaic", TutorialCatalog.FindHelp("Help.Annotation", "EditorObscure_Pixelate")!.Id);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void EveryTabRowKeepsDirectTextAndKeycapsAndExistingTutorialButtons(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var help = new ShortcutHelpWindow();
        help.Resources.MergedDictionaries.Add(SystemHighContrastThemeTests.LoadTheme());
        try
        {
            TabControl tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
            UniformGrid[] rows = tabs.Items.Cast<TabItem>().SelectMany(tab =>
                ((StackPanel)((ScrollViewer)tab.Content).Content).Children.OfType<UniformGrid>()).ToArray();
            SaveHelpEvidence(help, tabs, language);
            Assert.Equal(101, rows.Length);
            foreach (var (label, shortcut) in new[]
            {
                ("Shortcuts.Parent", "Alt+↑"),
                ("VideoFrame_Save", "Shift+S"),
                ("MediaExport_SaveEdits", "Ctrl+S"),
                ("MediaExport_ExportFormats", "Ctrl+Shift+S"),
            })
            {
                UniformGrid added = Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get(label));
                Assert.Equal(shortcut, AutomationProperties.GetName(Assert.Single(added.Children.OfType<WrapPanel>())));
            }
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Text_976A6C92B870"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("CaptureRatio_HelpLabel"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Pin_SelectText"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Gallery.Float"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("LibraryUx_KeepToggleName"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Stamp_Place"));
            Assert.Single(rows, row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("MediaInfo.Title"));
            foreach (UniformGrid row in rows)
            {
                DockPanel title = Assert.Single(row.Children.OfType<DockPanel>());
                TextBlock label = Assert.Single(title.Children.OfType<TextBlock>());
                WrapPanel keycaps = Assert.Single(row.Children.OfType<WrapPanel>());
                Assert.Empty(keycaps.Children.OfType<Button>());
                if (KoreanWordWrap.Strip(label.Text) == UiText.Get("MediaInfo.Title"))
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    Assert.Equal("Alt+Enter", AutomationProperties.GetName(keycaps));
                    continue;
                }
                if (KoreanWordWrap.Strip(label.Text) == UiText.Get("Text_976A6C92B870"))
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    Assert.Equal("Ctrl+C / " + UiText.Get("Text_C7011B8D345C"), AutomationProperties.GetName(keycaps));
                    continue;
                }
                if (KoreanWordWrap.Strip(label.Text) == UiText.Get("Pin_SelectText"))
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    Assert.Equal("F2", AutomationProperties.GetName(keycaps));
                    continue;
                }
                // The selector's ratio key has no tutorial animation yet; the row only names the key.
                if (KoreanWordWrap.Strip(label.Text) == UiText.Get("CaptureRatio_HelpLabel"))
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    Assert.Equal("R / Shift+R", AutomationProperties.GetName(keycaps));
                    continue;
                }
                if (KoreanWordWrap.Strip(label.Text) == UiText.Get("Gallery.Float") || KoreanWordWrap.Strip(label.Text) == UiText.Get("LibraryUx_KeepToggleName")
                    || KoreanWordWrap.Strip(label.Text) == UiText.Get("Stamp_Place"))
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    Assert.Equal(KoreanWordWrap.Strip(label.Text) == UiText.Get("Gallery.Float") ? "P" : KoreanWordWrap.Strip(label.Text) == UiText.Get("Stamp_Place") ? "S" : "K",
                        AutomationProperties.GetName(keycaps));
                    continue;
                }
                TabItem owner = tabs.Items.Cast<TabItem>().Single(tab =>
                    ((StackPanel)((ScrollViewer)tab.Content).Content).Children.Contains(row));
                FeatureTutorial? expected = TutorialCatalog.All.SingleOrDefault(feature =>
                    UiText.Get(feature.CategoryKey) == (string)owner.Header && feature.HelpActions.Any(action => UiText.Get(action) == KoreanWordWrap.Strip(label.Text)));
                if (expected is null)
                {
                    Assert.Empty(title.Children.OfType<Button>());
                    continue;
                }
                Button tutorial = Assert.Single(title.Children.OfType<Button>());
                FeatureTutorial feature = TutorialCatalog.Get(Assert.IsType<string>(tutorial.Tag));
                Assert.Same(expected, feature);
                Assert.True(tutorial.IsTabStop && tutorial.Focusable);
                Assert.Null(tutorial.Content);
                Assert.Same(help.FindResource("Button.Tutorial"), tutorial.Style);
                // The icon keeps the complete feature-specific name in accessibility and hover help.
                Assert.Equal(FeatureTutorialWindow.OpenButtonName(feature), AutomationProperties.GetName(tutorial));
                Assert.Equal(AutomationProperties.GetName(tutorial), tutorial.ToolTip);
                Assert.NotEqual(UiText.Get(feature.TitleKey), AutomationProperties.GetName(tutorial));
            }
            // The search results carry their own copy of every tutorial button.
            Button[] results = FirstRunWelcomeTests.Descendants(help).OfType<Button>()
                .Where(button => button.Tag is string && !rows.Any(row => FirstRunWelcomeTests.Descendants(row).Contains(button))).ToArray();
            Assert.Equal(42, results.Length);
            Assert.All(results, button => Assert.Equal(
                FeatureTutorialWindow.OpenButtonName(TutorialCatalog.Get((string)button.Tag)), AutomationProperties.GetName(button)));
        }
        finally { help.Close(); }
    });

    private static void SaveHelpEvidence(ShortcutHelpWindow help, TabControl tabs, string language)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_TUTORIAL_HELP_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        help.WindowStartupLocation = WindowStartupLocation.Manual;
        help.Left = -10000; help.Top = -10000; help.ShowActivated = false; help.ShowInTaskbar = false;
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("Help.Annotation")));
        help.Show();
        help.Dispatcher.Invoke(help.UpdateLayout, DispatcherPriority.ApplicationIdle);
        FirstRunWelcomeTests.Descendants(help).OfType<TextBox>().Single().Text = UiText.Get("EditorObscure_Pixelate");
        help.Dispatcher.Invoke(help.UpdateLayout, DispatcherPriority.ApplicationIdle);
        FrameworkElement snapshot = help;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(snapshot.ActualWidth), (int)Math.Ceiling(snapshot.ActualHeight),
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(snapshot);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"help-annotation-{language}.png"));
        encoder.Save(stream);
    }

    [Theory]
    [InlineData("ko-KR", "영역 캡처 사용법", "복사하고 닫기 사용법", "핀 · 저장 사용법", "텍스트 복사 사용법", "흐림 사용법 동영상")]
    [InlineData("en-US", "Region capture tutorial", "Copy and close tutorial", "Pins · Save tutorial", "Copy text tutorial", "Blur tutorial video")]
    public void TutorialNamesSayWhichFeatureTheyTeachAndQualifySharedTitles(string language, string region, string imageCopy,
        string pinSave, string textCopy, string blurVideo) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        Assert.Equal(region, FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get("capture-region")));
        Assert.Equal(imageCopy, FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get("image-copy")));
        Assert.Equal(pinSave, FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get("pin-save")));
        Assert.Equal(textCopy, FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get("text-copy")));
        Assert.Equal(TutorialCatalog.All.Count, TutorialCatalog.All.Select(FeatureTutorialWindow.TutorialTitle).Distinct().Count());

        var media = new FakeTutorialMedia();
        FeatureTutorialWindow window = Offscreen("image-blur", media, TutorialTestAssets.Create("image-blur", "text-copy"));
        try
        {
            Assert.Equal(FeatureTutorialWindow.TutorialTitle(TutorialCatalog.Get("image-blur")), window.Title);
            Assert.Equal(blurVideo, AutomationProperties.GetName(media.View));
            window.SelectFeature("text-copy");
            Assert.Equal(textCopy, window.Title);
            Assert.Equal(UiText.Get("TextCapture_TutorialAnimation"), AutomationProperties.GetName(media.View));
        }
        finally { window.Close(); }
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
    public void ConstructorListsAllFeaturesWithoutOpeningAndSwitchOpensOnlyTheSelectedVideo() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("en-US");
        var media = new FakeTutorialMedia();
        FeatureTutorialWindow window = Offscreen("image-blur", media, TutorialTestAssets.Create("image-blur", "pin-copy"));
        try
        {
            Assert.Empty(media.OpenedPaths);
            Assert.False(window.HasMedia);
            ComboBox choices = FirstRunWelcomeTests.Descendants(window).OfType<ComboBox>().Single();
            Assert.Equal(45, choices.Items.Count);
            Assert.Contains(media.View, FirstRunWelcomeTests.Descendants(window));
            Show(window);
            Assert.Equal(Path.Combine(window.AssetDirectory, "image-blur.en.mp4"), Assert.Single(media.OpenedPaths));
            Assert.True(window.HasMedia && window.HasFirstFrame);
            Assert.False(window.IsPlaying);
            window.TogglePlayback();
            Assert.True(window.IsPlaying && media.IsPlaying);
            window.SelectFeature("pin-copy");
            Assert.Equal(1, media.CloseCount);
            Assert.False(window.IsPlaying || window.HasMedia || window.HasFirstFrame);
            Drain(window);
            Assert.Equal(2, media.OpenedPaths.Count);
            Assert.EndsWith("pin-copy.en.mp4", media.OpenedPaths.Last(), StringComparison.Ordinal);
            Assert.Equal("pin-copy", window.SelectedFeatureId);
            Assert.True(window.HasFirstFrame);
            window.Hide();
            Assert.False(window.HasMedia || window.HasFirstFrame || window.IsPlaying);
            Assert.False(media.IsOpen);
            Assert.Equal(2, media.CloseCount);
            Show(window);
            Assert.Equal(3, media.OpenedPaths.Count);
            Assert.EndsWith("pin-copy.en.mp4", media.OpenedPaths.Last(), StringComparison.Ordinal);
            Assert.True(window.HasFirstFrame);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FirstFrameStillShowsBeforeTheVideoOpensAndFollowsEverySelection() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        var media = new FakeTutorialMedia { AutoOpen = false };
        string assets = TutorialTestAssets.Create("image-blur", "pin-copy", "image-text");
        TutorialTestAssets.AddStills(assets, "image-blur", "pin-copy");
        FeatureTutorialWindow window = Offscreen("image-blur", media, assets);
        try
        {
            Show(window);
            Assert.True(window.HasMedia);
            Assert.False(window.HasFirstFrame);
            Assert.True(window.HasPoster);
            Assert.Equal(Path.Combine(assets, "image-blur.ko.jpg"), window.PosterPath);
            Assert.False(LoadingTextVisible(window));
            media.RaiseOpened();
            Assert.True(window.HasFirstFrame && window.HasPoster);

            // The still follows the selection at once; the video opens only once it settles.
            window.SelectFeature("pin-copy");
            Assert.True(window.IsOpenPending);
            Assert.True(window.HasPoster);
            Assert.Equal(Path.Combine(assets, "pin-copy.ko.jpg"), window.PosterPath);
            Drain(window);
            Assert.EndsWith("pin-copy.ko.mp4", media.OpenedPaths.Last(), StringComparison.Ordinal);
            Assert.False(LoadingTextVisible(window));

            // Without a still the loading text shows until the video opens.
            window.SelectFeature("image-text");
            Assert.False(window.HasPoster);
            Drain(window);
            Assert.True(LoadingTextVisible(window));
            media.RaiseOpened();
            Assert.False(LoadingTextVisible(window));

            // A failure shows its message in place of the picture; a later selection brings the still back.
            window.SelectFeature("image-blur");
            Drain(window);
            Assert.True(window.HasPoster);
            media.RaiseFailed(new COMException("Media Foundation is not available", unchecked((int)0xC00D36E5)));
            Assert.False(window.HasPoster);
            AssertFallback(window, "Tutorial.Unavailable", TutorialCatalog.Get("image-blur").Steps);
            window.SelectFeature("pin-copy");
            Assert.True(window.HasPoster);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UnreadableStillIsLoggedAndTheVideoStillOpens() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("en-US");
        using var trace = new TraceCapture();
        var media = new FakeTutorialMedia { AutoOpen = false };
        string assets = TutorialTestAssets.Create("capture-scroll");
        File.WriteAllBytes(Path.Combine(assets, "capture-scroll.en.jpg"), [0xFF, 0xD8, 0x00]);
        FeatureTutorialWindow window = Offscreen("capture-scroll", media, assets);
        try
        {
            Show(window);
            Assert.False(window.HasPoster);
            Assert.True(LoadingTextVisible(window));
            Assert.Contains(trace.Lines, line => line.Contains("capture-scroll", StringComparison.Ordinal)
                && line.Contains(Path.Combine(assets, "capture-scroll.en.jpg"), StringComparison.Ordinal));
            Assert.EndsWith("capture-scroll.en.mp4", Assert.Single(media.OpenedPaths), StringComparison.Ordinal);
            media.RaiseOpened();
            Assert.True(window.HasFirstFrame);
            Assert.False(window.IsShowingFallback);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RapidSwitchOpensOnlyTheFeatureItStopsOn() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        var media = new FakeTutorialMedia();
        FeatureTutorialWindow window = Offscreen("image-rectangle", media, TutorialTestAssets.All());
        try
        {
            Show(window);
            window.SelectFeature("image-blur");
            window.SelectFeature("image-ellipse");
            window.SelectFeature("image-mosaic");
            Assert.Equal("image-mosaic", window.SelectedFeatureId);
            Assert.True(window.IsOpenPending);
            Assert.False(window.HasMedia);
            Assert.Single(media.OpenedPaths);
            Drain(window);
            Assert.Equal(2, media.OpenedPaths.Count);
            Assert.DoesNotContain(media.OpenedPaths, path => path.Contains("image-blur", StringComparison.Ordinal)
                || path.Contains("image-ellipse", StringComparison.Ordinal));
            Assert.EndsWith("image-mosaic.ko.mp4", media.OpenedPaths.Last(), StringComparison.Ordinal);
            Assert.True(window.HasFirstFrame);
            Assert.Equal(media.OpenedPaths[1], media.CurrentPath);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ClosingBeforeOrDuringOpenNeverReattachesOrPlays() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        string assets = TutorialTestAssets.Create("image-text");
        var pending = new FakeTutorialMedia();
        FeatureTutorialWindow beforeOpen = Offscreen("image-text", pending, assets);
        var opening = new FakeTutorialMedia { AutoOpen = false };
        FeatureTutorialWindow duringOpen = Offscreen("image-text", opening, assets);
        try
        {
            beforeOpen.Show();
            Assert.True(beforeOpen.IsOpenPending);
            beforeOpen.Close();
            Drain(beforeOpen);
            Assert.Empty(pending.OpenedPaths);
            Assert.True(pending.Disposed);

            Show(duringOpen);
            Assert.True(duringOpen.HasMedia);
            Assert.False(duringOpen.HasFirstFrame);
            duringOpen.Close();
            Assert.Equal(0, opening.Subscribers);
            opening.RaiseOpened();
            Drain(duringOpen);
            Assert.False(duringOpen.HasMedia || duringOpen.HasFirstFrame || duringOpen.IsPlaying);
            Assert.Equal(0, opening.PlayCount);
            Assert.True(opening.Disposed);
        }
        finally { beforeOpen.Close(); duringOpen.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    [Fact]
    public void ReusedTutorialTransfersOwnerAndOnlyNewOwnerVisibilityClosesIt() => StaTestHost.Run(() =>
    {
        static Window Owner() => new() { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        Func<ITutorialMedia>? previous = FeatureTutorialWindow.MediaFactoryOverrideForTest;
        var created = new List<FakeTutorialMedia>();
        FeatureTutorialWindow.MediaFactoryOverrideForTest = () => { var media = new FakeTutorialMedia(); created.Add(media); return media; };
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
            Assert.Same(second, tutorial.LifetimeOwner);
            Assert.Equal("pin-copy", tutorial.SelectedFeatureId);
            Assert.Single(created);
            first.Hide();
            Assert.True(tutorial.IsVisible);
            second.Hide();
            Assert.False(tutorial.IsVisible);
            Assert.False(tutorial.HasMedia || tutorial.IsOpenPending || tutorial.IsPlaying);
            Assert.True(created[0].Disposed);
            Assert.Null(tutorial.LifetimeOwner);
        }
        finally
        {
            FeatureTutorialWindow.DismissForCapture(); first.Close(); second.Close();
            FeatureTutorialWindow.MediaFactoryOverrideForTest = previous;
        }
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
    public void MissingVideoAfterSwitchIsLoggedAndKeepsTheSelectedStepsAsTheFallback() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        using var trace = new TraceCapture();
        var media = new FakeTutorialMedia();
        FeatureTutorialWindow window = Offscreen("image-text", media, TutorialTestAssets.Create("image-text"));
        try
        {
            Show(window);
            Assert.True(window.HasFirstFrame);
            window.SelectFeature("pin-save");
            Drain(window);
            Assert.Single(media.OpenedPaths);
            Assert.False(media.IsOpen);
            Assert.True(window.IsShowingFallback);
            Assert.False(window.HasMedia || window.HasFirstFrame || window.IsPlaying);
            AssertFallback(window, "Tutorial.Unavailable", TutorialCatalog.Get("pin-save").Steps);
            Assert.Contains(trace.Lines, line => line.Contains("pin-save", StringComparison.Ordinal)
                && line.Contains(Path.Combine(window.AssetDirectory, "pin-save.ko.mp4"), StringComparison.Ordinal));
            window.SelectFeature("image-text");
            Drain(window);
            Assert.False(window.IsShowingFallback);
            Assert.True(window.HasFirstFrame);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MediaFailureSuchAsAMissingMediaFeaturePackFallsBackToTheSteps() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("en-US");
        using var trace = new TraceCapture();
        var media = new FakeTutorialMedia { AutoOpen = false };
        FeatureTutorialWindow window = Offscreen("capture-region", media, TutorialTestAssets.Create("capture-region"));
        try
        {
            Show(window);
            Assert.True(window.HasMedia);
            media.RaiseFailed(new COMException("Media Foundation is not available", unchecked((int)0xC00D36E5)));
            Assert.True(window.IsShowingFallback);
            Assert.False(media.IsOpen || window.HasMedia);
            AssertFallback(window, "Tutorial.Unavailable", TutorialCatalog.Get("capture-region").Steps);
            Assert.Contains(trace.Lines, line => line.Contains("Media Foundation is not available", StringComparison.Ordinal));
            media.RaiseEnded();
            media.RaiseOpened();
            Assert.True(window.IsShowingFallback);
            Assert.False(window.HasFirstFrame || window.IsPlaying);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("open")]
    [InlineData("play")]
    [InlineData("pause")]
    [InlineData("rewind")]
    [InlineData("loop")]
    public void AnyMediaExceptionFallsBackWithoutThrowing(string call) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        var error = new InvalidOperationException("fixture media failure");
        var media = new FakeTutorialMedia { ThrowOnOpen = call == "open" ? error : null };
        FeatureTutorialWindow window = Offscreen("image-crop", media, TutorialTestAssets.Create("image-crop"));
        try
        {
            Show(window);
            if (call != "open")
            {
                Assert.True(window.HasFirstFrame);
                if (call is "pause" or "loop") window.TogglePlayback();
                media.ThrowOnPlay = call is "play" or "loop" ? error : null;
                media.ThrowOnPause = call == "pause" ? error : null;
                media.ThrowOnRewind = call == "rewind" ? error : null;
                media.ThrowOnClose = error; // A failing release is logged, never thrown.
                switch (call)
                {
                    case "play": window.TogglePlayback(); break;
                    case "pause": window.TogglePlayback(); break;
                    case "rewind": window.Replay(); break;
                    case "loop": media.RaiseEnded(); break;
                }
            }
            Assert.True(window.IsShowingFallback);
            Assert.False(window.HasMedia || window.IsPlaying);
            AssertFallback(window, "Tutorial.Unavailable", TutorialCatalog.Get("image-crop").Steps);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EndedVideoLoopsFromTheFirstFrameAndKeepsPlaying() => StaTestHost.Run(() =>
    {
        var media = new FakeTutorialMedia();
        FeatureTutorialWindow window = Offscreen("video-trim", media, TutorialTestAssets.Create("video-trim"));
        try
        {
            Show(window);
            window.TogglePlayback();
            int plays = media.PlayCount;
            media.RaiseEnded();
            Assert.Equal(1, media.RewindCount);
            Assert.Equal(plays + 1, media.PlayCount);
            Assert.True(window.IsPlaying && media.IsPlaying);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CloseDisposesTheMediaAndUnsubscribesFromMediaMotionAndOwner() => StaTestHost.Run(() =>
    {
        var owner = new Window { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        Func<ITutorialMedia>? previous = FeatureTutorialWindow.MediaFactoryOverrideForTest;
        var media = new FakeTutorialMedia();
        FeatureTutorialWindow.MediaFactoryOverrideForTest = () => media;
        try
        {
            owner.Show();
            FeatureTutorialWindow.ShowTutorial(owner, new HotkeySettings(), "pin-copy");
            FeatureTutorialWindow tutorial = Assert.Single(owner.OwnedWindows.OfType<FeatureTutorialWindow>());
            Drain(tutorial);
            Assert.True(tutorial.IsWatchingMotionPreference);
            Assert.Same(owner, tutorial.LifetimeOwner);
            Assert.Equal(3, media.Subscribers);
            tutorial.Close();
            Assert.True(media.Disposed);
            Assert.False(media.IsOpen);
            Assert.Equal(0, media.Subscribers);
            Assert.False(tutorial.IsWatchingMotionPreference);
            Assert.Null(tutorial.LifetimeOwner);
            owner.Hide();
            owner.Show();
            Assert.False(tutorial.IsVisible);
        }
        finally
        {
            FeatureTutorialWindow.DismissForCapture(); owner.Close();
            FeatureTutorialWindow.MediaFactoryOverrideForTest = previous;
        }
    });

    [Fact]
    public void DefaultVideoPathIsTheSelectedFeaturesMp4BesideTheApp() => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage("ko-KR");
        var window = new FeatureTutorialWindow(new HotkeySettings(), "library-move", () => new FakeTutorialMedia());
        try
        {
            Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(FeatureTutorialWindow).Assembly.Location)!,
                "Assets", "Tutorials", "library-move.ko.mp4"), window.AssetPath);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// The shipped build carries both languages' video and first-frame still for every feature and
    /// no GIF (Assets\Tutorials\*.mp4 and *.jpg are copied to the output by MyCapture.App.csproj).
    /// </summary>
    [Fact]
    public void EveryFeatureShipsANonEmptyKoreanAndEnglishVideoAndStillAndNoGif()
    {
        string tutorials = Path.Combine(Path.GetDirectoryName(typeof(FeatureTutorialWindow).Assembly.Location)!, "Assets", "Tutorials");
        Assert.Empty(TutorialAssetProblems(tutorials));
    }

    internal static IReadOnlyList<string> TutorialAssetProblems(string directory)
    {
        if (!Directory.Exists(directory)) return [$"missing folder {directory}"];
        var problems = new List<string>();
        foreach (FeatureTutorial feature in TutorialCatalog.All)
            foreach (string language in new[] { "ko", "en" })
            {
                var file = new FileInfo(Path.Combine(directory, $"{feature.Id}.{language}.mp4"));
                // These seven clips belong to the following video lane. Missing files use the tested still path.
                if (TutorialCatalog.PendingPracticeClips.Contains(feature.Id) && !file.Exists) continue;
                if (!file.Exists) problems.Add($"missing {file.Name}");
                else if (file.Length == 0) problems.Add($"empty {file.Name}");
                var still = new FileInfo(Path.ChangeExtension(file.FullName, ".jpg"));
                if (!still.Exists) { problems.Add($"missing {still.Name}"); continue; }
                using FileStream stream = still.OpenRead();
                BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                if (frame.PixelWidth != 960 || frame.PixelHeight != 540) problems.Add($"{still.Name} is {frame.PixelWidth}x{frame.PixelHeight}");
            }
        problems.AddRange(Directory.EnumerateFiles(directory, "*.gif").Select(path => $"leftover {Path.GetFileName(path)}"));
        return problems;
    }

    private static void AssertFallback(FeatureTutorialWindow window, string statusKey, IEnumerable<string> steps)
    {
        TextBlock[] texts = FirstRunWelcomeTests.Descendants(window).OfType<TextBlock>().ToArray();
        Assert.All(steps, step => Assert.Contains(texts, text => KoreanWordWrap.Strip(text.Text) == step && text.IsVisible));
        Assert.Contains(texts, text => KoreanWordWrap.Strip(text.Text) == UiText.Get(statusKey) && text.IsVisible);
        Button[] buttons = FirstRunWelcomeTests.Descendants(window).OfType<Button>().ToArray();
        Assert.False(buttons.Single(button => Equals(button.Content, UiText.Get("GifPreview_Play"))).IsEnabled);
        Assert.False(buttons.Single(button => Equals(button.Content, UiText.Get("TextCapture_TutorialReplay"))).IsEnabled);
    }

    private static bool LoadingTextVisible(FeatureTutorialWindow window) => FirstRunWelcomeTests.Descendants(window).OfType<TextBlock>()
        .Any(text => KoreanWordWrap.Strip(text.Text) == UiText.Get("TextCapture_TutorialLoading") && text.IsVisible);

    private static FeatureTutorialWindow Offscreen(string id, FakeTutorialMedia media, string assets) =>
        new(new HotkeySettings(), id, () => media) { AssetDirectory = assets, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
    private static void Show(FeatureTutorialWindow window) { window.Show(); Drain(window); }
    private static void Drain(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
