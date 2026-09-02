using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MyCapture.Core.Recording;

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

    // ----- General -----
    private bool _launchAtLogin;
    private bool _notifyOnQuickSave;
    private bool _playCaptureSound;

    // ----- Capture -----
    private bool _includeCursor;
    private bool _autoDetectWindows;
    private bool _showMagnifier;
    private string _delaySeconds = string.Empty;
    private bool _abortOnFocusLoss;
    private string _regionHistoryLimit = string.Empty;

    // ----- Recording -----
    private string _recordingFrameRate = string.Empty;
    private bool _useRecordingStartDelay;
    private string _recordingStartDelaySeconds = string.Empty;
    private bool _recordingIncludeCursor;

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

    // ----- Storage -----
    private string _maxItems = string.Empty;
    private string _maxGiB = string.Empty;
    private string _thumbnailLongEdge = string.Empty;
    private string _capturesDirectoryOverride = string.Empty;

    // ----- Export -----
    private string _quickSaveDirectoryOverride = string.Empty;
    private bool _copyToClipboardOnQuickSave;
    private string _fileNamePattern = string.Empty;

    // ----- Pin -----
    private bool _closeOnDoubleClick;
    private string _initialOpacity = string.Empty;
    private string _zoomStep = string.Empty;
    private string _ctrlClickDebounceMs = string.Empty;
    private string _closedWindowRestoreLimit = string.Empty;

    // ----- OCR -----
    private string _preferredLanguages = string.Empty;
    private string _upscaleFactor = string.Empty;
    private bool _cacheResults;

    // ----- Annotation -----
    private string _strokeThickness = string.Empty;
    private string _fontSize = string.Empty;
    private string _fontFamily = string.Empty;
    private string _mosaicBlockSize = string.Empty;
    private string _highlighterAlpha = string.Empty;

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

    public bool HasErrors => _errors.Count > 0;

    // ================= General =================

    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }
    public bool NotifyOnQuickSave { get => _notifyOnQuickSave; set => Set(ref _notifyOnQuickSave, value); }
    public bool PlayCaptureSound { get => _playCaptureSound; set => Set(ref _playCaptureSound, value); }

    // ================= Capture =================

    public bool IncludeCursor { get => _includeCursor; set => Set(ref _includeCursor, value); }
    public bool AutoDetectWindows { get => _autoDetectWindows; set => Set(ref _autoDetectWindows, value); }
    public bool ShowMagnifier { get => _showMagnifier; set => Set(ref _showMagnifier, value); }
    public bool AbortOnFocusLoss { get => _abortOnFocusLoss; set => Set(ref _abortOnFocusLoss, value); }

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

    // ================= Export =================

    public string QuickSaveDirectoryOverride
    {
        get => _quickSaveDirectoryOverride;
        set { if (Set(ref _quickSaveDirectoryOverride, value)) ValidateOptionalDirectory(value, nameof(QuickSaveDirectoryOverride)); }
    }

    public bool CopyToClipboardOnQuickSave { get => _copyToClipboardOnQuickSave; set => Set(ref _copyToClipboardOnQuickSave, value); }

    public string FileNamePattern
    {
        get => _fileNamePattern;
        set { if (Set(ref _fileNamePattern, value)) ValidateFileNamePattern(value); }
    }

    // ================= Pin =================

    public bool CloseOnDoubleClick { get => _closeOnDoubleClick; set => Set(ref _closeOnDoubleClick, value); }
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

    public string UpscaleFactor
    {
        get => _upscaleFactor;
        set { if (Set(ref _upscaleFactor, value)) ValidateDouble(value, SettingsRanges.UpscaleFactor); }
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
        set { if (Set(ref _fontFamily, value)) ValidateNonEmpty(value, nameof(FontFamily), "글꼴 이름을 입력해 주세요."); }
    }

    public string MosaicBlockSize
    {
        get => _mosaicBlockSize;
        set { if (Set(ref _mosaicBlockSize, value)) ValidateInt(value, SettingsRanges.MosaicBlockSize); }
    }

    public string HighlighterAlpha
    {
        get => _highlighterAlpha;
        set { if (Set(ref _highlighterAlpha, value)) ValidateInt(value, SettingsRanges.HighlighterAlpha); }
    }

    // ================= Public operations =================

    /// <summary>
    /// Re-reads every field from <paramref name="settings"/> (deep-copied first) and
    /// re-runs validation. Used by both the constructor and a Cancel/reload.
    /// </summary>
    public void LoadFrom(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings s = settings.DeepClone();

        _launchAtLogin = s.General.LaunchAtLogin;
        _notifyOnQuickSave = s.General.NotifyOnQuickSave;
        _playCaptureSound = s.General.PlayCaptureSound;

        _includeCursor = s.Capture.IncludeCursor;
        _autoDetectWindows = s.Capture.AutoDetectWindows;
        _showMagnifier = s.Capture.ShowMagnifier;
        _abortOnFocusLoss = s.Capture.AbortOnFocusLoss;
        _delaySeconds = Int(s.Capture.DelaySeconds);
        _regionHistoryLimit = Int(s.Capture.RegionHistoryLimit);

        _recordingFrameRate = Int(s.Recording.TargetFps);
        _useRecordingStartDelay = s.Recording.UseStartDelay;
        _recordingStartDelaySeconds = Int(s.Recording.StartDelaySeconds);
        _recordingIncludeCursor = s.Recording.IncludeCursor;

        _captureHotkey = s.Hotkeys.Capture.ToString();
        _openLibraryHotkey = s.Hotkeys.OpenLibrary.ToString();
        _pasteToScreenHotkey = s.Hotkeys.PasteToScreen.ToString();
        _hideAllPinsHotkey = s.Hotkeys.HideAllPins.ToString();
        _toggleClickThroughHotkey = s.Hotkeys.ToggleClickThrough.ToString();
        _repeatLastRegionHotkey = s.Hotkeys.RepeatLastRegion.ToString();
        _captureWindowHotkey = s.Hotkeys.CaptureWindow.ToString();
        _captureFullScreenHotkey = s.Hotkeys.CaptureFullScreen.ToString();
        _recordRegionHotkey = s.Hotkeys.RecordRegion.ToString();

        _maxItems = Int(s.Queue.MaxItems);
        _maxGiB = Gib(s.Queue.MaxBytes);
        _thumbnailLongEdge = Int(s.Queue.ThumbnailLongEdge);
        _capturesDirectoryOverride = s.Queue.CapturesDirectoryOverride;

        _quickSaveDirectoryOverride = s.Export.QuickSaveDirectoryOverride;
        _copyToClipboardOnQuickSave = s.Export.CopyToClipboardOnQuickSave;
        _fileNamePattern = s.Export.FileNamePattern;

        _closeOnDoubleClick = s.Pin.CloseOnDoubleClick;
        _initialOpacity = Dbl(s.Pin.InitialOpacity);
        _zoomStep = Dbl(s.Pin.ZoomStep);
        _ctrlClickDebounceMs = Int(s.Pin.CtrlClickDebounceMs);
        _closedWindowRestoreLimit = Int(s.Pin.ClosedWindowRestoreLimit);

        _preferredLanguages = string.Join(", ", s.Ocr.PreferredLanguages);
        _upscaleFactor = Dbl(s.Ocr.UpscaleFactor);
        _cacheResults = s.Ocr.CacheResults;

        _strokeThickness = Dbl(s.Annotation.StrokeThickness);
        _fontSize = Dbl(s.Annotation.FontSize);
        _fontFamily = s.Annotation.FontFamily;
        _mosaicBlockSize = Int(s.Annotation.MosaicBlockSize);
        _highlighterAlpha = Int(s.Annotation.HighlighterAlpha);

        // Preserve members not surfaced in the UI so mapping back is lossless.
        _preservedSchemaVersion = s.SchemaVersion;
        _preservedStrokeColor = s.Annotation.StrokeColor;
        _preservedTextColor = s.Annotation.TextColor;
        _preservedRecentColors = [.. s.Annotation.RecentColors];
        _preservedColorFormat = s.Capture.ColorFormat;
        _preservedPreserveTransparency = s.Export.PreserveTransparency;
        _preservedLanguage = s.General.Language;
        _preservedIsFirstRun = s.General.IsFirstRun;
        _preservedRecordingBitrateBitsPerSecond = s.Recording.BitrateBitsPerSecond;
        _preservedRecordingCoarseStepSeconds = s.Recording.CoarseStepSeconds;

        RaiseAllChanged();
        ValidateAll();
    }

    /// <summary>
    /// Resets every editable field to the built-in defaults. Deliberate and fully
    /// reversible: nothing is written to disk until Apply, so a subsequent Cancel or a
    /// re-<see cref="LoadFrom"/> restores the previous values.
    /// </summary>
    public void ResetToDefaults() => LoadFrom(new AppSettings());

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
                Language = _preservedLanguage,
                IsFirstRun = _preservedIsFirstRun,
            },
            Capture =
            {
                IncludeCursor = _includeCursor,
                AutoDetectWindows = _autoDetectWindows,
                ShowMagnifier = _showMagnifier,
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
                BitrateBitsPerSecond = _preservedRecordingBitrateBitsPerSecond,
                CoarseStepSeconds = _preservedRecordingCoarseStepSeconds,
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
            },
            Queue =
            {
                MaxItems = ParseInt(_maxItems),
                MaxBytes = (long)Math.Round(ParseDouble(_maxGiB) * SettingsRanges.BytesPerGiB),
                ThumbnailLongEdge = ParseInt(_thumbnailLongEdge),
                CapturesDirectoryOverride = _capturesDirectoryOverride.Trim(),
            },
            Export =
            {
                QuickSaveDirectoryOverride = _quickSaveDirectoryOverride.Trim(),
                CopyToClipboardOnQuickSave = _copyToClipboardOnQuickSave,
                FileNamePattern = _fileNamePattern.Trim(),
                PreserveTransparency = _preservedPreserveTransparency,
            },
            Pin =
            {
                CloseOnDoubleClick = _closeOnDoubleClick,
                InitialOpacity = ParseDouble(_initialOpacity),
                ZoomStep = ParseDouble(_zoomStep),
                CtrlClickDebounceMs = ParseInt(_ctrlClickDebounceMs),
                ClosedWindowRestoreLimit = ParseInt(_closedWindowRestoreLimit),
            },
            Ocr =
            {
                PreferredLanguages = ParseLanguages(_preferredLanguages),
                UpscaleFactor = ParseDouble(_upscaleFactor),
                CacheResults = _cacheResults,
            },
            Annotation =
            {
                StrokeColor = _preservedStrokeColor,
                StrokeThickness = ParseDouble(_strokeThickness),
                TextColor = _preservedTextColor,
                FontSize = ParseDouble(_fontSize),
                FontFamily = _fontFamily.Trim(),
                MosaicBlockSize = ParseInt(_mosaicBlockSize),
                HighlighterAlpha = (byte)ParseInt(_highlighterAlpha),
                RecentColors = [.. _preservedRecentColors],
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
    };

    // ================= INotifyDataErrorInfo =================

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return _errors.Values.SelectMany(v => v).ToList();
        }

        return _errors.TryGetValue(propertyName, out List<string>? list)
            ? list
            : Array.Empty<string>();
    }

    /// <summary>Every current error message across all fields, for the summary announcement.</summary>
    public IReadOnlyList<string> AllErrors() => _errors
        .SelectMany(kv => kv.Value)
        .ToList();

    // ================= Validation =================

    private void ValidateAll()
    {
        ValidateInt(_delaySeconds, SettingsRanges.DelaySeconds, nameof(DelaySeconds));
        ValidateInt(_regionHistoryLimit, SettingsRanges.RegionHistoryLimit, nameof(RegionHistoryLimit));
        ValidateRecordingFrameRate(_recordingFrameRate);
        ValidateInt(
            _recordingStartDelaySeconds,
            SettingsRanges.RecordingStartDelaySeconds,
            nameof(RecordingStartDelaySeconds));
        ValidateInt(_maxItems, SettingsRanges.MaxItems, nameof(MaxItems));
        ValidateMaxGiB(_maxGiB);
        ValidateInt(_thumbnailLongEdge, SettingsRanges.ThumbnailLongEdge, nameof(ThumbnailLongEdge));
        ValidateOptionalDirectory(_capturesDirectoryOverride, nameof(CapturesDirectoryOverride));
        ValidateOptionalDirectory(_quickSaveDirectoryOverride, nameof(QuickSaveDirectoryOverride));
        ValidateFileNamePattern(_fileNamePattern);
        ValidateDouble(_initialOpacity, SettingsRanges.InitialOpacity, nameof(InitialOpacity));
        ValidateDouble(_zoomStep, SettingsRanges.ZoomStep, nameof(ZoomStep));
        ValidateInt(_ctrlClickDebounceMs, SettingsRanges.CtrlClickDebounceMs, nameof(CtrlClickDebounceMs));
        ValidateInt(_closedWindowRestoreLimit, SettingsRanges.ClosedWindowRestoreLimit, nameof(ClosedWindowRestoreLimit));
        ValidateLanguages(_preferredLanguages);
        ValidateDouble(_upscaleFactor, SettingsRanges.UpscaleFactor, nameof(UpscaleFactor));
        ValidateDouble(_strokeThickness, SettingsRanges.StrokeThickness, nameof(StrokeThickness));
        ValidateDouble(_fontSize, SettingsRanges.FontSize, nameof(FontSize));
        ValidateNonEmpty(_fontFamily, nameof(FontFamily), "글꼴 이름을 입력해 주세요.");
        ValidateInt(_mosaicBlockSize, SettingsRanges.MosaicBlockSize, nameof(MosaicBlockSize));
        ValidateInt(_highlighterAlpha, SettingsRanges.HighlighterAlpha, nameof(HighlighterAlpha));
        ValidateAllHotkeys();
    }

    private void ValidateInt(string value, Range<int> range, [CallerMemberName] string? property = null)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            SetError(property!, $"정수를 입력해 주세요 (허용 범위 {range.Describe()}).");
            return;
        }

        SetErrorState(property!, range.Contains(parsed), $"허용 범위는 {range.Describe()}입니다.");
    }

    private void ValidateRecordingFrameRate(string value)
    {
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            SetError(nameof(RecordingFrameRate), "프레임 속도는 10, 15, 24, 30, 60 중 하나를 입력해 주세요.");
            return;
        }

        SetErrorState(
            nameof(RecordingFrameRate),
            SettingsRanges.RecordingFrameRates.Contains(parsed),
            "지원하는 프레임 속도는 10, 15, 24, 30, 60fps입니다.");
    }

    private void ValidateDouble(string value, Range<double> range, [CallerMemberName] string? property = null)
    {
        if (!double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            SetError(property!, $"숫자를 입력해 주세요 (허용 범위 {range.Describe()}).");
            return;
        }

        SetErrorState(property!, range.Contains(parsed), $"허용 범위는 {range.Describe()}입니다.");
    }

    private void ValidateMaxGiB(string value)
    {
        if (!double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gib)
            || double.IsNaN(gib) || double.IsInfinity(gib))
        {
            SetError(nameof(MaxGiB), "저장 한도(GiB)에 숫자를 입력해 주세요.");
            return;
        }

        long bytes = (long)Math.Round(gib * SettingsRanges.BytesPerGiB);
        bool ok = SettingsRanges.MaxBytes.Contains(bytes);
        double minGiB = SettingsRanges.MaxBytes.Min / (double)SettingsRanges.BytesPerGiB;
        double maxGiB = SettingsRanges.MaxBytes.Max / (double)SettingsRanges.BytesPerGiB;
        SetErrorState(
            nameof(MaxGiB),
            ok,
            string.Format(CultureInfo.InvariantCulture, "허용 범위는 {0:0.###} ~ {1:0.###} GiB입니다.", minGiB, maxGiB));
    }

    private void ValidateFileNamePattern(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            SetError(nameof(FileNamePattern), "파일명 패턴을 입력해 주세요.");
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
            "파일명 패턴이 유효한 파일 이름을 만들지 못합니다.");
    }

    private void ValidateLanguages(string value)
    {
        List<string> tags = ParseLanguages(value);
        if (tags.Count == 0)
        {
            SetError(nameof(PreferredLanguages), "OCR 언어를 하나 이상 입력해 주세요 (예: ko-KR, en-US).");
            return;
        }

        foreach (string tag in tags)
        {
            if (!IsPlausibleBcp47(tag))
            {
                SetError(nameof(PreferredLanguages), $"'{tag}'은(는) 올바른 언어 태그가 아닙니다 (예: ko-KR, en-US).");
                return;
            }
        }

        ClearError(nameof(PreferredLanguages));
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

        SetErrorState(property, ok, "올바른 폴더 경로가 아닙니다.");
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
        ];

        var parsed = new Dictionary<string, Hotkey>(fields.Length);
        foreach ((string property, string raw) in fields)
        {
            if (!Hotkey.TryParse(raw, out Hotkey hotkey))
            {
                SetError(property, "올바른 단축키가 아닙니다 (예: Ctrl+Shift+C).");
                continue;
            }

            parsed[property] = hotkey;
            ClearError(property);
        }

        // Capture must remain assigned: an unassigned capture chord makes the app inert.
        if (parsed.TryGetValue(nameof(CaptureHotkey), out Hotkey? capture) && capture is not null && !capture.IsAssigned)
        {
            SetError(nameof(CaptureHotkey), "캡처 단축키는 비워 둘 수 없습니다.");
        }

        // Duplicate detection over assigned chords using semantic (value) equality.
        var seen = new Dictionary<Hotkey, string>();
        foreach ((string property, Hotkey hotkey) in parsed.Select(kv => (kv.Key, kv.Value)))
        {
            if (!hotkey.IsAssigned)
            {
                continue;
            }

            if (seen.ContainsKey(hotkey))
            {
                SetError(property, $"'{hotkey}' 단축키가 중복되었습니다.");
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
        RaisePropertyChanged(property);
        return true;
    }

    private void RaisePropertyChanged(string? property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void RaiseAllChanged() => RaisePropertyChanged(null);

    // ================= Preserved (non-UI) members =================

    private int _preservedSchemaVersion = 1;
    private Primitives.ColorRgba _preservedStrokeColor;
    private Primitives.ColorRgba _preservedTextColor;
    private List<Primitives.ColorRgba> _preservedRecentColors = [];
    private ColorFormat _preservedColorFormat;
    private bool _preservedPreserveTransparency;
    private string _preservedLanguage = string.Empty;
    private bool _preservedIsFirstRun;
    private int _preservedRecordingBitrateBitsPerSecond;
    private double _preservedRecordingCoarseStepSeconds;
}
