using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// The shell side of <see cref="CaptureGuard"/>: reads what stands in the way of a capture command
/// and shows the user why the command did not start.
/// </summary>
/// <remarks>
/// A capture command that cannot start answers where the user is looking: the editor, selector,
/// countdown or progress window that holds it comes forward, and a one-line reason appears in the
/// editor's status line or as a notice. A capture already on its way to the screen (a frame being
/// taken, an editor opening) answers by appearing.
/// </remarks>
public partial class App
{
    /// <summary>What stands in the way of a capture command right now. Reads state only.</summary>
    private CaptureGuardState ReadCaptureGuardState()
    {
        var state = new CaptureGuardState
        {
            AppUpdating = _storeInstallationPending,
            AppExiting = _preparingExit,
            CaptureStarting = _preparingAdvancedCapture,
            Countdown = _activeCountdown is not null,
            ScrollingCapture = _scrollCancellation is not null,
            RecordingActive = _recorder?.HasRecordingSession == true,
            RecordingCanCaptureStill = _recorder?.CanCaptureStill == true,
        };
        return CaptureEditorRegistry.WithEditors(_overlay?.WithSession(state) ?? state);
    }

    /// <summary>
    /// Clears the receipt card and shell surfaces a new capture would photograph, then returns the
    /// first blocker. Nothing is cleared while the app is updating or exiting.
    /// </summary>
    private CaptureBlock CheckStillCapture(GlobalHotkeyCommand command)
    {
        CaptureGuardState state = ReadCaptureGuardState();
        if (!state.AppUpdating && !state.AppExiting)
        {
            DismissQuickAccessForCapture();
            _shellPresenter?.DismissForCapture();
        }

        return CaptureGuard.Find(state, command);
    }

    /// <summary>
    /// True when a still capture may start; otherwise the user is shown why it did not. A capture
    /// editor whose save finishes out of sight is waited for first: the user already sees it closed.
    /// </summary>
    private async Task<bool> GuardStillCaptureAsync(GlobalHotkeyCommand command)
    {
        await WaitForEditorFinishedAsync();
        CaptureBlock block = CheckStillCapture(command);
        if (block == CaptureBlock.None) return true;
        ReportCaptureBlock(block, CaptureCommandTitle(command) ?? "MyCapture");
        return false;
    }

    /// <summary>
    /// Completes at once unless Done has taken the capture editor off the screen while its save
    /// finishes; then once the editor has closed, or has come back because the save failed.
    /// </summary>
    private Task WaitForEditorFinishedAsync() => _overlay?.WaitForEditorFinishedAsync() ?? Task.CompletedTask;

    /// <summary>Shows why a capture command did not start.</summary>
    /// <param name="mode">The command's name, the title of a notice.</param>
    /// <param name="recording">The command was the screen recording, not a still capture.</param>
    private void ReportCaptureBlock(CaptureBlock block, string mode, bool recording = false)
    {
        _log?.LogInformation("{Mode} did not start: {Block}", mode, block);
        try
        {
            switch (block)
            {
                case CaptureBlock.None:
                case CaptureBlock.CaptureStarting:
                case CaptureBlock.EditorOpening:
                    // The earlier capture is about to appear in front; that is the answer.
                    return;
                case CaptureBlock.Selecting:
                    _ = _overlay?.BringSelectorForward();
                    return;
                case CaptureBlock.Countdown:
                    _ = _activeCountdown?.Activate();
                    return;
                case CaptureBlock.AppUpdating:
                    if (!BringHoldingWindowForward()) ShowCommandNotice(mode, UiText.Get("CaptureBlocked_Updating"));
                    return;
                case CaptureBlock.AppExiting:
                    if (!BringModalDialogForward()) ShowCommandNotice(mode, UiText.Get("CaptureBlocked_Exiting"));
                    return;
                case CaptureBlock.ScrollingCapture:
                    ShowCommandNotice(mode, UiText.Get("CaptureBlocked_Scrolling"));
                    return;
                case CaptureBlock.RecordingBusy:
                    _recorder?.ActivateExisting();
                    ShowCommandNotice(mode, UiText.Get("Text_90290F46A7D5"));
                    return;
            }

            string notice = UiText.Get(CaptureGuard.EditorNoticeKey(block, recording) ?? "CaptureBlocked_Editor");
            if (_overlay?.ShowEditorBlocked(notice) != true) ShowCommandNotice(mode, notice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Saying why must never take the resident shell down.
            _log?.LogWarning(ex, "Could not show why {Mode} did not start", mode);
        }
    }

    /// <summary>The import or Store update the app is holding for shows its progress in front.</summary>
    private bool BringHoldingWindowForward()
    {
        if ((_legacyImportWindow ?? (Window?)_settingsWindow) is not { } progress) return false;
        if (!progress.IsVisible) progress.Show();
        WindowForeground.BringForward(progress);
        return true;
    }

    /// <summary>The question exit is waiting on, such as unsaved edits, comes forward.</summary>
    private bool BringModalDialogForward()
    {
        if (WindowForeground.ModalDialog(Windows.OfType<Window>()) is not { } dialog) return false;
        WindowForeground.BringForward(dialog);
        return true;
    }

    /// <summary>
    /// An answer to a command the user just gave: why it did not start, or why it failed. Unlike
    /// background notifications it shows during a capture or recording session too: the user is
    /// waiting for it.
    /// </summary>
    private void ShowCommandNotice(string title, string message, TrayBalloonKind kind = TrayBalloonKind.Information)
    {
        if (_shellPresenter is { } presenter)
            _ = Dispatcher.BeginInvoke(new Action(() => presenter.ShowNotification(title, message, kind, answersCommand: true)));
        else
            _tray?.ShowBalloon(title, message, kind, playSound: false);
    }

    /// <summary>The name a capture command's notices carry; null for commands that capture nothing.</summary>
    private static string? CaptureCommandTitle(GlobalHotkeyCommand command) => command switch
    {
        GlobalHotkeyCommand.CaptureRegion => UiText.Get("Text_096A13A757DB"),
        GlobalHotkeyCommand.CaptureWindow => UiText.Get("Text_36458FABBDBB"),
        GlobalHotkeyCommand.CaptureFullScreen => UiText.Get("Text_254C836B9ED2"),
        GlobalHotkeyCommand.RepeatLastRegion => UiText.Get("Text_92E815063265"),
        GlobalHotkeyCommand.DelayedCapture => UiText.Get("Text_38476EDDA50B"),
        GlobalHotkeyCommand.ScrollingCapture => UiText.Get("Text_4B4D91FFC215"),
        GlobalHotkeyCommand.CaptureText => UiText.Get("TextCapture_Title"),
        GlobalHotkeyCommand.RecordRegion => UiText.Get("Text_4F5AF20FAA6E"),
        _ => null,
    };
}
