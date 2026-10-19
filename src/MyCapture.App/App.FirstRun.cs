using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Shell glue for the first-run welcome's choices, the "ready" notification and the ways into
/// Settings → Shortcuts from a tutorial or a shortcut conflict. The decisions live in
/// <see cref="FirstRunPlan"/>, <see cref="ResidentReadyNotification"/> and
/// <see cref="ShortcutSettingTarget"/>.
/// </summary>
public partial class App
{
    /// <summary>
    /// Applies the welcome's start-at-sign-in choice and records what Windows really does in the
    /// settings the welcome is about to save, so Settings shows the same state.
    /// </summary>
    private StartAtSignInOutcome? ApplyWelcomeStartAtSignIn(bool requested)
    {
        if (_settings is null || _services is null) return null;
        try
        {
            IStartupRegistration registration = _startupService ?? _services.GetRequiredService<IStartupRegistration>();
            StartAtSignInOutcome outcome = FirstRunPlan.ApplyStartAtSignIn(requested, registration);
            _settings.General.LaunchAtLogin = outcome.Enabled;
            _log?.LogInformation("Welcome start-at-sign-in: requested {Requested}, enabled {Enabled}", requested, outcome.Enabled);
            return outcome;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not apply the welcome's start-at-sign-in choice");
            return new StartAtSignInOutcome(false, UiText.Format("Text_0FA1A9D03BE3", ex.Message));
        }
    }

    private bool _residentDismissalSubscribed;
    private bool _welcomeConflictPending;
    private string? _welcomeStartupNotice;

    /// <summary>Keep shortcut problems first and defer the resident explanation until capture work ends.</summary>
    private void AnnounceAfterWelcome(WelcomeOutcome outcome, StartAtSignInOutcome? startup)
    {
        _readyNotification.RequestResident(outcome);
        _welcomeStartupNotice = startup?.Notice;
        _welcomeConflictPending = _hotkeys?.Failures.Count > 0;
        if (!_residentDismissalSubscribed && _shellPresenter is not null)
        {
            _shellPresenter.NotificationDismissed += QueueWelcomeResidentAnnouncement;
            _residentDismissalSubscribed = true;
        }
        RetryWelcomeResidentAnnouncement();
    }

    /// <summary>Called after capture success or cancellation, never merely when a selector is replaced.</summary>
    private void CompleteWelcomeCaptureAnnouncement()
    {
        if (_readyNotification.CompleteCapture()) _welcomeConflictPending |= _hotkeys?.Failures.Count > 0;
        QueueWelcomeResidentAnnouncement();
    }

    private void QueueWelcomeResidentAnnouncement()
    {
        if (!_readyNotification.HasPendingResident || Dispatcher.HasShutdownStarted) return;
        // Dismiss also runs immediately before a replacement notice is presented. Inspect the
        // new state after that presentation, so the resident notice cannot displace it.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(RetryWelcomeResidentAnnouncement));
    }

    private bool IsWelcomeCaptureInProgress => _preparingAdvancedCapture || _overlay?.IsSelecting == true
        || _activeCountdown is not null || _scrollCancellation is not null || !_capturePersistenceTask.IsCompleted
        || _recorder?.IsActive == true;

    /// <summary>Also retried after hotkey registration, even when the capture chord itself is unchanged.</summary>
    private void RetryWelcomeResidentAnnouncement()
    {
        if (!_readyNotification.HasPendingResident || _shellPresenter is null || _settings is null || _preparingExit) return;
        bool capturing = IsWelcomeCaptureInProgress;
        if (_readyNotification.WaitingForCapture || capturing) return;
        try
        {
            if (_hotkeys?.Failures.Count > 0)
            {
                if (_welcomeConflictPending)
                {
                    _welcomeConflictPending = false;
                    HotkeyRegistrationFailure first = _hotkeys.Failures[0];
                    string suffix = _hotkeys.Failures.Count == 1 ? string.Empty
                        : UiText.Format("Text_646F2DBF1852", _hotkeys.Failures.Count - 1);
                    string notice = _welcomeStartupNotice is { } startupNotice ? "\n" + startupNotice : string.Empty;
                    _welcomeStartupNotice = null;
                    ShowHotkeyConflict(first, UiText.Format("Text_114E1105C3EE", first.Hotkey, suffix)
                        + PrintScreenFailureLine(_hotkeys.Failures) + notice);
                }
                return;
            }
            _welcomeConflictPending = false;
            if (_hotkeys is null || _shellPresenter.HasNotification) return;
            if (_welcomeStartupNotice is { } startup)
            {
                _shellPresenter.ShowNotification(UiText.Get("Onboard_StartAtSignInTitle"), startup,
                    TrayBalloonKind.Warning, answersCommand: true);
                _welcomeStartupNotice = null;
                return;
            }
            _readyNotification.TryNotifyResident(hotkeysReady: true, captureInProgress: false, notificationVisible: false,
                (title, message) => _shellPresenter.ShowNotification(title, message, TrayBalloonKind.Information, answersCommand: true));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not show the notification after the welcome");
        }
    }

