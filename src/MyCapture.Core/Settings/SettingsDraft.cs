using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MyCapture.Core.GitHub;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;

namespace MyCapture.Core.Settings;

/// <summary>
/// An editable, framework-free draft of every user-facing setting, with per-field
/// validation surfaced through <see cref="INotifyDataErrorInfo"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately lives in the WPF-free core so the entire validation and
/// mapping surface is unit-testable without a UI. The settings window binds directly
/// to these properties; WPF honours <see cref="INotifyDataErrorInfo"/> when
/// <c>ValidatesOnNotifyDataErrors</c> is set on a binding, so a validation error paints
/// the field and is announced to assistive technology without any code-behind.
/// </para>
/// <para>
/// Numeric values are held as strings so a partially typed or non-numeric entry is a
/// validation error rather than a binding failure that silently swallows the keystroke.
/// Hotkeys are held as their canonical string form and validated by
/// <see cref="Hotkey.TryParse"/>, with cross-field duplicate detection over the whole
/// set of assigned chords.
/// </para>
/// <para>
/// The draft is constructed from a deep copy of the live settings, so editing and
/// cancelling can never mutate what the running app is reading.
/// </para>
/// </remarks>
public sealed class SettingsDraft : INotifyPropertyChanged, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = [];
    private readonly Dictionary<string, string> _saveErrors = [];
    private readonly Dictionary<string, string> _candidateErrors = [];

    // ----- General -----
    private bool _launchAtLogin;
    private bool _notifyOnQuickSave;
    private bool _playCaptureSound;
    private AfterCaptureAction _afterCapture;
    private bool _showShortcutHints;

    // ----- Capture -----
    private bool _includeCursor;
    private bool _autoDetectWindows;
    private bool _showMagnifier;
    private bool _showSelectionGrid;
    private string _delaySeconds = string.Empty;
    private bool _abortOnFocusLoss;
    private string _regionHistoryLimit = string.Empty;

    // ----- Recording -----
    private string _recordingFrameRate = string.Empty;
    private bool _useRecordingStartDelay;
    private string _recordingStartDelaySeconds = string.Empty;
    private bool _recordingIncludeCursor;
    private bool _recordingCursorHighlight;
    private bool _useRecordingFixedSize;
    private string _recordingPresetWidth = "0";
    private string _recordingPresetHeight = "0";
    private string _recordingCaptureMode = RecordingCaptureModeNames.Auto;

    private string _scrollingCaptureHotkey = string.Empty;
    private string _delayedCaptureHotkey = string.Empty;
    // ----- Hotkeys -----
    private string _captureHotkey = string.Empty;
    private string _openLibraryHotkey = string.Empty;
    private string _pasteToScreenHotkey = string.Empty;
    private string _hideAllPinsHotkey = string.Empty;
    private string _toggleClickThroughHotkey = string.Empty;
    private string _repeatLastRegionHotkey = string.Empty;
    private string _captureWindowHotkey = string.Empty;
    private string _captureFullScreenHotkey = string.Empty;
    private string _recordRegionHotkey = string.Empty;
    private string _uploadGitHubImageHotkey = string.Empty;
    private string _restoreClosedPinHotkey = string.Empty;
    private string _captureTextHotkey = string.Empty;
    private string _pauseRecordingHotkey = string.Empty;
    private bool _forceCoreShortcuts;

    // ----- Storage -----
    private string _maxItems = string.Empty;
    private string _maxGiB = string.Empty;
    private string _thumbnailLongEdge = string.Empty;
    private string _capturesDirectoryOverride = string.Empty;
    private string _imageRetentionHours = string.Empty;
    private string _folderFilePrefix = MyCapture.Core.Queue.CaptureFolderNaming.DefaultPrefix;
    private bool _useCurrentFolderForNewCaptures;
    private Guid? _defaultCaptureFolderId;
    private string _gitHubIssueUrl = GitHubIssueImageUrl.DefaultIssueUrl;
    private string _gitHubToken = string.Empty;
    private string _theme = AppThemeNames.Midnight;
    private string _accentColor = AppAccentNames.Red;
    private string _captureGuideColorMode = "theme";
    private string _captureGuideColorHex = CaptureGuideColorValue.DefaultCustomColor;

    // ----- Export -----
    private string _quickSaveDirectoryOverride = string.Empty;
    private bool _copyToClipboardOnQuickSave;
    private bool _showSourceWindowTitle;
    private string _fileNamePattern = string.Empty;

    // ----- Pin -----
    private bool _closeOnDoubleClick;
    private bool _restoreOpenPinsOnLaunch;
    private string _initialOpacity = string.Empty;
    private string _zoomStep = string.Empty;
    private string _ctrlClickDebounceMs = string.Empty;
    private string _closedWindowRestoreLimit = string.Empty;

    // ----- OCR -----
    private string _preferredLanguages = string.Empty;
    private string _ocrQuality = OcrQualityNames.Fast;
    private bool _cacheResults;

    // ----- Annotation -----
    private string _lastAnnotationTool = "Rectangle";
    private MyCapture.Core.Annotations.AnnotationStrokeStyle _annotationStrokeStyle;
    private double _annotationFillTransparency = 100;
    private string _strokeThickness = string.Empty;
    private string _fontSize = string.Empty;
    private string _fontFamily = string.Empty;
    private string _mosaicBlockSize = string.Empty;
    private string _highlighterAlpha = string.Empty;
    private string _highlighterOpacityPercent = string.Empty;
    private int _loadedHighlighterAlpha;
    private string _blurRadius = string.Empty;

    /// <summary>
    /// Builds a draft from a deep copy of <paramref name="source"/>. The source object
    /// is never referenced afterwards.
    /// </summary>
    public SettingsDraft(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        LoadFrom(source.DeepClone());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0 || _saveErrors.Count > 0 || _candidateErrors.Count > 0;

    /// <summary>Empty follows Windows; ko-KR and en are the supported explicit UI choices.</summary>
    public string Language
    {
        get => _language;
        set => Set(ref _language, value ?? string.Empty);
    }
    // ================= General =================

    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }
    public bool NotifyOnQuickSave { get => _notifyOnQuickSave; set => Set(ref _notifyOnQuickSave, value); }
    public bool PlayCaptureSound { get => _playCaptureSound; set => Set(ref _playCaptureSound, value); }
    public AfterCaptureAction AfterCapture { get => _afterCapture; set => Set(ref _afterCapture, value); }
    public bool ShowShortcutHints { get => _showShortcutHints; set => Set(ref _showShortcutHints, value); }

    // ================= Capture =================

    public bool IncludeCursor { get => _includeCursor; set => Set(ref _includeCursor, value); }
    public bool AutoDetectWindows { get => _autoDetectWindows; set => Set(ref _autoDetectWindows, value); }
    public bool ShowMagnifier { get => _showMagnifier; set => Set(ref _showMagnifier, value); }

    public bool ShowSelectionGrid { get => _showSelectionGrid; set => Set(ref _showSelectionGrid, value); }
    public bool AbortOnFocusLoss { get => _abortOnFocusLoss; set => Set(ref _abortOnFocusLoss, value); }

    /// <summary>Format the capture overlay's colour copy writes. Undefined values fall back to hex.</summary>
    public ColorFormat ColorFormat
    {
        get => _preservedColorFormat;
        set => Set(ref _preservedColorFormat, Enum.IsDefined(value) ? value : ColorFormat.Hex);
    }

    public string DelaySeconds
    {
        get => _delaySeconds;
        set { if (Set(ref _delaySeconds, value)) ValidateInt(value, SettingsRanges.DelaySeconds); }
    }

    public string RegionHistoryLimit
    {
        get => _regionHistoryLimit;
        set { if (Set(ref _regionHistoryLimit, value)) ValidateInt(value, SettingsRanges.RegionHistoryLimit); }
    }

    // ================= Recording =================

    public string RecordingFrameRate
    {
        get => _recordingFrameRate;
        set { if (Set(ref _recordingFrameRate, value)) ValidateRecordingFrameRate(value); }
    }

    public bool UseRecordingStartDelay
    {
        get => _useRecordingStartDelay;
        set => Set(ref _useRecordingStartDelay, value);
    }

    public string RecordingStartDelaySeconds
    {
        get => _recordingStartDelaySeconds;
        set
        {
            if (Set(ref _recordingStartDelaySeconds, value))
            {
                ValidateInt(value, SettingsRanges.RecordingStartDelaySeconds);
            }
        }
    }

    public bool RecordingIncludeCursor
    {
        get => _recordingIncludeCursor;
        set => Set(ref _recordingIncludeCursor, value);
    }

    /// <summary>Draws an emphasis ring around the pointer on recorded frames.</summary>
    public bool RecordingCursorHighlight
    {
        get => _recordingCursorHighlight;
        set => Set(ref _recordingCursorHighlight, value);
    }

    /// <summary>
    /// True records every region at <see cref="RecordingPresetWidth"/> × <see cref="RecordingPresetHeight"/>
    /// and the drag only places it; false records the size the user drags. Saved as a preset
    /// size or as no preset, so older settings without a size (or with 0) load as the dragged size.
    /// </summary>
    public bool UseRecordingFixedSize
    {
        get => _useRecordingFixedSize;
        set
        {
            if (!Set(ref _useRecordingFixedSize, value)) return;
            RaisePropertyChanged(nameof(UseRecordingDraggedSize));
            ValidateRecordingPresetSize();
        }
    }

    /// <summary>The other choice of <see cref="UseRecordingFixedSize"/>, for its own radio button.</summary>
    public bool UseRecordingDraggedSize
    {
        get => !_useRecordingFixedSize;
        set => UseRecordingFixedSize = !value;
    }

    /// <summary>Fixed recording width in physical pixels; used only with <see cref="UseRecordingFixedSize"/>.</summary>
    public string RecordingPresetWidth
    {
        get => _recordingPresetWidth;
        set { if (Set(ref _recordingPresetWidth, value)) ValidateRecordingPresetSize(); }
    }

    /// <summary>Fixed recording height in physical pixels; used only with <see cref="UseRecordingFixedSize"/>.</summary>
    public string RecordingPresetHeight
    {
        get => _recordingPresetHeight;
        set { if (Set(ref _recordingPresetHeight, value)) ValidateRecordingPresetSize(); }
    }

    /// <summary>Recording capture method as its setting name: <c>auto</c> (WGC first) or <c>gdi</c>.</summary>
    public string RecordingCaptureMode
    {
        get => _recordingCaptureMode;
        set => Set(ref _recordingCaptureMode, RecordingCaptureModeNames.ToSetting(RecordingCaptureModeNames.Parse(value)));
    }

    public string ScrollingCaptureHotkey { get => _scrollingCaptureHotkey; set { if (Set(ref _scrollingCaptureHotkey, value)) ValidateAllHotkeys(); } }
    public string DelayedCaptureHotkey { get => _delayedCaptureHotkey; set { if (Set(ref _delayedCaptureHotkey, value)) ValidateAllHotkeys(); } }
    // ================= Hotkeys =================

    public string CaptureHotkey
    {
        get => _captureHotkey;
        set { if (Set(ref _captureHotkey, value)) ValidateAllHotkeys(); }
    }

    public string OpenLibraryHotkey
    {
        get => _openLibraryHotkey;
        set { if (Set(ref _openLibraryHotkey, value)) ValidateAllHotkeys(); }
    }

    public string PasteToScreenHotkey
    {
        get => _pasteToScreenHotkey;
        set { if (Set(ref _pasteToScreenHotkey, value)) ValidateAllHotkeys(); }
    }

    public string HideAllPinsHotkey
    {
        get => _hideAllPinsHotkey;
        set { if (Set(ref _hideAllPinsHotkey, value)) ValidateAllHotkeys(); }
    }

    public string ToggleClickThroughHotkey
    {
        get => _toggleClickThroughHotkey;
        set { if (Set(ref _toggleClickThroughHotkey, value)) ValidateAllHotkeys(); }
    }

    public string RepeatLastRegionHotkey
    {
        get => _repeatLastRegionHotkey;
        set { if (Set(ref _repeatLastRegionHotkey, value)) ValidateAllHotkeys(); }
    }

    public string CaptureWindowHotkey
    {
        get => _captureWindowHotkey;
        set { if (Set(ref _captureWindowHotkey, value)) ValidateAllHotkeys(); }
    }

    public string CaptureFullScreenHotkey
    {
        get => _captureFullScreenHotkey;
        set { if (Set(ref _captureFullScreenHotkey, value)) ValidateAllHotkeys(); }
    }

    public string RecordRegionHotkey
    {
        get => _recordRegionHotkey;
        set { if (Set(ref _recordRegionHotkey, value)) ValidateAllHotkeys(); }
    }

    public string UploadGitHubImageHotkey
    {
        get => _uploadGitHubImageHotkey;
        set { if (Set(ref _uploadGitHubImageHotkey, value)) ValidateAllHotkeys(); }
    }

    public string RestoreClosedPinHotkey
    {
        get => _restoreClosedPinHotkey;
        set { if (Set(ref _restoreClosedPinHotkey, value)) ValidateAllHotkeys(); }
    }

    /// <summary>Optional chord for the text-only region capture.</summary>
    public string CaptureTextHotkey
    {
        get => _captureTextHotkey;
        set { if (Set(ref _captureTextHotkey, value)) ValidateAllHotkeys(); }
    }

    /// <summary>Optional chord that pauses or resumes the active region recording.</summary>
    public string PauseRecordingHotkey
    {
        get => _pauseRecordingHotkey;
        set { if (Set(ref _pauseRecordingHotkey, value)) ValidateAllHotkeys(); }
    }

    /// <summary>Claims assigned core chords ahead of other programs without reassigning keys.</summary>
    public bool ForceCoreShortcuts { get => _forceCoreShortcuts; set => Set(ref _forceCoreShortcuts, value); }

    // ================= Storage =================

    public string MaxItems
    {
        get => _maxItems;
        set { if (Set(ref _maxItems, value)) ValidateInt(value, SettingsRanges.MaxItems); }
    }

    public string MaxGiB
    {
        get => _maxGiB;
        set { if (Set(ref _maxGiB, value)) ValidateMaxGiB(value); }
    }

    public string ThumbnailLongEdge
    {
        get => _thumbnailLongEdge;
        set { if (Set(ref _thumbnailLongEdge, value)) ValidateInt(value, SettingsRanges.ThumbnailLongEdge); }
    }

    public string CapturesDirectoryOverride
    {
        get => _capturesDirectoryOverride;
        set { if (Set(ref _capturesDirectoryOverride, value)) ValidateOptionalDirectory(value, nameof(CapturesDirectoryOverride)); }
    }

    public string FolderFilePrefix
    {
        get => _folderFilePrefix;
        set { if (Set(ref _folderFilePrefix, value)) ValidateFolderFilePrefix(value); }
    }

    public bool UseCurrentFolderForNewCaptures
    {
        get => _useCurrentFolderForNewCaptures;
        set { if (Set(ref _useCurrentFolderForNewCaptures, value)) FolderDestinationEdited = true; }
    }

    public bool FolderDestinationEdited { get; private set; }

    /// <summary>
    /// Hours before an unpinned library image is deleted automatically, or
    /// <see cref="MyCapture.Core.Queue.CaptureRetention.NeverHours"/> for never. The settings
    /// window offers the presets of <see cref="MyCapture.Core.Queue.CaptureRetention.PresetHours"/>
    /// and keeps any other stored hour count as a custom choice.
    /// </summary>
    public string ImageRetentionHours
    {
        get => _imageRetentionHours;
        set { if (Set(ref _imageRetentionHours, value)) ValidateRetention(value); }
    }

    public string GitHubIssueUrl
    {
        get => _gitHubIssueUrl;
        set { if (Set(ref _gitHubIssueUrl, value)) ValidateGitHubIssueUrl(value); }
    }

    /// <summary>
    /// Optional PAT enabling background (no-browser) uploads. Preserved verbatim; an empty
    /// value keeps the classic browser flow.
    /// </summary>
    public string GitHubToken
    {
        get => _gitHubToken;
        set => Set(ref _gitHubToken, value?.Trim() ?? string.Empty);
    }

    public string Theme
    {
        get => _theme;
        set => Set(ref _theme, AppThemeNames.ToSetting(AppThemeNames.Parse(value)));
    }

    public string AccentColor
    {
        get => _accentColor;
        set => Set(ref _accentColor, AppAccentNames.ToSetting(AppAccentNames.Parse(value)));
    }

    public string CaptureGuideColorMode
    {
        get => _captureGuideColorMode;
        set
        {
            if (!Set(ref _captureGuideColorMode, value == "custom" ? "custom" : "theme")) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCustomCaptureGuideColor)));
            ValidateCaptureGuideColor();
        }
    }

    public bool IsCustomCaptureGuideColor => _captureGuideColorMode == "custom";

    public string CaptureGuideColorHex
    {
        get => _captureGuideColorHex;
        set
        {
            if (Set(ref _captureGuideColorHex, value ?? string.Empty)) ValidateCaptureGuideColor();
        }
    }

    public string CaptureGuideColorSetting => IsCustomCaptureGuideColor
        && CaptureGuideColorValue.TryNormalize(_captureGuideColorHex, out string color) ? color : string.Empty;

    private void ValidateCaptureGuideColor() => SetErrorState(nameof(CaptureGuideColorHex),
        !IsCustomCaptureGuideColor || CaptureGuideColorValue.TryNormalize(_captureGuideColorHex, out _),
        UiText.Get("Capture410.GuideColorInvalid"));

    // ================= Export =================
    private ImageExportOptions _lastImageExport = new();

    public bool ExportOptionsReplaced { get; private set; }

    public string QuickSaveDirectoryOverride
    {
        get => _quickSaveDirectoryOverride;
        set { if (Set(ref _quickSaveDirectoryOverride, value)) ValidateOptionalDirectory(value, nameof(QuickSaveDirectoryOverride)); }
    }

    public bool CopyToClipboardOnQuickSave { get => _copyToClipboardOnQuickSave; set => Set(ref _copyToClipboardOnQuickSave, value); }

    public bool ShowSourceWindowTitle { get => _showSourceWindowTitle; set => Set(ref _showSourceWindowTitle, value); }

    public string FileNamePattern
    {
        get => _fileNamePattern;
        set { if (Set(ref _fileNamePattern, value)) ValidateFileNamePattern(value); }
    }

    // ================= Pin =================

    public bool CloseOnDoubleClick { get => _closeOnDoubleClick; set => Set(ref _closeOnDoubleClick, value); }
    public bool RestoreOpenPinsOnLaunch { get => _restoreOpenPinsOnLaunch; set => Set(ref _restoreOpenPinsOnLaunch, value); }
    public bool CacheResults { get => _cacheResults; set => Set(ref _cacheResults, value); }

    public string InitialOpacity
    {
        get => _initialOpacity;
        set { if (Set(ref _initialOpacity, value)) ValidateDouble(value, SettingsRanges.InitialOpacity); }
    }

    public string ZoomStep
    {
        get => _zoomStep;
        set { if (Set(ref _zoomStep, value)) ValidateDouble(value, SettingsRanges.ZoomStep); }
    }

    public string CtrlClickDebounceMs
    {
        get => _ctrlClickDebounceMs;
        set { if (Set(ref _ctrlClickDebounceMs, value)) ValidateInt(value, SettingsRanges.CtrlClickDebounceMs); }
    }

    public string ClosedWindowRestoreLimit
    {
        get => _closedWindowRestoreLimit;
        set { if (Set(ref _closedWindowRestoreLimit, value)) ValidateInt(value, SettingsRanges.ClosedWindowRestoreLimit); }
    }

    // ================= OCR =================

    public string PreferredLanguages
    {
        get => _preferredLanguages;
        set { if (Set(ref _preferredLanguages, value)) ValidateLanguages(value); }
    }

    public string OcrQuality
    {
        get => _ocrQuality;
        set => Set(ref _ocrQuality, OcrQualityNames.ToSetting(OcrQualityNames.Parse(value)));
    }

    // ================= Annotation =================

    public string StrokeThickness
    {
        get => _strokeThickness;
        set { if (Set(ref _strokeThickness, value)) ValidateDouble(value, SettingsRanges.StrokeThickness); }
    }

    public string FontSize
    {
        get => _fontSize;
        set { if (Set(ref _fontSize, value)) ValidateDouble(value, SettingsRanges.FontSize); }
    }

    public string FontFamily
    {
        get => _fontFamily;
        set { if (Set(ref _fontFamily, value)) ValidateNonEmpty(value, nameof(FontFamily), UiText.Get("Text_B64CA1BE6711")); }
    }

    public string MosaicBlockSize
    {
        get => _mosaicBlockSize;
        set { if (Set(ref _mosaicBlockSize, value)) ValidateInt(value, SettingsRanges.MosaicBlockSize); }
    }

    /// <summary>The stored 0–255 highlighter alpha. The window edits <see cref="HighlighterOpacityPercent"/>.</summary>
    public string HighlighterAlpha
    {
        get => _highlighterAlpha;
        set
        {
            if (!Set(ref _highlighterAlpha, value)) return;
            ValidateInt(value, SettingsRanges.HighlighterAlpha);
            if (TryParseInt(value, out int alpha) && SettingsRanges.HighlighterAlpha.Contains(alpha)
                && _highlighterOpacityPercent != Int(AlphaToPercent(alpha)))
            {
                _highlighterOpacityPercent = Int(AlphaToPercent(alpha));
                RaisePropertyChanged(nameof(HighlighterOpacityPercent));
                ValidateInt(_highlighterOpacityPercent, SettingsRanges.HighlighterOpacityPercent, nameof(HighlighterOpacityPercent));
            }
        }
    }

    /// <summary>
    /// The highlighter opacity in percent (0–100), converted to the stored alpha at this boundary.
    /// A percentage that the current or the loaded alpha already rounds to keeps that alpha, so
    /// opening the window, or typing a value and typing the original back, never rewrites it.
    /// </summary>
    public string HighlighterOpacityPercent
    {
        get => _highlighterOpacityPercent;
        set
        {
            if (!Set(ref _highlighterOpacityPercent, value ?? string.Empty)) return;
            ValidateInt(_highlighterOpacityPercent, SettingsRanges.HighlighterOpacityPercent);
            if (!TryParseInt(_highlighterOpacityPercent, out int percent)
                || !SettingsRanges.HighlighterOpacityPercent.Contains(percent)) return;
            if (TryParseInt(_highlighterAlpha, out int current) && AlphaToPercent(current) == percent) return;
            _highlighterAlpha = Int(AlphaToPercent(_loadedHighlighterAlpha) == percent ? _loadedHighlighterAlpha : PercentToAlpha(percent));
            RaisePropertyChanged(nameof(HighlighterAlpha));
            ValidateInt(_highlighterAlpha, SettingsRanges.HighlighterAlpha, nameof(HighlighterAlpha));
        }
    }

    /// <summary>0–255 alpha to the nearest whole percent.</summary>
    public static int AlphaToPercent(int alpha) =>
        (int)Math.Round(Math.Clamp(alpha, 0, 255) * 100.0 / 255, MidpointRounding.AwayFromZero);

    /// <summary>A whole percent to the nearest 0–255 alpha; it converts back to the same percent.</summary>
    public static int PercentToAlpha(int percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100, MidpointRounding.AwayFromZero);

    public string BlurRadius
    {
        get => _blurRadius;
        set { if (Set(ref _blurRadius, value)) ValidateInt(value, SettingsRanges.BlurRadius); }
    }

    // ================= Public operations =================

    /// <summary>
    /// Re-reads every field from <paramref name="settings"/> (deep-copied first) and
    /// re-runs validation. Used by both the constructor and a Cancel/reload.
    /// </summary>
    public void LoadFrom(AppSettings settings)
    {
        _saveErrors.Clear();
        _candidateErrors.Clear();
        ExportOptionsReplaced = false;
        LoadValues(settings);
        _baseline = CaptureState();
    }

    /// <summary>
    /// Replaces every field with <paramref name="settings"/> but keeps the values the draft was
    /// loaded with as its baseline, so an imported file still counts as an unapplied change.
    /// </summary>
    public void ReplaceValues(AppSettings settings)
    {
        _saveErrors.Clear();
        _candidateErrors.Clear();
        ExportOptionsReplaced = true;
        LoadValues(settings);
    }

    /// <summary>
    /// True when any value differs from the settings this draft was loaded from. Typing a value
    /// and then restoring it counts as unchanged.
    /// </summary>
    public bool HasUnappliedChanges => !CaptureState().SequenceEqual(_baseline, StringComparer.Ordinal);

    /// <summary>Advances the saved baseline without replacing text or moving the editing caret.</summary>
    public void AcceptSaved(AppSettings saved, bool acceptReplacement = true)
    {
        _baseline = new SettingsDraft(saved).CaptureState();
        if (acceptReplacement) ExportOptionsReplaced = false;
        FolderDestinationEdited = UseCurrentFolderForNewCaptures != saved.Queue.UseCurrentFolderForNewCaptures;
        RaisePropertyChanged(nameof(HasUnappliedChanges));
    }

    /// <summary>A rejected OS setting stays inline until that field is edited again.</summary>
    public void SetSaveError(string property, string message)
    {
        _saveErrors[property] = message;
        RaiseErrorsChanged(property);
    }

    /// <summary>A cross-field error from merging valid edits with saved values, recomputed before saving.</summary>
    public void SetCandidateError(string property, string message)
    {
        _candidateErrors[property] = message;
        RaiseErrorsChanged(property);
    }

    public void ClearCandidateErrors()
    {
        string[] properties = _candidateErrors.Keys.ToArray();
        _candidateErrors.Clear();
        foreach (string property in properties) RaiseErrorsChanged(property);
    }

    private void LoadValues(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings s = settings.DeepClone();

        _launchAtLogin = s.General.LaunchAtLogin;
        _notifyOnQuickSave = s.General.NotifyOnQuickSave;
        _playCaptureSound = s.General.PlayCaptureSound;
        _afterCapture = s.General.AfterCapture;
        _showShortcutHints = s.General.ShowShortcutHints;

        _includeCursor = s.Capture.IncludeCursor;
        _autoDetectWindows = s.Capture.AutoDetectWindows;
        _showMagnifier = s.Capture.ShowMagnifier;
        _showSelectionGrid = s.Capture.ShowSelectionGrid;
        _abortOnFocusLoss = s.Capture.AbortOnFocusLoss;
        _delaySeconds = Int(s.Capture.DelaySeconds);
        _regionHistoryLimit = Int(s.Capture.RegionHistoryLimit);

        _recordingFrameRate = Int(s.Recording.TargetFps);
        _useRecordingStartDelay = s.Recording.UseStartDelay;
        _recordingStartDelaySeconds = Int(s.Recording.StartDelaySeconds);
        _recordingIncludeCursor = s.Recording.IncludeCursor;
        _recordingCursorHighlight = s.Recording.CursorHighlight;
        // The recorder applies a preset only when both sides are positive; no preset, the 0 older
        // versions saved, or a single side all record the dragged size. The boxes then offer a
        // common size, ready for when the fixed size is chosen.
        int? presetWidth = s.Recording.PresetWidth;
        int? presetHeight = s.Recording.PresetHeight;
        _useRecordingFixedSize = presetWidth > 0 && presetHeight > 0;
        _recordingPresetWidth = Int(presetWidth > 0 ? presetWidth.Value : SuggestedRecordingWidth);
        _recordingPresetHeight = Int(presetHeight > 0 ? presetHeight.Value : SuggestedRecordingHeight);
        _recordingCaptureMode = RecordingCaptureModeNames.ToSetting(s.Recording.CaptureMode);

        _captureHotkey = s.Hotkeys.Capture.ToString();
        _openLibraryHotkey = s.Hotkeys.OpenLibrary.ToString();
        _pasteToScreenHotkey = s.Hotkeys.PasteToScreen.ToString();
        _hideAllPinsHotkey = s.Hotkeys.HideAllPins.ToString();
        _toggleClickThroughHotkey = s.Hotkeys.ToggleClickThrough.ToString();
        _repeatLastRegionHotkey = s.Hotkeys.RepeatLastRegion.ToString();
        _captureWindowHotkey = s.Hotkeys.CaptureWindow.ToString();
        _captureFullScreenHotkey = s.Hotkeys.CaptureFullScreen.ToString();
        _recordRegionHotkey = s.Hotkeys.RecordRegion.ToString();
        _scrollingCaptureHotkey = s.Hotkeys.ScrollingCapture.ToString();
        _delayedCaptureHotkey = s.Hotkeys.DelayedCapture.ToString();
        _uploadGitHubImageHotkey = s.Hotkeys.UploadGitHubImage.ToString();
        _restoreClosedPinHotkey = s.Hotkeys.RestoreClosedPin.ToString();
        _captureTextHotkey = s.Hotkeys.CaptureText.ToString();
        _pauseRecordingHotkey = s.Hotkeys.PauseRecording.ToString();
        _forceCoreShortcuts = s.Hotkeys.ForceCoreShortcuts;

        _maxItems = Int(s.Queue.MaxItems);
        _maxGiB = Gib(s.Queue.MaxBytes);
        _thumbnailLongEdge = Int(s.Queue.ThumbnailLongEdge);
        _capturesDirectoryOverride = DisplayDirectory(
            s.Queue.CapturesDirectoryOverride,
            AppPaths.CreateDefault().CapturesRoot);
        _imageRetentionHours = Int(s.Queue.ImageRetentionHours);
        _folderFilePrefix = s.Queue.FolderFilePrefix;
        FolderDestinationEdited = false;
        _useCurrentFolderForNewCaptures = s.Queue.UseCurrentFolderForNewCaptures;
        _defaultCaptureFolderId = s.Queue.DefaultCaptureFolderId;

        _quickSaveDirectoryOverride = DisplayDirectory(
            s.Export.QuickSaveDirectoryOverride,
            AppPaths.CreateDefault().QuickSaveRoot);
        _gitHubIssueUrl = GitHubIssueImageUrl.TryNormalize(s.GitHub.IssueUrl, out string issueUrl)
            ? issueUrl
            : GitHubIssueImageUrl.DefaultIssueUrl;
        _gitHubToken = s.GitHub.Token ?? string.Empty;
        _theme = AppThemeNames.ToSetting(AppThemeNames.Parse(s.General.Theme));
        _accentColor = AppAccentNames.ToSetting(AppAccentNames.Parse(s.General.AccentColor));
        bool customGuide = CaptureGuideColorValue.TryNormalize(s.General.CaptureGuideColor, out string guideColor);
        _captureGuideColorMode = customGuide ? "custom" : "theme";
        _captureGuideColorHex = customGuide ? guideColor : CaptureGuideColorValue.DefaultCustomColor;
        _copyToClipboardOnQuickSave = s.Export.CopyToClipboardOnQuickSave;
        _showSourceWindowTitle = s.Export.ShowSourceWindowTitle;
        _lastImageExport = s.Export.LastImageExport with { };
        _fileNamePattern = s.Export.FileNamePattern;

        _closeOnDoubleClick = s.Pin.CloseOnDoubleClick;
        _restoreOpenPinsOnLaunch = s.Pin.RestoreOpenPinsOnLaunch;
        _initialOpacity = Dbl(s.Pin.InitialOpacity);
        _zoomStep = Dbl(s.Pin.ZoomStep);
        _ctrlClickDebounceMs = Int(s.Pin.CtrlClickDebounceMs);
        _closedWindowRestoreLimit = Int(s.Pin.ClosedWindowRestoreLimit);

        _preferredLanguages = string.Join(", ", s.Ocr.PreferredLanguages);
        _ocrQuality = OcrQualityNames.ToSetting(s.Ocr.Quality);
        _cacheResults = s.Ocr.CacheResults;

        _lastAnnotationTool = s.Annotation.LastTool;
        _annotationStrokeStyle = s.Annotation.StrokeStyle;
        _annotationFillTransparency = s.Annotation.FillTransparency;
        _strokeThickness = Dbl(s.Annotation.StrokeThickness);
        _fontSize = Dbl(s.Annotation.FontSize);
        _fontFamily = s.Annotation.FontFamily;
        _mosaicBlockSize = Int(s.Annotation.MosaicBlockSize);
        _highlighterAlpha = Int(s.Annotation.HighlighterAlpha);
        _loadedHighlighterAlpha = s.Annotation.HighlighterAlpha;
        _highlighterOpacityPercent = Int(AlphaToPercent(s.Annotation.HighlighterAlpha));
        _blurRadius = Int(s.Annotation.BlurRadius);

        // Preserve members not surfaced in the UI so mapping back is lossless.
        _preservedSchemaVersion = s.SchemaVersion;
        _preservedStrokeColor = s.Annotation.StrokeColor;
        _preservedTextColor = s.Annotation.TextColor;
        _preservedTextBold = s.Annotation.TextBold;
        _preservedTextBackground = s.Annotation.TextBackground;
        _preservedTextOutline = s.Annotation.TextOutline;
        _preservedRecentFontFamilies = [.. s.Annotation.RecentFontFamilies];
        _preservedRecentColors = [.. s.Annotation.RecentColors];
        _preservedColorFormat = s.Capture.ColorFormat;
        _preservedPreserveTransparency = s.Export.PreserveTransparency;
        _language = s.General.Language;
        _preservedIsFirstRun = s.General.IsFirstRun;
        _preservedCoreShortcutsAsked = s.Hotkeys.CoreShortcutsAsked;
        _preservedFirstCaptureTipShown = s.General.FirstCaptureTipShown;
        _preservedFirstPinTipShown = s.General.FirstPinTipShown;
        _preservedRecordingBitrateBitsPerSecond = s.Recording.BitrateBitsPerSecond;
        _preservedRecordingCoarseStepSeconds = s.Recording.CoarseStepSeconds;

        RaiseAllChanged();
        ValidateAll();
    }

    /// <summary>
    /// Resets every editable field to the defaults a new installation starts with
    /// (<see cref="NewInstallDefaults"/>). Deliberate and fully reversible: nothing is written
    /// to disk until Apply, so a subsequent Cancel or a re-<see cref="LoadFrom"/> restores the
    /// previous values. The baseline is kept, so the reset counts as an unapplied change.
    /// </summary>
    public void ResetToDefaults()
    {
        AppSettings defaults = NewInstallDefaults.Create();
        defaults.Hotkeys.CoreShortcutsAsked = _preservedCoreShortcutsAsked;
        ReplaceValues(defaults);
    }

    /// <summary>
    /// Every value <see cref="ToAppSettings"/> writes, in a fixed order, as invariant text.
    /// The GitHub token stays in memory only; it is compared, never logged.
    /// </summary>
    private string[] CaptureState() =>
    [
        B(_launchAtLogin), B(_notifyOnQuickSave), B(_playCaptureSound), _afterCapture.ToString(), B(_showShortcutHints),
        _language, _theme, _accentColor, _captureGuideColorMode, IsCustomCaptureGuideColor ? _captureGuideColorHex : string.Empty,
        B(_includeCursor), B(_autoDetectWindows), B(_showMagnifier), B(_showSelectionGrid), B(_abortOnFocusLoss),
        _preservedColorFormat.ToString(), _delaySeconds, _regionHistoryLimit,
        _recordingFrameRate, B(_useRecordingStartDelay), _recordingStartDelaySeconds, B(_recordingIncludeCursor),
        B(_recordingCursorHighlight), B(_useRecordingFixedSize), _recordingPresetWidth, _recordingPresetHeight, _recordingCaptureMode,
        Int(_preservedRecordingBitrateBitsPerSecond), Dbl(_preservedRecordingCoarseStepSeconds),
        _captureHotkey, _openLibraryHotkey, _pasteToScreenHotkey, _hideAllPinsHotkey, _toggleClickThroughHotkey,
        _repeatLastRegionHotkey, _captureWindowHotkey, _captureFullScreenHotkey, _recordRegionHotkey,
        _scrollingCaptureHotkey, _delayedCaptureHotkey, _uploadGitHubImageHotkey, _restoreClosedPinHotkey,
        _captureTextHotkey, _pauseRecordingHotkey, B(_forceCoreShortcuts), B(_preservedCoreShortcutsAsked),
        _maxItems, _maxGiB, _thumbnailLongEdge, _capturesDirectoryOverride, _imageRetentionHours, _folderFilePrefix,
        B(_useCurrentFolderForNewCaptures), _defaultCaptureFolderId?.ToString() ?? string.Empty,
        _gitHubIssueUrl, _gitHubToken,
        _quickSaveDirectoryOverride, B(_copyToClipboardOnQuickSave), B(_showSourceWindowTitle), _fileNamePattern,
        B(_preservedPreserveTransparency),
        _lastImageExport.Format.ToString(), Int(_lastImageExport.LongEdge), Int(_lastImageExport.ReductionPercent),
        B(_lastImageExport.IncludeSourceUrl), Int(_lastImageExport.Margin), Int(_lastImageExport.CornerRadius),
        _lastImageExport.Background.ToString(), B(_lastImageExport.Shadow),
        B(_closeOnDoubleClick), B(_restoreOpenPinsOnLaunch), _initialOpacity, _zoomStep, _ctrlClickDebounceMs, _closedWindowRestoreLimit,
        _preferredLanguages, _ocrQuality, B(_cacheResults),
        _lastAnnotationTool, _annotationStrokeStyle.ToString(), Dbl(_annotationFillTransparency), _strokeThickness,
        _fontSize, _fontFamily, _mosaicBlockSize, _highlighterAlpha, _highlighterOpacityPercent, _blurRadius,
        _preservedStrokeColor.ToString(), _preservedTextColor.ToString(),
        B(_preservedTextBold), B(_preservedTextBackground), B(_preservedTextOutline),
        string.Join(";", _preservedRecentFontFamilies),
        string.Join(";", _preservedRecentColors.Select(color => color.ToString())),
    ];

    private static string B(bool value) => value ? "1" : "0";

    /// <summary>
    /// Materialises the draft into a new <see cref="AppSettings"/>. Callers must check
    /// <see cref="HasErrors"/> first; mapping a draft that has errors throws.
    /// </summary>
    public AppSettings ToAppSettings()
    {
        if (HasErrors)
        {
            throw new InvalidOperationException("Cannot map a settings draft that has validation errors.");
        }

        var s = new AppSettings
        {
            SchemaVersion = _preservedSchemaVersion,
            General =
            {
                LaunchAtLogin = _launchAtLogin,
                NotifyOnQuickSave = _notifyOnQuickSave,
                PlayCaptureSound = _playCaptureSound,
                AfterCapture = _afterCapture,
                ShowShortcutHints = _showShortcutHints,
                Language = _language,
                Theme = AppThemeNames.ToSetting(AppThemeNames.Parse(_theme)),
                AccentColor = AppAccentNames.ToSetting(AppAccentNames.Parse(_accentColor)),
                CaptureGuideColor = CaptureGuideColorSetting,
                ThemeRevision = 1,
                IsFirstRun = _preservedIsFirstRun,
                FirstCaptureTipShown = _preservedFirstCaptureTipShown,
                FirstPinTipShown = _preservedFirstPinTipShown,
            },
            Capture =
            {
                IncludeCursor = _includeCursor,
                AutoDetectWindows = _autoDetectWindows,
                ShowMagnifier = _showMagnifier,
                ShowSelectionGrid = _showSelectionGrid,
                AbortOnFocusLoss = _abortOnFocusLoss,
                ColorFormat = _preservedColorFormat,
                DelaySeconds = ParseInt(_delaySeconds),
                RegionHistoryLimit = ParseInt(_regionHistoryLimit),
            },
            Recording =
            {
                FrameRate = (RecordingFrameRate)ParseInt(_recordingFrameRate),
                UseStartDelay = _useRecordingStartDelay,
                StartDelaySeconds = ParseInt(_recordingStartDelaySeconds),
                IncludeCursor = _recordingIncludeCursor,
                CursorHighlight = _recordingCursorHighlight,
                BitrateBitsPerSecond = _preservedRecordingBitrateBitsPerSecond,
                CoarseStepSeconds = _preservedRecordingCoarseStepSeconds,
                // The dragged size is saved as no preset, which the recorder already reads as free.
                PresetWidth = _useRecordingFixedSize ? ParseOptionalInt(_recordingPresetWidth) : null,
                PresetHeight = _useRecordingFixedSize ? ParseOptionalInt(_recordingPresetHeight) : null,
                CaptureMode = RecordingCaptureModeNames.Parse(_recordingCaptureMode),
            },
            Hotkeys =
            {
                Capture = ParseHotkey(_captureHotkey),
                OpenLibrary = ParseHotkey(_openLibraryHotkey),
                PasteToScreen = ParseHotkey(_pasteToScreenHotkey),
                HideAllPins = ParseHotkey(_hideAllPinsHotkey),
                ToggleClickThrough = ParseHotkey(_toggleClickThroughHotkey),
                RepeatLastRegion = ParseHotkey(_repeatLastRegionHotkey),
                CaptureWindow = ParseHotkey(_captureWindowHotkey),
                CaptureFullScreen = ParseHotkey(_captureFullScreenHotkey),
                RecordRegion = ParseHotkey(_recordRegionHotkey),
                ScrollingCapture = ParseHotkey(_scrollingCaptureHotkey),
                DelayedCapture = ParseHotkey(_delayedCaptureHotkey),
                UploadGitHubImage = ParseHotkey(_uploadGitHubImageHotkey),
                RestoreClosedPin = ParseHotkey(_restoreClosedPinHotkey),
                CaptureText = ParseHotkey(_captureTextHotkey),
                PauseRecording = ParseHotkey(_pauseRecordingHotkey),
                ForceCoreShortcuts = _forceCoreShortcuts,
                CoreShortcutsAsked = _preservedCoreShortcutsAsked,
            },
            Queue =
            {
                MaxItems = ParseInt(_maxItems),
                MaxBytes = (long)Math.Round(ParseDouble(_maxGiB) * SettingsRanges.BytesPerGiB),
                ThumbnailLongEdge = ParseInt(_thumbnailLongEdge),
                CapturesDirectoryOverride = PersistDirectory(
                    _capturesDirectoryOverride,
                    AppPaths.CreateDefault().CapturesRoot),
                ImageRetentionHours = ParseInt(_imageRetentionHours),
                FolderFilePrefix = MyCapture.Core.Queue.CaptureFolderNaming.NormalizePrefix(_folderFilePrefix),
                UseCurrentFolderForNewCaptures = _useCurrentFolderForNewCaptures,
                DefaultCaptureFolderId = _useCurrentFolderForNewCaptures ? _defaultCaptureFolderId : null,
            },
            GitHub =
            {
                IssueUrl = GitHubIssueImageUrl.TryNormalize(_gitHubIssueUrl, out string issueUrl)
                    ? issueUrl
                    : GitHubIssueImageUrl.DefaultIssueUrl,
                Token = _gitHubToken,
            },
            Export =
            {
                QuickSaveDirectoryOverride = PersistDirectory(
                    _quickSaveDirectoryOverride,
                    AppPaths.CreateDefault().QuickSaveRoot),
                CopyToClipboardOnQuickSave = _copyToClipboardOnQuickSave,
                ShowSourceWindowTitle = _showSourceWindowTitle,
                LastImageExport = _lastImageExport with { },
                FileNamePattern = _fileNamePattern.Trim(),
                PreserveTransparency = _preservedPreserveTransparency,
            },
            Pin =
            {
                CloseOnDoubleClick = _closeOnDoubleClick,
                RestoreOpenPinsOnLaunch = _restoreOpenPinsOnLaunch,
                InitialOpacity = ParseDouble(_initialOpacity),
                ZoomStep = ParseDouble(_zoomStep),
                CtrlClickDebounceMs = ParseInt(_ctrlClickDebounceMs),
                ClosedWindowRestoreLimit = ParseInt(_closedWindowRestoreLimit),
            },
            Ocr =
            {
                PreferredLanguages = ParseLanguages(_preferredLanguages),
                Quality = OcrQualityNames.Parse(_ocrQuality),
                UpscaleFactor = OcrQualityProfile.UpscaleFactor(OcrQualityNames.Parse(_ocrQuality)),
                CacheResults = _cacheResults,
            },
            Annotation =
            {
                LastTool = _lastAnnotationTool,
                StrokeStyle = _annotationStrokeStyle,
                FillTransparency = _annotationFillTransparency,
                StrokeColor = _preservedStrokeColor,
                StrokeThickness = ParseDouble(_strokeThickness),
                TextColor = _preservedTextColor,
                FontSize = ParseDouble(_fontSize),
                FontFamily = _fontFamily.Trim(),
                TextBold = _preservedTextBold,
                TextBackground = _preservedTextBackground,
                TextOutline = _preservedTextOutline,
                RecentFontFamilies = [.. _preservedRecentFontFamilies],
                MosaicBlockSize = ParseInt(_mosaicBlockSize),
                HighlighterAlpha = (byte)ParseInt(_highlighterAlpha),
                RecentColors = [.. _preservedRecentColors],
                BlurRadius = ParseInt(_blurRadius),
            },
        };

        return s;
    }

    /// <summary>
    /// The parsed hotkey set as it currently stands, for callers that want to feed a
    /// transactional reconfigure without a full <see cref="ToAppSettings"/> mapping.
    /// </summary>
    public HotkeySettings ToHotkeySettings() => new()
    {
        Capture = ParseHotkey(_captureHotkey),
        PasteToScreen = ParseHotkey(_pasteToScreenHotkey),
        HideAllPins = ParseHotkey(_hideAllPinsHotkey),
        ToggleClickThrough = ParseHotkey(_toggleClickThroughHotkey),
        RepeatLastRegion = ParseHotkey(_repeatLastRegionHotkey),
        CaptureWindow = ParseHotkey(_captureWindowHotkey),
        CaptureFullScreen = ParseHotkey(_captureFullScreenHotkey),
        RecordRegion = ParseHotkey(_recordRegionHotkey),
        ScrollingCapture = ParseHotkey(_scrollingCaptureHotkey),
        DelayedCapture = ParseHotkey(_delayedCaptureHotkey),
        UploadGitHubImage = ParseHotkey(_uploadGitHubImageHotkey),
        OpenLibrary = ParseHotkey(_openLibraryHotkey),
        RestoreClosedPin = ParseHotkey(_restoreClosedPinHotkey),
        CaptureText = ParseHotkey(_captureTextHotkey),
        PauseRecording = ParseHotkey(_pauseRecordingHotkey),
        ForceCoreShortcuts = _forceCoreShortcuts,
        CoreShortcutsAsked = _preservedCoreShortcutsAsked,
    };

    // ================= INotifyDataErrorInfo =================

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return AllErrors();
        }

        IEnumerable<string> errors = _errors.TryGetValue(propertyName, out List<string>? list) ? list : [];
        return errors.Concat(_saveErrors.TryGetValue(propertyName, out string? message) ? [message] : [])
            .Concat(_candidateErrors.TryGetValue(propertyName, out string? candidateMessage) ? [candidateMessage] : []).ToList();
    }

    /// <summary>Every current error message across all fields, for the summary announcement.</summary>
    public IReadOnlyList<string> AllErrors() => _errors
        .SelectMany(kv => kv.Value)
        .Concat(_saveErrors.Values).Distinct()
        .Concat(_candidateErrors.Values).Distinct()
        .ToList();

    // ================= Validation =================

    private void ValidateAll()
    {
        ValidateCaptureGuideColor();
        ValidateInt(_delaySeconds, SettingsRanges.DelaySeconds, nameof(DelaySeconds));
        ValidateInt(_regionHistoryLimit, SettingsRanges.RegionHistoryLimit, nameof(RegionHistoryLimit));
        ValidateRecordingFrameRate(_recordingFrameRate);
        ValidateInt(
            _recordingStartDelaySeconds,
            SettingsRanges.RecordingStartDelaySeconds,
            nameof(RecordingStartDelaySeconds));
        ValidateRecordingPresetSize();
        ValidateInt(_maxItems, SettingsRanges.MaxItems, nameof(MaxItems));
        ValidateMaxGiB(_maxGiB);
        ValidateInt(_thumbnailLongEdge, SettingsRanges.ThumbnailLongEdge, nameof(ThumbnailLongEdge));
        ValidateRetention(_imageRetentionHours);
        ValidateFolderFilePrefix(_folderFilePrefix);
        ValidateOptionalDirectory(_capturesDirectoryOverride, nameof(CapturesDirectoryOverride));
        ValidateOptionalDirectory(_quickSaveDirectoryOverride, nameof(QuickSaveDirectoryOverride));
        ValidateGitHubIssueUrl(_gitHubIssueUrl);
        ValidateFileNamePattern(_fileNamePattern);
        ValidateDouble(_initialOpacity, SettingsRanges.InitialOpacity, nameof(InitialOpacity));
        ValidateDouble(_zoomStep, SettingsRanges.ZoomStep, nameof(ZoomStep));
        ValidateInt(_ctrlClickDebounceMs, SettingsRanges.CtrlClickDebounceMs, nameof(CtrlClickDebounceMs));
        ValidateInt(_closedWindowRestoreLimit, SettingsRanges.ClosedWindowRestoreLimit, nameof(ClosedWindowRestoreLimit));
        ValidateLanguages(_preferredLanguages);
        ValidateDouble(_strokeThickness, SettingsRanges.StrokeThickness, nameof(StrokeThickness));
        ValidateDouble(_fontSize, SettingsRanges.FontSize, nameof(FontSize));
        ValidateNonEmpty(_fontFamily, nameof(FontFamily), UiText.Get("Text_B64CA1BE6711"));
        ValidateInt(_mosaicBlockSize, SettingsRanges.MosaicBlockSize, nameof(MosaicBlockSize));
        ValidateInt(_highlighterAlpha, SettingsRanges.HighlighterAlpha, nameof(HighlighterAlpha));
        ValidateInt(_highlighterOpacityPercent, SettingsRanges.HighlighterOpacityPercent, nameof(HighlighterOpacityPercent));
        ValidateInt(_blurRadius, SettingsRanges.BlurRadius, nameof(BlurRadius));
        ValidateAllHotkeys();
    }

    private void ValidateInt(string value, Range<int> range, [CallerMemberName] string? property = null)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            SetError(property!, UiText.Format("Text_9A6259D8B7CF", range.Describe()));
            return;
        }

        SetErrorState(property!, range.Contains(parsed), UiText.Format("Text_CF32FE576AE7", range.Describe()));
    }

    /// <summary>An hour count in range, or the stored "never" value the window offers.</summary>
    private void ValidateRetention(string value)
    {
        Range<int> range = SettingsRanges.ImageRetentionHours;
        if (!TryParseInt(value, out int hours))
        {
            SetError(nameof(ImageRetentionHours), UiText.Format("Text_9A6259D8B7CF", range.Describe()));
            return;
        }

        SetErrorState(nameof(ImageRetentionHours), MyCapture.Core.Queue.CaptureRetention.IsValidHours(hours),
            UiText.Format("Text_CF32FE576AE7", range.Describe()));
    }

    private static bool TryParseInt(string? value, out int parsed) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    /// <summary>
    /// Validates the size boxes only while the fixed size is chosen; with the dragged size they
    /// are disabled and ignored, so a stale value there never blocks Apply.
    /// </summary>
    private void ValidateRecordingPresetSize()
    {
        if (_useRecordingFixedSize)
        {
            ValidateInt(_recordingPresetWidth, SettingsRanges.RecordingPresetSize, nameof(RecordingPresetWidth));
            ValidateInt(_recordingPresetHeight, SettingsRanges.RecordingPresetSize, nameof(RecordingPresetHeight));
        }
        else
        {
            ClearError(nameof(RecordingPresetWidth));
            ClearError(nameof(RecordingPresetHeight));
        }
    }

    private void ValidateRecordingFrameRate(string value)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            SetError(nameof(RecordingFrameRate), UiText.Get("Text_2E447977E64D"));
            return;
        }

        SetErrorState(
            nameof(RecordingFrameRate),
            SettingsRanges.RecordingFrameRates.Contains(parsed),
            UiText.Get("Text_594449493143"));
    }

    private void ValidateDouble(string value, Range<double> range, [CallerMemberName] string? property = null)
    {
        if (!double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            SetError(property!, UiText.Format("Text_5461D9E26042", range.Describe()));
            return;
        }

        SetErrorState(property!, range.Contains(parsed), UiText.Format("Text_CF32FE576AE7", range.Describe()));
    }

    private void ValidateMaxGiB(string value)
    {
        if (!double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gib)
            || double.IsNaN(gib) || double.IsInfinity(gib))
        {
            SetError(nameof(MaxGiB), UiText.Get("Text_764879D98E25"));
            return;
        }

        long bytes = (long)Math.Round(gib * SettingsRanges.BytesPerGiB);
        bool ok = SettingsRanges.MaxBytes.Contains(bytes);
        double minGiB = SettingsRanges.MaxBytes.Min / (double)SettingsRanges.BytesPerGiB;
        double maxGiB = SettingsRanges.MaxBytes.Max / (double)SettingsRanges.BytesPerGiB;
        SetErrorState(
            nameof(MaxGiB),
            ok,
            string.Format(CultureInfo.InvariantCulture, UiText.Get("Text_3862727D2DCB"), minGiB, maxGiB));
    }

    private void ValidateFolderFilePrefix(string value)
    {
        bool valid;
        try { _ = MyCapture.Core.Queue.CaptureFolderNaming.NormalizePrefix(value); valid = true; }
        catch (ArgumentException) { valid = false; }
        SetErrorState(nameof(FolderFilePrefix), valid, UiText.Get("Folder410.PrefixInvalid"));
    }

    private void ValidateFileNamePattern(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            SetError(nameof(FileNamePattern), UiText.Get("Text_174531E69AB0"));
            return;
        }

        // The pattern must render to a non-empty, filesystem-safe stem. QuickSaveNaming
        // sanitises illegal characters; a pattern that renders to nothing usable falls
        // back to a generic stem, which is a silent surprise, so it is rejected here.
        string stem = MyCapture.Core.Storage.QuickSaveNaming.BuildStem(trimmed, DateTimeOffset.Now);
        bool ok = !string.Equals(stem, MyCapture.Core.Storage.QuickSaveNaming.FallbackStem, StringComparison.Ordinal)
                  || trimmed.Contains(MyCapture.Core.Storage.QuickSaveNaming.FallbackStem, StringComparison.OrdinalIgnoreCase);
        SetErrorState(
            nameof(FileNamePattern),
            ok,
            UiText.Get("Text_D1357BFAAF9A"));
    }

    private void ValidateLanguages(string value)
    {
        List<string> tags = ParseLanguages(value);
        if (tags.Count == 0)
        {
            SetError(nameof(PreferredLanguages), UiText.Get("Text_AA763F299972"));
            return;
        }

        foreach (string tag in tags)
        {
            if (!IsPlausibleBcp47(tag))
            {
                SetError(nameof(PreferredLanguages), UiText.Format("Text_CDC04935B675", tag));
                return;
            }
        }

        ClearError(nameof(PreferredLanguages));
    }

    private void ValidateGitHubIssueUrl(string value)
    {
        SetErrorState(
            nameof(GitHubIssueUrl),
            GitHubIssueImageUrl.TryNormalize(value, out _),
            UiText.Get("Settings.GitHubIssueUrlInvalid"));
    }

    private static string DisplayDirectory(string stored, string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored.Trim();

    private static string PersistDirectory(string displayed, string fallback)
    {
        string trimmed = displayed?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (string.Equals(Path.GetFullPath(trimmed), Path.GetFullPath(fallback), StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        return trimmed;
    }

    private void ValidateOptionalDirectory(string value, string property)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            ClearError(property); // Empty means "use the default location".
            return;
        }

        bool ok;
        try
        {
            _ = Path.GetFullPath(trimmed);
            ok = trimmed.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException) { ok = false; }
        catch (NotSupportedException) { ok = false; }
        catch (PathTooLongException) { ok = false; }

        SetErrorState(property, ok, UiText.Get("Text_F64AB9FE8BCD"));
    }

    private void ValidateNonEmpty(string value, string property, string message) =>
        SetErrorState(property, !string.IsNullOrWhiteSpace(value), message);

    /// <summary>
    /// Validates every hotkey field at once: each must parse, and no two assigned chords
    /// may collide under case-insensitive semantic equality. Runs on any hotkey edit
    /// because a duplicate is a relationship between two fields, not a property of one.
    /// </summary>
    private void ValidateAllHotkeys()
    {
        (string Property, string Raw)[] fields =
        [
            (nameof(CaptureHotkey), _captureHotkey),
            (nameof(OpenLibraryHotkey), _openLibraryHotkey),
            (nameof(PasteToScreenHotkey), _pasteToScreenHotkey),
            (nameof(HideAllPinsHotkey), _hideAllPinsHotkey),
            (nameof(ToggleClickThroughHotkey), _toggleClickThroughHotkey),
            (nameof(RepeatLastRegionHotkey), _repeatLastRegionHotkey),
            (nameof(CaptureWindowHotkey), _captureWindowHotkey),
            (nameof(CaptureFullScreenHotkey), _captureFullScreenHotkey),
            (nameof(RecordRegionHotkey), _recordRegionHotkey),
            (nameof(ScrollingCaptureHotkey), _scrollingCaptureHotkey),
            (nameof(DelayedCaptureHotkey), _delayedCaptureHotkey),
            (nameof(UploadGitHubImageHotkey), _uploadGitHubImageHotkey),
            (nameof(RestoreClosedPinHotkey), _restoreClosedPinHotkey),
            (nameof(CaptureTextHotkey), _captureTextHotkey),
            (nameof(PauseRecordingHotkey), _pauseRecordingHotkey),
        ];

        var parsed = new Dictionary<string, Hotkey>(fields.Length);
        foreach ((string property, string raw) in fields)
        {
            if (!Hotkey.TryParse(raw, out Hotkey hotkey))
            {
                SetError(property, UiText.Get("Text_1FBF8C644D76"));
                continue;
            }

            if (!hotkey.IsSafeGlobalShortcut)
            {
                SetError(property, UiText.Get("Hotkey.ModifierRequired"));
                continue;
            }

            parsed[property] = hotkey;
            ClearError(property);
        }

        // Duplicate detection over assigned chords using semantic (value) equality.
        var seen = new Dictionary<Hotkey, string>();
        foreach ((string property, Hotkey hotkey) in parsed.Select(kv => (kv.Key, kv.Value)))
        {
            if (!hotkey.IsAssigned)
            {
                continue;
            }

            if (seen.TryGetValue(hotkey, out string? firstProperty))
            {
                SetError(firstProperty, UiText.Format("Text_AD823133F009", hotkey));
                SetError(property, UiText.Format("Text_AD823133F009", hotkey));
            }
            else
            {
                seen[hotkey] = property;
            }
        }
    }

    // ================= Parsing helpers =================

    private static int ParseInt(string value) =>
        int.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static int? ParseOptionalInt(string value)
    {
        return int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static double ParseDouble(string value) =>
        double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Hotkey ParseHotkey(string value) =>
        Hotkey.TryParse(value, out Hotkey hotkey) ? hotkey : Hotkey.None;

    public static List<string> ParseLanguages(string value) =>
        (value ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>
    /// A pragmatic BCP-47 shape check: letters/digits separated by single hyphens, first
    /// subtag 2-3 letters. Not a full registry lookup — the OCR engine is the final
    /// authority — but enough to reject obvious typos before Apply.
    /// </summary>
    private static bool IsPlausibleBcp47(string tag)
    {
        string[] parts = tag.Split('-');
        if (parts.Length == 0 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsLetter))
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 8 || !part.All(char.IsLetterOrDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Dbl(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Gib(long bytes) =>
        (bytes / (double)SettingsRanges.BytesPerGiB).ToString("0.###", CultureInfo.InvariantCulture);

    // ================= Error bookkeeping =================

    private void SetErrorState(string property, bool valid, string message)
    {
        if (valid)
        {
            ClearError(property);
        }
        else
        {
            SetError(property, message);
        }
    }

    private void SetError(string property, string message)
    {
        if (_errors.TryGetValue(property, out List<string>? existing)
            && existing.Count == 1
            && existing[0] == message)
        {
            return; // No change; avoid a redundant notification loop.
        }

        _errors[property] = [message];
        RaiseErrorsChanged(property);
    }

    private void ClearError(string property)
    {
        if (_errors.Remove(property))
        {
            RaiseErrorsChanged(property);
        }
    }

    private void RaiseErrorsChanged(string property)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        RaisePropertyChanged(nameof(HasErrors));
    }

    // ================= INotifyPropertyChanged =================

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        if (property is not null && _candidateErrors.Remove(property)) RaiseErrorsChanged(property);
        if (property is not null && _saveErrors.Remove(property)) RaiseErrorsChanged(property);
        RaisePropertyChanged(property);
        return true;
    }

    private void RaisePropertyChanged(string? property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void RaiseAllChanged() => RaisePropertyChanged(null);

    /// <summary>The fixed recording size the boxes offer while no size is saved (720p).</summary>
    private const int SuggestedRecordingWidth = 1280;
    private const int SuggestedRecordingHeight = 720;

    // ================= Preserved (non-UI) members =================

    private int _preservedSchemaVersion = 1;
    private Primitives.ColorRgba _preservedStrokeColor;
    private Primitives.ColorRgba _preservedTextColor;
    private bool _preservedTextBold;
    private bool _preservedTextBackground;
    private bool _preservedTextOutline = true;
    private List<string> _preservedRecentFontFamilies = [];
    private List<Primitives.ColorRgba> _preservedRecentColors = [];
    private ColorFormat _preservedColorFormat;
    private bool _preservedPreserveTransparency;
    private string _language = string.Empty;
    private bool _preservedIsFirstRun;
    private bool _preservedCoreShortcutsAsked;
    private bool _preservedFirstCaptureTipShown;
    private bool _preservedFirstPinTipShown;
    private int _preservedRecordingBitrateBitsPerSecond;
    private double _preservedRecordingCoarseStepSeconds;

    /// <summary>The state <see cref="LoadFrom"/> last loaded; see <see cref="HasUnappliedChanges"/>.</summary>
    private string[] _baseline = [];
}
