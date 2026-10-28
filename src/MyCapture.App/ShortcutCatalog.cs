using System.Windows.Input;

namespace MyCapture.App;

internal enum ShortcutScope { Annotation, Crop, QuickAccess, Video, Ocr, Selector, Library, Pin }
internal sealed record ShortcutDefinition(ShortcutScope Scope, string Command, Key Key, ModifierKeys Modifiers, string Hint);
internal sealed record ShortcutHelpEntry(string Action, IReadOnlyList<ShortcutDefinition> Shortcuts, string? GestureKey = null)
{
    internal string Keys => GestureKey is null
        ? string.Join(" / ", Shortcuts.Select(shortcut => shortcut.Hint).Distinct())
        : UiText.Get(GestureKey);
}

/// <summary>Shared source for routing and the small hints next to actions.</summary>
internal static class ShortcutCatalog
{
    internal static readonly IReadOnlyList<ShortcutDefinition> All = Build();
    private static IReadOnlyList<ShortcutDefinition> Build()
    {
        var definitions = new List<ShortcutDefinition>();
        void Add(ShortcutScope scope, string command, Key key, string hint, ModifierKeys modifiers = ModifierKeys.None) =>
            definitions.Add(new(scope, command, key, modifiers, hint));
        foreach (var (command, key) in new[] { ("Select", Key.V), ("Crop", Key.X), ("Rectangle", Key.R), ("Ellipse", Key.E),
            ("Arrow", Key.A), ("Pen", Key.P), ("Highlighter", Key.H), ("Text", Key.T), ("StepNumber", Key.N), ("Image", Key.I), ("Stamp", Key.S), ("Mosaic", Key.M), ("Blur", Key.B) })
            Add(ShortcutScope.Annotation, command, key, key.ToString());
        Add(ShortcutScope.Annotation, "Done", Key.Enter, "Ctrl+Enter", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "EditText", Key.Enter, "Enter");
        Add(ShortcutScope.Annotation, "EditText", Key.F2, "F2");
        Add(ShortcutScope.Annotation, "Copy", Key.C, "Ctrl+C", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "Save", Key.S, "Ctrl+S", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "SaveAs", Key.S, "Ctrl+Shift+S", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Annotation, "Privacy", Key.R, "Ctrl+Shift+R", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Annotation, "Cancel", Key.Escape, "Esc");
        Add(ShortcutScope.Annotation, "Undo", Key.Z, "Ctrl+Z", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "Redo", Key.Y, "Ctrl+Y", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "Redo", Key.Z, "Ctrl+Shift+Z", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Annotation, "SelectAll", Key.A, "Ctrl+A", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "Duplicate", Key.D, "Ctrl+D", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "BringForward", Key.OemCloseBrackets, "Ctrl+]", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "SendBackward", Key.OemOpenBrackets, "Ctrl+[", ModifierKeys.Control);
        Add(ShortcutScope.Annotation, "Delete", Key.Delete, "Del");
        Add(ShortcutScope.Annotation, "Delete", Key.Back, "Backspace");
        Add(ShortcutScope.Annotation, "RotateLeft", Key.Q, "Q");
        Add(ShortcutScope.Annotation, "RotateRight", Key.W, "W");
        Add(ShortcutScope.Crop, "Apply", Key.Enter, "Enter");
        Add(ShortcutScope.Crop, "Cancel", Key.Escape, "Esc");
        foreach (var (command, key, hint) in new[] { ("Left", Key.Left, "←"), ("Right", Key.Right, "→"), ("Up", Key.Up, "↑"), ("Down", Key.Down, "↓") })
        {
            Add(ShortcutScope.Crop, command, key, hint);
            Add(ShortcutScope.Crop, command + "Fast", key, "Shift+" + hint, ModifierKeys.Shift);
            Add(ShortcutScope.Annotation, command, key, hint);
            Add(ShortcutScope.Annotation, command + "Fast", key, "Shift+" + hint, ModifierKeys.Shift);
        }
        foreach (var (command, key) in new[] { ("Copy", Key.C), ("Edit", Key.E), ("Pin", Key.P), ("Save", Key.S), ("CopyText", Key.T) })
            Add(ShortcutScope.QuickAccess, command, key, key.ToString());
        Add(ShortcutScope.QuickAccess, "Close", Key.Escape, "Esc");
        Add(ShortcutScope.QuickAccess, "Activate", Key.Q, "Alt+Q", ModifierKeys.Alt);
        Add(ShortcutScope.QuickAccess, "Copy", Key.C, "Ctrl+C", ModifierKeys.Control);
        Add(ShortcutScope.QuickAccess, "Save", Key.S, "Ctrl+S", ModifierKeys.Control);
        Add(ShortcutScope.Ocr, "Copy", Key.C, "Ctrl+C", ModifierKeys.Control);
        Add(ShortcutScope.Ocr, "Export", Key.S, "Ctrl+S", ModifierKeys.Control);
        Add(ShortcutScope.Ocr, "Close", Key.Escape, "Esc");
        foreach (var (command, key, hint) in new[] { ("Play", Key.Space, "Space"), ("In", Key.I, "I"), ("Out", Key.O, "O"),
            ("FrameEdit", Key.E, "E"), ("First", Key.Home, "Home"), ("Last", Key.End, "End"),
            ("Previous", Key.Left, "←"), ("Next", Key.Right, "→"), ("PreviousFrame", Key.OemComma, ","), ("NextFrame", Key.OemPeriod, "."),
            ("Gif", Key.G, "G"), ("EditLayer", Key.F2, "F2"), ("DeleteLayer", Key.Delete, "Del"), ("Cancel", Key.Escape, "Esc") })
            Add(ShortcutScope.Video, command, key, hint);
        // X is "crop" in the image editor; in the recording editor the same key trims.
        Add(ShortcutScope.Video, "Trim", Key.X, "X");
        Add(ShortcutScope.Video, "PreviousFrame", Key.Left, "Shift+←", ModifierKeys.Shift);
        Add(ShortcutScope.Video, "NextFrame", Key.Right, "Shift+→", ModifierKeys.Shift);
        Add(ShortcutScope.Video, "PreviousFrame", Key.Left, "Ctrl+←", ModifierKeys.Control);
        Add(ShortcutScope.Video, "NextFrame", Key.Right, "Ctrl+→", ModifierKeys.Control);
        Add(ShortcutScope.Video, "PreviousFrame", Key.Left, "Ctrl+Shift+←", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "NextFrame", Key.Right, "Ctrl+Shift+→", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "AddText", Key.T, "Ctrl+T", ModifierKeys.Control);
        Add(ShortcutScope.Video, "Split", Key.K, "Ctrl+K", ModifierKeys.Control);
        Add(ShortcutScope.Video, "Undo", Key.Z, "Ctrl+Z", ModifierKeys.Control);
        Add(ShortcutScope.Video, "Redo", Key.Y, "Ctrl+Y", ModifierKeys.Control);
        Add(ShortcutScope.Video, "Redo", Key.Z, "Ctrl+Shift+Z", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "ZoomIn", Key.OemPlus, "Ctrl+Shift++", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "ZoomIn", Key.Add, "Ctrl+Shift++", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "ZoomOut", Key.OemMinus, "Ctrl+Shift+−", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Video, "ZoomOut", Key.Subtract, "Ctrl+Shift+−", ModifierKeys.Control | ModifierKeys.Shift);
        return definitions.AsReadOnly();
    }
    private static readonly IReadOnlyList<ShortcutDefinition> Documentation = BuildDocumentation();
    private static IReadOnlyList<ShortcutDefinition> BuildDocumentation()
    {
        var definitions = new List<ShortcutDefinition>();
        void Add(ShortcutScope scope, string command, Key key, string hint, ModifierKeys modifiers = ModifierKeys.None) =>
            definitions.Add(new(scope, command, key, modifiers, hint));
        // These scopes document existing handlers. Their routing stays with the owning surface.
        Add(ShortcutScope.Selector, "Color", Key.C, "C");
        Add(ShortcutScope.Selector, "Color", Key.C, "Shift+C", ModifierKeys.Shift);
        Add(ShortcutScope.Selector, "Text", Key.T, "T");
        Add(ShortcutScope.Selector, "Text", Key.T, "Shift+T", ModifierKeys.Shift);
        Add(ShortcutScope.Selector, "Ratio", Key.R, "R");
        Add(ShortcutScope.Selector, "Ratio", Key.R, "Shift+R", ModifierKeys.Shift);
        Add(ShortcutScope.Selector, "Confirm", Key.Enter, "Enter");
        Add(ShortcutScope.Selector, "Cancel", Key.Escape, "Esc");
        Add(ShortcutScope.Selector, "SelectAll", Key.A, "Ctrl+A", ModifierKeys.Control);
        foreach (var (key, hint) in new[] { (Key.Left, "←"), (Key.Right, "→"), (Key.Up, "↑"), (Key.Down, "↓") })
        {
            Add(ShortcutScope.Selector, "Move", key, hint);
            Add(ShortcutScope.Selector, "MoveFast", key, "Ctrl+" + hint, ModifierKeys.Control);
            Add(ShortcutScope.Selector, "Resize", key, "Shift+" + hint, ModifierKeys.Shift);
            Add(ShortcutScope.Selector, "ResizeFast", key, "Ctrl+Shift+" + hint, ModifierKeys.Control | ModifierKeys.Shift);
        }
        foreach (var (command, key, hint) in new[] { ("Pin", Key.P, "P"), ("Open", Key.Enter, "Enter"),
            ("Preview", Key.Space, "Space"), ("Rename", Key.F2, "F2"), ("Keep", Key.K, "K"), ("Move", Key.M, "M"),
            ("Text", Key.T, "T"), ("Gif", Key.G, "G"), ("Delete", Key.Delete, "Del"), ("Menu", Key.Apps, "Menu"),
            ("Back", Key.Back, "Backspace") })
            Add(ShortcutScope.Library, command, key, hint);
        Add(ShortcutScope.Library, "Copy", Key.C, "Ctrl+C", ModifierKeys.Control);
        Add(ShortcutScope.Library, "Info", Key.Enter, "Alt+Enter", ModifierKeys.Alt);
        Add(ShortcutScope.Library, "Menu", Key.F10, "Shift+F10", ModifierKeys.Shift);
        Add(ShortcutScope.Library, "Undo", Key.Z, "Ctrl+Z", ModifierKeys.Control);
        Add(ShortcutScope.Library, "Search", Key.F, "Ctrl+F", ModifierKeys.Control);
        Add(ShortcutScope.Library, "NewFolder", Key.N, "Ctrl+Shift+N", ModifierKeys.Control | ModifierKeys.Shift);
        Add(ShortcutScope.Library, "Back", Key.Left, "Alt+←", ModifierKeys.Alt);
        Add(ShortcutScope.Library, "Forward", Key.Right, "Alt+→", ModifierKeys.Alt);
        Add(ShortcutScope.Library, "SelectAll", Key.A, "Ctrl+A", ModifierKeys.Control);
        Add(ShortcutScope.Pin, "Copy", Key.C, "Ctrl+C", ModifierKeys.Control);
        Add(ShortcutScope.Pin, "Save", Key.S, "Ctrl+S", ModifierKeys.Control);
        Add(ShortcutScope.Pin, "SelectText", Key.F2, "F2");
        Add(ShortcutScope.Pin, "Close", Key.Escape, "Esc");
        return definitions.AsReadOnly();
    }

