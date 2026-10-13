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
/// Always-visible key captions read at the caption size (12), key combinations live in the
/// tooltip only, and the larger captions still fit the video editor's command rows and
/// the quick access card.
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
    [InlineData("Annotation", "Undo", false)]
    [InlineData("Annotation", "Copy", false)]
    [InlineData("Annotation", "Rectangle", true)]
    [InlineData("Annotation", "Done", false)]
    [InlineData("Annotation", "Privacy", false)]
    [InlineData("Video", "Play", true)]
    [InlineData("Video", "PreviousFrame", true)]
    [InlineData("Video", "Undo", false)]
    [InlineData("Video", "AddText", false)]
    [InlineData("Video", "ZoomIn", false)]
    [InlineData("Video", "ZoomOut", false)]
    [InlineData("QuickAccess", "Copy", true)]
    public void OnlyShortKeysArePrintedUnderTheirCommand(string scopeName, string command, bool caption) => StaTestHost.Run(() =>
    {
        ShortcutScope scope = Enum.Parse<ShortcutScope>(scopeName);
        ShortcutDefinition definition = ShortcutCatalog.Get(scope, command);
        Assert.Equal(caption, ShortcutPresentation.ShowsCaption(definition));
        var button = new Button { Content = "Command", ToolTip = "Runs the command" };
        ShortcutPresentation.Attach(button, scope, command);
        // Every key stays discoverable: the tooltip names it and UI Automation reads it.
        Assert.Equal($"Runs the command ({definition.Hint})", button.ToolTip);
        Assert.Equal(definition.Hint, AutomationProperties.GetAcceleratorKey(button));
        if (caption)
        {
            TextBlock hint = Assert.IsType<StackPanel>(button.Content).Children.OfType<TextBlock>().Last();
            Assert.Equal(definition.Hint, hint.Text);
            Assert.Equal(12, hint.FontSize);
            Assert.Equal(FontWeights.Normal, hint.FontWeight);
        }
        else
        {
            Assert.Equal("Command", button.Content);
        }
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
            var layout = (Grid)editor.Content;
            Grid bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            StackPanel transport = Assert.Single(Descendants(layout).OfType<StackPanel>(), panel => panel.Name == "VideoTransport");
            StackPanel view = Assert.Single(Descendants(layout).OfType<StackPanel>(), panel => panel.Name == "VideoTimelineViewTools");
            int captions = 0;
            foreach (FrameworkElement row in new FrameworkElement[] { bar, transport, view })
            {
                foreach (ButtonBase button in Descendants(row).OfType<ButtonBase>().Where(button => button.IsVisible))
                {
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    if (string.IsNullOrEmpty(key)) continue;
                    ShortcutDefinition definition = ShortcutCatalog.All.First(item => item.Scope == ShortcutScope.Video && item.Hint == key);
                    TextBlock[] keyTexts = Descendants(button).OfType<TextBlock>().Where(text => text.Text == key).ToArray();
                    if (!ShortcutPresentation.ShowsCaption(definition))
                    {
                        Assert.Empty(keyTexts);
                        continue;
                    }

                    foreach (TextBlock caption in Descendants(button).OfType<TextBlock>().Where(text => text.IsVisible && text.FontSize <= 12.01))
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

            Assert.True(captions >= 8, $"{captions} captions");
            // The larger captions never cost the bar its trim and add-text tools.
            Assert.True(editor.CommandBarLevelForTest <= 3, $"{language} {width}: bar level {editor.CommandBarLevelForTest}");
            // The frame steps say what they do, and keep their keys in the tooltip.
            foreach (var (nameKey, labelKey, key) in new[]
            {
                ("Text_B39342508541", "VideoEditorUx_PreviousFrameShort", ","),
                ("Text_B753165F6585", "VideoEditorUx_NextFrameShort", "."),
            })
            {
                Button step = Assert.Single(Descendants(transport).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(nameKey));
                Assert.Contains(Descendants(step).OfType<TextBlock>(), text => text.IsVisible && text.Text == UiText.Get(labelKey));
                Assert.Equal(key, AutomationProperties.GetAcceleratorKey(step));
                Assert.Contains(key, (string)step.ToolTip, StringComparison.Ordinal);
            }

            // The centred transport and the timeline tools at the right never overlap.
            var controlRow = (Grid)VisualTreeHelper.GetParent(transport);
            double transportRight = transport.TranslatePoint(new Point(transport.ActualWidth, 0), controlRow).X;
            double viewLeft = view.TranslatePoint(default, controlRow).X;
            Assert.True(viewLeft >= transportRight, $"{language} {width}: tools at {viewLeft}, transport ends at {transportRight}");
            Assert.True(view.TranslatePoint(new Point(view.ActualWidth, 0), controlRow).X <= controlRow.ActualWidth + 0.5);
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
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            Ocr410Fixture.LoadTheme(card);
            card.Show();
            card.UpdateLayout();
            IReadOnlyList<Button> actions = card.ActionButtonsForTest;
            Assert.Equal(5, actions.Count);
            foreach (Button button in actions)
            {
                string key = AutomationProperties.GetAcceleratorKey(button);
                TextBlock caption = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == key);
                Assert.Equal(12, caption.FontSize);
                Assert.True(caption.IsVisible);
                Assert.True(caption.ActualWidth + 0.5 >= caption.DesiredSize.Width, $"'{key}' is clipped");
                Rect bounds = caption.TransformToAncestor(button).TransformBounds(new Rect(caption.RenderSize));
                Assert.True(bounds.Bottom <= button.ActualHeight + 0.5, $"'{key}' leaves its button: {bounds} in {button.ActualHeight}");
            }
        }
        finally { card.Close(); }
    });
}
