using System.Windows.Input;

namespace MyCapture.App;

internal enum ShortcutScope { Annotation, Crop, QuickAccess, Video, Ocr }
internal sealed record ShortcutDefinition(ShortcutScope Scope, string Command, Key Key, ModifierKeys Modifiers, string Hint);

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
    internal static ShortcutDefinition Get(ShortcutScope scope, string command) => All.First(item => item.Scope == scope && item.Command == command);
    internal static string? Match(ShortcutScope scope, Key key, ModifierKeys modifiers) =>
        All.FirstOrDefault(item => item.Scope == scope && item.Key == key && item.Modifiers == modifiers)?.Command;
    internal static bool Execute(ShortcutScope scope, Key key, ModifierKeys modifiers, IReadOnlyDictionary<string, Action> actions)
    {
        string? command = Match(scope, key, modifiers);
        if (command is null || !actions.TryGetValue(command, out Action? action)) return false;
        action(); return true;
    }
}
