using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Shortcut captions use the caption size (12), including key combinations, and their
/// shortcut line fits the video editor's command rows and the compact receipt's fixed buttons.
/// </summary>
public sealed class ShortcutCaptionTests(ITestOutputHelper output)
{
    [Fact]
    public void CaptionSizeIsTheCaptionToken()
    {
        using Stream source = typeof(ShortcutCaptionTests).Assembly.GetManifestResourceStream("ThemeFixture.Tokens.xaml")!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement token = XDocument.Load(source).Root!.Elements().Single(element => (string?)element.Attribute(x + "Key") == "FontSize.Caption");
        double caption = double.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ShortcutPresentation.CaptionFontSize, caption);
        Assert.Equal(12, caption);
    }

    [Theory]
    [InlineData("Annotation", "Undo")]
    [InlineData("Annotation", "Copy")]
    [InlineData("Annotation", "Rectangle")]
    [InlineData("Annotation", "Done")]
    [InlineData("Annotation", "Privacy")]
    [InlineData("Video", "Play")]
    [InlineData("Video", "PreviousFrame")]
    [InlineData("Video", "Undo")]
    [InlineData("Video", "AddText")]
    [InlineData("Video", "ZoomIn")]
    [InlineData("Video", "ZoomOut")]
    [InlineData("QuickAccess", "Copy")]
    public void SingleKeysAndCombinationsUseTheSameReservedCaptionLine(string scopeName, string command) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        ShortcutScope scope = Enum.Parse<ShortcutScope>(scopeName);
        ShortcutDefinition definition = ShortcutCatalog.Get(scope, command);
        Assert.True(ShortcutPresentation.ShowsCaption(definition));
        var button = new Button { Content = "Command", ToolTip = "Runs the command" };
        ShortcutPresentation.Attach(button, scope, command);
        // Every key stays discoverable: the tooltip names it and UI Automation reads it.
        Assert.Equal($"Runs the command ({definition.Hint})", button.ToolTip);
        Assert.Equal(definition.Hint, AutomationProperties.GetAcceleratorKey(button));
        Grid face = Assert.IsType<Grid>(button.Content);
        Assert.Equal("CommandButtonFace", face.Name);
        Assert.Equal(2, face.RowDefinitions.Count);
        Assert.Equal(new GridLength(20), face.RowDefinitions[0].Height);
        Assert.Equal(new GridLength(14), face.RowDefinitions[1].Height);
        Assert.Equal("Command", Assert.IsType<TextBlock>(face.Children[0]).Text);
        TextBlock hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
        Assert.Equal(1, Grid.GetRow(hint));
        Assert.Equal(definition.Hint, hint.Text);
        Assert.Equal(12, hint.FontSize);
        Assert.Equal(FontWeights.Normal, hint.FontWeight);
        ShortcutMotionTestScope.AssertState(hint, false);
        face.Measure(new Size(200, 44));
        face.Arrange(new Rect(face.DesiredSize));
        Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight);
        ShortcutPresentation.SetAltRevealed(button, true);
        face.Measure(new Size(200, 44));
        face.Arrange(new Rect(face.DesiredSize));
        ShortcutMotionTestScope.AssertState(hint, true);
        Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight);
        ShortcutPresentation.SetAltRevealed(button, false);
        face.Measure(new Size(200, 44));
        face.Arrange(new Rect(face.DesiredSize));
        ShortcutMotionTestScope.AssertState(hint, false);
        Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight);
    });

    [Fact]
    public void ToolRailCornerLettersReadAt12WithoutCoveringTheGlyph() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var glyph = new Border { Width = 20, Height = 20 };
        var button = new ToggleButton { Content = glyph, Width = 40, Height = 38, Padding = new Thickness(2) };
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Rectangle", corner: true);
        var grid = Assert.IsType<Grid>(button.Content);
        TextBlock letter = grid.Children.OfType<TextBlock>().Single();
        Assert.Equal(12, letter.FontSize);
        ShortcutMotionTestScope.AssertState(letter, false);
        ShortcutPresentation.SetAltRevealed(button, true);
        ShortcutMotionTestScope.AssertState(letter, true);
        grid.Measure(new Size(32, 32));
        grid.Arrange(new Rect(0, 0, 32, 32));
        Rect glyphBounds = glyph.TransformToAncestor(grid).TransformBounds(new Rect(glyph.RenderSize));
        Rect letterBounds = letter.TransformToAncestor(grid).TransformBounds(new Rect(letter.RenderSize));
        Assert.False(glyphBounds.IntersectsWith(letterBounds), $"letter {letterBounds} covers glyph {glyphBounds}");
    });

    [Theory]
    [InlineData(false, 44)]
    [InlineData(true, 36)]
    public void AlwaysShownCaptionsUseTheSameFixedHeightAndRowsAsAlt(bool compact, double expectedHeight) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        bool alwaysShow = false;
        try
        {
            ShortcutPresentation.ReadEnabled = () => alwaysShow;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Resources = SystemHighContrastThemeTests.LoadTheme(),
            };
            var button = new Button { Content = "Privacy" };
            ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Privacy");
            CommandButtonPresentation.Apply(button, compact);
            row.Children.Add(button);
            Grid face = Assert.IsType<Grid>(button.Content);
            TextBlock caption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
            Size? restingSize = null;
            Rect? altCaptionBounds = null;
            foreach ((bool preference, bool alt) in new[]
            {
                (false, false), (false, true), (true, false), (true, true), (true, false), (false, false),
            })
            {
                alwaysShow = preference;
                ShortcutPresentation.SetAltRevealed(row, alt);
                ShortcutPresentation.Refresh();
                row.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                row.Measure(new Size(1000, 100));
                row.Arrange(new Rect(row.DesiredSize));
                bool visible = preference || alt;
                Assert.Equal(expectedHeight, button.ActualHeight, 2);
                if (restingSize is { } size) Assert.Equal(size, button.RenderSize);
                else restingSize = button.RenderSize;
                ShortcutMotionTestScope.AssertState(caption, visible);
                Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight, 2);
                if (!visible) continue;
                Rect bounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5
                    && bounds.Right <= button.ActualWidth + 0.5 && bounds.Bottom <= button.ActualHeight + 0.5);
                if (altCaptionBounds is { } expectedBounds) Assert.Equal(expectedBounds, bounds);
                else altCaptionBounds = bounds;
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void DynamicShortcutWidthsFollowTheLoadedFontAndShrinkWhenTheGestureChanges() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new Window
        {
            Width = 600, Height = 160, Left = -4000, Top = -4000,
            ShowInTaskbar = false, ShowActivated = false, FontFamily = new FontFamily("Courier New"),
        };
        int dpiNotifications = 0;
        window.DpiChanged += (_, _) => dpiNotifications++;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            const string longGesture = "Ctrl+Shift+Alt+F12";
            var name = new TextBlock { Text = "Run" };
            var button = new Button { Content = name };
            CommandButtonPresentation.Apply(button);
            button.Height = 44;
            AutomationProperties.SetAcceleratorKey(button, longGesture);
            CommandButtonPresentation.RefreshHint(button);
            Grid face = Assert.IsType<Grid>(button.Content);
            double beforeInheritance = face.MinWidth;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(button);
            window.Content = row;
            window.Show();
            window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
            TextBlock loadedCaption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
            Assert.True(button.IsLoaded);
            Assert.Equal(window.FontFamily, loadedCaption.FontFamily);
            Assert.True(Math.Abs(beforeInheritance - face.MinWidth) > 0.5,
                "Loaded must remeasure the shortcut after the window's font is inherited.");
            double nativeScale = PresentationSource.FromVisual(button)!.CompositionTarget!.TransformToDevice.M11;
            foreach (double scale in new[] { nativeScale, 1.5, 2, nativeScale })
            {
                // Simulate a monitor move on the same loaded window. The production DPI
                // observer must refresh the reserved width without a manual RefreshHint.
                double previousScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                int previousNotifications = dpiNotifications;
                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                bool visualChangeRaisedEvent = dpiNotifications > previousNotifications;
                if (previousScale != scale)
                    Assert.True(visualChangeRaisedEvent, $"DPI change from {previousScale} to {scale} must notify the window.");
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                TextBlock scaledCaption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(scaledCaption).DpiScaleX);
                double longWidth = face.MinWidth;
                Rect? originalBounds = null;
                // Removing metadata preserves the caption, including its inherited DPI.
                string[] gestures = [longGesture, "I", string.Empty, longGesture];

                foreach (string gesture in gestures)
                {
                    // The first long gesture is already assigned, so it specifically tests
                    // the automatic DPI refresh before testing subsequent dynamic changes.
                    if (originalBounds is not null)
                    {
                        AutomationProperties.SetAcceleratorKey(button, gesture);
                        CommandButtonPresentation.RefreshHint(button);
                    }
                    Assert.Same(face, button.Content);
                    Assert.Equal(2, face.Children.Count);
                    Assert.Same(loadedCaption, face.Children[1]);
                    if (gesture.Length == 0)
                    {
                        Assert.Empty(loadedCaption.Text);
                        Assert.Equal(0, face.MinWidth);
                        Assert.Empty(AutomationProperties.GetName(loadedCaption));
                        Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(loadedCaption));
                        Assert.False(loadedCaption.IsHitTestVisible);
                    }
                    else if (gesture == "I") Assert.True(face.MinWidth > 0 && face.MinWidth < longWidth);
                    else
                    {
                        TextBlock currentCaption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                        Assert.True(longWidth.Equals(face.MinWidth),
                            $"scale={scale}, stage={(originalBounds is null ? "first" : "restore")}, gesture='{gesture}', "
                            + $"expected MinWidth={longWidth}, actual MinWidth={face.MinWidth}, "
                            + $"hintDpi={VisualTreeHelper.GetDpi(currentCaption).DpiScaleX}, faceDpi={VisualTreeHelper.GetDpi(face).DpiScaleX}, "
                            + $"hintLoaded={currentCaption.IsLoaded}, buttonLoaded={button.IsLoaded}, "
                            + $"sourceScale={PresentationSource.FromVisual(currentCaption)?.CompositionTarget?.TransformToDevice.M11}.");
                    }

                    Rect? restingBounds = null;
                    foreach (bool alt in new[] { false, true, false })
                    {
                        ShortcutPresentation.SetAltRevealed(window, alt);
                        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                        Rect bounds = button.TransformToAncestor(row).TransformBounds(new Rect(button.RenderSize));
                        if (restingBounds is { } resting) Assert.Equal(resting, bounds);
                        else restingBounds = bounds;
                        Assert.True(name.IsVisible);
                        Assert.Equal("Run", name.Text);
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= row.ActualWidth + 0.5);
                        TextBlock caption = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                        Assert.Equal(gesture, caption.Text);
                        ShortcutMotionTestScope.AssertState(caption, alt);
                        if (gesture.Length == 0) continue;
                        if (!alt) continue;
                        double reservedRemainder = face.MinWidth - caption.DesiredSize.Width;
                        string measurement = $"MinWidth={face.MinWidth}, DesiredSize={caption.DesiredSize.Width}, "
                            + $"RenderSize={caption.RenderSize.Width}, faceActual={face.ActualWidth}, buttonActual={button.ActualWidth}, "
                            + $"sourceScale={PresentationSource.FromVisual(caption)?.CompositionTarget?.TransformToDevice.M11}";
                        Assert.True(reservedRemainder >= 0 && reservedRemainder <= 1.5,
                            $"scale={scale}, gesture='{gesture}', {measurement}, "
                            + $"actualDpi={VisualTreeHelper.GetDpi(caption).DpiScaleX}, remainder={reservedRemainder}; "
                            + $"DPI events={dpiNotifications}, SetRootDpi raised event={visualChangeRaisedEvent}; expected remainder 0..1.5.");
                        Rect captionBounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                        Assert.True(captionBounds.Left >= -0.5 && captionBounds.Right <= button.ActualWidth + 0.5);
                    }
                    if (gesture == longGesture)
                    {
                        if (originalBounds is { } expected) Assert.Equal(expected, restingBounds!.Value);
                        else originalBounds = restingBounds;
                    }
                    else Assert.True(restingBounds!.Value.Width < originalBounds!.Value.Width);
                }
            }
        }
        finally
        {
            window.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void LabelsThatAlreadyNameTheirKeyAreNotRepeated() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var zoom = new Button { ToolTip = "Zoom in detailed timeline (Ctrl+Shift+-)" };
        ShortcutPresentation.Attach(zoom, ShortcutScope.Video, "ZoomOut");
        Assert.Equal("Zoom in detailed timeline (Ctrl+Shift+-)", zoom.ToolTip);
        var delete = new Button { Content = new Border(), ToolTip = "Delete selected timed text (Delete)" };
        ShortcutPresentation.Attach(delete, ShortcutScope.Video, "DeleteLayer");
        Assert.Equal("Delete selected timed text (Delete)", delete.ToolTip);
    });

    [Theory]
    [InlineData("ko-KR", 760, 555)]
    [InlineData("ko-KR", 920, 680)]
    [InlineData("en-US", 760, 555)]
    [InlineData("en-US", 920, 680)]
    public void VideoEditorCaptionsReadAt12AndFitTheCommandRows(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-captions-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            editor.SetDetailsExpandedForTest(true);
            editor.UpdateLayout();
            var layout = (Grid)editor.Content;
            Assert.All(Descendants(layout).OfType<TextBlock>().Where(text =>
                text.Parent is Grid { Name: "CommandButtonFace" } && Grid.GetRow(text) == 1),
                caption => ShortcutMotionTestScope.AssertState(caption, false));
            ShortcutPresentation.SetAltRevealed(editor, true);
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            Grid bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            Grid transport = Assert.Single(Descendants(layout).OfType<Grid>(), panel => panel.Name == "VideoTransport");
            StackPanel precise = Assert.Single(Descendants(layout).OfType<StackPanel>(), panel => panel.Name == "VideoPreciseTransport");
            int captions = 0;
            foreach (FrameworkElement row in new FrameworkElement[] { bar, transport, precise })
            {
                foreach (ButtonBase button in Descendants(row).OfType<ButtonBase>().Where(button => button.IsVisible))
                {
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    if (string.IsNullOrEmpty(key)) continue;
                    TextBlock[] keyTexts = Descendants(button).OfType<TextBlock>().Where(text => text.Text == key).ToArray();
                    Assert.Contains(key, Assert.IsType<string>(button.ToolTip));
                    Assert.Single(keyTexts);
                    Assert.True(keyTexts[0].IsVisible);

                    foreach (TextBlock caption in keyTexts.Where(text => text.IsVisible))
                    {
                        captions++;
                        Assert.Equal(12, caption.FontSize);
                        string name = $"{language} {width}: '{caption.Text}' on {AutomationProperties.GetName(button)}";
                        Assert.True(caption.ActualWidth + 0.5 >= caption.DesiredSize.Width, $"{name} is clipped");
                        Assert.True(caption.ActualHeight < caption.FontSize * 2, $"{name} wraps");
                        Rect bounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                        Assert.True(bounds.Left >= -0.5 && bounds.Right <= button.ActualWidth + 0.5
                            && bounds.Top >= -0.5 && bounds.Bottom <= button.ActualHeight + 0.5, $"{name} leaves its button: {bounds}");
                    }
                }
            }

            Assert.Equal(14, captions); // Four editing commands, Save, Export, Save frame, Play, four seek commands and two frame steps.
            foreach (var (nameKey, key) in new[]
            {
                ("MediaExport_SaveEdits", "Ctrl+S"),
                ("MediaExport_ExportFormats", "Ctrl+Shift+S"),
            })
            {
                Button output = Assert.Single(Descendants(bar).OfType<Button>(), button =>
                    AutomationProperties.GetName(button) == UiText.Get(nameKey));
                Assert.True(output.IsVisible);
                Assert.Equal(key, AutomationProperties.GetAcceleratorKey(output));
            }
            // Trim stays visible; secondary tools remain available from the compact overflow.
            Assert.Contains(Descendants(bar).OfType<Button>(), button => button.IsVisible
                && AutomationProperties.GetName(button) == UiText.Get("VideoEditorUx_Trim"));
            Button addText = Assert.Single(Descendants(bar).OfType<Button>(), button =>
                AutomationProperties.GetName(button) == UiText.Get("VideoEditorUx_AddText"));
            if (!addText.IsVisible)
            {
                Button more = Assert.Single(Descendants(bar).OfType<Button>(), button => button.Name == "VideoMoreTools");
                Assert.True(more.IsVisible);
                ContextMenu menu = Assert.IsType<ContextMenu>(more.ContextMenu);
                Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Visibility == Visibility.Visible
                    && AutomationProperties.GetName(item) == UiText.Get("VideoEditorUx_AddText")
                    && item.InputGestureText == ShortcutCatalog.Get(ShortcutScope.Video, "AddText").Hint);
            }
            // The frame steps say what they do, and keep their keys in the tooltip.
            foreach (var (nameKey, labelKey, key) in new[]
            {
                ("Text_B39342508541", "VideoEditorUx_PreviousFrameShort", ","),
                ("Text_B753165F6585", "VideoEditorUx_NextFrameShort", "."),
            })
            {
                Button step = Assert.Single(Descendants(precise).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(nameKey));
                Assert.Contains(Descendants(step).OfType<TextBlock>(), text => text.IsVisible && text.Text == UiText.Get(labelKey));
                Assert.Equal(key, AutomationProperties.GetAcceleratorKey(step));
                Assert.Contains(key, (string)step.ToolTip, StringComparison.Ordinal);
            }

            // The time readout and Details at the right never overlap in the transport row.
            Border time = Assert.Single(Descendants(transport).OfType<Border>(), item => item.Name == "VideoPositionChip");
            ToggleButton details = Assert.Single(Descendants(transport).OfType<ToggleButton>(), item => item.Name == "VideoDetailsToggle");
            double timeRight = time.TranslatePoint(new Point(time.ActualWidth, 0), transport).X;
            double detailsLeft = details.TranslatePoint(default, transport).X;
            Assert.True(detailsLeft >= timeRight, $"{language} {width}: details at {detailsLeft}, time ends at {timeRight}");
            Assert.True(details.TranslatePoint(new Point(details.ActualWidth, 0), transport).X <= transport.ActualWidth + 0.5);
            output.WriteLine($"Video captions verified: {language}, {width}x{height}, {captions} captions at 12 DIP; "
                + "all fit, wrapping, visibility, shortcut and transport overlap assertions completed.");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void QuickAccessKeysRemainDiscoverableWhenVisibleCaptionPreferenceChanges(string language) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        bool enabled = true;
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            ShortcutPresentation.ReadEnabled = () => enabled;
            ShortcutPresentation.Refresh();
            card.IsKept = true;
            Border host = QuickAccessDetachedFixture.Layout(card);
            Assert.False(card.CanActivateByHotkey);
            IReadOnlyList<Button> actions = card.ActionButtonsForTest;
            Assert.Equal(5, actions.Count);
            // The detached layout needs the same Loaded hook that attaches raw accelerator captions in a real window.
            foreach (Button button in actions)
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, button));
            foreach (bool show in new[] { true, false, true })
            {
                enabled = show;
                ShortcutPresentation.Refresh();
                host.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                host.Measure(new Size(card.Width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, card.Width, host.DesiredSize.Height));
                foreach (Button button in actions)
                {
                    // Each action names itself under its icon, and Alt captions never change its height.
                    Assert.Equal(QuickAccessLabelFixture.ExpectedActionHeight(button), button.ActualHeight, 2);
                    Assert.True(button.ActualHeight > 36);
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    Assert.False(string.IsNullOrWhiteSpace(key));
                    Assert.Equal(AutomationProperties.GetName(button) + "  " + key, button.ToolTip);
                    TextBlock caption = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == key);
                    ShortcutMotionTestScope.AssertState(caption, show);
                    if (!show) continue;
                    Rect bounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                    Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5
                        && bounds.Right <= button.ActualWidth + 0.5 && bounds.Bottom <= button.ActualHeight + 0.5);
                }
            }
        }
        finally
        {
            card.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });
}
