using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorSaveShortcutTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void SaveAndExportShortcutsShareButtonActionsAndDescribeTheirKeys(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(editor, "_shortcutActions");
            int saves = 0, exports = 0;
            actions["Save"] = () => saves++;
            actions["Export"] = () => exports++;
            Assert.Equal("Save", ShortcutCatalog.Match(ShortcutScope.Video, Key.S, ModifierKeys.Control));
            Assert.Equal("Export", ShortcutCatalog.Match(ShortcutScope.Video, Key.S, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.True(editor.HandleShortcut(Key.S, ModifierKeys.Control, focusedElement: null));
            Assert.Equal(1, saves);
            Assert.Equal(0, exports);
            Assert.True(editor.HandleShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, focusedElement: null));
            Assert.Equal(1, saves);
            Assert.Equal(1, exports);
            Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.None, focusedElement: null));
            foreach ((string name, string shortcut) in new[] { ("VideoSave", "Ctrl+S"), ("VideoExport", "Ctrl+Shift+S") })
            {
                Button button = Assert.Single(FirstRunWelcomeTests.Descendants(editor).OfType<Button>(), button => button.Name == name);
                Assert.Contains(shortcut, Assert.IsType<string>(button.ToolTip), StringComparison.Ordinal);
                Assert.Equal(shortcut, AutomationProperties.GetAcceleratorKey(button));
                Assert.Single(FirstRunWelcomeTests.Descendants(button).OfType<TextBlock>(), text => text.Text == shortcut);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Assert.Equal(2, saves);
            Assert.Equal(2, exports);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void SaveAndExportShortcutsLeaveTextInputAndBusyOperationsAlone() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = CreateEditor();
        try
        {
            Dictionary<string, Action> actions = Field<Dictionary<string, Action>>(editor, "_shortcutActions");
            int calls = 0;
            actions["Save"] = actions["Export"] = () => calls++;
            foreach (IInputElement text in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
            {
                Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Control, text));
                Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, text));
            }
            Assert.Equal(0, calls);
            typeof(VideoEditorWindow).GetField("_operationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, true);
            Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Control, focusedElement: null));
            Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, focusedElement: null));
            Assert.Equal(0, calls);
        }
        finally
        {
            typeof(VideoEditorWindow).GetField("_operationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, false);
            editor.Close();
        }
    });

    [Fact]
    public void SaveShortcutKeepsTheExistingCloseAfterUnchangedSave() => StaTestHost.Run(() =>
    {
        string root = Path.Combine(AppContext.BaseDirectory, "video-shortcut-fixture", Guid.NewGuid().ToString("N"));
        VideoEditorWindow editor = OpenReadyEditor(root, 10000);
        bool closed = false;
        editor.Closed += (_, _) => closed = true;
        try
        {
            Assert.False(editor.HasUnsavedEditsForTest);
            Assert.True(editor.HandleShortcut(Key.S, ModifierKeys.Control, focusedElement: null));
            Assert.True(closed);
        }
        finally { if (!closed) editor.Close(); }
    });

    private static VideoEditorWindow CreateEditor()
    {
        // The window is never shown; the missing source is never opened or recorded.
        string root = Path.Combine(AppContext.BaseDirectory, "video-shortcut-fixture", Guid.NewGuid().ToString("N"));
        return new VideoEditorWindow(new RecordingResult(Path.Combine(root, "not-opened.mp4"), 10000, 30, 300, 640, 360),
            AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
    }
}
