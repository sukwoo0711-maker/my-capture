using System.Windows.Input;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AnnotationEditorSelectionShortcutTests
{
    [Theory]
    [InlineData(Key.A, ModifierKeys.Control, "SelectAll")]
    [InlineData(Key.D, ModifierKeys.Control, "Duplicate")]
    [InlineData(Key.OemCloseBrackets, ModifierKeys.Control, "BringForward")]
    [InlineData(Key.OemOpenBrackets, ModifierKeys.Control, "SendBackward")]
    [InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, "Redo")]
    [InlineData(Key.Left, ModifierKeys.None, "Left")]
    [InlineData(Key.Right, ModifierKeys.Shift, "RightFast")]
    [InlineData(Key.Up, ModifierKeys.None, "Up")]
    [InlineData(Key.Down, ModifierKeys.Shift, "DownFast")]
    public void SelectionShortcuts_KeepPhysicalImeKeyRouting(Key key, ModifierKeys modifiers, string command)
    {
        Assert.Equal(command, ShortcutCatalog.Match(ShortcutScope.Annotation, key, modifiers));
        Key physical = ShortcutInput.Resolve(Key.ImeProcessed, key, textInputFocused: false);
        Assert.Equal(command, ShortcutCatalog.Match(ShortcutScope.Annotation, physical, modifiers));
        Assert.Equal(Key.ImeProcessed, ShortcutInput.Resolve(Key.ImeProcessed, key, textInputFocused: true));
    }

    [Fact]
    public void SelectionShortcuts_DoNotConflictWithExistingChords()
    {
        Assert.All(ShortcutCatalog.All.GroupBy(shortcut => (shortcut.Scope, shortcut.Key, shortcut.Modifiers)),
            group => Assert.Single(group));
        Assert.Equal("Arrow", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.A, ModifierKeys.None));
        Assert.Equal("Copy", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.C, ModifierKeys.Control));
        Assert.Equal("Redo", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.Y, ModifierKeys.Control));
        Assert.Equal("Cancel", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.Escape, ModifierKeys.None));
        Assert.Equal("LeftFast", ShortcutCatalog.Match(ShortcutScope.Crop, Key.Left, ModifierKeys.Shift));
    }
}
