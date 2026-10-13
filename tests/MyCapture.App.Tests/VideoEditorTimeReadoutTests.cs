using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor writes every time one way (m:ss.fff), and the time readout on the preview stays
/// off the selected layer's outline and handles.
/// </summary>
public sealed partial class VideoEditorTimeReadoutTests : KoreanCaptionTest
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    // A caption in the readout's bottom-left corner: the readout moves to the top left.
    [InlineData(760, 555, 0.01, 0.80, 0.42, 0.18, HorizontalAlignment.Left, VerticalAlignment.Top)]
    [InlineData(920, 680, 0.01, 0.80, 0.42, 0.18, HorizontalAlignment.Left, VerticalAlignment.Top)]
    [InlineData(1180, 760, 0.01, 0.80, 0.42, 0.18, HorizontalAlignment.Left, VerticalAlignment.Top)]
    // A layer across the whole left side: the readout moves to the right.
    [InlineData(920, 680, 0.0, 0.0, 0.45, 1.0, HorizontalAlignment.Right, VerticalAlignment.Bottom)]
    // A caption across the whole bottom: the readout moves to the top.
    [InlineData(920, 680, 0.0, 0.82, 1.0, 0.16, HorizontalAlignment.Left, VerticalAlignment.Top)]
    // A layer clear of the corner: the readout stays.
    [InlineData(920, 680, 0.55, 0.05, 0.4, 0.2, HorizontalAlignment.Left, VerticalAlignment.Bottom)]
    public void TimeReadoutLeavesTheSelectedLayerFree(
        double windowWidth, double windowHeight, double x, double y, double width, double height,
        HorizontalAlignment expectedHorizontal, VerticalAlignment expectedVertical) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-time-readout-");
        VideoEditorWindow? editor = null;
        try
        {
            var caption = new TimedTextOverlay
            {
                Text = "선택한 텍스트",
                StartMs = 0,
                EndMs = 1_000,
                Bounds = new VideoLayerBounds(x, y, width, height),
            };
            editor = OpenEditor(root, windowWidth, windowHeight, caption);
            Border chip = Chip(editor);
            Assert.Equal((HorizontalAlignment.Left, VerticalAlignment.Bottom), editor.PositionChipCornerForTest);

            var list = (ListBox)typeof(VideoEditorWindow).GetField("_overlayList", Private)!.GetValue(editor)!;
            list.SelectedIndex = 0;
            Settle(editor);
            var canvas = (VideoLayerCanvas)typeof(VideoEditorWindow).GetField("_layerCanvas", Private)!.GetValue(editor)!;
            Rect selection = canvas.SelectionViewBounds ?? throw new Xunit.Sdk.XunitException("the layer is not selected");
            Assert.Equal((expectedHorizontal, expectedVertical), editor.PositionChipCornerForTest);
            Rect shown = new(chip.TranslatePoint(new Point(0, 0), canvas), chip.RenderSize);
            Assert.False(shown.IntersectsWith(selection), $"the readout {shown} covers the selected layer {selection}");

            list.SelectedIndex = -1;
            Settle(editor);
            Assert.Null(canvas.SelectionViewBounds);
            Assert.Equal((HorizontalAlignment.Left, VerticalAlignment.Bottom), editor.PositionChipCornerForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(920, 680)]
    public void EveryTimeTheEditorShowsUsesOneFormat(double windowWidth, double windowHeight) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-time-format-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenEditor(root, windowWidth, windowHeight, new TimedTextOverlay { Text = "자막", StartMs = 300, EndMs = 900 });
            Assert.True(editor.TimelineForTest.SetTrimRange(100, 900));
            Settle(editor);
            var times = new List<string>();
            foreach (DependencyObject element in Descendants((DependencyObject)editor.Content))
            {
                string? text = element switch
                {
                    TextBlock block when block.IsVisible => block.Text,
                    TextBox box when box.IsVisible => box.Text,
                    _ => null,
                };
                if (text is null) continue;
                times.AddRange(TimeToken().Matches(text).Select(match => match.Value));
            }

            // The readout, the start/end inputs, the kept length, the header and the layer list.
            Assert.True(times.Count >= 6, string.Join(", ", times));
            Assert.All(times, time => Assert.Matches(OneFormat(), time));
            Assert.Contains("0:00.100", times);
            Assert.Contains("0:00.900", times);
            Assert.Contains("0:00.800", times);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [GeneratedRegex(@"\d+(?::\d+)+(?:[.,]\d+)?")]
    private static partial Regex TimeToken();

    // m:ss.fff with minutes 0-59 and no leading zero, or h:mm:ss.fff from an hour.
    [GeneratedRegex(@"^(?:[1-9]\d*:[0-5]\d|[1-5]?\d):[0-5]\d\.\d{3}$")]
    private static partial Regex OneFormat();

    private static VideoEditorWindow OpenEditor(string root, double width, double height, TimedTextOverlay caption)
    {
        string clip = Path.Combine(root, "clip.mp4");
        FrameIndexClip.Encode(clip, 640, 360, 30, FrameIndexClip.Constant(30, 30));
        double durationMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
        var recording = new RecordingResult(clip, durationMs, 30, 30, 640, 360);
        var document = VideoEditDocument.CreateFor(640, 360, durationMs);
        document.TextOverlays.Add(caption);
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = width, Height = height,
            ShowActivated = false, ShowInTaskbar = false,
        };
        editor.Show();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
        Settle(editor);
        return editor;
    }

    private static Border Chip(VideoEditorWindow editor) =>
        Descendants((DependencyObject)editor.Content).OfType<Border>().Single(border => border.Name == "VideoPositionChip");

    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(40));
            window.UpdateLayout();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
