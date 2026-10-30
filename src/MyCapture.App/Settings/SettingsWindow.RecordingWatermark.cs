using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private DispatcherTimer? _watermarkLockTimer;
    internal string? WatermarkImageFolder { get; set; }
    internal Func<bool> RecordingWatermarkLocked { get; set; } = static () => false;
    internal Func<RecordingWatermarkSettings, RecordingWatermarkSettings?>? EditRecordingWatermarkSettings { get; set; }

    private void InitializeRecordingWatermark()
    {
        _watermarkLockTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
        _watermarkLockTimer.Tick += (_, _) => RefreshRecordingWatermarkAvailability();
        Activated += (_, _) => RefreshRecordingWatermarkAvailability();
        IsVisibleChanged += (_, _) =>
        {
            RefreshRecordingWatermarkAvailability();
            if (IsVisible) _watermarkLockTimer.Start(); else _watermarkLockTimer.Stop();
        };
        Closed += (_, _) => _watermarkLockTimer.Stop();
    }

    private void PreserveLockedWatermark()
    {
        if (!RecordingWatermarkLocked()) return;
        RefreshRecordingWatermark();
    }

    /// <summary>Accepts a recording-bar save without replacing other pending settings edits.</summary>
    internal void RefreshRecordingWatermark()
    {
        _draft.PropertyChanged -= OnDraftPropertyChanged;
        try
        {
            _draft.RecordingWatermark = (_currentSettings().Recording.Watermark ?? new()).DeepClone();
            _acceptedValues[nameof(SettingsDraft.RecordingWatermark)] = _draft.RecordingWatermark;
            _acceptedValues[nameof(SettingsDraft.RecordingWatermarkEnabled)] = _draft.RecordingWatermarkEnabled;
        }
        finally { _draft.PropertyChanged += OnDraftPropertyChanged; }
    }

    private void RefreshRecordingWatermarkAvailability() =>
        RecordingWatermarkSection.IsEnabled = !RecordingWatermarkLocked();

    private void OnRecordingWatermarkSettings(object sender, RoutedEventArgs e)
    {
        RefreshRecordingWatermarkAvailability();
        if (!RecordingWatermarkSection.IsEnabled) return;
        RecordingWatermarkSettings? result;
        if (EditRecordingWatermarkSettings is { } edit) result = edit(_draft.RecordingWatermark.DeepClone());
        else
        {
            var dialog = new RecordingWatermarkSettingsDialog(_draft.RecordingWatermark, WatermarkImageFolder,
                isLocked: RecordingWatermarkLocked) { Owner = this };
            result = dialog.ShowDialog() == true ? dialog.Result : null;
        }
        RefreshRecordingWatermarkAvailability();
        if (result is not null && !RecordingWatermarkLocked()) _draft.RecordingWatermark = result;
    }
}
