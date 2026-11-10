using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    /// <summary>Accept a recording-bar preference save without discarding other pending edits.</summary>
    internal void RefreshRecordingAudioPreferences()
    {
        _draft.PropertyChanged -= OnDraftPropertyChanged;
        try
        {
            var recording = _currentSettings().Recording;
            _draft.RecordSystemAudio = recording.RecordSystemAudio;
            _draft.RecordMicrophone = recording.RecordMicrophone;
            _acceptedValues[nameof(SettingsDraft.RecordSystemAudio)] = _draft.RecordSystemAudio;
            _acceptedValues[nameof(SettingsDraft.RecordMicrophone)] = _draft.RecordMicrophone;
        }
        finally { _draft.PropertyChanged += OnDraftPropertyChanged; }
    }
}
