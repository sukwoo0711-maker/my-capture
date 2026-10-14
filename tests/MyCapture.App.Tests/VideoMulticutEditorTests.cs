using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoMulticutEditorTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void SplitDeleteRestoreAndUndoEachChangeOneSnapshotWithoutMovingLayers() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        VideoEditorWindow editor = CreateEditor();
        try
        {
            VideoEditDocument document = Document(editor);
            var layer = new TimedTextOverlay { StartMs = 2500, EndMs = 5500, Text = "Layer" };
            document.TextOverlays.Add(layer);
            Split(editor, 2000);
            Split(editor, 4000);
            Assert.Equal(2, Undo(editor).Count);
            Assert.Collection(Document(editor).SplitPoints,
                split => Assert.Equal(2000, split, 6), split => Assert.Equal(4000, split, 6));
            editor.TimelineForTest.SelectSegmentAt(3000);
            Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
            Assert.Equal(3, Undo(editor).Count);
            VideoTimeRange cut = Assert.Single(Document(editor).RemovedRanges);
            Assert.Equal(2000, cut.StartMs, 6);
            Assert.Equal(4000, cut.EndMs, 6);
            Assert.Equal(2500, layer.StartMs);
            Assert.Equal(5500, layer.EndMs);
            Assert.Equal("남는 길이 0:08.000", Field<TextBlock>(editor, "_keptLengthLabel").Text);
            Assert.Equal("잘린 구간 1개 · 잘림 0:02.000", Field<TextBlock>(editor, "_removedLengthLabel").Text);
            Assert.Equal("출력 0:02.000 / 0:08.000", Field<TextBlock>(editor, "_positionLabel").Text);

            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.Empty(Document(editor).RemovedRanges);
            Assert.Equal(2, Document(editor).SplitPoints.Count);
            Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
            editor.TimelineForTest.SelectSegmentAt(3000);
            Button restore = Field<Button>(editor, "_restoreSegmentButton");
            Assert.True(restore.IsEnabled);
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(Document(editor).RemovedRanges);
            Assert.Equal(4, Undo(editor).Count);
            Assert.Equal(10000, Document(editor).OutputDurationMs);
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.Single(Document(editor).RemovedRanges);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void LayerAndSegmentSelectionAreMutuallyExclusive() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Document(editor).TextOverlays.Add(new() { StartMs = 1000, EndMs = 3000, Text = "Layer" });
            Invoke(editor, "RefreshOverlayList", new object?[] { null });
            ListBox layers = Field<ListBox>(editor, "_overlayList");
            layers.SelectedIndex = 0;
            Split(editor, 5000);
            editor.TimelineForTest.SelectSegmentAt(2000);
            Assert.Null(layers.SelectedItem);
            layers.SelectedIndex = 0;
            Assert.Null(editor.TimelineForTest.SelectedSegment);
            editor.HandleShortcut(Key.Delete, ModifierKeys.None);
            Assert.Empty(Document(editor).TextOverlays);
            Assert.Empty(Document(editor).RemovedRanges);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void ResetAndRecoveredSnapshotIncludeCutsAndSplits() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Split(editor, 2000);
            Split(editor, 4000);
            editor.TimelineForTest.SelectSegmentAt(3000);
            editor.HandleShortcut(Key.Delete, ModifierKeys.None);
            VideoEditDocument snapshot = (VideoEditDocument)Invoke(editor, "BuildCurrentDocument")!;
            VideoEditDocument recovered = JsonSerializer.Deserialize<VideoEditDocument>(JsonSerializer.Serialize(snapshot))!;
            Assert.True(VideoEditorWindow.DocumentsEquivalent(snapshot, recovered));
            recovered.RemovedRanges.Clear();
            Assert.False(VideoEditorWindow.DocumentsEquivalent(snapshot, recovered));
            Button reset = Field<Button>(editor, "_resetTrimButton");
            Assert.True(reset.IsEnabled);
            int undo = Undo(editor).Count;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(Document(editor).RemovedRanges);
            Assert.Empty(Document(editor).SplitPoints);
            Assert.Equal(undo + 1, Undo(editor).Count);
            editor.HandleShortcut(Key.Z, ModifierKeys.Control);
            Assert.True(VideoEditorWindow.DocumentsEquivalent(snapshot, Document(editor)));
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void OuterTrimCannotSilentlyRestoreAnEntirelyRemovedRange() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Split(editor, 2000);
            Split(editor, 4000);
            editor.TimelineForTest.SelectSegmentAt(3000);
            editor.HandleShortcut(Key.Delete, ModifierKeys.None);
            int undo = Undo(editor).Count;
            editor.TimelineForTest.SetTrimRange(2500, 3500);
            Assert.Equal(0, editor.TimelineForTest.InMs);
            Assert.Equal(10000, editor.TimelineForTest.OutMs);
            Assert.Single(Document(editor).RemovedRanges);
            Assert.Equal(undo, Undo(editor).Count);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void PhysicalImeKeyResolvesToSplitAndTooShortSegmentsDoNotAddUndo() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Key key = ShortcutInput.Resolve(Key.ImeProcessed, Key.K, textInputFocused: false);
            Assert.Equal("Split", ShortcutCatalog.Match(ShortcutScope.Video, key, ModifierKeys.Control));
            editor.TimelineForTest.SetPlayhead(0);
            Assert.True(editor.HandleShortcut(key, ModifierKeys.Control));
            Assert.Empty(Undo(editor));
            Split(editor, 5000);
            Split(editor, 5000);
            Assert.Single(Undo(editor));
            editor.TimelineForTest.SelectSegmentAt(1000);
            editor.HandleShortcut(Key.Delete, ModifierKeys.None);
            editor.TimelineForTest.SelectSegmentAt(6000);
            editor.HandleShortcut(Key.Delete, ModifierKeys.None);
            Assert.Equal(2, Undo(editor).Count);
            Assert.Equal(5000, Document(editor).OutputDurationMs);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void ClickSelectsSourceSegmentAndDraggingOnlyScrubs() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10000, 30);
        timeline.SetDetailVisible(false);
        timeline.SetCuts(VideoCutTimeline.Create(0, 10000, [new(2000, 4000)], [2000, 4000, 7000]));
        timeline.Measure(new Size(600, double.PositiveInfinity));
        timeline.Arrange(new Rect(0, 0, 600, timeline.DesiredSize.Height));
        timeline.UpdateLayout();
        double x = timeline.OverviewPxForTest(3000);
        double y = timeline.OverviewTrackMiddleForTest;
        timeline.PressForTest(x, y);
        timeline.ReleaseForTest(x, y);
        Assert.Equal(new VideoTimeRange(2000, 4000), timeline.SelectedSegment);
        timeline.ClearSegmentSelection();
        timeline.PressForTest(timeline.OverviewPxForTest(5000), y);
        timeline.MoveForTest(timeline.OverviewPxForTest(6000));
        timeline.ReleaseForTest(timeline.OverviewPxForTest(6000), y);
        Assert.Null(timeline.SelectedSegment);
        Assert.InRange(timeline.PlayheadMs, 5999, 6001);
    });

    private static VideoEditorWindow CreateEditor()
    {
        // No files or process-wide Application: the nonexistent fixture path is confined to
        // the test output directory, and readiness uses the editor's fallback directly.
        string root = Path.Combine(AppContext.BaseDirectory, "multicut-in-memory");
        var recording = new RecordingResult(Path.Combine(root, "missing.mp4"), 10000, 30, 300, 640, 360);
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        Invoke(editor, "CompleteOpen", 10000d, true);
        return editor;
    }

    private static void Split(VideoEditorWindow editor, double timeMs)
    {
        editor.TimelineForTest.SetPlayhead(timeMs);
        Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
    }

    private static VideoEditDocument Document(VideoEditorWindow editor) => Field<VideoEditDocument>(editor, "_editDocument");
    private static List<VideoEditDocument> Undo(VideoEditorWindow editor) => Field<List<VideoEditDocument>>(editor, "_undo");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
}
