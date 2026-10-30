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
    private DispatcherTimer? _practiceOfferTimer;


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
        void RefreshShortcuts()
        {
            welcome.UpdateHotkeys(_settings!.Hotkeys);
            bool Available(GlobalHotkeyCommand command, Hotkey key) => key.IsAssigned
                && _hotkeys?.GetAssigned(command) == key && !_hotkeys.Failures.Any(failure => failure.Command == command);
            welcome.UpdateShortcutAvailability(Available(GlobalHotkeyCommand.CaptureRegion, _settings.Hotkeys.Capture),
                Available(GlobalHotkeyCommand.PasteToScreen, _settings.Hotkeys.PasteToScreen),
                Available(GlobalHotkeyCommand.OpenLibrary, _settings.Hotkeys.OpenLibrary));
        }
        RefreshShortcuts();
        welcome.Activated += (_, _) => RefreshShortcuts();
        welcome.CaptureRequested += (_, _) => HandleCaptureRequested();
        void OpenPracticeEditor(object? sender, EventArgs args)
        {
            if (welcome.Session.CaptureId is not { } id || _queue?.Find(id) is not { } record
                || !System.IO.File.Exists(_queue.GetFilePath(record, Core.Queue.CaptureFileNames.Original)))
            { welcome.Session.Notice("Journey.NoCapture"); welcome.Session.StartGroup(PracticeStep.A1); return; }
            var editor = Windows.OfType<AnnotationEditorWindow>().FirstOrDefault(window => window.DraftRecordSource?.Invoke()?.Id == id && window.IsVisible);
            if (editor is not null) { editor.Activate(); EditorReady(id, editor.Editor); }
            else EnsureGalleryWindow()?.OpenCapture(id, edit: true);
        }
        void EditorReady(Guid recordId, AnnotationEditorControl editor)
        {
            if (recordId != welcome.Session.CaptureId || welcome.Session.Step is < PracticeStep.A3 or > PracticeStep.A6) return;
            if (welcome.Session.EditorId is { } currentEditor && editor.PracticeTarget?.EditorId != currentEditor) return;
            if (editor.PracticeTarget is { } existing && existing.SessionId == welcome.Session.Id && existing.RecordId == recordId)
            {
                welcome.Notify(new(PracticeEventKind.EditorReady, existing.SessionId, RecordId: recordId, EditorId: existing.EditorId));
                return;
            }
            var target = new PracticeEditorTarget(welcome.Session.Id, recordId, Guid.NewGuid());
            editor.PracticeTarget = target;
            welcome.Notify(new(PracticeEventKind.EditorReady, target.SessionId, RecordId: recordId, EditorId: target.EditorId));
        }
        welcome.EditorRequested += OpenPracticeEditor;
        welcome.PinRequested += (_, _) => HandlePasteToScreen();
        welcome.RestorePinRequested += async (_, _) =>
        {
            if (_pins is null || welcome.Session.LogicalPinId is not { } id) return;
            try
            {
                if (await _pins.RestoreLastClosedAsync(id) is null) welcome.Session.CannotRestorePin();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { welcome.Session.CannotRestorePin(); }
        };
        welcome.ShortcutChangeRequested += command => HandleShortcutSettingsRequested(ShortcutSettingTarget.ForCommand(command));
        welcome.CoreShortcutsAnswered += accepted => { ApplyPracticeShortcutAnswer(accepted); RefreshShortcuts(); };
        welcome.LibraryRequested += (_, _) =>
        {
            if (welcome.Session.CaptureId is { } id && _queue?.Find(id) is { } record
                && System.IO.File.Exists(_queue.GetFilePath(record, Core.Queue.CaptureFileNames.Original))) EnsureGalleryWindow()?.OpenCapture(id, edit: false);
            else
            {
                welcome.Session.ForgetCapture();
                HandleGalleryRequested();
            }
            if (_galleryWindow?.IsVisible == true) welcome.NotifyLibraryOpened();
        };
        void Result(PracticeEvent signal)
        {
            bool pin = signal.Kind is PracticeEventKind.PinOpened or PracticeEventKind.PinResized or PracticeEventKind.OpacityMenuOpened or PracticeEventKind.OpacityMenuClosed
                or PracticeEventKind.OpacityChanged or PracticeEventKind.PinClosing or PracticeEventKind.PinClosed or PracticeEventKind.PinRestored;
            if (pin && signal.SessionId == Guid.Empty) signal = signal with { SessionId = welcome.Session.Id };
            welcome.Notify(signal);
        }
        void LibraryEditorOpened(Guid id) => welcome.Notify(new(PracticeEventKind.LibraryEditorOpened, welcome.Session.Id, RecordId: id));
        PracticeSignals.Result += Result;
        PracticeSignals.EditorReady += EditorReady;
        PracticeSignals.LibraryEditorOpened += LibraryEditorOpened;
        // Sequence checks never read or retain private clipboard content.
        var clipboardWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        clipboardWatch.Tick += (_, _) =>
        {
            if (welcome.Session.ClipboardSequence != 0)
                welcome.Notify(new(PracticeEventKind.ClipboardChanged, welcome.Session.Id, ClipboardSequence: ClipboardWriter.GetVersion()));
        };
        clipboardWatch.Start();
        welcome.Closed += (_, _) =>
        {
            clipboardWatch.Stop();
            PracticeSignals.Result -= Result;
            PracticeSignals.EditorReady -= EditorReady;
            PracticeSignals.LibraryEditorOpened -= LibraryEditorOpened;
            if (ReferenceEquals(_onboardingWindow, welcome)) _onboardingWindow = null;
        };
        return welcome;
    }

    private PracticeCaptureTarget? PracticeCaptureTarget()
    {
        if (_onboardingWindow is not { } welcome || !welcome.Session.CapturePending) return null;
        return new(welcome.Session.Id, welcome.Session.CaptureRequestId);
    }
    private static void PublishPracticeCapture(Capture.CaptureSelectionCompletedEventArgs selection, Guid? recordId)
    {
        if (selection.PracticeTarget is not { } target) return;
        PracticeSignals.Publish(new(recordId.HasValue ? PracticeEventKind.CaptureSaved : PracticeEventKind.CaptureCancelled,
            target.SessionId, target.RequestId, RecordId: recordId ?? Guid.Empty));
    }
    private static void CancelPracticeCapture(PracticeCaptureTarget? target)
    {
        if (target is { } capture)
            PracticeSignals.Publish(new(PracticeEventKind.CaptureCancelled, capture.SessionId, capture.RequestId));
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
