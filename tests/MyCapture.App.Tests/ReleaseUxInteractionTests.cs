using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ReleaseUxInteractionTests
{
    [Fact]
    public void HelpUsesConfiguredKeysAndSearchFiltersAcrossFiveTabsIncludingLibrary()
    {
        Sta(() =>
        {
            var previous = ShortcutHelpWindow.ReadHotkeys;
            ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control, 0x77) };
            try
            {
                var help = new ShortcutHelpWindow();
                var root = Assert.IsType<DockPanel>(help.Content);
                var tabs = root.Children.OfType<TabControl>().Single();
                Assert.Equal(5, tabs.Items.Count);
                var library = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(((TabItem)tabs.Items[2]).Content).Content);
                Assert.Equal(new[] { "Ctrl+Shift+N", "F2", "M", "Del" }, library.Children.OfType<UniformGrid>()
                    .SelectMany(row => row.Children.OfType<WrapPanel>()).Select(System.Windows.Automation.AutomationProperties.GetName));
                var capture = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(((TabItem)tabs.Items[0]).Content).Content);
                Assert.Contains(capture.Children.OfType<UniformGrid>(), row => row.Children.OfType<WrapPanel>().Any(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Ctrl+F8"));
                // A search shows one list across every tab instead of filtering the selected one.
                root.Children.OfType<Grid>().Single().Children.OfType<TextBox>().Single().Text = "Ctrl+F8";
                Assert.Equal(Visibility.Collapsed, tabs.Visibility);
                var results = Assert.IsType<StackPanel>(root.Children.OfType<ScrollViewer>().Single().Content);
                Border match = Assert.Single(results.Children.OfType<Border>(), row => row.Visibility == Visibility.Visible);
                Assert.Contains(FirstRunWelcomeTests.Descendants(match).OfType<WrapPanel>(),
                    keys => System.Windows.Automation.AutomationProperties.GetName(keys) == "Ctrl+F8");
                help.Close();
                // The first-run welcome moved to its own window (FirstRunWelcomeTests); it still
                // shows the configured capture keys.
                var welcome = new WelcomeWindow(ShortcutHelpWindow.ReadHotkeys(), AfterCaptureAction.Editor);
                Assert.Contains(FirstRunWelcomeTests.Descendants(welcome).OfType<WrapPanel>(),
                    keys => System.Windows.Automation.AutomationProperties.GetName(keys) == "Ctrl+F8");
                welcome.Close();
            }
            finally { ShortcutHelpWindow.ReadHotkeys = previous; }
        });
    }

    [Fact]
    public void OlderSettings_DefaultsAndNewOptionsRoundTrip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Compact)!;
        Assert.Equal(AfterCaptureAction.Editor, old.General.AfterCapture);
        Assert.False(old.General.PlayCaptureSound);
        Assert.False(old.Hotkeys.ScrollingCapture.IsAssigned);
        Assert.False(old.Hotkeys.DelayedCapture.IsAssigned);
        var draft = new SettingsDraft(old) { AfterCapture = AfterCaptureAction.QuickAccess, PlayCaptureSound = true,
            ScrollingCaptureHotkey = "Ctrl+Shift+F8", DelayedCaptureHotkey = "Ctrl+Shift+F7" };
        AppSettings saved = draft.ToAppSettings().DeepClone();
        Assert.Equal(AfterCaptureAction.QuickAccess, saved.General.AfterCapture);
        Assert.True(saved.General.PlayCaptureSound);
        Assert.Equal("Ctrl+Shift+F8", saved.Hotkeys.ScrollingCapture.ToString());
        Assert.Equal("Ctrl+Shift+F7", saved.Hotkeys.DelayedCapture.ToString());
        Assert.Equal(saved.Hotkeys.ScrollingCapture, saved.Hotkeys.DeepCloneHotkeys().ScrollingCapture);
        Assert.Equal(saved.Hotkeys.DelayedCapture, draft.ToHotkeySettings().DelayedCapture);
    }

    [Fact]
    public void DuplicateHotkeys_ReportBothFieldsAndClearAfterCorrection()
    {
        var draft = new SettingsDraft(new AppSettings()) { ScrollingCaptureHotkey = "Ctrl+Shift+C" };
        Assert.NotEmpty(draft.GetErrors(nameof(draft.CaptureHotkey)).Cast<string>());
        Assert.NotEmpty(draft.GetErrors(nameof(draft.ScrollingCaptureHotkey)).Cast<string>());
        draft.ScrollingCaptureHotkey = "";
        Assert.Empty(draft.GetErrors(nameof(draft.CaptureHotkey)).Cast<string>());
    }

    [Theory]
    [InlineData(24.0, false, false)]
    [InlineData(23.99, false, true)]
    [InlineData(-1.0, false, true)]
    [InlineData(1.0, true, false)]
    public void RetentionWarning_OnlyUnder24Hours(double remaining, bool pinned, bool expected)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var record = new CaptureRecord { CreatedAt = now.AddHours(-168 + remaining), IsPinned = pinned };
        var tile = new GalleryItemViewModel(record, _ => "unused", 320) { Clock = () => now };
        Assert.Equal(expected, tile.IsExpiringSoon);
    }

    [Fact]
    public void RecorderMapsRealKeysAndUpdatesDraft()
    {
        Assert.Null(HotkeyRecorder.FromKey(Key.LeftCtrl, ModifierKeys.Control));
        Assert.Equal("Ctrl+Shift+F8", HotkeyRecorder.FromKey(Key.F8, ModifierKeys.Control | ModifierKeys.Shift)!.ToString());
        Sta(() =>
        {
            var draft = new SettingsDraft(new AppSettings());
            var recorder = new HotkeyRecorder { DataContext = draft };
            recorder.SetBinding(HotkeyRecorder.TextProperty, new Binding(nameof(draft.ScrollingCaptureHotkey)) { Mode = BindingMode.TwoWay });
            recorder.Accept(HotkeyRecorder.FromKey(Key.F8, ModifierKeys.Control | ModifierKeys.Shift)!);
            Assert.Equal("Ctrl+Shift+F8", draft.ScrollingCaptureHotkey);
        });
    }

    [Fact]
    public void EllipseHighlighterAndStepNumbers_PreserveUndoAndLayerJson()
    {
        var document = AnnotationDocument.CreateFor(640, 480);
        var controller = new AnnotationEditorController(document, new UndoStack()) { Tool = EditorTool.Ellipse };
        controller.PointerDown(new PointD(20, 20)); controller.PointerMove(new PointD(120, 70)); controller.PointerUp(new PointD(120, 70));
        Assert.IsType<EllipseAnnotation>(Assert.Single(document.Items));
        controller.Tool = EditorTool.Highlighter;
        controller.PointerDown(new PointD(5, 80)); controller.PointerMove(new PointD(140, 80)); controller.PointerUp(new PointD(140, 80));
        Assert.True(Assert.IsType<PenAnnotation>(document.Items.Last()).IsHighlighter);
        Assert.Equal("1", controller.AddStepNumber(new PointD(20, 100)).Text);
        Assert.Equal("2", controller.AddStepNumber(new PointD(80, 100)).Text);
        controller.PerformUndo();
        Assert.Equal(3, document.Items.Count);
        controller.PerformRedo();
        string json = JsonSerializer.Serialize(document, JsonDefaults.Compact);
        var restored = JsonSerializer.Deserialize<AnnotationDocument>(json, JsonDefaults.Compact)!;
        Assert.Equal(4, restored.Items.Count);
        Assert.Equal(new[] { "1", "2" }, restored.Items.OfType<TextAnnotation>().Where(t => t.IsStepNumber).Select(t => t.Text));
        Assert.True(((TextAnnotation)restored.Items.Last().Clone()).IsStepNumber);
    }

    [Fact]
    public void EditorToolKeyboardRoutes_SelectEveryNewTool()
    {
        Sta(() =>
        {
            var image = Image(); var rect = new RectD(0, 0, 32, 32);
            var editor = new AnnotationEditorControl(new FrozenFrame(image, rect, null, 0), rect, image);
            foreach (Key key in new[] { Key.M, Key.X, Key.E, Key.H, Key.N })
                Assert.True(editor.HandleShortcut(key, ModifierKeys.None));
            Assert.False(editor.HandleShortcut(Key.M, ModifierKeys.Alt));
        });
    }

    [Fact]
    public void QuickAccess_ActionsAreReachableAndPauseTimerDuringInteraction()
    {
        Sta(() =>
        {
            int copied = 0, pinned = 0, saved = 0;
            var card = new QuickAccessWindow(Image(), () => { copied++; return Task.FromResult(true); }, () => { },
                () => { pinned++; return Task.CompletedTask; }, () => { saved++; return Task.FromResult(true); }, () => { }, () => Task.FromResult("unused.png"));
            var panel = Assert.IsType<StackPanel>(Assert.IsType<Border>(card.Content).Child);
            IReadOnlyList<Button> actions = card.ActionButtonsForTest;
            foreach (int index in new[] { 0, 2, 3 })
                actions[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((1, 1, 1), (copied, pinned, saved));
            var progress = panel.Children.OfType<ProgressBar>().Single();
            card.Advance(TimeSpan.FromSeconds(9), interacting: true);
            Assert.Equal(100, progress.Value);
            card.Advance(QuickAccessWindow.Lifetime / 2, interacting: false);
            Assert.Equal(50, progress.Value);
            card.Close();
        });
    }

    [Fact]
    public void ErrorDetailsAreCollapsedAndSeparateFromRecoveryCopy()
    {
        Sta(() =>
        {
            var error = new IOException("technical-detail-token");
            Assert.DoesNotContain(error.Message, ThemedDialog.RecoveryMessage(error));
            var dialog = new ThemedDialog(ThemedDialog.RecoveryMessage(error), "Save", MessageBoxButton.OKCancel,
                MessageBoxResult.Cancel, error.ToString());
            var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(dialog.Content).Content);
            Expander details = panel.Children.OfType<Expander>().Single();
            Assert.False(details.IsExpanded);
            Assert.DoesNotContain(panel.Children.OfType<TextBlock>(), t => t.Text.Contains(error.Message, StringComparison.Ordinal));
            Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
            dialog.Close();
        });
    }

    private static BitmapSource Image()
    {
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4);
        bitmap.Freeze(); return bitmap;
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