    /// <summary>Applied settings changed the capture key: the "ready" notification names the new one.</summary>
    private void AnnounceCaptureKeyChange(Hotkey previous, Hotkey current)
    {
        if (_tray is null || _hotkeys?.Failures.Count != 0) return;
        if (_readyNotification.HasPendingResident)
        {
            QueueWelcomeResidentAnnouncement();
            return;
        }
        try
        {
            _ = _readyNotification.NotifyCaptureKeyChanged(previous, current,
                (title, message) => _tray.ShowBalloon(title, message, TrayBalloonKind.Information, playSound: false));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not announce the changed capture shortcut");
        }
    }

    /// <summary>
    /// A shortcut that could not be registered: the notification stays until closed or acted on,
    /// and "Change shortcut" opens Settings → Shortcuts on the first failed row.
    /// </summary>
    private void ShowHotkeyConflict(HotkeyRegistrationFailure first, string message)
    {
        string row = ShortcutSettingTarget.ForCommand(first.Command);
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_hotkeys?.Failures.Count == 0)
                {
                    QueueWelcomeResidentAnnouncement();
                    return;
                }
                if (IsWelcomeCaptureInProgress)
                {
                    _welcomeConflictPending |= _readyNotification.HasPendingResident;
                    return;
                }
                _shellPresenter?.ShowActionNotification(UiText.Get("Text_5564316AA7EB"), message,
                    UiText.Get("Shell_ChangeShortcut"), () => HandleShortcutSettingsRequested(row), TrayBalloonKind.Error,
                    answersCommand: true);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not show the shortcut conflict notification");
            }
        }));
    }

    /// <summary>Settings → Shortcuts with keyboard focus on one row.</summary>
    private void HandleShortcutSettingsRequested(string draftProperty)
    {
        if (_preparingExit || _storeInstallationPending)
        {
            HandleSettingsRequested();
            return;
        }
        _log?.LogInformation("Shortcut settings requested for {Row}", draftProperty);
        EnsureSettingsWindow()?.ShowShortcut(draftProperty);
    }
    private void SaveFirstUseTips()
    {
        try { if (_settings is not null) _services?.GetRequiredService<SettingsStore>().Save(_settings); }
        catch (Exception ex) { _log?.LogWarning(ex, "Could not save first-use tip state"); }
    }

    private void ShowCaptureToast(Core.Queue.CaptureRecord record, bool copied)
    {
        if (_settings is null || _shellPresenter is null || _hotkeys?.Failures.Count > 0) return;
        bool first = !_settings.General.FirstCaptureTipShown;
        if (!first && _settings.General.AfterCapture != AfterCaptureAction.ClipboardOnly) return;
        bool shown = _shellPresenter.ShowCaptureReceipt(UiText.Get(copied ? "Capture.CopiedAndSaved" : "Capture.Saved"),
            () => EnsureGalleryWindow()?.OpenCapture(record.Id, edit: false),
            first ? UiText.Get("Onboard_FirstCaptureToastTip") : null,
            first ? async () => await PinCaptureFromTipAsync(record) : null, answersCommand: true);
        if (shown && FirstUseTips.ShowCaptureOnce(_settings.General, () => { })) SaveFirstUseTips();
    }

    private async Task PinCaptureFromTipAsync(Core.Queue.CaptureRecord record)
    {
        if (_queue is null || _pins is null) return;
        try
        {
            using IDisposable lease = _queue.AcquireEvictionLease(record.Id);
            var rendered = ReferenceEquals(_currentRecord, record) && _overlay?.CurrentEditor is { } editor
                ? await editor.RenderCurrentImageAsync()
                : await Task.Run(() => MyCapture.Platform.Imaging.ImageCodec.TryLoad(_queue.GetFilePath(record, Core.Queue.CaptureFileNames.Rendered))
                    ?? throw new IOException("The rendered capture is unavailable."));
            _pins.PinImage(rendered);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not pin the capture from its first-use tip");
            _shellPresenter?.ShowNotification(UiText.Get("QuickAccess.Pin"), UiText.Get("Onboard_PinFailed"), TrayBalloonKind.Error, answersCommand: true);
        }
    }

    private void ShowFirstPinTip()
    {
        if (_settings is null || _shellPresenter is null || _hotkeys?.Failures.Count > 0 || _settings.General.FirstPinTipShown) return;
        Hotkey hotkey = _settings.Hotkeys.HideAllPins;
        string text = hotkey.IsAssigned ? UiText.Format("Onboard_FirstPinTip", hotkey) : UiText.Get("Onboard_FirstPinTipUnassigned");
        if (_shellPresenter.ShowCaptureReceipt(text, () => HandleShortcutSettingsRequested(nameof(SettingsDraft.HideAllPinsHotkey)),
                answersCommand: true, actionTooltip: UiText.Get("Help.Settings"))
            && FirstUseTips.ShowPinOnce(_settings.General, () => { })) SaveFirstUseTips();
    }

}
