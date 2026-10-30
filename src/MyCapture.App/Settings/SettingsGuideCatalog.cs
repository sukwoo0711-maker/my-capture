using System.IO;

namespace MyCapture.App.Settings;

/// <summary>A comparison of one option. Asset names are fixed identifiers, never settings or user paths.</summary>
internal sealed record SettingsGuideComparison(string Id, string SettingKey, int MotionFrames = 0)
{
    public string Title => UiText.Get($"SettingsGuide.{Id}.Title");
    internal string Before => UiText.Get(Id == "G06.method" ? "RecordingBackend_Auto" : $"SettingsGuide.{Id}.Before");
    internal string After => UiText.Get(Id switch
    {
        "G01.card" => "Capture.AfterCard", "G01.editor" => "Capture.AfterEditor", "G01.clipboard" => "Capture.AfterClipboard",
        "G06.method" => "RecordingBackend_Gdi", _ => $"SettingsGuide.{Id}.After",
    });
    internal string Description => UiText.Get($"SettingsGuide.{Id}.Description");
    internal string AssetFile(string language, string frame) => $"{Id}.{language}.{frame}.png";
}

internal sealed record SettingsGuideDefinition(string Id, IReadOnlyList<SettingsGuideComparison> Comparisons)
{
    internal string Title => UiText.Get($"SettingsGuide.{Id}.Title");
    internal string ButtonLabel => UiText.Get(Id == "G09" ? "SettingsGuide.CompareResults" : "SettingsGuide.SeeDifference");
    internal IReadOnlyList<string> SettingKeys => Comparisons.SelectMany(c => c.SettingKey.Split('|')).Distinct().ToArray();
}

/// <summary>Stable guide and serialized setting identifiers for settings rows and search.</summary>
internal static class SettingsGuideCatalog
{
    internal static IReadOnlyList<SettingsGuideDefinition> All { get; } = Array.AsReadOnly<SettingsGuideDefinition>(
    [
        new("G01", [new("G01.card", "General.AfterCapture"), new("G01.editor", "General.AfterCapture"), new("G01.clipboard", "General.AfterCapture")]),
        new("G02", [new("G02.magnifier", "Capture.ShowMagnifier"), new("G02.grid", "Capture.ShowSelectionGrid"), new("G02.windows", "Capture.AutoDetectWindows")]),
        new("G03", [new("G03.keys", "Capture.AlwaysShowKeyPanel")]),
        new("G04", [new("G04.size", "Recording.PresetWidth|Recording.PresetHeight", 3)]),
        new("G05", [new("G05.highlight", "Recording.IncludeCursor|Recording.CursorHighlight", 3), new("G05.hidden", "Recording.IncludeCursor|Recording.CursorHighlight")]),
        new("G06", [new("G06.method", "Recording.CaptureMode")]),
        new("G07", [new("G07.mosaic", "Annotation.MosaicBlockSize"), new("G07.highlighter", "Annotation.HighlighterAlpha"), new("G07.blur", "Annotation.BlurRadius")]),
        new("G08", [new("G08.restart", "Pin.RestoreOpenPinsOnLaunch"), new("G08.reopen", "Pin.ClosedWindowRestoreLimit"), new("G08.none", "Pin.ClosedWindowRestoreLimit")]),
        new("G09", [new("G09.balanced", "Ocr.Quality"), new("G09.accurate", "Ocr.Quality")]),
        new("G10", [new("G10.alt", "General.AlwaysShowShortcutHints"), new("G10.always", "General.AlwaysShowShortcutHints")]),
        new("G11", [new("G11.time", "Queue.ImageRetentionHours"), new("G11.items", "Queue.MaxItems"), new("G11.bytes", "Queue.MaxBytes")]),
        new("G12", [new("G12.folder", "Queue.UseCurrentFolderForNewCaptures"), new("G12.storage", "Queue.CapturesDirectoryOverride"), new("G12.export", "Export.QuickSaveDirectoryOverride")]),
        new("G13", [new("G13.title", "Export.ShowSourceWindowTitle")]),
    ]);

    internal static SettingsGuideDefinition Get(string guideId) => All.FirstOrDefault(g => g.Id == guideId)
        ?? throw new ArgumentOutOfRangeException(nameof(guideId), guideId, "Unknown settings guide.");

    internal static IReadOnlyList<SettingsGuideDefinition> ForSetting(string settingKey) =>
        All.Where(g => g.SettingKeys.Contains(settingKey, StringComparer.Ordinal)).ToArray();

    internal static string AssetDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "SettingsGuides");
}
