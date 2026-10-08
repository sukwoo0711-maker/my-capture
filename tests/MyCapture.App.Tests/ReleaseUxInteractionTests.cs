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
    public void HelpUsesConfiguredKeysAndSearchFiltersAcrossFourTabs()
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
                Assert.Equal(4, tabs.Items.Count);
                var capture = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(((TabItem)tabs.Items[0]).Content).Content);
                Assert.Contains(capture.Children.OfType<UniformGrid>(), row => row.Children.OfType<TextBlock>().Any(t => t.Text == "Ctrl+F8"));
                root.Children.OfType<TextBox>().Single().Text = "Ctrl+F8";
                Assert.Single(capture.Children.OfType<UniformGrid>(), row => row.Visibility == Visibility.Visible);
                help.Close();
                var onboarding = new ShortcutHelpWindow(onboarding: true);
                var steps = Assert.IsType<DockPanel>(onboarding.Content).Children.OfType<StackPanel>().Single();
                var next = steps.Children.OfType<Button>().First();
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("2 / 3", steps.Children.OfType<TextBlock>().First().Text);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("3 / 3", steps.Children.OfType<TextBlock>().First().Text);
                onboarding.Close();
            }
            finally { ShortcutHelpWindow.ReadHotkeys = previous; }
        });
    }

    [Fact]
    public void OlderSettings_DefaultsAndNewOptionsRoundTrip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Compact)!;
        Assert.True(old.General.ShowQuickAccess);
        Assert.False(old.General.PlayCaptureSound);
        Assert.False(old.Hotkeys.ScrollingCapture.IsAssigned);
        Assert.False(old.Hotkeys.DelayedCapture.IsAssigned);
        var draft = new SettingsDraft(old) { ShowQuickAccess = false, PlayCaptureSound = true,
            ScrollingCaptureHotkey = "Ctrl+Shift+F8", DelayedCaptureHotkey = "Ctrl+Shift+F7" };
        AppSettings saved = draft.ToAppSettings().DeepClone();
        Assert.False(saved.General.ShowQuickAccess);
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
            var panel = Assert.IsType<StackPanel>(card.Content);
            var actions = panel.Children.OfType<UniformGrid>().Single();
            foreach (int index in new[] { 0, 2, 3 })
                ((Button)actions.Children[index]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((1, 1, 1), (copied, pinned, saved));
            var progress = panel.Children.OfType<ProgressBar>().Single();
            card.Advance(TimeSpan.FromSeconds(9), interacting: true);
            Assert.Equal(100, progress.Value);
            card.Advance(TimeSpan.FromSeconds(4), interacting: false);
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
