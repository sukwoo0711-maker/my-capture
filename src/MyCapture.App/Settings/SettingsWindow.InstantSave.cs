using MyCapture.App.Themes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Threading;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private static readonly PropertyInfo[] EditableProperties = typeof(SettingsDraft).GetProperties()
        .Where(property => property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0).ToArray();
    private static readonly TimeSpan TextSaveDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SavedNoticeDelay = TimeSpan.FromSeconds(2);
    private readonly HashSet<string> _pendingText = [];
    private IDisposable? _textSave;
    private IDisposable? _savedNotice;
    private Dictionary<string, object?> _acceptedValues = [];
    private string _restartNotice = string.Empty;
    private bool _saveQueued;
    private bool _saveAllQueued;
    private bool _saving;
    private bool _saveFailed;

    /// <summary>
    /// Runs the typing save or the saved-notice timeout once after its delay; disposing the handle
    /// cancels it. Tests replace it with their own clock, so a stalled runner cannot expire a delay
    /// between two steps.
    /// </summary>
    internal Func<TimeSpan, Action, IDisposable> ScheduleDelay { get; set; } = DispatcherDelay.Schedule;

    private void InitializeInstantSave() =>
        _acceptedValues = EditableProperties.ToDictionary(property => property.Name, property => property.GetValue(_draft));

    /// <summary>A result-window choice updates this one field without discarding pending text edits.</summary>
    internal void RefreshOcrQuality()
    {
        _draft.PropertyChanged -= OnDraftPropertyChanged;
        try
        {
            _draft.OcrQuality = OcrQualityNames.ToSetting(_currentSettings().Ocr.Quality);
            _acceptedValues[nameof(SettingsDraft.OcrQuality)] = _draft.OcrQuality;
        }
        finally { _draft.PropertyChanged += OnDraftPropertyChanged; }
    }

    private void ScheduleSave(string? propertyName)
    {
        PropertyInfo? property = EditableProperties.FirstOrDefault(item => item.Name == propertyName);
        if (propertyName is not null && property is null) return;
        if (_saving)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ScheduleSave(propertyName)));
            return;
        }
        CancelSavedNotice();
        if (!_saveFailed) SetStatusMessage(_restartNotice);
        // Setters raise PropertyChanged before their validation and related fields finish.
        // Even toggles and choices must therefore wait until the dispatcher regains control.
        bool isText = property?.PropertyType == typeof(string) && propertyName is not
            (nameof(SettingsDraft.Theme) or nameof(SettingsDraft.AccentColor) or nameof(SettingsDraft.Language)
            or nameof(SettingsDraft.OcrQuality) or nameof(SettingsDraft.RecordingCaptureMode)
            or nameof(SettingsDraft.CaptureGuideColorMode));
        if (isText)
        {
            _pendingText.Add(propertyName!);
            // Each keystroke restarts the pause.
            CancelTextSave();
            _textSave = ScheduleDelay(TextSaveDelay, () =>
            {
                _textSave = null;
                SavePending(includeText: true);
            });
            return;
        }
        _saveAllQueued |= propertyName is null;
        if (_saveQueued) return;
        _saveQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_saveQueued) return;
            bool all = _saveAllQueued;
            _saveQueued = _saveAllQueued = false;
            SavePending(includeText: all);
        }));
    }

    /// <summary>Ctrl+S and every close path flush pending typing without rolling back saved changes.</summary>
    internal bool FlushPendingSettings()
    {
        CancelTextSave();
        _saveQueued = _saveAllQueued = false;
        return SavePending(includeText: true);
    }

    private bool SavePending(bool includeText, bool revalidateCandidateErrors = true)
    {
        if (_saving) return false;
        PreserveLockedWatermark();
        if (revalidateCandidateErrors) _draft.ClearCandidateErrors();
        if (includeText) _pendingText.Clear();
        bool Changed(PropertyInfo property) => !Equals(property.GetValue(_draft), _acceptedValues[property.Name]);
        if (!EditableProperties.Any(Changed) && !_draft.ExportOptionsReplaced)
        {
            if (_saveFailed) { _saveFailed = false; SetStatusMessage(_restartNotice); }
            return !_draft.HasErrors;
        }

        var candidate = new SettingsDraft(_currentSettings());
        var appliedProperties = new List<PropertyInfo>();
        bool replacingAll = _draft.ExportOptionsReplaced && !_draft.HasErrors && _pendingText.Count == 0;
        if (replacingAll)
        {
            candidate.ReplaceValues(_draft.ToAppSettings());
            appliedProperties.AddRange(EditableProperties);
        }
        else
        {
            var required = new HashSet<string>();
            void IncludeDependencies(string mode, bool active, params string[] dependencies)
            {
                if (!active || !Changed(EditableProperties.Single(property => property.Name == mode))
                    || _draft.GetErrors(mode).Cast<string>().Any()) return;
                string? error = dependencies.SelectMany(name => _draft.GetErrors(name).Cast<string>()).FirstOrDefault();
                if (error is not null)
                {
                    _draft.SetCandidateError(mode, error);
                    if (mode == nameof(SettingsDraft.UseRecordingFixedSize))
                        _draft.SetCandidateError(nameof(SettingsDraft.UseRecordingDraggedSize), error);
                    return;
                }
                required.UnionWith(dependencies);
            }
            IncludeDependencies(nameof(SettingsDraft.UseRecordingFixedSize), _draft.UseRecordingFixedSize,
                nameof(SettingsDraft.RecordingPresetWidth), nameof(SettingsDraft.RecordingPresetHeight));
            IncludeDependencies(nameof(SettingsDraft.CaptureGuideColorMode), _draft.IsCustomCaptureGuideColor,
                nameof(SettingsDraft.CaptureGuideColorHex));
            // Save independent valid edits while leaving invalid text and its inline error intact.
            // Discover writable properties so a future boolean setting needs no save-model list.
            foreach (PropertyInfo property in EditableProperties)
            {
                if (_pendingText.Contains(property.Name) && !required.Contains(property.Name)
                    || _draft.GetErrors(property.Name).Cast<string>().Any()
                    || !Changed(property) && !required.Contains(property.Name)) continue;
                property.SetValue(candidate, property.GetValue(_draft));
                appliedProperties.Add(property);
            }
        }
        if (candidate.HasErrors)
        {
            bool rejected = false;
            foreach (PropertyInfo property in appliedProperties)
                if (candidate.GetErrors(property.Name).Cast<string>().FirstOrDefault() is { } error)
                {
                    _draft.SetCandidateError(property.Name, error);
                    rejected = true;
                }
            RefreshErrorSummary();
            // Rebuild once the rejected fields are excluded, so an unrelated valid toggle
            // still persists even when merging a hotkey with its saved partner conflicts.
            if (rejected) return SavePending(includeText: false, revalidateCandidateErrors: false);
            return false;
        }
        if (!candidate.HasUnappliedChanges)
        {
            // The failed edit may have been replaced by invalid text. There is no valid
            // pending write left to retry, so an old write failure must not trap Close.
            if (_saveFailed) { _saveFailed = false; SetStatusMessage(_restartNotice); }
            foreach (PropertyInfo property in appliedProperties)
                _acceptedValues[property.Name] = property.GetValue(_draft);
            _draft.AcceptSaved(_currentSettings(), acceptReplacement: replacingAll);
            RefreshErrorSummary();
            return !_draft.HasErrors && !_saveFailed;
        }

        _saving = true;
        try
        {
            AppSettings next = LegacyApplyCandidate(candidate.ToAppSettings());
            // GiB is a rounded display value. An unrelated instant save must preserve the
            // exact byte budget, including a value changed by the library while settings is open.
            if (!replacingAll && !appliedProperties.Any(property => property.Name == nameof(SettingsDraft.MaxGiB)))
                next.Queue.MaxBytes = _currentSettings().Queue.MaxBytes;
            var attemptedValues = appliedProperties.ToDictionary(property => property.Name, property => property.GetValue(_draft));
            // The apply service can roll back an entire settings group, including future boolean
            // members. Capture its requested projection before it mutates the candidate.
            var requested = new SettingsDraft(next);
            // A reset/import explicitly replaces the folder option, including partial saves while other fields are invalid.
            SettingsApplyResult result = _applyDraft?.Invoke(next, _draft.ExportOptionsReplaced || _draft.FolderDestinationEdited, replacingAll) ?? _apply(next);
            _saveFailed = !result.Saved;
            if (!result.Saved)
            {
                string message = string.Join(Environment.NewLine, result.Messages);
                SetStatusMessage(message);
                _showSaveFailureMessage(message);
                return false;
            }

            string notice = string.Join(Environment.NewLine, result.Messages);
            var persisted = new SettingsDraft(_currentSettings());
            IReadOnlySet<string> rejectedEdits = SettingsApplyAcceptance.RejectedEdits(result,
                appliedProperties.ToDictionary(property => property.Name, property => property.GetValue(requested)),
                appliedProperties.ToDictionary(property => property.Name, property => property.GetValue(persisted)));
            foreach (PropertyInfo property in appliedProperties)
                if (rejectedEdits.Contains(property.Name))
                    _draft.SetSaveError(property.Name, notice.Length > 0 ? notice : UiText.Get("Text_ABAB2010D671"));
                else
                    _acceptedValues[property.Name] = attemptedValues[property.Name];
            _draft.AcceptSaved(_currentSettings(), acceptReplacement: replacingAll);
            Applied?.Invoke(this, result);
            if (result.RestartRequired) _restartNotice = notice;
            SetStatusMessage(notice.Length > 0 ? notice : _restartNotice.Length > 0 ? _restartNotice
                : _draft.HasErrors ? string.Empty : UiText.Get("Settings_Saved"));
            if (notice.Length == 0 && _restartNotice.Length == 0 && !_draft.HasErrors)
                _savedNotice ??= ScheduleDelay(SavedNoticeDelay, () =>
                {
                    _savedNotice = null;
                    // A later import or storage failure owns the status line now.
                    if (KoreanWordWrap.Strip(SettingsStatus.Text) == UiText.Get("Settings_Saved")) SetStatusMessage(string.Empty);
                });
            RefreshErrorSummary();
            return !_draft.HasErrors;
        }
        finally { _saving = false; }
    }

    private void StopInstantSave()
    {
        _saveQueued = _saveAllQueued = false;
        CancelTextSave();
        CancelSavedNotice();
        _pendingText.Clear();
    }

    private void CancelTextSave()
    {
        _textSave?.Dispose();
        _textSave = null;
    }

    private void CancelSavedNotice()
    {
        _savedNotice?.Dispose();
        _savedNotice = null;
    }
}

/// <summary>Compares the actual saved projection after group rollback, without naming UI properties.</summary>
internal static class SettingsApplyAcceptance
{
    internal static IReadOnlySet<string> RejectedEdits(SettingsApplyResult result,
        IReadOnlyDictionary<string, object?> requested, IReadOnlyDictionary<string, object?> persisted) =>
        requested.Where(edit => !result.Saved || !persisted.TryGetValue(edit.Key, out object? actual)
            || !Equals(edit.Value, actual)).Select(edit => edit.Key).ToHashSet(StringComparer.Ordinal);
}
