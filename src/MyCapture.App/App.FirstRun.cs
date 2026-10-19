using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
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
    private ThemedDialog? _coreShortcutsQuestion;
    private bool _coreShortcutsQuestionPending;
    private SettingsApplyResult? _coreShortcutsApplyFailure;

    /// <summary>The one-time choice uses the shared dialog, with overwrite as its default action.</summary>
    internal static ThemedDialog CreateCoreShortcutsQuestion() => new(
        UiText.Get("CoreShortcuts.Body"), UiText.Get("CoreShortcuts.Title"),
        MessageBoxButton.OKCancel, MessageBoxResult.OK,
        okLabel: UiText.Get("CoreShortcuts.Overwrite"), cancelLabel: UiText.Get("CoreShortcuts.KeepCurrent"));

    private void ShowLaunchPresentation(bool quietLaunch, bool openSettings, bool answeredThisLaunch = false)
    {
        if (_settings is null || _preparingExit) return;
        LaunchPresentation launch = FirstRunPlan.ForLaunch(_launchedFirstRun, quietLaunch, openSettings,
            answeredThisLaunch || _settings.Hotkeys.CoreShortcutsAsked);
        if (launch.ShowCoreShortcuts)
        {
            _coreShortcutsQuestionPending = true;
            ThemedDialog question = CreateCoreShortcutsQuestion();
            _coreShortcutsQuestion = question;
            question.Closed += (_, _) =>
            {
                _coreShortcutsQuestion = null;
                if (_shutdownRequested)
                {
                    // The exit closed the question, not the user: nothing is recorded, so the next launch asks again.
                    _coreShortcutsQuestionPending = false;
                    _log?.LogInformation("Core shortcut question closed by exit without an answer");
                    return;
                }
                try
                {
                    AppSettings next = _settings.DeepClone();
                    if (question.Result == MessageBoxResult.OK) CoreShortcuts.Accept(next.Hotkeys);
                    else CoreShortcuts.Decline(next.Hotkeys);
                    // The welcome still owns the first start-at-sign-in choice. A chord another
                    // program holds must not undo the answer: the rest of the set takes effect
                    // and the conflict notification names what is left.
                    SettingsApplyResult result = _settingsApply!.Apply(next, applyStartup: !_launchedFirstRun,
                        keepAvailableHotkeys: true);
                    if (!result.Saved || !result.HotkeysApplied || result.Messages.Count > 0)
                    {
                        _coreShortcutsApplyFailure = result;
                        _log?.LogWarning("Core shortcut choice could not be fully applied: saved {Saved}, hotkeys {HotkeysApplied}; {Messages}",
                            result.Saved, result.HotkeysApplied, string.Join("; ", result.Messages));
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Could not apply the core shortcut choice");
                    _coreShortcutsApplyFailure = SettingsApplyResult.NotSaved([ThemedDialog.RecoveryMessage(ex)]);
                }
                finally
                {
                    _coreShortcutsQuestionPending = false;
                }
                ShowLaunchPresentation(quietLaunch, openSettings, answeredThisLaunch: true);
            };
            ThemedDialog.ShowOwnerless(question);
            return;
        }

        if (launch.ShowWelcome) ShowFirstRunWelcome();
        else ShowStartupHotkeyNotification();
        // --settings opens Settings on every launch, beside a first-run welcome too.
        if (openSettings) _ = Dispatcher.BeginInvoke(new Action(HandleSettingsRequested));
        else if (launch.OpenLibrary) _ = Dispatcher.BeginInvoke(new Action(HandleGalleryRequested));
    }

    private void ShowFirstRunWelcome()
    {
        var welcome = new WelcomeWindow(_settings!.Hotkeys, _settings.General.AfterCapture, _settings.Queue.ImageRetentionHours);
        welcome.Closed += (_, _) =>
        {
            StartAtSignInOutcome? startup = ApplyWelcomeStartAtSignIn(welcome.StartAtSignIn);
            try { _services?.GetRequiredService<SettingsStore>().CompleteFirstRun(_settings!); }
            catch (Exception ex) { _log?.LogWarning(ex, "Could not persist first-run completion"); }
            // One notification shows at a time, so a shortcut problem carries the
            // start-at-sign-in notice with it; the "ready" notification waits for working shortcuts.
            if (_hotkeys?.Failures.Count > 0 || _coreShortcutsApplyFailure is not null) ShowStartupHotkeyNotification(startup?.Notice);
            else AnnounceAfterWelcome(welcome.Outcome, startup);
            switch (FirstRunPlan.Next(welcome.Outcome))
            {
                case AfterWelcome.OpenLibrary:
                    _ = Dispatcher.BeginInvoke(new Action(HandleGalleryRequested));
                    break;
                case AfterWelcome.StartCapture:
                    // The welcome has just left the screen; the frozen frame must not show it.
                    _overlay?.NoteForegroundDismissal();
                    _ = Dispatcher.BeginInvoke(new Action(HandleCaptureRequested));
                    break;
                case AfterWelcome.OpenSettings:
                    _ = Dispatcher.BeginInvoke(new Action(HandleSettingsRequested));
                    break;
            }
        };
        welcome.Show();
    }

    /// <param name="notice">Another problem to report in the same notification, such as a failed start-at-sign-in choice.</param>
    private void ShowStartupHotkeyNotification(string? notice = null)
    {
        if (_hotkeys is null || _tray is null || _services is null || _coreShortcutsQuestionPending) return;
        SettingsApplyResult? applyFailure = _coreShortcutsApplyFailure;
        _coreShortcutsApplyFailure = null;
        IReadOnlyList<HotkeyRegistrationFailure> failures = _hotkeys.Failures.Count > 0
            ? _hotkeys.Failures : applyFailure?.HotkeyFailures ?? [];
        // What the answer could not apply (an unsaved file, startup) follows the conflict.
        string[] otherProblems = [.. (applyFailure?.Messages ?? Array.Empty<string>()).Append(notice ?? string.Empty)
            .Where(line => line.Length > 0)];
        if (failures.Count > 0)
        {
            _tray.SetState(TrayIconState.Error);

            HotkeyRegistrationFailure first = failures[0];
            string suffix = failures.Count == 1
                ? string.Empty
                : UiText.Format("Text_646F2DBF1852", failures.Count - 1);

            ShowHotkeyConflict(first,
                UiText.Format("Text_114E1105C3EE", first.Hotkey, suffix)
                    + PrintScreenFailureLine(failures)
                    + (_services.GetRequiredService<SettingsStore>().LastLoadMigratedUnsafeHotkeys ? "\n" + UiText.Get("Hotkey.Migrated") : string.Empty)
                    + string.Concat(otherProblems.Select(line => "\n" + line)));
        }
        else if (otherProblems.Length > 0)
        {
            ShowShortcutConflictNotice(GlobalHotkeyCommand.CaptureRegion, string.Join("\n", otherProblems));
        }
        else
        {
            try
            {
                if (_services.GetRequiredService<SettingsStore>().LastLoadMigratedUnsafeHotkeys)
                    _tray.ShowBalloon(UiText.Get("Hotkey.MigratedTitle"), UiText.Get("Hotkey.Migrated"),
                        TrayBalloonKind.Information, playSound: false);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not show the shortcut migration notification");
            }
        }
    }

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
        if (_tray is null || _coreShortcutsQuestionPending) return;
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
        => ShowShortcutConflictNotice(first.Command, message);

    private void ShowShortcutConflictNotice(GlobalHotkeyCommand command, string message)
    {
        string row = ShortcutSettingTarget.ForCommand(command);
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
