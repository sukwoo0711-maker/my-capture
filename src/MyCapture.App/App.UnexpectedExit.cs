using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;

namespace MyCapture.App;

public partial class App
{
    private UnexpectedExitNotice? _unexpectedExitNotice;
    private bool _unexpectedExitNoticeOffered;

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

    private async Task OfferUnexpectedExitAfterLaunchAsync()
    {
        if (s_residentProbe is not null || ErrorReporting.Exits is not { } exits || _unexpectedExitNoticeOffered) return;
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
                show: ThemedDialog.ShowOwnerless);
        }
        catch (Exception error)
        {
            _log?.LogWarning(error, "Could not show unexpected exit notice");
        }
    }

    private bool UnexpectedExitCaptureActive()
    {
        Capture.CaptureGuardState state = ReadCaptureGuardState();
        return state.CaptureStarting || state.Selecting || state.EditorOpening || state.EditorOpen
            || state.Countdown || state.ScrollingCapture || state.RecordingActive;
    }

    /// <summary>Awaited by normal exit preparation so a shown notice is handled before process shutdown.</summary>
    private Task CloseUnexpectedExitNoticeAsync() => _unexpectedExitNotice?.CloseAsync() ?? Task.CompletedTask;
}
