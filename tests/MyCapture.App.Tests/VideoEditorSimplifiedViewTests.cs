using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorSimplifiedViewTests
{
    [Theory]
    [InlineData("ko-KR", 760, 555)]
    [InlineData("en-US", 960, 516)]
    [InlineData("ko-KR", 1180, 760)]
    public void DefaultViewKeepsTheEssentialActionsAndTextTrackVisible(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-simple-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            Assert.False(editor.AreDetailsExpandedForTest);
            ToggleButton details = Named<ToggleButton>(layout, "VideoDetailsToggle");
            Assert.False(details.IsChecked);
            Assert.True(details.IsVisible && details.Focusable && details.IsTabStop);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(details)));
            Assert.False(Named<FrameworkElement>(layout, "VideoDetailsPanel").IsVisible);
            Assert.False(Field<TextBox>(editor, "_trimInInput").IsVisible);
            Assert.False(Field<Border>(editor, "_layerPanel").IsVisible);

            foreach (string key in new[] { "VideoEditorUx_Trim", "VideoEditorUx_AddText", "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130" })
            {
                Button button = Command(layout, key);
                Assert.True(button.IsEnabled && button.IsTabStop && button.Focusable, key);
                AssertInside(button, layout, layout.ActualHeight);
                Assert.True(button.ActualWidth >= 24 && button.ActualHeight >= 24, key);
            }
            AssertInside(Named<FrameworkElement>(layout, "VideoPositionChip"), layout, layout.ActualHeight);
            AssertInside(editor.TimelineForTest.TrimStripForTest, layout, layout.ActualHeight);
            Assert.True(editor.TimelineForTest.SetTrimRange(2_000, 8_000));
            Assert.Equal(2_000, editor.TimelineForTest.InMs);
            Assert.Equal(8_000, editor.TimelineForTest.OutMs);
            AssertInside(Field<ScrollViewer>(editor, "_layerTracksScroll"), layout, layout.ActualHeight);

            object primary = editor.FindResource("Button.Primary");
            Assert.Same(Command(layout, "MediaExport_SaveEdits"),
                Assert.Single(Descendants(layout).OfType<Button>(), button => ReferenceEquals(button.Style, primary)));
            Assert.Same(editor.FindResource("Button.Secondary"), Command(layout, "MediaExport_ExportFormats").Style);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(960, 516)]
    [InlineData(1180, 760)]
    public void DetailsToggleReservesASeparateColumnAndRestoresThePreviewWhenCollapsed(int width, int height) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-details-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            ToggleButton toggle = Named<ToggleButton>(layout, "VideoDetailsToggle");
            FrameworkElement preview = Named<FrameworkElement>(layout, "VideoPreviewViewport");
            Rect initial = Bounds(preview, layout);
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            toggle.RaiseEvent(enter);
            editor.UpdateLayout();
            Assert.True(enter.Handled);
            Assert.True(editor.AreDetailsExpandedForTest);
            AssertSeparatedPreview(editor, initial, expanded: true);
            editor.SetDetailsExpandedForTest(false);
            foreach (bool expanded in new[] { true, false, true, false })
            {
                toggle.IsChecked = expanded;
                editor.UpdateLayout();
                Assert.Equal(expanded, editor.AreDetailsExpandedForTest);
                foreach (string name in new[] { "VideoDetailsPanel", "VideoPreciseTransport", "VideoTrimBar", "VideoLayersPanel" })
                    Assert.Equal(expanded, Named<FrameworkElement>(layout, name).IsVisible);
                AssertSeparatedPreview(editor, initial, expanded);
                AssertInside(editor.TimelineForTest.TrimStripForTest, layout, layout.ActualHeight);
                AssertInside(Field<ScrollViewer>(editor, "_layerTracksScroll"), layout, layout.ActualHeight);
                if (expanded)
                {
                    ScrollViewer inspector = Named<ScrollViewer>(layout, "VideoDetailsScroll");
                    var inspectorViewport = Assert.IsType<ScrollContentPresenter>(inspector.Template.FindName("PART_ScrollContentPresenter", inspector));
                    foreach (string key in new[] { "Video.TrimIn", "Video.TrimOut" })
                    {
                        TextBox input = Assert.Single(Descendants(layout).OfType<TextBox>(), box => AutomationProperties.GetName(box) == UiText.Get(key));
                        Assert.True(input.IsEnabled && input.Focusable && input.IsTabStop);
                        input.BringIntoView();
                        editor.UpdateLayout();
                        AssertInside(input, inspectorViewport, inspector.ViewportHeight);
                    }
                    foreach (string key in new[] { "VideoEditorUx_ResetTrim", "Video.AddLayer" })
                    {
                        Button command = Command(layout, key);
                        Assert.True(command.IsVisible && command.IsTabStop);
                        command.BringIntoView();
                        editor.UpdateLayout();
                        AssertInside(command, inspectorViewport, inspector.ViewportHeight);
                    }
                }
            }
            editor.SetDetailsExpandedForTest(true);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            editor.Width += 20;
            editor.UpdateLayout();
            Assert.True(editor.AreDetailsExpandedForTest);
            editor.SetDetailsExpandedForTest(false);
            Assert.True(editor.HandleShortcut(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.False(editor.AreDetailsExpandedForTest);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(960, 516)]
    [InlineData(1180, 760)]
    public void ExpandedDetailsLeaveRenderedVideoAndEverySelectedLayerHandleUncovered(int width, int height) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-uncovered-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = Path.Combine(root, "source.mp4");
            FrameIndexClip.Encode(clip, 640, 360, 30, FrameIndexClip.Constant(30, 30));
            double duration = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            var document = VideoEditDocument.CreateFor(640, 360, duration);
            var text = new TimedTextOverlay
            {
                Text = "Right edge", StartMs = 0, EndMs = duration,
                Bounds = new VideoLayerBounds(0.75, 0.2, 0.2, 0.5),
            };
            document.TextOverlays.Add(text);
            editor = new VideoEditorWindow(new RecordingResult(clip, duration, 30, 30, 640, 360),
                AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                Width = width, Height = height,
            };
            editor.MinHeight = Math.Min(editor.MinHeight, height);
            LoadTheme(editor);
            editor.Show();
            PumpUntil(() => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest);
            Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
            // The checks below measure the MediaElement itself, whose size arrives with MediaOpened.
            // On a cold hosted runner the open fallback makes the editor ready first, while
            // NaturalVideoWidth is still 0 (CI: Expected 640, Actual 0), so wait for MediaOpened too.
            Assert.True(RangePatternClip.PumpUntil(() => editor.HasMediaOpenedForTest || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(30))
                && editor.HasMediaOpenedForTest, editor.HasMediaFailedForTest ? editor.MediaFailureForTest : "MediaOpened did not arrive");
            Assert.Equal(640, editor.MediaForTest.NaturalVideoWidth);
            Assert.Equal(360, editor.MediaForTest.NaturalVideoHeight);
            Assert.Equal(Stretch.Uniform, editor.MediaForTest.Stretch);
            SelectTrack(editor, text.Id);
            editor.SetDetailsExpandedForTest(true);
            Settle(editor);
            var layout = (Grid)editor.Content;
            var canvas = Field<VideoLayerCanvas>(editor, "_layerCanvas");
            var rendered = new RenderTargetBitmap((int)Math.Ceiling(layout.ActualWidth), (int)Math.Ceiling(layout.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rendered.Render(layout);
            Rect panel = Bounds(Named<FrameworkElement>(layout, "VideoDetailsPanel"), layout);
            Rect video = new(canvas.TranslatePoint(canvas.ContentRect.TopLeft, layout), canvas.ContentRect.Size);
            Rect media = Bounds(editor.MediaForTest, layout);
            Assert.False(panel.IntersectsWith(media), $"Details {panel} cover rendered media {media}");
            Assert.False(panel.IntersectsWith(video), $"Details {panel} cover source frame {video}");
            Assert.Equal(640d / 360, video.Width / video.Height, precision: 6);
            Assert.True(video.Width > 0 && video.Height > 0);
            Assert.Equal(text.Id, canvas.SelectedId);
            Rect selection = canvas.SelectionViewBounds ?? throw new Xunit.Sdk.XunitException("Selected layer handles were not rendered.");
            Rect selectionInWindow = new(canvas.TranslatePoint(selection.TopLeft, layout), selection.Size);
            Assert.False(panel.IntersectsWith(selectionInWindow), $"Details {panel} cover layer handles {selectionInWindow}");
            Rect outline = Rect.Inflate(selection, -5, -5);
            foreach (Point handle in new[] { outline.TopLeft, outline.TopRight, outline.BottomRight, outline.BottomLeft })
            {
                Point position = canvas.TranslatePoint(handle, layout);
                Assert.False(panel.Contains(position));
                var hit = Assert.IsAssignableFrom<DependencyObject>(layout.InputHitTest(position));
                Assert.True(IsWithin(hit, canvas), $"Handle {position} hit {hit.GetType().Name} instead of the layer canvas.");
            }
            AssertInside(editor.TimelineForTest.TrimStripForTest, layout, layout.ActualHeight);
            AssertInside(Command(layout, "Text_9A9C87658130"), layout, layout.ActualHeight);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void DetailsKeyboardFocusEntersTraversesAndReturnsWithoutNativeInput() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-details-focus-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 760, 555);
            // A selected layer enables Edit/Delete, so traversal covers the complete inspector.
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.True(editor.TimelineForTest.SetTrimRange(2_000, 8_000));
            var layout = (Grid)editor.Content;
            ToggleButton toggle = Named<ToggleButton>(layout, "VideoDetailsToggle");
            Assert.Same(toggle, Keyboard.Focus(toggle));
            Assert.True(toggle.IsKeyboardFocused);
            KeyEventArgs enter = KeyEvent(editor, Key.Enter);
            toggle.RaiseEvent(enter);
            Settle(editor);
            Assert.True(enter.Handled);
            Assert.True(editor.AreDetailsExpandedForTest);
            Button first = Command(layout, "Text_DC710AA11970");
            Assert.Same(first, Keyboard.FocusedElement);
            FrameworkElement panel = Named<FrameworkElement>(layout, "VideoDetailsPanel");
            var expected = new HashSet<DependencyObject>
            {
                first, Command(layout, "Text_E166B75FADF4"), Command(layout, "Text_E5CAF7CFC916"),
                Command(layout, "Text_DFCA22AB9F61"), Command(layout, "Text_B39342508541"), Command(layout, "Text_B753165F6585"),
                Field<TextBox>(editor, "_trimInInput"), Field<TextBox>(editor, "_trimOutInput"),
                Command(layout, "VideoEditorUx_ResetTrim"), Command(layout, "Video.AddLayer"),
                Field<ListBox>(editor, "_overlayList"),
                Field<Button>(editor, "_editTextButton"), Field<Button>(editor, "_deleteTextButton"),
            };
            var reached = new HashSet<DependencyObject>();
            for (int step = 0; step < 40 && !expected.IsSubsetOf(reached); step++)
            {
                var focused = Assert.IsAssignableFrom<DependencyObject>(Keyboard.FocusedElement);
                foreach (DependencyObject command in expected)
                    if (IsWithin(focused, command)) reached.Add(command);
                if (expected.IsSubsetOf(reached)) break;
                Assert.True(Assert.IsAssignableFrom<UIElement>(focused).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Settle(editor);
            }
            Assert.True(expected.IsSubsetOf(reached), "Tab did not reach: " + string.Join(", ", expected.Except(reached).Select(AutomationProperties.GetName)));
            TextBox input = Field<TextBox>(editor, "_trimInInput");
            Assert.Same(input, Keyboard.Focus(input));
            Assert.True(panel.IsKeyboardFocusWithin);
            editor.SetDetailsExpandedForTest(false);
            Settle(editor);
            Assert.Same(toggle, Keyboard.FocusedElement);
            Assert.False(panel.IsVisible);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void TrackDoubleClickUsesTheSelectedEditPathAndRejectsSingleMissAndBusyClicks() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-track-double-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var text = new TimedTextOverlay { Text = "Selected text", StartMs = 0, EndMs = 8_000 };
            Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Add(text);
            typeof(VideoEditorWindow).GetMethod("RefreshOverlayList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [null]);
            SelectTrack(editor, text.Id);
            Settle(editor);
            VideoLayerTimeline track = Field<VideoLayerTimeline>(editor, "_layerTimeline");
            Rect bar = track.SelectedBarBounds;
            Assert.False(bar.IsEmpty);
            Point pointer = new(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
            editor.LayerTrackPointerPositionForTest = _ => pointer;
            int edits = 0;
            Field<Dictionary<string, Action>>(editor, "_shortcutActions")["EditLayer"] = () =>
            {
                Assert.Equal(text.Id, track.SelectedLayerId);
                Assert.Same(text, Assert.IsType<ListBoxItem>(Field<ListBox>(editor, "_overlayList").SelectedItem).Tag);
                edits++;
            };
            MouseButtonEventArgs Click(int count)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, count);
                track.RaiseEvent(args);
                return args;
            }
            Assert.False(Click(1).Handled);
            Assert.Equal(0, edits);
            pointer = new Point(bar.Right + 20, bar.Bottom + 20);
            Assert.False(Click(2).Handled);
            Assert.Equal(0, edits);
            pointer = new Point(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
            MethodInfo busy = typeof(VideoEditorWindow).GetMethod("SetOperationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busy.Invoke(editor, [true]);
            Click(2);
            Assert.Equal(0, edits);
            busy.Invoke(editor, [false]);
            Assert.True(Click(2).Handled);
            Assert.Equal(1, edits);
            Assert.False(editor.AreDetailsExpandedForTest);
        }
        finally
        {
            if (editor is not null)
            {
                editor.LayerTrackPointerPositionForTest = null;
                typeof(VideoEditorWindow).GetMethod("SetOperationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            }
            editor?.Close(); OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ExpandedDetailsCheckedTemplateUsesNoAccentFillAlongsideThePrimarySave() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-one-primary-");
        VideoEditorWindow? editor = null;
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            editor = OpenReadyEditor(root, 10_000, show: true);
            editor.SetDetailsExpandedForTest(true);
            Settle(editor);
            var layout = (Grid)editor.Content;
            object primary = editor.FindResource("Button.Primary");
            Assert.Same(Command(layout, "MediaExport_SaveEdits"),
                Assert.Single(Descendants(layout).OfType<Button>(), button => ReferenceEquals(button.Style, primary)));
            ToggleButton toggle = Named<ToggleButton>(layout, "VideoDetailsToggle");
            var checkedLayer = Assert.IsType<Border>(toggle.Template.FindName("CheckedLayer", toggle));
            Assert.True(checkedLayer.Opacity > 0, "The checked template layer must actually be displayed.");
            Brush fill = Assert.IsAssignableFrom<Brush>(checkedLayer.Background);
            string[] accentKeys = ResourceKeys(editor.Resources).Where(key => key.StartsWith("Accent.", StringComparison.Ordinal)).Distinct().ToArray();
            Assert.Contains("Accent.Subtle", accentKeys);
            foreach (string key in accentKeys)
            {
                if (editor.TryFindResource(key) is not Brush accent) continue;
                Assert.False(ReferenceEquals(fill, accent), $"Expanded Details uses {key}; the visual-system neutral selection style must be integrated.");
            }
            Assert.False(ReferenceEquals(checkedLayer.BorderBrush, editor.TryFindResource("Border.Accent")),
                "Expanded Details uses Border.Accent; the visual-system neutral selection style must be integrated.");
        }
        finally
        {
            editor?.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void CollapsedInspectorKeepsNavigationTrimHistoryAndLayerShortcutsWorking() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-keys-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.False(editor.AreDetailsExpandedForTest);
            timeline.SetPlayhead(2_000);
            Assert.True(editor.HandleShortcut(Key.I, ModifierKeys.None));
            timeline.SetPlayhead(8_000);
            Assert.True(editor.HandleShortcut(Key.O, ModifierKeys.None));
            Assert.Equal(2_000, timeline.InMs, 3);
            Assert.Equal(8_000, timeline.OutMs, 3);
            Assert.True(editor.HandleShortcut(Key.Home, ModifierKeys.None));
            Assert.Equal(timeline.InMs, timeline.PlayheadMs, 3);
            Assert.True(editor.HandleShortcut(Key.End, ModifierKeys.None));
            Assert.Equal(timeline.OutMs, timeline.PlayheadMs, 3);
            timeline.SetPlayhead(4_000);
            Assert.True(editor.HandleShortcut(Key.OemPeriod, ModifierKeys.None));
            Assert.True(timeline.PlayheadMs > 4_000);
            Assert.True(editor.HandleShortcut(Key.OemComma, ModifierKeys.None));
            Assert.Equal(4_000, timeline.PlayheadMs, 1);
            Assert.True(editor.HandleShortcut(Key.Right, ModifierKeys.None));
            Assert.True(timeline.PlayheadMs > 4_000);
            Assert.True(editor.HandleShortcut(Key.Left, ModifierKeys.None));
            Assert.Equal(4_000, timeline.PlayheadMs, 1);
            Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
            Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").SplitPoints);
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").SplitPoints);
            Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
            Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").SplitPoints);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.False(editor.AreDetailsExpandedForTest);

            // Dialog commands use the same shortcut dispatcher while the inspector is folded;
            // replace only their modal endpoints so this test never opens an interactive dialog.
            var actions = Field<Dictionary<string, Action>>(editor, "_shortcutActions");
            int add = 0, edit = 0;
            actions["AddText"] = () => add++;
            actions["EditLayer"] = () => edit++;
            var text = new TimedTextOverlay { Text = "Selected text", StartMs = 2_000, EndMs = 8_000 };
            Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Add(text);
            typeof(VideoEditorWindow).GetMethod("RefreshOverlayList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [null]);
            VideoLayerTimeline tracks = Field<VideoLayerTimeline>(editor, "_layerTimeline");
            tracks.SelectLayer(text.Id);
            typeof(VideoLayerTimeline).GetMethod("NotifyLayerSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracks, null);
            Assert.Equal(text.Id, tracks.SelectedLayerId);
            ListBoxItem selected = Assert.IsType<ListBoxItem>(Field<ListBox>(editor, "_overlayList").SelectedItem);
            Assert.Same(text, selected.Tag);
            Assert.False(Field<Border>(editor, "_layerPanel").IsVisible);
            Assert.True(editor.HandleShortcut(Key.T, ModifierKeys.Control));
            Assert.True(editor.HandleShortcut(Key.F2, ModifierKeys.None));
            Assert.Equal(1, add);
            Assert.Equal(1, edit);
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            tracks.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Equal(2, edit);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void BusyEditorKeepsDetailsAccessibleWhileEditingAndSeekingStayDisabled() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-busy-details-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            editor.TimelineForTest.SetPlayhead(4_000);
            MethodInfo busy = typeof(VideoEditorWindow).GetMethod("SetOperationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busy.Invoke(editor, [true]);
            var layout = (Grid)editor.Content;
            ToggleButton details = Named<ToggleButton>(layout, "VideoDetailsToggle");
            Assert.True(details.IsEnabled);
            details.IsChecked = true;
            editor.UpdateLayout();
            Assert.True(editor.AreDetailsExpandedForTest);
            foreach (string key in new[] { "VideoEditorUx_Trim", "VideoEditorUx_AddText", "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130", "Video.AddLayer" })
                Assert.False(Command(layout, key).IsEnabled, key);
            Assert.False(Field<TextBox>(editor, "_trimInInput").IsEnabled);
            Assert.False(editor.TimelineForTest.IsEnabled);
            Assert.False(Field<VideoLayerTimeline>(editor, "_layerTimeline").IsEnabled);
            editor.HandleShortcut(Key.Right, ModifierKeys.None);
            Assert.Equal(4_000, editor.TimelineForTest.PlayheadMs, 3);
            busy.Invoke(editor, [false]);
            Assert.True(Command(layout, "VideoEditorUx_Trim").IsEnabled);
            Assert.True(editor.AreDetailsExpandedForTest);
        }
        finally
        {
            if (editor is not null)
                typeof(VideoEditorWindow).GetMethod("SetOperationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants(root).OfType<T>(), element => element.Name == name);

    private static Button Command(DependencyObject root, string key) =>
        Assert.Single(Descendants(root).OfType<Button>(), button => AutomationProperties.GetName(button) == UiText.Get(key));

    private static Rect Bounds(FrameworkElement element, UIElement relativeTo) =>
        new(element.TranslatePoint(new Point(), relativeTo), element.RenderSize);

    private static void AssertSeparatedPreview(VideoEditorWindow editor, Rect collapsedPreview, bool expanded)
    {
        var layout = (Grid)editor.Content;
        Rect preview = Bounds(Named<FrameworkElement>(layout, "VideoPreviewViewport"), layout);
        FrameworkElement details = Named<FrameworkElement>(layout, "VideoDetailsPanel");
        Assert.Equal(expanded, details.IsVisible);
        if (!expanded)
        {
            Assert.Equal(collapsedPreview, preview);
            return;
        }
        Assert.True(preview.Width > 0 && preview.Width < collapsedPreview.Width);
        Assert.True(preview.Height >= 112, $"Expanded preview height is {preview.Height:0.#}.");
        Assert.Equal(collapsedPreview.Left, preview.Left, precision: 3);
        Assert.False(preview.IntersectsWith(Bounds(details, layout)), "Details must reserve their own column outside the preview.");
        Rect content = Field<VideoLayerCanvas>(editor, "_layerCanvas").ContentRect;
        Assert.Equal(640d / 360, content.Width / content.Height, precision: 6);
    }

    private static void SelectTrack(VideoEditorWindow editor, Guid id)
    {
        VideoLayerTimeline track = Field<VideoLayerTimeline>(editor, "_layerTimeline");
        track.SelectLayer(id);
        typeof(VideoLayerTimeline).GetMethod("NotifyLayerSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(track, null);
        Assert.Equal(id, track.SelectedLayerId);
    }

    private static void Settle(Window window) =>
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

    private static void PumpUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(15))
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(ready(), "The generated source video did not finish opening within 15 seconds.");
    }

    private static KeyEventArgs KeyEvent(Window window, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    private static bool IsWithin(DependencyObject current, DependencyObject ancestor)
    {
        for (DependencyObject? element = current; element is not null;)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    private static IEnumerable<string> ResourceKeys(ResourceDictionary resources)
    {
        foreach (string key in resources.Keys.OfType<string>()) yield return key;
        foreach (ResourceDictionary merged in resources.MergedDictionaries)
            foreach (string key in ResourceKeys(merged)) yield return key;
    }
}
