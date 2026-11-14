using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Capture;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

internal sealed partial class RecordingControlWindow
{
    private readonly Button _audioButton = new();
    private readonly HashSet<string> _reportedAudioFailures = [];
    private readonly Queue<string> _audioMessages = [];
    private ContextMenu? _audioMenu;
    private bool _audioSessionStarted, _audioStartedWithSources;
    private bool _requestedSystem, _requestedMicrophone;
    private bool _audioCommandPending;
    private RecordingAudioDiagnostics? _audioSnapshot;
    private string? _audioMessage;
    private double _audioMessageWidth;
    private DateTimeOffset _audioMessageUntil;

    internal event Action<bool, bool>? AudioPreferencesChanged;
    internal event Action<string>? AudioFailureMessageShown;
    internal Button AudioButtonForTest => _audioButton;
    internal ContextMenu? AudioMenuForTest => _audioMenu;
    internal Func<DateTimeOffset> AudioUtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    private bool CanChangeAudio => !_stopping && !_completionPending && !_finished && !_windowClosed
        && (!_audioSessionStarted || _audioStartedWithSources);

    private void ConfigureAudioButton()
    {
        _requestedSystem = _settings.RecordSystemAudio;
        _requestedMicrophone = _settings.RecordMicrophone;
        _audioButton.Content = _audioStatusText;
        _audioButton.Style = TryStyle("Button.Ghost");
        _audioButton.Height = _audioButton.MinHeight = 36;
        _audioButton.MinWidth = 0;
        _audioButton.Padding = new Thickness(4, 0, 4, 0);
        _audioButton.Margin = new Thickness(0, 0, 4, 0);
        _audioButton.VerticalAlignment = VerticalAlignment.Center;
        _audioButton.Click += (_, _) => OpenAudioMenu();
        UpdateAudioLabel(_requestedSystem, _requestedMicrophone);
    }

    private void BeginAudioSession()
    {
        _audioSessionStarted = true;
        _audioStartedWithSources = _requestedSystem || _requestedMicrophone;
        _reportedAudioFailures.Clear();
        _audioMessages.Clear();
        _audioSnapshot = null;
        _audioMessage = null;
        _audioMessageWidth = 0;
    }

    /// <summary>Settings edits change the next recording, never a running session.</summary>
    internal void RefreshAudioPreferences(bool systemEnabled, bool microphoneEnabled)
    {
        if (_audioSessionStarted) return;
        _requestedSystem = _settings.RecordSystemAudio = systemEnabled;
        _requestedMicrophone = _settings.RecordMicrophone = microphoneEnabled;
        UpdateAudioLabel(systemEnabled, microphoneEnabled);
        RefreshAudioMenu();
        ApplyPhysicalLayout(positionHwnd: IsLoaded);
    }

    private bool ApplyAudioPreferences(bool systemEnabled, bool microphoneEnabled)
    {
        if (!CanChangeAudio) return false;
        AudioPreferencesChanged?.Invoke(systemEnabled, microphoneEnabled);
        _requestedSystem = _settings.RecordSystemAudio = systemEnabled;
        _requestedMicrophone = _settings.RecordMicrophone = microphoneEnabled;
        if (_audioSessionStarted)
        {
            _audioCommandPending = _recorder?.SetAudioSources(systemEnabled, microphoneEnabled) != true;
            RefreshAudioState();
        }
        else UpdateAudioLabel(systemEnabled, microphoneEnabled);
        RefreshAudioMenu();
        ApplyPhysicalLayout(positionHwnd: IsLoaded);
        return true;
    }

