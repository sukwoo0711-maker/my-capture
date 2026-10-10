using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The recording editor's trim redesign: one trim model shared by the handles, I/O and the
/// typed times; the kept length; undo; the X trim command; and handle targets.
/// </summary>
public sealed class VideoEditorUxTrimTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0, "00:00.0")]
    [InlineData(7_233.333, "00:07.2")]
    [InlineData(7_250, "00:07.3")]
    [InlineData(59_960, "01:00.0")]
    [InlineData(3_723_400, "1:02:03.4")]
    public void ClockRoundsToTenthsForGlanceableTimes(double milliseconds, string expected) =>
        Assert.Equal(expected, TrimPresentation.Clock(milliseconds));

    [Fact]
    public void KeptAndRemovedLengthReadInBothLanguages()
    {
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("남는 길이 00:07.2", TrimPresentation.KeptLength(1_200, 8_400));
            Assert.Equal("잘림 00:02.8", TrimPresentation.RemovedLength(10_000, 1_200, 8_400));
            Assert.Equal("자르지 않음", TrimPresentation.RemovedLength(10_000, 0, 10_000));
            Assert.Equal("시작 00:01.2", TrimPresentation.StartChip(1_200));
            Assert.Equal("끝 00:08.4", TrimPresentation.EndChip(8_400));
        }

        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Kept 00:07.2", TrimPresentation.KeptLength(1_200, 8_400));
            Assert.Equal("Cut 00:02.8", TrimPresentation.RemovedLength(10_000, 1_200, 8_400));
            Assert.Equal("Not trimmed", TrimPresentation.RemovedLength(10_000, 0, 10_000));
        }
    }

    [Fact]
    public void XIsTrimInTheRecordingEditorAndStaysCropInTheImageEditor()
    {
        Assert.Equal("Trim", ShortcutCatalog.Match(ShortcutScope.Video, Key.X, ModifierKeys.None));
        Assert.Equal("Crop", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.X, ModifierKeys.None));
        Assert.Equal("X", ShortcutCatalog.Get(ShortcutScope.Video, "Trim").Hint);
    }

    [Fact]
    public void HandleTargetsAreTouchSizedAndWinOverTheZoomLane() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10_000, 30);
        Assert.True(timeline.SetTrimRange(2_000, 8_000));
        const double width = 600;

        foreach (bool detail in new[] { false, true })
        {
            timeline.SetDetailVisible(detail);
            double height = timeline.TrimStripHeight;
            Assert.True(height >= 44, $"trim strip is {height} DIP tall");
            double inX = width * 0.2;
            double outX = width * 0.8;

            // The whole strip height at the handle grabs it, the lanes included.
            for (double y = 1; y < height; y += 4)
            {
                Assert.Equal("TrimIn", timeline.OverviewHitForTest(inX, y, width));
                Assert.Equal("TrimOut", timeline.OverviewHitForTest(outX, y, width));
            }

            // At least 40 DIP (Windows touch minimum) of contiguous width grabs each handle.
            double middle = height - 12;
            Assert.True(TargetWidth(timeline, inX, middle, width, "TrimIn") >= 40);
            Assert.True(TargetWidth(timeline, outX, middle, width, "TrimOut") >= 40);

            // The kept middle, away from the handles, seeks the playhead.
            Assert.Equal("Playhead", timeline.OverviewHitForTest(width / 2, middle - 6, width));
        }

        // With the detail strip, its zoom range sits in a lane under the trim range and
        // never covers a trim handle.
        timeline.SetDetailVisible(true);
        timeline.FitAll();
        double lane = timeline.TrimStripHeight - 3;
        Assert.Equal("TrimIn", timeline.OverviewHitForTest(width * 0.2, lane, width));
        Assert.Equal("BrushBody", timeline.OverviewHitForTest(width / 2, lane, width));
        Assert.Equal("Playhead", timeline.OverviewHitForTest(width / 2, 24, width));
    });

    [Fact]
    public void TimeChipsOnTheHandlesAlsoGrabThem() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10_000, 30);
        timeline.SetDetailVisible(false);
        Assert.True(timeline.SetTrimRange(2_000, 8_000));
        const double width = 600;
        // The start chip extends right of the start handle, the end chip left of the end handle.
        Assert.Equal("TrimIn", timeline.OverviewHitForTest(120 + 40, 8, width));
        Assert.Equal("TrimOut", timeline.OverviewHitForTest(480 - 40, 8, width));
        // Below the chip lane the same spot is the kept range.
        Assert.Equal("Playhead", timeline.OverviewHitForTest(120 + 40, 40, width));
    });

    [Fact]
    public void DragIoAndTypedTimesChangeOneTrimModelWithOneUndoEach() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-trim-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000);
            TwoLineTimeline timeline = editor.TimelineForTest;
            var undo = Field<List<VideoEditDocument>>(editor, "_undo");
            VideoEditDocument Document() => Field<VideoEditDocument>(editor, "_editDocument");
            TextBlock kept = Field<TextBlock>(editor, "_keptLengthLabel");
            TextBlock removed = Field<TextBlock>(editor, "_removedLengthLabel");
            Button reset = Field<Button>(editor, "_resetTrimButton");
            Border badge = Field<Border>(editor, "_trimPreviewBadge");
            Assert.Equal("남는 길이 00:10.0", kept.Text);
            Assert.Equal("자르지 않음", removed.Text);
            Assert.False(reset.IsEnabled);

            // 1. Dragging the start handle: one undo step for the whole drag, and the preview
            // is asked for each handle position while the badge names the handle.
            timeline.BeginTrimDragForTest(end: false);
            timeline.MoveTrimDragForTest(1_000);
            timeline.MoveTrimDragForTest(2_016);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("시작 00:02.0", ((TextBlock)badge.Child).Text);
            Assert.Equal(2_000, timeline.PlayheadMs, precision: 3);
            Assert.Equal(2_000, editor.PreviewSeekCoordinatorForTest.IntentPositionMs, precision: 3);
            timeline.EndTrimDragForTest();
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.Single(undo);
            AssertRange(2_000, 10_000, Document().TrimInMs, Document().TrimOutMs);
            Assert.Equal("남는 길이 00:08.0", kept.Text);
            Assert.Equal("잘림 00:02.0", removed.Text);
            Assert.True(reset.IsEnabled);

            // 2. O at the playhead sets the end of the same model.
            timeline.SetPlayhead(7_000);
            Assert.True(editor.HandleShortcut(Key.O, ModifierKeys.None));
            Assert.Equal(2, undo.Count);
            AssertRange(2_000, 7_000, Document().TrimInMs, Document().TrimOutMs);
            AssertRange(2_000, 7_000, timeline.InMs, timeline.OutMs);

            // 3. Typed times: both fields applied together are one change.
            Field<TextBox>(editor, "_trimInInput").Text = "1.5";
            Field<TextBox>(editor, "_trimOutInput").Text = "00:00:06.000";
            Invoke(editor, "ApplyTrimInputs");
            Assert.Equal(3, undo.Count);
            AssertRange(1_500, 6_000, Document().TrimInMs, Document().TrimOutMs);
            Assert.Equal("남는 길이 00:04.5", kept.Text);
            Assert.Equal("잘림 00:05.5", removed.Text);
            Assert.Equal(TrimPresentation.TimelineStatus(1_500, 6_000),
                AutomationProperties.GetItemStatus(TrimStrip(timeline)));

            // 4. Undo walks back through typed, I/O and drag edits; redo replays one.
            Invoke(editor, "RestoreEdit", false);
            AssertRange(2_000, 7_000, timeline.InMs, timeline.OutMs);
            Invoke(editor, "RestoreEdit", false);
            AssertRange(2_000, 10_000, timeline.InMs, timeline.OutMs);
            Invoke(editor, "RestoreEdit", false);
            AssertRange(0, 10_000, timeline.InMs, timeline.OutMs);
            AssertRange(0, 10_000, Document().TrimInMs, Document().TrimOutMs);
            Invoke(editor, "RestoreEdit", true);
            AssertRange(2_000, 10_000, Document().TrimInMs, Document().TrimOutMs);
            Assert.Equal("남는 길이 00:08.0", kept.Text);

            // 5. "Full length" removes the trim as one more undoable change.
            int before = undo.Count;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AssertRange(0, 10_000, Document().TrimInMs, Document().TrimOutMs);
            Assert.Equal(before + 1, undo.Count);
            Assert.False(reset.IsEnabled);
            Invoke(editor, "RestoreEdit", false);
            AssertRange(2_000, 10_000, Document().TrimInMs, Document().TrimOutMs);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void XSelectsTheStartHandleAndShowsItsFrame() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-x-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000);
            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.SetTrimRange(3_000, 9_000));
            timeline.SetPlayhead(500);
            Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
            Assert.False(timeline.IsEndHandleActive);
            Assert.Equal(3_000, timeline.PlayheadMs, precision: 3);
            Assert.Equal(UiText.Get("VideoEditorUx_TrimHandleStart"), Field<TextBlock>(editor, "_statusLabel").Text);

            // The end handle is the same command's second step (and Tab inside the timeline).
            timeline.ActivateTrimHandle(end: true);
            Assert.True(timeline.IsEndHandleActive);

            // X is ignored while saving or exporting runs, like the other edit commands.
            typeof(VideoEditorWindow).GetField("_operationRunning", Private)!.SetValue(editor, true);
            Assert.False(editor.HandleShortcut(Key.X, ModifierKeys.None));
            typeof(VideoEditorWindow).GetField("_operationRunning", Private)!.SetValue(editor, false);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void HangulModeXRunsTheTrimCommand() => StaTestHost.Run(() =>
    {
        var source = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = -4000, Width = 120, Height = 80,
            ShowActivated = false, ShowInTaskbar = false,
        };
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory()), NullLoggerFactory.Instance);
        try
        {
            source.Show();
            typeof(VideoEditorWindow).GetField("_mediaReady", Private)!.SetValue(video, true);
            int hits = 0;
            Field<Dictionary<string, Action>>(video, "_shortcutActions")["Trim"] = () => hits++;
            // Hangul mode reports X as ImeProcessed; the 1차 ShortcutInput mapping restores it.
            KeyEventArgs args = EditorKeysTests.KeyArgs(source, Key.X, ime: true, Keyboard.KeyDownEvent);
            video.RaiseEvent(args);
            Assert.True(args.Handled);
            Assert.Equal(1, hits);
        }
        finally
        {
            typeof(VideoEditorWindow).GetField("_mediaReady", Private)!.SetValue(video, false);
            video.Close();
            source.Close();
        }
    });

    [Fact]
    public void TrimControlsHaveAccessibleNamesShortcutsAndTouchTargets() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-a11y-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            Button[] buttons = Descendants(layout).OfType<Button>().ToArray();
            foreach (var (key, command) in new[] { ("VideoEditorUx_MarkInHere", "I"), ("VideoEditorUx_MarkOutHere", "O") })
            {
                Button mark = Assert.Single(buttons, b => AutomationProperties.GetName(b) == UiText.Get(key));
                Assert.Equal(command, AutomationProperties.GetAcceleratorKey(mark));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(mark)));
                Assert.True(mark.ActualHeight >= 40 && mark.ActualWidth >= 40, $"{key}: {mark.ActualWidth}x{mark.ActualHeight}");
            }

            Button reset = Assert.Single(buttons, b => AutomationProperties.GetName(b) == UiText.Get("VideoEditorUx_ResetTrim"));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(reset)));
            ToggleButton detail = Assert.Single(Descendants(layout).OfType<ToggleButton>(),
                t => AutomationProperties.GetName(t) == UiText.Get("VideoEditorUx_DetailTimeline"));
            Assert.False(detail.IsChecked);
            Assert.False(editor.TimelineForTest.DetailVisible);
            Button play = Assert.Single(buttons, b => AutomationProperties.GetName(b) == UiText.Get("Text_9A9C87658130"));
            Assert.True(play.ActualHeight >= 44 && play.ActualWidth >= 44, $"play: {play.ActualWidth}x{play.ActualHeight}");

            FrameworkElement strip = TrimStrip(editor.TimelineForTest);
            Assert.Equal(UiText.Get("VideoEditorUx_TrimTimelineName"), AutomationProperties.GetName(strip));
            Assert.Contains("I", AutomationProperties.GetHelpText(strip), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetItemStatus(strip)));
            Assert.True(strip.ActualHeight >= 44);

            // Zooming opens the folded detailed timeline.
            Assert.True(editor.HandleShortcut(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.True(detail.IsChecked);
            Assert.True(editor.TimelineForTest.DetailVisible);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(760, 555)]
    // 1920x1080 at 200% scale leaves about 960x516 DIPs of work area.
    [InlineData(960, 516)]
    public void TrimTimelineAndBarFitTheMinimumAndHighScaleWindows(int width, int height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-fit-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            var client = (FrameworkElement)VisualTreeHelper.GetParent(layout);
            ScrollViewer tools = Assert.Single(layout.Children.OfType<ScrollViewer>());
            Assert.Equal(0, tools.VerticalOffset);
            FrameworkElement strip = TrimStrip(editor.TimelineForTest);
            AssertInside(strip, tools, tools.ViewportHeight);
            var bar = Assert.Single(Descendants(layout).OfType<WrapPanel>(), panel => panel.Name == "VideoTrimBar");
            foreach (FrameworkElement control in Descendants(bar).OfType<FrameworkElement>().Where(e => e is Button or TextBox))
            {
                AssertInside(control, tools, tools.ViewportHeight);
                foreach (TextBlock text in Descendants(control).OfType<TextBlock>())
                    Assert.True(text.ActualWidth + 0.5 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right,
                        $"clipped '{text.Text}' at {width}x{height}");
            }

            foreach (string key in new[] { "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130" })
            {
                Button button = Assert.Single(Descendants(layout).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key));
                AssertInside(button, client, client.ActualHeight);
            }

            Grid viewport = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoPreviewViewport");
            Assert.True(viewport.ActualHeight >= 112, $"preview {viewport.ActualHeight} at {width}x{height}");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    private static VideoEditorWindow OpenReadyEditor(string root, double durationMs, bool show = false, int width = 1180, int height = 760)
    {
        // A layout-only recording: the media never opens, so the editor is made ready with the
        // same CompleteOpen path the open-timeout fallback uses.
        var recording = new RecordingResult(Path.Combine(root, "layout-only.mp4"), durationMs, 30, (int)(durationMs / 1000 * 30), 640, 360);
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        if (show)
        {
            LoadTheme(editor);
            editor.FontSize = (double)editor.FindResource("FontSize.Body");
            editor.MinWidth = Math.Min(editor.MinWidth, width);
            editor.MinHeight = Math.Min(editor.MinHeight, height);
            editor.Width = width;
            editor.Height = height;
            editor.Show();
        }

        typeof(VideoEditorWindow).GetMethod("CompleteOpen", Private)!.Invoke(editor, [durationMs, true]);
        if (show)
        {
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
        }

        return editor;
    }

    private static FrameworkElement TrimStrip(TwoLineTimeline timeline) =>
        timeline.TrimStripForTest;

    private static double TargetWidth(TwoLineTimeline timeline, double x, double y, double width, string hit)
    {
        double left = x, right = x;
        while (left > 0 && timeline.OverviewHitForTest(left - 0.5, y, width) == hit) left -= 0.5;
        while (right < width && timeline.OverviewHitForTest(right + 0.5, y, width) == hit) right += 0.5;
        return right - left;
    }

    private static void AssertInside(FrameworkElement element, FrameworkElement container, double containerHeight)
    {
        Point top = element.TranslatePoint(new Point(), container);
        Point bottom = element.TranslatePoint(new Point(element.ActualWidth, element.ActualHeight), container);
        string name = AutomationProperties.GetName(element);
        Assert.True(element.IsVisible && element.ActualWidth > 0, $"{name} is not visible");
        Assert.True(top.X >= -0.5 && top.Y >= -0.5 && bottom.X <= container.ActualWidth + 0.5 && bottom.Y <= containerHeight + 0.5,
            $"{element.GetType().Name} '{name}' {top}..{bottom} outside {container.ActualWidth}x{containerHeight}");
    }

    private static void LoadTheme(Window window)
    {
        System.Xml.Linq.XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(VideoEditorUxTrimTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            System.Xml.Linq.XElement dictionary = System.Xml.Linq.XDocument.Load(source).Root!;
            combined ??= new System.Xml.Linq.XElement(dictionary.Name);
            foreach (System.Xml.Linq.XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (System.Xml.Linq.XElement entry in dictionary.Elements()) combined.Add(new System.Xml.Linq.XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
            combined!.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
    }

    private static void AssertRange(double expectedIn, double expectedOut, double actualIn, double actualOut)
    {
        // Positions snap to frames (n x 1000/30 ms), so compare to the millisecond.
        Assert.Equal(expectedIn, actualIn, precision: 3);
        Assert.Equal(expectedOut, actualOut, precision: 3);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static void Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
