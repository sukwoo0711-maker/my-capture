using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

/// <summary>App-owned preference persistence; absent in diagnostics and isolated controls.</summary>
internal static class AnnotationEditorPreferences
{
    internal static Func<AnnotationDefaults>? Read { get; set; }
    internal static Func<bool>? ReadShowSourceWindowTitle { get; set; }
    internal static Action<AnnotationDefaults>? Write { get; set; }
}