    private void RefreshAudioState()
    {
        if (!_audioSessionStarted || _stopping || _finished) return;
        if (_audioCommandPending && _recorder?.SetAudioSources(_requestedSystem, _requestedMicrophone) == true)
            _audioCommandPending = false;
        if (_recorder?.AudioDiagnostics is { } diagnostics) PresentAudioState(diagnostics);
        if (_audioMessage is not null && AudioUtcNow() >= _audioMessageUntil)
        {
            _audioMessage = null;
            _audioMessageWidth = 0;
            _statusText.Text = UiText.Get(_paused ? "RecordingPause_Status" : _readyAnnounced ? "Text_38B12BA58C48" : "Text_72A13C33A79E");
            if (_audioMessages.Count > 0) ShowNextAudioMessage();
            AnnounceStatus(UiText.Get(_paused ? "RecordingPause_StatusHelp" : "RecordingStatus_StopHelp"));
        }
    }

    /// <summary>The UI consumes the same immutable snapshot in production and fake-source tests.</summary>
    internal void PresentAudioState(RecordingAudioDiagnostics diagnostics)
    {
        if (diagnostics.EncoderState == AudioEncoderState.Unavailable)
        {
            // A video-only encoder has no source session whose flags can follow commands.
            // Only the presentation combines that capability fact with the latest choices.
            RecordingAudioSourceDiagnostics Requested(RecordingAudioSourceDiagnostics source, bool enabled) => source with
            { Status = source.Status with { Enabled = enabled, State = enabled ? AudioSourceState.Unavailable : AudioSourceState.Off } };
            diagnostics = diagnostics with
            {
                System = Requested(diagnostics.System, _requestedSystem),
                Microphone = Requested(diagnostics.Microphone, _requestedMicrophone),
            };
        }
        _audioSnapshot = diagnostics;
        bool Effective(RecordingAudioSourceDiagnostics source) => source.Status.Enabled
            && source.Status.State == AudioSourceState.Capturing;
        UpdateAudioLabel(Effective(diagnostics.System), Effective(diagnostics.Microphone));
        ReportAudioFailure(RecordingAudioSourceKind.System, diagnostics.System, diagnostics.EncoderState);
        ReportAudioFailure(RecordingAudioSourceKind.Microphone, diagnostics.Microphone, diagnostics.EncoderState);
        RefreshAudioMenu();
        ApplyPhysicalLayout(positionHwnd: IsLoaded);
    }

