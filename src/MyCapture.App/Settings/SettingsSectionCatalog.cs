using System.Windows;

namespace MyCapture.App.Settings;

/// <summary>Stable destinations shared by settings search and contextual entry points.</summary>
internal enum SettingsSection
{
    Capture, Recording, Editing, Pins, Text, Storage, Shortcuts, General, About
}

internal sealed record SettingsSectionDefinition(SettingsSection Section, string TabName, string LabelKey);

/// <summary>The existing draft field, its category, optional fold, and optional comparison guide.</summary>
internal sealed record SettingsFieldDefinition(string DraftProperty, SettingsSection Section,
    string? GroupName = null, string? GuideId = null, string? ElementName = null);

internal static class SettingsSectionCatalog
{
    internal static IReadOnlyList<SettingsSectionDefinition> Sections { get; } =
    [
        new(SettingsSection.Capture, "CaptureTab", "SettingsUx_TabCapture"),
        new(SettingsSection.Recording, "RecordingTab", "SettingsUx_TabRecording"),
        new(SettingsSection.Editing, "EditingTab", "SettingsUx_TabEditing"),
        new(SettingsSection.Pins, "PinsTab", "SettingsStructure_TabPins"),
        new(SettingsSection.Text, "TextTab", "SettingsStructure_TabText"),
        new(SettingsSection.Storage, "StorageTab", "SettingsStructure_TabStorage"),
        new(SettingsSection.Shortcuts, "ShortcutsTab", "SettingsUx_TabShortcuts"),
        new(SettingsSection.General, "GeneralTab", "SettingsUx_TabGeneral"),
        new(SettingsSection.About, "UpdateTab", "SettingsStructure_TabAbout"),
    ];

