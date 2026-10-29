using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Shell glue for the first-use guide. The guide subscribes to what the app already does (a
/// finished capture, a new pin, the library window) instead of starting its own copies of those
/// paths, so the steps advance only when the real thing happened.
/// </summary>
public partial class App
{
    private WelcomeWindow? _onboardingWindow;
    private Guid? _onboardingCaptureId;
    private DispatcherTimer? _practiceOfferTimer;

    /// <summary>A capture ended: whether it was saved, and whether it was copied to the clipboard.</summary>
    private event Action<bool, bool>? _onboardingCaptureEnded;

    private WelcomeWindow CreateOnboardingWindow(bool replay) => CreatePracticeWindow(replay);

    private WelcomeWindow CreatePracticeWindow(bool replay, bool consent = false, bool featurePractice = false)
    {
        var settings = _settings!;
        var welcome = new WelcomeWindow(settings.Hotkeys, settings.General.AfterCapture,
            settings.Queue.ImageRetentionHours, options: new WelcomeOptions
            {
                Replay = replay,
                ShowCoreShortcutsConsent = consent,
                FeaturePractice = featurePractice,
                StartAtSignIn = replay ? settings.General.LaunchAtLogin : null,
                UseAccurateOcr = replay ? OcrQualityProfile.UseNeuralModel(settings.Ocr.Quality) : null,
            });
        _onboardingWindow = welcome;
        _onboardingCaptureId = null;
        // A shortcut Windows did not register is never offered as the way to continue.
        void RefreshShortcuts()
        {
            welcome.UpdateHotkeys(_settings!.Hotkeys);
            bool Available(GlobalHotkeyCommand command, Hotkey key) => key.IsAssigned
                && _hotkeys?.GetAssigned(command) == key
                && !_hotkeys.Failures.Any(failure => failure.Command == command);
            welcome.UpdateShortcutAvailability(Available(GlobalHotkeyCommand.CaptureRegion, _settings.Hotkeys.Capture),
                Available(GlobalHotkeyCommand.PasteToScreen, _settings.Hotkeys.PasteToScreen),
                Available(GlobalHotkeyCommand.OpenLibrary, _settings.Hotkeys.OpenLibrary));
        }
        RefreshShortcuts();
        welcome.Activated += (_, _) =>
        {
            RefreshShortcuts();
            // A capture or recording that started and ended without a result for this guide leaves the button waiting.
            if (welcome.CapturePending && !IsWelcomeCaptureInProgress) welcome.ResetPendingCapture();
        };
        welcome.CaptureRequested += (_, _) => HandleCaptureRequested();
        bool captureForEditor = false;
        void OpenPracticeEditor(object? sender, EventArgs args)
        {
            if (_overlay?.CurrentEditor is { } activeEditor)
            {
                Window.GetWindow(activeEditor)?.Activate();
                return;
            }
            Guid? id = _onboardingCaptureId ?? _queue?.Records.FirstOrDefault(record => record.MediaKind == Core.Queue.CaptureMediaKind.Image)?.Id;
            if (id is { } captureId)
            {
                _onboardingCaptureId = captureId;
                EnsureGalleryWindow()?.OpenCapture(captureId, edit: true);
            }
            else
            {
                captureForEditor = true;
                HandleCaptureRequested();
            }
        }
        welcome.EditorRequested += OpenPracticeEditor;
        welcome.PinRequested += OpenPracticeEditor;
        welcome.ShortcutChangeRequested += command => HandleShortcutSettingsRequested(ShortcutSettingTarget.ForCommand(command));
        welcome.CoreShortcutsAnswered += accepted =>
        {
            ApplyPracticeShortcutAnswer(accepted);
            RefreshShortcuts();
        };
        welcome.LibraryRequested += (_, _) =>
        {
            HandleGalleryRequested();
            // The library window reports its own activation; this covers a window that was already in front.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (ReferenceEquals(_onboardingWindow, welcome) && _galleryWindow?.IsVisible == true) welcome.NotifyLibraryOpened();
            }));
        };

        void CaptureEnded(bool saved, bool copied)
        {
            if (saved)
            {
                if (welcome.CurrentStep == WelcomeStep.Capture || captureForEditor)
                    _onboardingCaptureId = _currentRecord?.Id;
                welcome.NotifyCaptureCompleted(copied);
                if (captureForEditor)
                {
                    captureForEditor = false;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                        new Action(() => { if (ReferenceEquals(_onboardingWindow, welcome)) OpenPracticeEditor(null, EventArgs.Empty); }));
                }
            }
            else welcome.NotifyCaptureCancelled();
        }
        void ArrowAdded() => welcome.NotifyArrowAdded();
        void ArrowShortcut() => welcome.NotifyArrowShortcutFired();
        void PinZoomChanged(Guid pinId) => welcome.NotifyPinZoomChanged(pinId);
        // The overlay also closes after a capture that is still being saved; that result arrives by CaptureEnded.
        void CaptureClosed(object? sender, EventArgs args)
        {
            if (_capturePersistenceTask.IsCompleted) welcome.NotifyCaptureCancelled();
        }
        void CaptureFailed(Exception exception) => welcome.NotifyCaptureCancelled();
        _onboardingCaptureEnded += CaptureEnded;
        PracticeSignals.ArrowAdded += ArrowAdded;
        PracticeSignals.ArrowShortcutFired += ArrowShortcut;
        if (_pins is not null) _pins.UserZoomChanged += PinZoomChanged;
        if (_overlay is not null)
        {
            _overlay.OverlayClosed += CaptureClosed;
            _overlay.TransitionFailed += CaptureFailed;
        }
        welcome.Closed += (_, _) =>
        {
            _onboardingCaptureEnded -= CaptureEnded;
            PracticeSignals.ArrowAdded -= ArrowAdded;
            PracticeSignals.ArrowShortcutFired -= ArrowShortcut;
            if (_pins is not null) _pins.UserZoomChanged -= PinZoomChanged;
            if (_overlay is not null)
            {
                _overlay.OverlayClosed -= CaptureClosed;
                _overlay.TransitionFailed -= CaptureFailed;
            }
            if (ReferenceEquals(_onboardingWindow, welcome))
            {
                _onboardingWindow = null;
                _onboardingCaptureId = null;
            }
        };
        return welcome;
    }

    /// <summary>Tray menu and Settings: the guide again, with the current choices preset and nothing applied until it is finished.</summary>
    private void ShowOnboardingAgain()
    {
        if (_settings is null || _preparingExit || _storeInstallationPending) return;
        if (_onboardingWindow is { } existing)
        {
            if (existing.IsFeaturePractice) existing.Close();
            else
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Show();
                existing.Activate();
                return;
            }
        }
        var welcome = CreateOnboardingWindow(replay: true);
        MarkFullPracticePresented();
        welcome.Closed += (_, _) =>
        {
            if (_shutdownRequested) return;
            if (welcome.Outcome == WelcomeOutcome.Completed)
            {
                ApplyReplayedPracticeChoices(welcome);
            }
            if (welcome.Outcome == WelcomeOutcome.OpenSettings) HandleSettingsRequested();
        };
        welcome.Show();
    }

    private void ApplyReplayedPracticeChoices(WelcomeWindow welcome)
    {
        ApplyWelcomeStartAtSignIn(welcome.StartAtSignIn);
        try
        {
            FirstRunOcrChoice.Complete(_settings!, welcome.UseAccurateOcr,
                settings => _services!.GetRequiredService<SettingsStore>().Save(settings),
                () => StartOcrModelDownload(isBackground: false, deferForBatterySaver: true));
        }
        catch (Exception ex) { _log?.LogWarning(ex, "Could not save replayed onboarding choices"); }
    }

    private void MarkFullPracticePresented()
    {
        if (_settings is null || _services is null) return;
        try { PracticeOffer.MarkPresented(_settings, () => _services.GetRequiredService<SettingsStore>().Save(_settings)); }
        catch (Exception ex) { _log?.LogWarning(ex, "Could not save practice revision"); }
    }

    private void QueueNewFeaturePractice()
    {
        if (_settings is null || !PracticeOffer.IsEligible(_settings, _launchedFirstRun) || _practiceOfferTimer is not null) return;
        _practiceOfferTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        _practiceOfferTimer.Tick += (_, _) => TryOfferNewFeaturePractice();
        _practiceOfferTimer.Start();
    }

    private void TryOfferNewFeaturePractice()
    {
        if (_preparingExit || _shutdownRequested || _settings is null
            || !PracticeOffer.IsEligible(_settings, _launchedFirstRun))
        {
            _practiceOfferTimer?.Stop();
            _practiceOfferTimer = null;
            return;
        }
        if (_shellPresenter is null || _services is null) return;
        bool idle = !_storeInstallationPending && !_coreShortcutsQuestionPending && _onboardingWindow is null
            && !IsWelcomeCaptureInProgress && !_shellPresenter.HasNotification && !_readyNotification.HasPendingResident
            && _settingsWindow?.IsVisible != true && _pins?.IsInteractionActive != true
            && !Windows.OfType<AnnotationEditorWindow>().Any(window => window.IsVisible);
        try
        {
            bool shown = PracticeOffer.TryShow(_settings, _launchedFirstRun, idle, () =>
            {
                _shellPresenter.ShowActionNotification(UiText.Get("Practice.OfferTitle"), UiText.Get("Practice.OfferBody"),
                    UiText.Get("Practice.OfferAction"), ShowNewFeaturePractice);
                return _shellPresenter.HasNotification;
            }, () => _services.GetRequiredService<SettingsStore>().Save(_settings));
            if (shown)
            {
                _practiceOfferTimer?.Stop();
                _practiceOfferTimer = null;
            }
        }
        catch (Exception ex)
        {
            _practiceOfferTimer?.Stop();
            _practiceOfferTimer = null;
            _log?.LogWarning(ex, "Could not offer new feature practice");
        }
    }

    private void ShowNewFeaturePractice()
    {
        if (_settings is null || _preparingExit || _storeInstallationPending) return;
        if (_onboardingWindow is { } existing) { existing.Activate(); return; }
        CreatePracticeWindow(replay: true, featurePractice: true).Show();
    }
}
