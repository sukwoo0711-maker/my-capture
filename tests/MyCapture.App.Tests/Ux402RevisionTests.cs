using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Core.Localization;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ux402RevisionTests
{
    [Theory]
    [InlineData("Done", false)] [InlineData("Copy", false)] [InlineData("Save", false)]
    [InlineData("Done", true)] [InlineData("Copy", true)] [InlineData("Save", true)]
    public void CommitAppliesChangedCropBeforeHandingOffPixels(string command, bool keyboard) => StaTestHost.Run(() =>
    {
        var editor = Editor();
        editor.ShortcutActions["Crop"]();
        editor.PendingCrop!.SetWidth(200); editor.PendingCrop.SetHeight(100);
        AnnotationEditingResult? result = null;
        editor.CommitRequested = value => { result = value; return Task.FromResult(false); };
        if (keyboard)
        {
            var shortcut = ShortcutCatalog.Get(ShortcutScope.Annotation, command);
            Assert.True(editor.HandleShortcut(shortcut.Key, shortcut.Modifiers));
        }
        else
        {
            var button = Descendants(editor).OfType<Button>().Single(candidate => AutomationProperties.GetAcceleratorKey(candidate)
                == ShortcutCatalog.Get(ShortcutScope.Annotation, command).Hint);
            if (command == "Save")
                button.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+S")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Assert.NotNull(result);
        Assert.Equal(200, result.Document.CanvasWidth); Assert.Equal(100, result.Document.CanvasHeight);
        Assert.Equal(200, result.SelectedBitmap.PixelWidth); Assert.Equal(100, result.SelectedBitmap.PixelHeight);
        Assert.Null(editor.PendingCrop);
        editor.ShortcutActions["Undo"]();
        Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
        editor.ShortcutActions["Redo"]();
        Assert.Equal(200, editor.CreateCurrentImageSnapshot().CanvasWidth);
    });

    [Fact]
    public void UntouchedCropAddsNoUndoCommand() => StaTestHost.Run(() =>
    {
        var editor = Editor();
        editor.ShortcutActions["RotateRight"]();
        editor.ShortcutActions["Crop"]();
        editor.CommitRequested = _ => Task.FromResult(false);
        editor.ShortcutActions["Done"]();
        editor.ShortcutActions["Undo"]();
        Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
        Assert.Equal(360, editor.CreateCurrentImageSnapshot().CanvasHeight);
    });

    [Theory]
    [InlineData("Crop", "Select")] [InlineData("Rectangle", "Rectangle")]
    public void CropIsNeverRememberedOrReopened(string stored, string expected) => StaTestHost.Run(() =>
    {
        var read = AnnotationEditorPreferences.Read; var write = AnnotationEditorPreferences.Write;
        var saved = new AnnotationDefaults { LastTool = stored };
        try
        {
            AnnotationEditorPreferences.Read = () => saved;
            AnnotationEditorPreferences.Write = value => saved = value;
            var editor = Editor();
            Assert.Null(editor.PendingCrop);
            Assert.Equal(expected, Field<object>(editor, "_preferredTool").ToString());
            editor.ShortcutActions["Crop"]();
            Assert.Equal(expected, saved.LastTool);
            editor.CancelPendingCrop();
            Assert.Null(Editor().PendingCrop);
        }
        finally { AnnotationEditorPreferences.Read = read; AnnotationEditorPreferences.Write = write; }
    });

    [Fact]
    public void CropInspectorEnterConfirmsOnlyAndEscapeCancelsWhileComboAndButtonsOwnActivation() => StaTestHost.Run(() =>
    {
        var editor = Editor(); var window = new Window { Content = editor, Width = 1000, Height = 620 };
        try
        {
            window.Show(); window.Activate(); editor.ShortcutActions["Crop"](); window.UpdateLayout();
            var width = Field<TextBox>(editor, "_cropWidth"); var height = Field<TextBox>(editor, "_cropHeight");
            width.Focus(); width.Text = "2"; width.Text = "200";
            Assert.Equal(640, editor.PendingCrop!.Region.Width);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            Assert.NotNull(editor.PendingCrop); Assert.Equal(200, editor.PendingCrop.Region.Width);
            Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
            Assert.Same(width, Keyboard.FocusedElement);
            height.Focus(); height.Text = "100";
            Assert.Equal(360, editor.PendingCrop!.Region.Height);
            width.Focus(); Assert.Equal(100, editor.PendingCrop.Region.Height);
            width.Text = "9999"; height.Focus();
            Assert.Equal(640, editor.PendingCrop.Region.Width); Assert.Equal(100, editor.PendingCrop.Region.Height);
            var combo = Field<ComboBox>(editor, "_cropRatio"); combo.Focus();
            Assert.False(editor.HandleShortcut(Key.Down, ModifierKeys.None));
            Assert.False(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            Assert.False(editor.HandleShortcut(Key.R, ModifierKeys.None));
            // Apply and Cancel moved from the panel into the crop command bar.
            var cancel = Descendants(editor).OfType<Button>().Single(button =>
                AutomationProperties.GetAcceleratorKey(button) == "Esc" && AutomationProperties.GetAutomationId(button) == "EditorFlowCropCancel");
            Assert.True(cancel.IsVisible);
            cancel.Focus();
            Assert.False(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            RaiseKey(cancel, Key.Enter);
            Assert.Null(editor.PendingCrop);
            Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
            editor.ShortcutActions["Crop"](); width.Focus(); width.Text = "240";
            Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            Assert.Null(editor.PendingCrop); Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
            editor.ShortcutActions["Crop"](); width.Focus(); width.Text = "240";
            AnnotationEditingResult? saved = null;
            editor.CommitRequested = value => { saved = value; return Task.FromResult(false); };
            Assert.True(editor.HandleShortcut(Key.S, ModifierKeys.Control));
            Assert.NotNull(saved); Assert.Equal(240, saved.Document.CanvasWidth);
            Assert.Null(editor.PendingCrop);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("20.5")]
    [InlineData("1")] [InlineData("-10")] [InlineData("999999999999999")]
    public void InvalidCropTextSurvivesFocusChangesAndBlocksEveryCommitUntilCorrected(string invalid) => StaTestHost.Run(() =>
    {
        var editor = Editor(); var window = new Window { Content = editor, Width = 1000, Height = 620 };
        try
        {
            window.Show(); window.Activate(); editor.ShortcutActions["Crop"](); window.UpdateLayout();
            var width = Field<TextBox>(editor, "_cropWidth"); var height = Field<TextBox>(editor, "_cropHeight");
            width.Focus(); width.Text = invalid;
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            Assert.Equal(invalid, width.Text); Assert.Same(width, Keyboard.FocusedElement);
            Assert.NotEmpty(AutomationProperties.GetHelpText(width));
            Assert.Equal(UiText.Get("Crop.InvalidSize"), Field<TextBlock>(editor, "_statusText").Text);
            AnnotationEditingResult? committed = null;
            editor.CommitRequested = value => { committed = value; return Task.FromResult(false); };
            // Invalid input blocks even an otherwise untouched full-image crop.
            foreach (string command in new[] { "Done", "Copy", "Save", "SaveAs" }) editor.ShortcutActions[command]();
            Assert.Null(committed); Assert.NotNull(editor.PendingCrop);
            height.Focus(); height.Text = "100"; editor.HandleShortcut(Key.Enter, ModifierKeys.None);
            Assert.Equal(invalid, width.Text); Assert.Equal(100, editor.PendingCrop.Region.Height);
            editor.Focus(); editor.HandleShortcut(Key.Enter, ModifierKeys.None);
            Assert.NotNull(editor.PendingCrop); Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
            width.Focus(); width.Text = "240";
            editor.HandleShortcut(Key.Enter, ModifierKeys.None);
            Assert.NotNull(editor.PendingCrop); Assert.Empty(AutomationProperties.GetHelpText(width));
            editor.ShortcutActions["Save"]();
            Assert.NotNull(committed); Assert.Equal(240, committed.Document.CanvasWidth); Assert.Equal(100, committed.Document.CanvasHeight);
            Assert.Null(editor.PendingCrop);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("ko", AfterCaptureAction.Editor)] [InlineData("ko", AfterCaptureAction.QuickAccess)] [InlineData("ko", AfterCaptureAction.ClipboardOnly)]
    [InlineData("en", AfterCaptureAction.Editor)] [InlineData("en", AfterCaptureAction.QuickAccess)] [InlineData("en", AfterCaptureAction.ClipboardOnly)]
    public void LibrarySaveFailureGuidanceMatchesConfiguredMode(string language, AfterCaptureAction mode)
    {
        using var culture = UiText.UseLanguage(language);
        string message = App.LibrarySaveFailureMessage(mode, new IOException("test-save-failure"));
        Assert.Contains(language == "ko" ? "라이브러리" : "library", message);
        Assert.Equal(mode == AfterCaptureAction.Editor, message.Contains(language == "ko" ? "편집창" : "editor", StringComparison.Ordinal));
        Assert.Equal(mode != AfterCaptureAction.Editor, message.Contains(language == "ko" ? "다시 캡처" : "capture again", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyboardFocusedButtonsKeepOnlySpaceAndEnterForActivation() => StaTestHost.Run(() =>
    {
        var editor = Editor(); var window = new Window { Content = editor, Width = 1000, Height = 620 };
        try
        {
            window.Show(); window.Activate(); window.UpdateLayout();
            var button = Descendants(editor).OfType<ToggleButton>().First();
            Assert.True(button.Focus());
            Assert.False(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            Assert.False(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
            foreach (var (key, tool) in new[] { (Key.R, "Rectangle"), (Key.E, "Ellipse") })
            {
                Assert.True(editor.HandleShortcut(key, ModifierKeys.None));
                Assert.Equal(tool, Descendants(editor).OfType<ToggleButton>().Single(b => b.IsChecked == true)
                    .GetValue(System.Windows.Automation.AutomationProperties.AcceleratorKeyProperty) switch
                    { "R" => "Rectangle", "E" => "Ellipse", _ => "unexpected" });
            }
            // Crop is a mode of the command bar rather than a rail tool.
            Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
            Assert.NotNull(editor.PendingCrop); Assert.True(editor.IsCropCommandBarVisible);
            Assert.DoesNotContain(Descendants(editor).OfType<ToggleButton>(), b => b.IsChecked == true);
            Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None)); Assert.Null(editor.PendingCrop);
            Assert.False(editor.IsCropCommandBarVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TooltipKeepsDescriptionAndCommandGroupsAlignOrWrapByAvailableWidth() => StaTestHost.Run(() =>
    {
        var button = new Button { Content = "Copy", ToolTip = "Copy the edited image and close" };
        AutomationProperties.SetName(button, "Copy");
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Copy");
        Assert.Equal("Copy the edited image and close (Ctrl+C)", button.ToolTip);
        var alreadyLabeled = new Button { Content = "Copy", ToolTip = "Copy the edited image and close (Ctrl+C)" };
        ShortcutPresentation.Attach(alreadyLabeled, ShortcutScope.Annotation, "Copy");
        Assert.Equal(button.ToolTip, alreadyLabeled.ToolTip);
        var panel = new CommandGroupsPanel();
        var left = new Border { Width = 240, Height = 40 }; var right = new Border { Width = 280, Height = 40 };
        panel.Children.Add(left); panel.Children.Add(right);
        foreach (double width in new[] { 900d, 480d })
        {
            panel.Measure(new Size(width, 200)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            Point position = right.TranslatePoint(default, panel);
            Assert.Equal(width == 900 ? width - right.ActualWidth : 0, position.X);
            Assert.Equal(width == 900 ? 0 : 40 + CommandGroupsPanel.GroupGap, position.Y);
        }
    });

    [Fact]
    public void VideoRetainsFrameModifiersAndBusyKeyHandlingWithoutEatingIdleEscape() => StaTestHost.Run(() =>
    {
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory()), NullLoggerFactory.Instance);
        try
        {
            foreach (var (key, command) in new[] { (Key.Left, "PreviousFrame"), (Key.Right, "NextFrame") })
                Assert.Equal(command, ShortcutCatalog.Match(ShortcutScope.Video, key, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.False(video.HandleShortcut(Key.Escape, ModifierKeys.None));
            typeof(VideoEditorWindow).GetField("_operationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(video, true);
            foreach (var key in new[] { Key.Space, Key.Left, Key.Right }) Assert.True(video.HandleShortcut(key, ModifierKeys.None));
            Assert.True(video.HandleShortcut(Key.Right, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.True(video.HandleShortcut(Key.Escape, ModifierKeys.None));
            Assert.False(video.HandleShortcut(Key.Z, ModifierKeys.Control));
        }
        finally
        {
            typeof(VideoEditorWindow).GetField("_operationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(video, false);
            video.Close();
        }
    });

    private static void RaiseKey(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static AnnotationEditorControl Editor()
    {
        var bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 360 * 4], 640 * 4);
        bitmap.Freeze(); var region = new RectD(0, 0, 640, 360);
        return new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
