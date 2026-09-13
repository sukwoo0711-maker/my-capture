using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoEditingTimeContractTests
{
    [Theory]
    [InlineData(4999, false)]
    [InlineData(5000, true)]
    [InlineData(6500, true)]
    [InlineData(6999, true)]
    [InlineData(7000, false)]
    public void MiddleText_PreviewPixelsFollowOriginalSourceStartInclusiveEndExclusive(double sourceMs, bool visible) => StaTestHost.Run(() =>
    {
        var view = new TimedTextPreviewView();
        view.SetCanvas(320, 240);
        view.SetOverlays([new TimedTextOverlay { Text = "Middle caption", StartMs = 5000, EndMs = 7000 }]);
        view.SetSourceTime(sourceMs);
        view.Measure(new Size(320, 240));
        view.Arrange(new Rect(0, 0, 320, 240));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        byte[] pixels = new byte[320 * 240 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        int glyphPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 170 && pixels[i + 1] > 170 && pixels[i + 2] > 170 && pixels[i + 3] > 170) glyphPixels++;
        Assert.Equal(visible, glyphPixels > 10);
    });

    [Theory]
    [InlineData("2.5", 2500)]
    [InlineData("00:00:02.500", 2500)]
    [InlineData("01:02:03.004", 3723004)]
    public void SourceInput_AcceptsSecondsAndPreciseTimecode(string text, double expected)
    {
        Assert.True(SourceTimeInput.TryParse(text, out double value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("00:80:00")]
    public void SourceInput_RejectsInvalidTime(string text) => Assert.False(SourceTimeInput.TryParse(text, out _));

    [Theory]
    [InlineData(86_400_001)]
    [InlineData(90_000_000)]
    public void SourceInput_LongDurationRoundtripPreservesDays(double milliseconds)
    {
        string formatted = SourceTimeInput.Format(milliseconds);
        Assert.DoesNotContain(":", formatted);
        Assert.True(SourceTimeInput.TryParse(formatted, out double parsed));
        Assert.Equal(milliseconds, parsed);
    }

    [Fact]
    public void AtomicTrim_PreservesPreviousRangeOnInvalidInput_AndKeepsSourcePlayhead() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10000, 30);
        int changes = 0;
        timeline.TrimChanged += (_, _) => changes++;
        Assert.True(timeline.SetTrimRange(2000, 6000));
        Assert.False(timeline.SetTrimRange(7000, 6000));
        Assert.False(timeline.SetTrimRange(double.NaN, 8000));
        Assert.False(timeline.SetTrimRange(0, 10001));
        Assert.Equal(1, changes);
        Assert.Equal(2000, timeline.InMs);
        Assert.Equal(6000, timeline.OutMs);
        timeline.SetPlayhead(9000);
        Assert.Equal(9000, timeline.PlayheadMs);
        Assert.True(timeline.SetTrimRange(7000, 9500));
        Assert.Equal(7000, timeline.InMs);
        Assert.Equal(9500, timeline.OutMs);
    });

    [Fact]
    public void TrimDrag_ManyUpdatesProduceOneUndo_AndRestoreTimelineAndDocument() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-trim-history-");
        try
        {
            var recording = new RecordingResult(System.IO.Path.Combine(root, "clip.mp4"), 10000, 30, 300, 320, 240);
            var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
            try
            {
                TwoLineTimeline timeline = editor.TimelineForTest;
                timeline.Initialize(10000, 30);
                // Raise the same begin/end notifications used by pointer capture, without native mouse input.
                Raise(timeline, "TrimInteractionStarted");
                timeline.SetIn(1000); timeline.SetIn(2000); timeline.SetIn(3000);
                Raise(timeline, "TrimInteractionCompleted");
                var history = (List<VideoEditDocument>)Field(editor, "_undo")!;
                Assert.Single(history);
                Invoke(editor, "RestoreEdit", false);
                Assert.Equal(0, timeline.InMs);
                Assert.Equal(0, ((VideoEditDocument)Field(editor, "_editDocument")!).TrimInMs);
                Invoke(editor, "RestoreEdit", true);
                Assert.Equal(3000, timeline.InMs);
                Assert.Equal(3000, ((VideoEditDocument)Field(editor, "_editDocument")!).TrimInMs);
            }
            finally { editor.Close(); }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Invoke(object instance, string name, params object[] arguments) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
    private static void Raise(TwoLineTimeline timeline, string name) => ((EventHandler?)Field(timeline, name))?.Invoke(timeline, EventArgs.Empty);
}
