using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The recording editor's pointer and keyboard model after the third real-device round: a
/// click on the trim strip seeks and leaves the arrow keys to the playhead, only a handle the
/// user took hold of answers to ←/→, Tab is never trapped, Space always plays, and new layers
/// get their default length even at the end of the kept range.
/// </summary>
public sealed class VideoEditorUxInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ClickOnTheStripSeeksAndLeavesTheArrowKeysToThePlayhead() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-click-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.SetTrimRange(2_000, 8_000));
            double y = timeline.OverviewTrackMiddleForTest;
            double x = timeline.OverviewPxForTest(5_000);

            timeline.PressForTest(x, y);
            timeline.ReleaseForTest(x, y);
            Assert.Equal(5_000, timeline.PlayheadMs, precision: 0);
            Assert.False(timeline.IsHandleArmed);

            // → on the timeline is not a handle nudge: the timeline leaves it to the window,
            // which moves the playhead, and the handles stay where they were.
            var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Right)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            timeline.RaiseEvent(right);
            Assert.False(right.Handled);
            Assert.True(editor.HandleShortcut(Key.Right, ModifierKeys.None));
            Assert.True(timeline.PlayheadMs > 5_000, $"playhead {timeline.PlayheadMs}");
            Assert.Equal(2_000, timeline.InMs, precision: 3);
            Assert.Equal(8_000, timeline.OutMs, precision: 3);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ArmedHandleAnswersToArrowsButNeverTrapsTab() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-armed-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.SetTrimRange(2_000, 8_000));

            // X takes hold of the start handle; X again moves on to the end handle, with or
            // without keyboard focus on the timeline (a mouse click on the button has none).
            Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
            Assert.True(timeline.IsHandleArmed);
            Assert.False(timeline.IsEndHandleActive);
            Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
            Assert.True(timeline.IsEndHandleActive);

            var right = KeyPress(editor, Key.Right);
            timeline.RaiseEvent(right);
            Assert.True(right.Handled);
            double frameMs = FrameStepCalculator.FrameDurationMs(30);
            Assert.Equal(8_000 + frameMs, timeline.OutMs, precision: 1);

            var tab = KeyPress(editor, Key.Tab);
            timeline.RaiseEvent(tab);
            Assert.False(tab.Handled);
            Assert.True(timeline.IsEndHandleActive);

            var home = KeyPress(editor, Key.Home);
            timeline.RaiseEvent(home);
            Assert.False(home.Handled);
            Assert.Equal(2_000, timeline.InMs, precision: 3);

            var escape = KeyPress(editor, Key.Escape);
            timeline.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(timeline.IsHandleArmed);
            var left = KeyPress(editor, Key.Left);
            timeline.RaiseEvent(left);
            Assert.False(left.Handled);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void PressNearAHandleSeeksUnlessItTurnsIntoADrag() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-grab-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TwoLineTimeline timeline = editor.TimelineForTest;
            var undo = Field<List<VideoEditDocument>>(editor, "_undo");
            Assert.True(timeline.SetTrimRange(2_000, 8_000));
            int undoBefore = undo.Count;
            double width = timeline.TrimStripForTest.ActualWidth;
            Assert.True(width > 200, $"strip width {width}");
            double y = timeline.OverviewTrackMiddleForTest;
            // Inside the 44 DIP grab zone of the start handle, outside the handle itself.
            double pressX = timeline.OverviewPxForTest(2_000) + 16;

            // 1. A press that never moves is a click: it seeks there and keeps the handle.
            timeline.PressForTest(pressX, y);
            Assert.False(timeline.IsTrimDragging);
            timeline.ReleaseForTest(pressX, y);
            Assert.Equal(pressX / width * 10_000, timeline.PlayheadMs, precision: 0);
            Assert.Equal(2_000, timeline.InMs, precision: 3);
            Assert.False(timeline.IsHandleArmed);
            Assert.Equal(undoBefore, undo.Count);

            // 2. The same press followed by movement drags the handle (one undo step).
            timeline.PressForTest(pressX, y);
            timeline.MoveForTest(pressX + 40);
            Assert.True(timeline.IsTrimDragging);
            Assert.True(timeline.IsHandleArmed);
            timeline.ReleaseForTest(pressX + 40, y);
            Assert.False(timeline.IsTrimDragging);
            Assert.True(timeline.InMs > 2_000, $"in {timeline.InMs}");
            Assert.Equal(undoBefore + 1, undo.Count);

            // 3. A press on the handle itself drags at once, as before.
            double handleX = timeline.OverviewPxForTest(timeline.InMs);
            timeline.PressForTest(handleX + 3, y);
            Assert.True(timeline.IsTrimDragging);
            timeline.ReleaseForTest(handleX + 3, y);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void SpaceTogglesPlaybackAndThePlayButtonShowsPause() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-space-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            Assert.False(editor.PlayButtonShowsPauseForTest);

            // The window sees Space before any focused button does.
            var space = KeyPress(editor, Key.Space);
            editor.RaiseEvent(space);
            Assert.True(space.Handled);
            Assert.True(editor.PlayButtonShowsPauseForTest);

            var again = KeyPress(editor, Key.Space);
            editor.RaiseEvent(again);
            Assert.True(again.Handled);
            Assert.False(editor.PlayButtonShowsPauseForTest);

            Button play = Assert.Single(Descendants((DependencyObject)editor.Content).OfType<Button>(),
                b => System.Windows.Automation.AutomationProperties.GetName(b) == UiText.Get("Text_9A9C87658130"));
            Assert.Equal(UiText.Get("VideoEditorUx_Play"), play.ToolTip);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void NewLayersKeepTheirDefaultLengthAtTheEndOfTheKeptRange() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-layer-end-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.SetTrimRange(1_000, 9_000));
            timeline.SetPlayhead(9_000);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", Private)!.Invoke(editor, [false]);
            FrameEditLayer layer = Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.Equal(6_000, layer.StartMs, precision: 3);
            Assert.Equal(9_000, layer.EndMs, precision: 3);

            // In the middle the layer still starts at the playhead.
            typeof(VideoEditorWindow).GetMethod("RestoreEdit", Private)!.Invoke(editor, [false]);
            timeline.SetPlayhead(4_000);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", Private)!.Invoke(editor, [true]);
            layer = Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.Equal(4_000, layer.StartMs, precision: 3);
            Assert.Equal(7_000, layer.EndMs, precision: 3);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    private static KeyEventArgs KeyPress(Window window, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
}
