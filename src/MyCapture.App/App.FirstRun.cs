using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
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
                    SettingsApplyResult result = _settingsApply!.ApplyCoreShortcutAnswer(next, applyStartup: !_launchedFirstRun);
                    // Chords still held elsewhere are in the hotkey failures and reported with the
                    // launch's conflict notification; anything else (an unsaved file) is said now.
                    if (!result.Saved || !result.HotkeysApplied || result.Messages.Count > 0)
                    {
                        _log?.LogWarning("Core shortcut choice could not be fully applied: saved {Saved}, hotkeys {HotkeysApplied}; {Messages}",
                            result.Saved, result.HotkeysApplied, string.Join("; ", result.Messages));
                        ShowCoreShortcutsProblem(string.Join("\n", result.Messages));
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "Could not apply the core shortcut choice");
                    ShowCoreShortcutsProblem(ThemedDialog.RecoveryMessage(ex));
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

        if (launch.ShowWelcome)
        {
            // The conflict itself is announced after the welcome; the tray says so meanwhile.
            if (_hotkeys?.Failures.Count > 0) _tray?.SetState(TrayIconState.Error);
            ShowFirstRunWelcome();
        }
        else ShowStartupHotkeyNotification();
        // --settings opens Settings on every launch, beside a first-run welcome too.
        if (openSettings) _ = Dispatcher.BeginInvoke(new Action(HandleSettingsRequested));
        else if (launch.OpenLibrary) _ = Dispatcher.BeginInvoke(new Action(HandleGalleryRequested));
    }

    private void ShowFirstRunWelcome()
    {
        var welcome = CreateOnboardingWindow(replay: false);
        welcome.Closed += (_, _) =>
        {
            StartAtSignInOutcome? startup = ApplyWelcomeStartAtSignIn(welcome.StartAtSignIn);
            try { CompleteWelcomeOcrChoice(welcome.UseAccurateOcr); }
            catch (Exception ex) { _log?.LogWarning(ex, "Could not persist first-run completion"); }
            AnnounceAfterWelcome(welcome.Outcome, startup);
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
        WindowForeground.BringForward(welcome);
    }

    /// <summary>
    /// Records the welcome's OCR checkbox and completes the first run. The app's exit also
    /// closes the welcome: then, like the core-shortcut question, the unanswered checkbox is not
    /// applied and no model download starts, while the first run still completes as before.
    /// </summary>
    private void CompleteWelcomeOcrChoice(bool selected)
    {
        bool closedByExit = _shutdownRequested;
        FirstRunOcrChoice.Complete(_settings!, selected,
            settings => _services!.GetRequiredService<SettingsStore>().CompleteFirstRun(settings),
            () => StartOcrModelDownload(isBackground: false, deferForBatterySaver: true),
            shutdownRequested: closedByExit);
        if (closedByExit)
            _log?.LogInformation("Welcome closed by exit; accurate OCR choice not recorded, quality {Quality}",
                _settings!.Ocr.Quality);
        else
            _log?.LogInformation("Welcome accurate OCR: selected {Selected}, quality {Quality}",
                selected, _settings!.Ocr.Quality);
    }

    /// <summary>
    /// A launch without the welcome reports the shortcuts Windows did not register, once the
    /// core-shortcut question (if any) has been answered.
    /// </summary>
    private void ShowStartupHotkeyNotification()
    {
        if (_hotkeys is null || _tray is null || _services is null || _coreShortcutsQuestionPending) return;
        if (_hotkeys.Failures.Count > 0)
        {
            _tray.SetState(TrayIconState.Error);

            HotkeyRegistrationFailure first = _hotkeys.Failures[0];
            string suffix = _hotkeys.Failures.Count == 1
                ? string.Empty
                : UiText.Format("Text_646F2DBF1852", _hotkeys.Failures.Count - 1);

            ShowHotkeyConflict(first,
                UiText.Format("Text_114E1105C3EE", first.Hotkey, suffix)
                    + PrintScreenFailureLine(_hotkeys.Failures)
                    + (_services.GetRequiredService<SettingsStore>().LastLoadMigratedUnsafeHotkeys ? "\n" + UiText.Get("Hotkey.Migrated") : string.Empty));
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

    /// <summary>The core-shortcut answer could not be saved: say so with a way into Settings → Shortcuts.</summary>
    private void ShowCoreShortcutsProblem(string message)
    {
        if (message.Length == 0 || _shellPresenter is null) return;
        string row = ShortcutSettingTarget.ForCommand(GlobalHotkeyCommand.CaptureRegion);
        try
        {
            _shellPresenter.ShowActionNotification(UiText.Get("CoreShortcuts.Title"), message,
                UiText.Get("Shell_ChangeShortcut"), () => HandleShortcutSettingsRequested(row), TrayBalloonKind.Error,
                answersCommand: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not report the core shortcut answer problem");
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
        if (_readyNotification is null) return;
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
        if (_tray is null || _coreShortcutsQuestionPending || _hotkeys?.Failures.Count != 0) return;
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
        bool shown = _shellPresenter.ShowCaptureReceipt(CaptureSelectionProvenance.ReceiptText(
                UiText.Get(copied ? "Capture.CopiedAndSaved" : "Capture.Saved"), Core.Capture.CaptureProvenance.FromRecord(record)),
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
            _pins.PinImage(rendered, Core.Capture.CaptureProvenance.FromRecord(record));
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
