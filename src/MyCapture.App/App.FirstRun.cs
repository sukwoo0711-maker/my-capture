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

    /// <summary>
    /// After the welcome: a start-at-sign-in failure is reported first; otherwise the "ready"
    /// notification names the capture key, unless a capture is starting.
    /// </summary>
    private void AnnounceAfterWelcome(WelcomeOutcome outcome, StartAtSignInOutcome? startup)
    {
        if (_tray is null || _settings is null) return;
        try
        {
            if (startup?.Notice is { } notice)
            {
                _tray.ShowBalloon(UiText.Get("Onboard_StartAtSignInTitle"), notice, TrayBalloonKind.Warning, playSound: false);
                return;
            }
            if (!FirstRunPlan.AnnouncesReady(outcome)) return;
            _readyNotification.Notify(_hotkeys?.Failures.Count == 0, _settings.Hotkeys.Capture,
                (title, message) => _tray.ShowBalloon(title, message, TrayBalloonKind.Information, playSound: false));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not show the notification after the welcome");
        }
    }

    /// <summary>Applied settings changed the capture key: the "ready" notification names the new one.</summary>
    private void AnnounceCaptureKeyChange(Hotkey previous, Hotkey current)
    {
        if (_tray is null) return;
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
                _shellPresenter?.ShowActionNotification(UiText.Get("Text_5564316AA7EB"), message,
                    UiText.Get("Shell_ChangeShortcut"), () => HandleShortcutSettingsRequested(row), TrayBalloonKind.Error);
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
}