    private void UpdateAudioLabel(bool system, bool microphone)
    {
        string key = system ? microphone ? "RecordingAudio_Both" : "RecordingAudio_System"
            : microphone ? "RecordingAudio_Microphone" : "Recording_NoAudio";
        _audioStatusText.Text = UiText.Get(key);
        AutomationProperties.SetName(_audioStatusText, _audioStatusText.Text);
        AutomationProperties.SetName(_audioButton, _audioStatusText.Text);
        _audioButton.ToolTip = UiText.Get("RecordingAudio_ChooseSources");
        AutomationProperties.SetHelpText(_audioButton, (string)_audioButton.ToolTip);
        _audioButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    internal ContextMenu CreateAudioMenu()
    {
        ContextMenu menu = CaptureWindowExclusion.CreateContextMenu();
        menu.PlacementTarget = _audioButton;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 4;
        AutomationProperties.SetName(menu, UiText.Get("RecordingAudio_ChooseSources"));
        foreach (RecordingAudioSourceKind kind in Enum.GetValues<RecordingAudioSourceKind>())
        {
            var item = new MenuItem { Tag = kind, IsCheckable = true, MinHeight = 36 };
            item.Click += (_, _) =>
            {
                menu.IsOpen = false;
                ApplyAudioPreferences(kind == RecordingAudioSourceKind.System ? !_requestedSystem : _requestedSystem,
                    kind == RecordingAudioSourceKind.Microphone ? !_requestedMicrophone : _requestedMicrophone);
            };
            menu.Items.Add(item);
        }
        if (_audioSessionStarted && !_audioStartedWithSources)
            menu.Items.Add(new MenuItem { Header = UiText.Get("RecordingAudio_NextRecording"), IsEnabled = false, MinHeight = 36 });
        UpdateAudioMenu(menu);
        menu.Opened += (_, _) => CaptureWindowExclusion.ExcludeMenuPopup(menu);
        return menu;
    }

    private void OpenAudioMenu()
    {
        if (_audioMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateAudioMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_audioMenu, menu)) _audioMenu = null; };
        _audioMenu = menu;
        menu.IsOpen = true;
    }

    private void RefreshAudioMenu()
    {
        if (_audioMenu is { } menu) UpdateAudioMenu(menu);
    }

    private void UpdateAudioMenu(ContextMenu menu)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not RecordingAudioSourceKind kind) continue;
            bool system = kind == RecordingAudioSourceKind.System;
            RecordingAudioSourceDiagnostics? source = system ? _audioSnapshot?.System : _audioSnapshot?.Microphone;
            string label = UiText.Get(system ? "RecordingAudio_System" : "RecordingAudio_Microphone");
            bool unavailable = source?.Status is { Enabled: true, State: AudioSourceState.Unavailable }
                || source is { Failures: > 0, Status: { Enabled: true, State: AudioSourceState.Starting } };
            item.Header = unavailable ? UiText.Format("RecordingAudio_Unavailable", label) : label;
            item.IsChecked = system ? _requestedSystem : _requestedMicrophone;
            item.IsEnabled = CanChangeAudio;
            item.Icon = item.IsChecked ? MenuGlyph(CheckGlyph) : null;
            AutomationProperties.SetName(item, (string)item.Header);
        }
    }

    private void ReportAudioFailure(RecordingAudioSourceKind kind, RecordingAudioSourceDiagnostics source, AudioEncoderState encoder)
    {
        if (!source.Status.Enabled || source.Status.State != AudioSourceState.Unavailable) return;
        string failure = encoder == AudioEncoderState.Unavailable ? "encoder" : source.LastFailure ?? "unavailable";
        if (!_reportedAudioFailures.Add(failure == "encoder" ? failure : $"{kind}:{failure}")) return;
        string sourceLabel = UiText.Get(kind == RecordingAudioSourceKind.System ? "RecordingAudio_System" : "RecordingAudio_Microphone");
        string key = failure.Contains("80070005", StringComparison.OrdinalIgnoreCase) && kind == RecordingAudioSourceKind.Microphone
            ? "RecordingAudio_AccessDenied" : failure.Contains("80070490", StringComparison.OrdinalIgnoreCase)
                ? "RecordingAudio_NoDevice" : failure.Contains("88890004", StringComparison.OrdinalIgnoreCase)
                    ? "RecordingAudio_Disconnected" : failure.Contains("8889000A", StringComparison.OrdinalIgnoreCase)
                        ? "RecordingAudio_InUse" : failure.Contains("88890008", StringComparison.OrdinalIgnoreCase)
                            ? "RecordingAudio_FormatChanged" : "RecordingAudio_Failed";
        _audioMessages.Enqueue(UiText.Format(key, sourceLabel));
        if (_audioMessage is null) ShowNextAudioMessage();
    }

    private void ShowNextAudioMessage()
    {
        _audioMessage = _audioMessages.Dequeue();
        _audioMessageUntil = AudioUtcNow().AddSeconds(8);
        var measure = new TextBlock { Text = _audioMessage, FontFamily = _statusText.FontFamily, FontSize = _statusText.FontSize };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _audioMessageWidth = measure.DesiredSize.Width + _audioButton.DesiredSize.Width + 240;
        AnnounceStatus(_audioMessage);
        AudioFailureMessageShown?.Invoke(_audioMessage);
    }

    internal void PreviewAudioSessionForTest(bool startedWithAudio, RecordingAudioDiagnostics diagnostics)
    {
        _audioSessionStarted = true;
        _audioStartedWithSources = startedWithAudio;
        _requestedSystem = diagnostics.System.Status.Enabled;
        _requestedMicrophone = diagnostics.Microphone.Status.Enabled;
        PreviewRecordingForTest(false, TimeSpan.FromSeconds(12));
        PresentAudioState(diagnostics);
    }
}
