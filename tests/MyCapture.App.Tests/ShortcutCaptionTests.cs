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
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Shortcut captions use the caption size (12), including key combinations, and their
/// reserved line fits the video editor's command rows and the quick access card.
/// </summary>
public sealed class ShortcutCaptionTests
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
        Assert.Equal(new GridLength(14), face.RowDefinitions[1].Height);
        Assert.Equal("Command", Assert.IsType<TextBlock>(face.Children[0]).Text);
        TextBlock hint = Assert.IsType<TextBlock>(face.Children[1]);
        Assert.Equal(1, Grid.GetRow(hint));
        Assert.Equal(definition.Hint, hint.Text);
        Assert.Equal(12, hint.FontSize);
        Assert.Equal(FontWeights.Normal, hint.FontWeight);
    });

    [Fact]
    public void ToolRailCornerLettersReadAt12WithoutCoveringTheGlyph() => StaTestHost.Run(() =>
    {
        var glyph = new Border { Width = 20, Height = 20 };
        var button = new ToggleButton { Content = glyph, Width = 40, Height = 38, Padding = new Thickness(2) };
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Rectangle", corner: true);
        var grid = Assert.IsType<Grid>(button.Content);
        TextBlock letter = grid.Children.OfType<TextBlock>().Single();
        Assert.Equal(12, letter.FontSize);
        grid.Measure(new Size(32, 32));
        grid.Arrange(new Rect(0, 0, 32, 32));
        Point top = letter.TranslatePoint(default, grid);
        Assert.True(top.Y >= glyph.ActualHeight - 0.5, $"letter at {top.Y}, glyph ends at {glyph.ActualHeight}");
    });

    [Fact]
    public void LabelsThatAlreadyNameTheirKeyAreNotRepeated() => StaTestHost.Run(() =>
    {
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
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-captions-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            editor.SetDetailsExpandedForTest(true);
            editor.UpdateLayout();
            var layout = (Grid)editor.Content;
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
                    ShortcutDefinition definition = ShortcutCatalog.All.First(item => item.Scope == ShortcutScope.Video && item.Hint == key);
                    TextBlock[] keyTexts = Descendants(button).OfType<TextBlock>().Where(text => text.Text == key).ToArray();
                    Assert.Contains(key, Assert.IsType<string>(button.ToolTip));
                    if (!ReferenceEquals(row, precise) || !ShortcutPresentation.ShowsCaption(definition))
                    {
                        Assert.Empty(keyTexts);
                        continue;
                    }

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

            Assert.Equal(6, captions); // Four seek commands and two frame steps retain visible key captions.
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
    public void QuickAccessKeyCaptionsReadAt12AndFitTheirButtons(string language) => StaTestHost.Run(() =>
    {
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
            Ocr410Fixture.LoadTheme(card);
            card.Show();
            card.UpdateLayout();
            Assert.False(card.CanActivateByHotkey);
            IReadOnlyList<Button> actions = card.ActionButtonsForTest;
            Assert.Equal(5, actions.Count);
            foreach (bool show in new[] { true, false, true })
            {
                enabled = show;
                ShortcutPresentation.Refresh();
                card.UpdateLayout();
                foreach (Button button in actions)
                {
                    Assert.Equal(44, button.ActualHeight, 2);
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    TextBlock caption = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == key);
                    Assert.Equal(12, caption.FontSize);
                    Assert.Equal(show ? Visibility.Visible : Visibility.Collapsed, caption.Visibility);
                    if (show)
                    {
                        Assert.True(caption.ActualWidth + 0.5 >= caption.DesiredSize.Width, $"'{key}' is clipped");
                        Rect bounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                        Assert.True(bounds.Bottom <= button.ActualHeight + 0.5, $"'{key}' leaves its button: {bounds} in {button.ActualHeight}");
                    }
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