    // The order follows the controls on each page. A manual control (font, retention or secret)
    // names its existing element instead of adding a binding with different save semantics.
    internal static IReadOnlyList<SettingsFieldDefinition> Fields { get; } =
    [
        new("AfterCapture", SettingsSection.Capture, null, "G01", null),
        new("PlayCaptureSound", SettingsSection.Capture, null, null, null),
        new("NotifyOnQuickSave", SettingsSection.Capture, null, null, null),
        new("IncludeCursor", SettingsSection.Capture, null, null, null),
        new("ShowMagnifier", SettingsSection.Capture, null, "G02", null),
        new("ShowSelectionGrid", SettingsSection.Capture, null, "G02", null),
        new("AutoDetectWindows", SettingsSection.Capture, null, "G02", null),
        new("CaptureGuideColorMode", SettingsSection.Capture, null, null, null),
        new("CaptureGuideColorHex", SettingsSection.Capture, null, null, null),
        new("AlwaysShowKeyPanel", SettingsSection.Capture, "CaptureControlsExpander", "G03", null),
        new("ColorFormat", SettingsSection.Capture, "CaptureControlsExpander", null, null),
        new("AbortOnFocusLoss", SettingsSection.Capture, "CaptureControlsExpander", null, null),
        new("DelaySeconds", SettingsSection.Capture, "CaptureControlsExpander", null, null),
        new("RegionHistoryLimit", SettingsSection.Capture, "CaptureControlsExpander", null, null),
        new("RecordingFrameRate", SettingsSection.Recording, null, null, null),
        new("UseRecordingStartDelay", SettingsSection.Recording, null, null, null),
        new("RecordingStartDelaySeconds", SettingsSection.Recording, null, null, null),
        new("RecordingIncludeCursor", SettingsSection.Recording, null, "G05", null),
        new("RecordingCursorHighlight", SettingsSection.Recording, null, "G05", null),
        new("UseRecordingDraggedSize", SettingsSection.Recording, null, "G04", null),
        new("UseRecordingFixedSize", SettingsSection.Recording, null, "G04", null),
        new("RecordingPresetWidth", SettingsSection.Recording, null, "G04", null),
        new("RecordingPresetHeight", SettingsSection.Recording, null, "G04", null),
        new("RecordingWatermarkEnabled", SettingsSection.Recording, null, null, null),
        new("RecordingWatermark", SettingsSection.Recording, null, null, "RecordingWatermarkSettingsButton"),
        new("RecordingCaptureMode", SettingsSection.Recording, "RecordingCompatibilityExpander", "G06", null),
        new("StrokeThickness", SettingsSection.Editing, null, null, null),
        new("FontFamily", SettingsSection.Editing, null, null, "FontFamilyBox"),
        new("FontSize", SettingsSection.Editing, null, null, null),
        new("MosaicBlockSize", SettingsSection.Editing, null, "G07", null),
        new("HighlighterOpacityPercent", SettingsSection.Editing, null, "G07", null),
        new("BlurRadius", SettingsSection.Editing, null, "G07", null),
        new("RestoreOpenPinsOnLaunch", SettingsSection.Pins, null, "G08", null),
        new("CloseOnDoubleClick", SettingsSection.Pins, null, null, null),
        new("InitialOpacity", SettingsSection.Pins, null, null, null),
        new("ZoomStep", SettingsSection.Pins, "PinHistoryExpander", null, null),
        new("ClosedWindowRestoreLimit", SettingsSection.Pins, "PinHistoryExpander", "G08", null),
        new("OcrQuality", SettingsSection.Text, null, "G09", null),
        new("PreferredLanguages", SettingsSection.Text, null, null, null),
        new("CacheResults", SettingsSection.Text, null, null, null),
        new("QuickSaveDirectoryOverride", SettingsSection.Storage, null, "G12", null),
        new("FileNamePattern", SettingsSection.Storage, null, null, null),
        new("CapturesDirectoryOverride", SettingsSection.Storage, null, "G12", null),
        new("ImageRetentionHours", SettingsSection.Storage, null, "G11", "RetentionChoiceBox"),
        new("UseCurrentFolderForNewCaptures", SettingsSection.Storage, null, "G12", null),
        new("FolderFilePrefix", SettingsSection.Storage, null, null, null),
        new("MaxItems", SettingsSection.Storage, "StorageLimitsExpander", "G11", null),
        new("MaxGiB", SettingsSection.Storage, "StorageLimitsExpander", "G11", null),
        new("ThumbnailLongEdge", SettingsSection.Storage, "StorageLimitsExpander", null, null),
        new("ShowSourceWindowTitle", SettingsSection.Storage, "ImageExportExpander", "G13", null),
        new("GitHubIssueUrl", SettingsSection.Storage, "AdvancedExpander", null, null),
        new("GitHubToken", SettingsSection.Storage, "AdvancedExpander", null, "GitHubTokenBox"),
        new("AlwaysShowShortcutHints", SettingsSection.Shortcuts, null, "G10", null),
        new("CaptureHotkey", SettingsSection.Shortcuts, null, null, null),
        new("RecordRegionHotkey", SettingsSection.Shortcuts, null, null, null),
        new("CaptureTextHotkey", SettingsSection.Shortcuts, null, null, null),
        new("PasteToScreenHotkey", SettingsSection.Shortcuts, null, null, null),
        new("HideAllPinsHotkey", SettingsSection.Shortcuts, null, null, null),
        new("CaptureWindowHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("CaptureFullScreenHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("RepeatLastRegionHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("ScrollingCaptureHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("DelayedCaptureHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("PauseRecordingHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("OpenLibraryHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("ToggleClickThroughHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("RestoreClosedPinHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("UploadGitHubImageHotkey", SettingsSection.Shortcuts, "AdditionalShortcutsExpander", null, null),
        new("ForceCoreShortcuts", SettingsSection.Shortcuts, "ShortcutConflictsExpander", null, null),
        new("Language", SettingsSection.General, null, null, null),
        new("Theme", SettingsSection.General, null, null, null),
        new("LaunchAtLogin", SettingsSection.General, null, null, null),
        new("AutoCheckUpdates", SettingsSection.About, null, null, null),
    ];

    internal static SettingsFieldDefinition? Find(string draftProperty) =>
        Fields.FirstOrDefault(field => string.Equals(field.DraftProperty, draftProperty, StringComparison.Ordinal));
}

/// <summary>The guide lane supplies the actual window; tests supply a presenter without opening it.</summary>
internal interface ISettingsGuidePresenter
{
    bool IsAvailable => true;
    void Show(string guideId, Window owner);
}

/// <summary>Opens the guide included in this build while preserving the injectable test presenter.</summary>
internal sealed class SettingsGuidePresenter : ISettingsGuidePresenter
{
    internal static SettingsGuidePresenter Instance { get; } = new();

    public bool IsAvailable => true;

    public void Show(string guideId, Window owner) => SettingsGuide.Show(guideId, owner);
}
