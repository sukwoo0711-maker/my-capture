using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The recording editor says what it is and groups its commands by purpose: identity,
/// history, editing tools, then save/export; narrow windows compact instead of clipping.
/// </summary>
public sealed class VideoEditorUxLayoutTests
{
    [Fact]
    public void TitleAndHeaderNameTheRecordedVideoEditor() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-title-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            // WindowIdentity puts the product first, as in every MyCapture window.
            Assert.Equal("MyCapture — 녹화 동영상 편집", editor.Title);
            Assert.Equal("녹화 동영상 편집 창", AutomationProperties.GetName(editor));
            var bar = Assert.Single(Descendants((DependencyObject)editor.Content).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            TextBlock[] texts = Descendants(bar).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
            Assert.Contains(texts, text => text.Text == "녹화 동영상 편집");
            Assert.Contains(texts, text => text.Text == "동영상 00:10.0 · 640×360 · 30fps");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void CommandBarGroupsHistoryToolsAndOutputInOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-groups-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            var bar = Assert.Single(Descendants((DependencyObject)editor.Content).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            Assert.Equal(UiText.Get("VideoEditorUx_CommandBarName"), AutomationProperties.GetName(bar));
            double X(string name)
            {
                Button button = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == name);
                Assert.True(button.IsVisible, name);
                return button.TranslatePoint(new Point(), bar).X;
            }

            // Undo/redo, then trim · add text · frame image, then save · export at the right.
            double[] order =
            [
                X(UiText.Get("Text_CE706412FA75")), X(UiText.Get("Text_078C44D5A66E")),
                X(UiText.Get("VideoEditorUx_Trim")), X(UiText.Get("VideoEditorUx_AddText")), X(UiText.Get("VideoEditorUx_FrameImageEdit")),
                X(UiText.Get("MediaExport_SaveEdits")), X(UiText.Get("MediaExport_ExportFormats")),
            ];
            Assert.Equal(order.Order().ToArray(), order);
            foreach (var (name, gesture) in new[]
            {
                ("VideoEditorUx_Trim", "X"), ("VideoEditorUx_AddText", "Ctrl+T"), ("VideoEditorUx_FrameImageEdit", "E"),
            })
            {
                Button tool = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(name));
                Assert.Equal(gesture, AutomationProperties.GetAcceleratorKey(tool));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(tool)), name);
                Assert.Contains(Descendants(tool).OfType<TextBlock>(), text => text.IsVisible && text.Text == UiText.Get(name));
            }

            Assert.Equal(0, editor.CommandBarLevelForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(960, 516)]
    [InlineData(1180, 760)]
    public void NarrowWindowsCompactTheCommandBarInsteadOfClippingIt(int width, int height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-compact-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            var bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            foreach (Button button in Descendants(bar).OfType<Button>().Where(b => b.IsVisible))
            {
                AssertInside(button, layout, layout.ActualHeight);
                foreach (TextBlock text in Descendants(button).OfType<TextBlock>().Where(t => t.IsVisible))
                    Assert.True(text.ActualWidth + 0.5 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right,
                        $"clipped '{text.Text}' at {width}x{height}");
            }

            // Frame image edit, save and export never leave; trim and add text keep X, Ctrl+T,
            // the trim bar and the layer menu when the narrowest bar drops them.
            foreach (string key in new[] { "VideoEditorUx_FrameImageEdit", "MediaExport_SaveEdits", "MediaExport_ExportFormats" })
                Assert.True(Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key)).IsVisible, key);
            if (width <= 760) Assert.True(editor.CommandBarLevelForTest >= 3, $"level {editor.CommandBarLevelForTest} at {width}");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ResizingRefitsTheCommandBarBothWays() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-resize-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            var layout = (Grid)editor.Content;
            var bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            Assert.Equal(0, editor.CommandBarLevelForTest);
            foreach (var (width, height) in new[] { (760, 555), (960, 516), (1180, 760), (760, 555), (1400, 820) })
            {
                editor.MinHeight = Math.Min(editor.MinHeight, height);
                editor.Width = width;
                editor.Height = height;
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                Button export = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_ExportFormats"));
                AssertInside(export, layout, layout.ActualHeight);
            }

            Assert.Equal(0, editor.CommandBarLevelForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LayerTracksAppearOnlyOnceThereIsALayer() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-tracks-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            ScrollViewer tracks = Field<ScrollViewer>(editor, "_layerTracksScroll");
            Assert.Equal(Visibility.Collapsed, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Equal(Visibility.Visible, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("RestoreEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.Equal(Visibility.Collapsed, tracks.Visibility);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });
}
