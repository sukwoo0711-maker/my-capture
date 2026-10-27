using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

/// <summary>App-owned preference persistence; absent in diagnostics and isolated controls.</summary>
internal static class AnnotationEditorPreferences
{
    internal static Func<AnnotationDefaults>? Read { get; set; }
    internal static Func<bool>? ReadShowSourceWindowTitle { get; set; }
    internal static Action<AnnotationDefaults>? Write { get; set; }

    /// <summary>
    /// The folder that holds the registered stamp picture, under the app's data root. Absent in
    /// diagnostics and isolated controls, where the stamp command reports that it is unavailable.
    /// </summary>
    internal static Func<string?>? StampFolder { get; set; }
}
