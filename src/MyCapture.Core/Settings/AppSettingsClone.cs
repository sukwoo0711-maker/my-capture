namespace MyCapture.Core.Settings;

/// <summary>
/// Produces an independent deep copy of <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The settings window edits a <em>draft</em>, never the live settings object the rest
/// of the process is reading through <c>Func</c> suppliers. If the window mutated the
/// live object directly, pressing Cancel could not undo the change and a half-edited
/// value could leak into a capture taken while the window was open.
/// </para>
/// <para>
/// A hand-written clone is used rather than serialise/deserialise round-tripping so
/// the copy is exact regardless of the JSON contract (which deliberately ignores some
/// members such as <see cref="GeneralSettings.IsFirstRun"/>), and so cloning never
/// touches the disk or throws on a serialiser edge case.
/// </para>
/// </remarks>
public static class AppSettingsClone
{
    public static AppSettings DeepClone(this AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppSettings
        {
            SchemaVersion = source.SchemaVersion,
            Hotkeys = Clone(source.Hotkeys),
            Queue = Clone(source.Queue),
            Export = Clone(source.Export),
            Capture = Clone(source.Capture),
            Pin = Clone(source.Pin),
            Annotation = Clone(source.Annotation),
            Ocr = Clone(source.Ocr),
            General = Clone(source.General),
        };
    }

    // Hotkey is an immutable record, so the reference can be shared; a reassignment on
    // the clone replaces the reference rather than mutating the shared instance.
    private static HotkeySettings Clone(HotkeySettings s) => new()
    {
        Capture = s.Capture,
        PasteToScreen = s.PasteToScreen,
        HideAllPins = s.HideAllPins,
        ToggleClickThrough = s.ToggleClickThrough,
        RepeatLastRegion = s.RepeatLastRegion,
        CaptureWindow = s.CaptureWindow,
        CaptureFullScreen = s.CaptureFullScreen,
    };

    private static QueueSettings Clone(QueueSettings s) => new()
    {
        MaxItems = s.MaxItems,
        MaxBytes = s.MaxBytes,
        CapturesDirectoryOverride = s.CapturesDirectoryOverride,
        ThumbnailLongEdge = s.ThumbnailLongEdge,
    };

    private static ExportSettings Clone(ExportSettings s) => new()
    {
        QuickSaveDirectoryOverride = s.QuickSaveDirectoryOverride,
        CopyToClipboardOnQuickSave = s.CopyToClipboardOnQuickSave,
        FileNamePattern = s.FileNamePattern,
        PreserveTransparency = s.PreserveTransparency,
    };

    private static CaptureSettings Clone(CaptureSettings s) => new()
    {
        IncludeCursor = s.IncludeCursor,
        AutoDetectWindows = s.AutoDetectWindows,
        ShowMagnifier = s.ShowMagnifier,
        ColorFormat = s.ColorFormat,
        DelaySeconds = s.DelaySeconds,
        AbortOnFocusLoss = s.AbortOnFocusLoss,
        RegionHistoryLimit = s.RegionHistoryLimit,
    };

    private static PinSettings Clone(PinSettings s) => new()
    {
        CloseOnDoubleClick = s.CloseOnDoubleClick,
        CtrlClickDebounceMs = s.CtrlClickDebounceMs,
        InitialOpacity = s.InitialOpacity,
        ClosedWindowRestoreLimit = s.ClosedWindowRestoreLimit,
        ZoomStep = s.ZoomStep,
    };

    private static AnnotationDefaults Clone(AnnotationDefaults s) => new()
    {
        StrokeColor = s.StrokeColor,
        StrokeThickness = s.StrokeThickness,
        TextColor = s.TextColor,
        FontSize = s.FontSize,
        FontFamily = s.FontFamily,
        MosaicBlockSize = s.MosaicBlockSize,
        HighlighterAlpha = s.HighlighterAlpha,
        RecentColors = [.. s.RecentColors],
    };

    private static OcrSettings Clone(OcrSettings s) => new()
    {
        PreferredLanguages = [.. s.PreferredLanguages],
        UpscaleFactor = s.UpscaleFactor,
        CacheResults = s.CacheResults,
    };

    private static GeneralSettings Clone(GeneralSettings s) => new()
    {
        LaunchAtLogin = s.LaunchAtLogin,
        NotifyOnQuickSave = s.NotifyOnQuickSave,
        PlayCaptureSound = s.PlayCaptureSound,
        Language = s.Language,
        IsFirstRun = s.IsFirstRun,
    };
}
