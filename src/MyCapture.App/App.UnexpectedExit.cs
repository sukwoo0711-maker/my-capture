using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;

namespace MyCapture.App;

public partial class App
{
    private UnexpectedExitNotice? _unexpectedExitNotice;
    private bool _unexpectedExitNoticeOffered;

    /// <summary>A quiet launch keeps pending records unread until the library or settings opens.</summary>
    private bool _unexpectedExitNoticeDeferred;

    /// <summary>How long a final exit waits for the shown records to be marked.</summary>
    private static readonly TimeSpan FinalExitMarkLimit = TimeSpan.FromSeconds(1);

    /// <summary>Shows the notice without an owner. Tests place it off screen.</summary>
    internal Action<ThemedDialog> ShowUnexpectedExitNotice { get; set; } = ThemedDialog.ShowOwnerless;

    /// <summary>The sole exit-record writer: called only by the terminating AppDomain handler.</summary>
    private void RecordUnexpectedExit(Exception? error)
    {
        try
        {
            UnexpectedExitLog exits = ErrorReporting.Exits
                ?? new UnexpectedExitLog(UnexpectedExitLog.FolderFor(ResolveAppPaths()));
            SafeErrorRecord primary = SafeErrorRecord.From(DateTimeOffset.UtcNow, LogLevel.Critical, 0,
                "MyCapture.App.App", null, error);
            exits.TryWrite(primary, _fileLog?.RecentLines(8));
        }
        catch (Exception)
        {
            // The process is already terminating; diagnostics must not trigger another failure.
        }
    }

    /// <param name="quietLaunch">
    /// A Windows sign-in launch that stays in the tray. Its notice waits for the first library or settings
    /// window; if the app ends first, the records stay pending for the next launch that shows a window.
    /// </param>
    private async Task OfferUnexpectedExitAfterLaunchAsync(bool quietLaunch)
    {
        if (s_residentProbe is not null || ErrorReporting.Exits is not { } exits || _unexpectedExitNoticeOffered) return;
        if (quietLaunch)
        {
            _unexpectedExitNoticeDeferred = true;
            return;
        }
        _unexpectedExitNoticeOffered = true;
        try
        {
            // Without a pending record this reads one folder on the thread pool and ends.
            _unexpectedExitNotice = await UnexpectedExitNotice.OfferAsync(exits,
                () => Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask),
                stop: () => _preparingExit || _storeInstallationPending || _shutdownRequested || Dispatcher.HasShutdownStarted,
                busy: () => _coreShortcutsQuestionPending || _coreShortcutsQuestion?.IsVisible == true
                    || Windows.Cast<Window>().OfType<WelcomeWindow>().Any(window => window.IsVisible)
                    || UnexpectedExitCaptureActive(),
                show: ShowUnexpectedExitNotice);
        }
        catch (Exception error)
        {
            _log?.LogWarning(error, "Could not show unexpected exit notice");
        }
    }

    /// <summary>The first library or settings window a quiet launch shows offers the notice it kept back.</summary>
    private void OfferDeferredUnexpectedExitNotice(Window window)
    {
        if (!_unexpectedExitNoticeDeferred || window is not (GalleryWindow or SettingsWindow)) return;
        _unexpectedExitNoticeDeferred = false;
        _ = OfferUnexpectedExitAfterLaunchAsync(quietLaunch: false);
    }

    private bool UnexpectedExitCaptureActive()
    {
        Capture.CaptureGuardState state = ReadCaptureGuardState();
        return state.CaptureStarting || state.Selecting || state.EditorOpening || state.EditorOpen
            || state.Countdown || state.ScrollingCapture || state.RecordingActive;
    }

    /// <summary>
    /// An explicit exit closes a notice still on screen and marks its records handled; exit preparation
    /// awaits that before process shutdown.
    /// </summary>
    private Task CloseUnexpectedExitNoticeAsync() => _unexpectedExitNotice?.CloseAsync() ?? Task.CompletedTask;

    /// <summary>A step the app may survive takes the notice off screen and leaves its records pending.</summary>
    private void WithdrawUnexpectedExitNotice() => _unexpectedExitNotice?.Withdraw();

    /// <summary>
    /// A final exit nothing can cancel any more (a session end that goes ahead, the framework's own exit in
    /// OnExit) closes a notice still on screen and waits a moment for its records to be marked, because the
    /// process may end right after. A notice that was only withdrawn keeps its records pending.
    /// </summary>
    private void MarkUnexpectedExitNoticeForFinalExit()
    {
        if (_unexpectedExitNotice is not { } notice) return;
        try
        {
            if (!notice.CloseAsync().Wait(FinalExitMarkLimit))
                _log?.LogWarning("Unexpected exit records were not marked before the app ended");
        }
        catch (Exception error)
        {
            // The exit goes on; unmarked records only show the notice again next time.
            _log?.LogWarning(error, "Could not close the unexpected exit notice for the final exit");
        }
    }
}
