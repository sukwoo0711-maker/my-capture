using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>A capture ended: whether it was saved, and whether it was copied to the clipboard.</summary>
    private event Action<bool, bool>? _onboardingCaptureEnded;

    private WelcomeWindow CreateOnboardingWindow(bool replay)
    {
        var settings = _settings!;
        var welcome = new WelcomeWindow(settings.Hotkeys, settings.General.AfterCapture,
            settings.Queue.ImageRetentionHours, options: new WelcomeOptions
            {
                Replay = replay,
                StartAtSignIn = replay ? settings.General.LaunchAtLogin : null,
                UseAccurateOcr = replay ? OcrQualityProfile.UseNeuralModel(settings.Ocr.Quality) : null,
            });
        _onboardingWindow = welcome;
        // A shortcut Windows did not register is never offered as the way to continue.
        void RefreshShortcuts()
        {
            bool Available(GlobalHotkeyCommand command) => _hotkeys is not null
                && !_hotkeys.Failures.Any(failure => failure.Command == command);
            welcome.UpdateShortcutAvailability(Available(GlobalHotkeyCommand.CaptureRegion),
                Available(GlobalHotkeyCommand.PasteToScreen), Available(GlobalHotkeyCommand.OpenLibrary));
        }
        RefreshShortcuts();
        welcome.Activated += (_, _) =>
        {
            RefreshShortcuts();
            // A capture or recording that started and ended without a result for this guide leaves the button waiting.
            if (welcome.CapturePending && !IsWelcomeCaptureInProgress) welcome.ResetPendingCapture();
        };
        welcome.CaptureRequested += (_, _) => HandleCaptureRequested();
        welcome.PinRequested += (_, _) => HandlePasteToScreen();
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
            if (saved) welcome.NotifyCaptureCompleted(copied);
            else welcome.NotifyCaptureCancelled();
        }
        void PinCreated() => welcome.NotifyPinCreated();
        // The overlay also closes after a capture that is still being saved; that result arrives by CaptureEnded.
        void CaptureClosed(object? sender, EventArgs args)
        {
            if (_capturePersistenceTask.IsCompleted) welcome.NotifyCaptureCancelled();
        }
        void CaptureFailed(Exception exception) => welcome.NotifyCaptureCancelled();
        _onboardingCaptureEnded += CaptureEnded;
        if (_pins is not null) _pins.PinCreated += PinCreated;
        if (_overlay is not null)
        {
            _overlay.OverlayClosed += CaptureClosed;
            _overlay.TransitionFailed += CaptureFailed;
        }
        welcome.Closed += (_, _) =>
        {
            _onboardingCaptureEnded -= CaptureEnded;
            if (_pins is not null) _pins.PinCreated -= PinCreated;
            if (_overlay is not null)
            {
                _overlay.OverlayClosed -= CaptureClosed;
                _overlay.TransitionFailed -= CaptureFailed;
            }
            if (ReferenceEquals(_onboardingWindow, welcome)) _onboardingWindow = null;
        };
        return welcome;
    }

    /// <summary>Tray menu and Settings: the guide again, with the current choices preset and nothing applied until it is finished.</summary>
    private void ShowOnboardingAgain()
    {
        if (_settings is null || _preparingExit || _storeInstallationPending) return;
        if (_onboardingWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }
        var welcome = CreateOnboardingWindow(replay: true);
        welcome.Closed += (_, _) =>
        {
            if (_shutdownRequested) return;
            if (welcome.Outcome == WelcomeOutcome.Completed)
            {
                // Reviewing the guide is reversible: only the explicit finish applies choices.
                ApplyWelcomeStartAtSignIn(welcome.StartAtSignIn);
                try
                {
                    FirstRunOcrChoice.Complete(_settings, welcome.UseAccurateOcr,
                        settings => _services!.GetRequiredService<SettingsStore>().Save(settings),
                        () => StartOcrModelDownload(isBackground: false, deferForBatterySaver: true));
                }
                catch (Exception ex) { _log?.LogWarning(ex, "Could not save replayed onboarding choices"); }
            }
            if (welcome.Outcome == WelcomeOutcome.OpenSettings) HandleSettingsRequested();
        };
        welcome.Show();
    }
}