    internal static IEnumerable<ShortcutDefinition> ForScope(ShortcutScope scope) =>
        All.Concat(Documentation).Where(item => item.Scope == scope);

    internal static ShortcutDefinition Get(ShortcutScope scope, string command) => ForScope(scope).First(item => item.Command == command);
    internal static string Hints(ShortcutScope scope, params string[] commands) =>
        string.Join(" / ", commands.SelectMany(command => ForScope(scope).Where(item => item.Command == command))
            .Select(item => item.Hint).Distinct());

    internal static IReadOnlyList<ShortcutHelpEntry> Help(ShortcutScope scope)
    {
        ShortcutHelpEntry Row(string action, params string[] commands) => new(action,
            commands.SelectMany(command => ForScope(scope).Where(item => item.Command == command)).ToArray());
        return scope switch
        {
            ShortcutScope.Annotation => [
                Row("Text_8D1A750C9351", "Select"), Row("Tools_Crop", "Crop"),
                Row("Text_DC0760235344", "Rectangle"), Row("Tools.Ellipse", "Ellipse"),
                Row("Text_2785CE58DF74", "Arrow"), Row("Text_37DB9D0B1C8E", "Pen"),
                Row("Tools.Highlighter", "Highlighter"), Row("Text_258AD4B095A1", "Text"),
                Row("Tools.StepNumber", "StepNumber"), Row("Text_302BAE127938", "Image"), Row("Stamp_Place", "Stamp"),
                Row("Text_3F8046019B32", "Mosaic"), Row("EditorBlur_Name", "Blur"),
                Row("EditorUx_RotateLeftHint", "RotateLeft"), Row("EditorUx_RotateRightHint", "RotateRight"),
                Row("EditorFlow_CopyAndClose", "Copy"), Row("Shortcuts.DoneInText", "Done"),
                Row("Shortcuts.EditText", "EditText"), Row("QuickAccess.Save", "Save"), Row("Text_57950AFF46BC", "SaveAs"),
                Row("Shortcuts.Privacy", "Privacy"), Row("Shortcuts.StepBack", "Cancel"),
                Row("Shortcuts.Undo", "Undo"), Row("Shortcuts.Redo", "Redo"),
                Row("Shortcuts.SelectAll", "SelectAll"), Row("Shortcuts.Duplicate", "Duplicate"),
                Row("Shortcuts.BringForward", "BringForward"), Row("Shortcuts.SendBackward", "SendBackward"),
                Row("Shortcuts.DeleteSelection", "Delete"), Row("Shortcuts.Move1", "Left", "Right", "Up", "Down"),
                Row("Shortcuts.Move10", "LeftFast", "RightFast", "UpFast", "DownFast")],
            ShortcutScope.Crop => [Row("EditorFlow_CropApplyHint", "Apply"), Row("EditorFlow_CropCancelHint", "Cancel"),
                Row("Shortcuts.CropMove1", "Left", "Right", "Up", "Down"),
                Row("Shortcuts.CropMove10", "LeftFast", "RightFast", "UpFast", "DownFast")],
            ShortcutScope.QuickAccess => [Row("QuickAccess.Copy", "Copy"), Row("QuickAccess.Edit", "Edit"),
                Row("QuickAccess.Pin", "Pin"), Row("QuickAccess.Save", "Save"), Row("QuickAccess.CopyText", "CopyText"),
                Row("Dialog.Close", "Close"), Row("QuickAccess.Title", "Activate")],
            ShortcutScope.Selector => [Row("ColorPick_HelpLabel", "Color"), Row("TextCapture_HelpToggle", "Text"),
                Row("CaptureRatio_HelpLabel", "Ratio"), Row("Shortcuts.CaptureConfirm", "Confirm"),
                Row("Shortcuts.CaptureCancel", "Cancel"), Row("Shortcuts.CaptureAll", "SelectAll"),
                Row("Shortcuts.SelectionMove1", "Move"), Row("Shortcuts.SelectionMove10", "MoveFast"),
                Row("Shortcuts.SelectionResize1", "Resize"), Row("Shortcuts.SelectionResize10", "ResizeFast")],
            ShortcutScope.Library => [Row("Gallery.Float", "Pin"), Row("Shortcuts.LibraryCopy", "Copy"),
                Row("Shortcuts.LibraryOpen", "Open"), Row("Shortcuts.LibraryPreview", "Preview"), Row("MediaInfo.Title", "Info"),
                Row("Folder410.Rename", "Rename"), Row("LibraryUx_KeepToggleName", "Keep"),
                Row("Folder410.MoveCaptures", "Move"), Row("Shortcuts.LibraryText", "Text"), Row("Shortcuts.LibraryGif", "Gif"),
                Row("Folder410.Delete", "Delete"), Row("Shortcuts.ContextMenu", "Menu"), Row("Shortcuts.Undo", "Undo"),
                Row("Shortcuts.Search", "Search"), Row("Folder410.NewFolder", "NewFolder"),
                Row("Shortcuts.Back", "Back"), Row("Shortcuts.Forward", "Forward"), Row("Shortcuts.SelectAll", "SelectAll"),
                new("Shortcuts.MouseNavigation", [], "Shortcuts.MouseButtons"), new("Shortcuts.LibraryDrag", [], "Shortcuts.Drag")],
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
    }
    internal static string? Match(ShortcutScope scope, Key key, ModifierKeys modifiers) =>
        All.FirstOrDefault(item => item.Scope == scope && item.Key == key && item.Modifiers == modifiers)?.Command;
    internal static bool Execute(ShortcutScope scope, Key key, ModifierKeys modifiers, IReadOnlyDictionary<string, Action> actions)
    {
        string? command = Match(scope, key, modifiers);
        if (command is null || !actions.TryGetValue(command, out Action? action)) return false;
        action(); return true;
    }
}
