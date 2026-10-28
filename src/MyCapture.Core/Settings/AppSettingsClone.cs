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
/// The retired Enhanced OCR quality is normalized to Accurate in every new copy.
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
            Recording = Clone(source.Recording),
            Pin = Clone(source.Pin),
            Annotation = Clone(source.Annotation),
            Ocr = Clone(source.Ocr),
            GitHub = Clone(source.GitHub),
            General = Clone(source.General),
            StoreRating = Clone(source.StoreRating),
            StoreUpdate = Clone(source.StoreUpdate),
            Updates = Clone(source.Updates),
        };
    }

    // Hotkey is an immutable record, so the reference can be shared; a reassignment on
    // the clone replaces the reference rather than mutating the shared instance.
    private static HotkeySettings Clone(HotkeySettings s) => new()
    {
        ScrollingCapture = s.ScrollingCapture,
        DelayedCapture = s.DelayedCapture,
        Capture = s.Capture,
        OpenLibrary = s.OpenLibrary,
        PasteToScreen = s.PasteToScreen,
        HideAllPins = s.HideAllPins,
        ToggleClickThrough = s.ToggleClickThrough,
        RepeatLastRegion = s.RepeatLastRegion,
        CaptureWindow = s.CaptureWindow,
        CaptureFullScreen = s.CaptureFullScreen,
        RecordRegion = s.RecordRegion,
        UploadGitHubImage = s.UploadGitHubImage,
        RestoreClosedPin = s.RestoreClosedPin,
        CaptureText = s.CaptureText,
        PauseRecording = s.PauseRecording,
        ForceCoreShortcuts = s.ForceCoreShortcuts,
        CoreShortcutsAsked = s.CoreShortcutsAsked,
    };

    private static QueueSettings Clone(QueueSettings s) => new()
    {
        MaxItems = s.MaxItems,
        MaxBytes = s.MaxBytes,
        CapturesDirectoryOverride = s.CapturesDirectoryOverride,
        ThumbnailLongEdge = s.ThumbnailLongEdge,
        ImageRetentionHours = s.ImageRetentionHours,
        FolderFilePrefix = s.FolderFilePrefix,
        UseCurrentFolderForNewCaptures = s.UseCurrentFolderForNewCaptures,
        DefaultCaptureFolderId = s.DefaultCaptureFolderId,
    };

    private static ExportSettings Clone(ExportSettings s) => new()
    {
        LastImageExport = s.LastImageExport with { },
        QuickSaveDirectoryOverride = s.QuickSaveDirectoryOverride,
        LastSaveDirectory = s.LastSaveDirectory,
        CopyToClipboardOnQuickSave = s.CopyToClipboardOnQuickSave,
        FileNamePattern = s.FileNamePattern,
        PreserveTransparency = s.PreserveTransparency,
        ShowSourceWindowTitle = s.ShowSourceWindowTitle,
    };

    private static CaptureSettings Clone(CaptureSettings s) => new()
    {
        IncludeCursor = s.IncludeCursor,
        AutoDetectWindows = s.AutoDetectWindows,
        ShowMagnifier = s.ShowMagnifier,
        ShowSelectionGrid = s.ShowSelectionGrid,
        ColorFormat = s.ColorFormat,
        DelaySeconds = s.DelaySeconds,
        AbortOnFocusLoss = s.AbortOnFocusLoss,
        RegionHistoryLimit = s.RegionHistoryLimit,
        StartGuideShownCount = s.StartGuideShownCount,
    };

    private static Recording.RecordingSettings Clone(Recording.RecordingSettings s) => new()
    {
        FrameRate = s.FrameRate,
        UseStartDelay = s.UseStartDelay,
        StartDelaySeconds = s.StartDelaySeconds,
        IncludeCursor = s.IncludeCursor,
        BitrateBitsPerSecond = s.BitrateBitsPerSecond,
        CoarseStepSeconds = s.CoarseStepSeconds,
        CursorHighlight = s.CursorHighlight,
        PresetWidth = s.PresetWidth,
        PresetHeight = s.PresetHeight,
        CaptureMode = s.CaptureMode,
    };

    private static PinSettings Clone(PinSettings s) => new()
    {
        ClickThroughHintShownCount = s.ClickThroughHintShownCount,
        CloseOnDoubleClick = s.CloseOnDoubleClick,
        RestoreOpenPinsOnLaunch = s.RestoreOpenPinsOnLaunch,
        CtrlClickDebounceMs = s.CtrlClickDebounceMs,
        InitialOpacity = s.InitialOpacity,
        ClosedWindowRestoreLimit = s.ClosedWindowRestoreLimit,
        ZoomStep = s.ZoomStep,
    };

    private static AnnotationDefaults Clone(AnnotationDefaults s) => new()
    {
        LastTool = s.LastTool,
        StrokeStyle = s.StrokeStyle,
        FillTransparency = s.FillTransparency,
        StrokeColor = s.StrokeColor,
        StrokeThickness = s.StrokeThickness,
        TextColor = s.TextColor,
        FontSize = s.FontSize,
        FontFamily = s.FontFamily,
        TextBold = s.TextBold,
        TextBackground = s.TextBackground,
        TextOutline = s.TextOutline,
        RecentFontFamilies = [.. s.RecentFontFamilies],
        MosaicBlockSize = s.MosaicBlockSize,
        HighlighterAlpha = s.HighlighterAlpha,
        RecentColors = [.. s.RecentColors],
        BlurRadius = s.BlurRadius,
        Stamp = s.Stamp.Clone(),
    };

    private static OcrSettings Clone(OcrSettings s) => new()
    {
        PreferredLanguages = [.. s.PreferredLanguages],
        Quality = OcrQualityNames.Normalize(s.Quality),
        UpscaleFactor = s.Quality == OcrQuality.Enhanced
            ? OcrQualityProfile.UpscaleFactor(OcrQuality.Accurate)
            : s.UpscaleFactor,
        CacheResults = s.CacheResults,
    };

    private static GeneralSettings Clone(GeneralSettings s) => new()
    {
        AfterCapture = s.AfterCapture,
        FirstCaptureTipShown = s.FirstCaptureTipShown,
        FirstPinTipShown = s.FirstPinTipShown,
        ShowShortcutHints = s.ShowShortcutHints,
        AlwaysShowShortcutHints = s.AlwaysShowShortcutHints,
        ShortcutHintsRevision = s.ShortcutHintsRevision,
        LaunchAtLogin = s.LaunchAtLogin,
        NotifyOnQuickSave = s.NotifyOnQuickSave,
        PlayCaptureSound = s.PlayCaptureSound,
        Language = s.Language,
        Theme = s.Theme,
        AccentColor = s.AccentColor,
        CaptureGuideColor = s.CaptureGuideColor,
        ThemeRevision = s.ThemeRevision,
        IsFirstRun = s.IsFirstRun,
    };

    // The token is copied in memory like every other value. Leaving it out made the settings
    // window start from an empty token and write that back on Apply (or a tray language
    // change), silently deleting the saved token. Exports still omit it (SettingsStore.ExportTo).
    private static GitHubSettings Clone(GitHubSettings s) => new()
    {
        IssueUrl = s.IssueUrl,
        Token = s.Token,
    };

    private static StoreRatingState Clone(StoreRatingState s) => new()
    {
        FirstSeenUtc = s.FirstSeenUtc,
        SuccessfulCaptureCount = s.SuccessfulCaptureCount,
        LastCaptureFailed = s.LastCaptureFailed,
        RequestAttemptedUtc = s.RequestAttemptedUtc,
        InvitationShownUtc = s.InvitationShownUtc,
    };

    private static StoreUpdateState Clone(StoreUpdateState s) => new()
    {
        LastSeenVersion = s.LastSeenVersion,
        CompletionNoticeVersion = s.CompletionNoticeVersion,
    };

    private static UpdateSettings Clone(UpdateSettings s) => new()
    {
        AutoCheck = s.AutoCheck,
        LastCheckUtc = s.LastCheckUtc,
        DismissedVersion = s.DismissedVersion,
    };
}
