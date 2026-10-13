using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Gallery;

namespace MyCapture.App;

public partial class App
{
    private RecoverableLibraryService? _libraryRecovery;
    private Task _initialLibraryRecoveryTask = Task.CompletedTask;
    private bool _libraryRecoveryReady;

    private bool CanAutomaticallyRemoveCapture(Guid id) => _libraryRecoveryReady
        && !_preparingExit && !_storeInstallationPending
        && _initialEditRecoveryTask.IsCompleted && _capturePersistenceTask.IsCompleted
        && _overlay?.IsSelecting != true && _activeCountdown is null && _scrollCancellation is null
        && !_preparingAdvancedCapture && !_pasteToScreenInFlight && !_gitHubUploadInFlight
        && _currentRecord?.Id != id && _pendingRecord?.Id != id
        && _persistence?.IsBusy(id) != true;

    private async Task RecoverLibraryStateAsync()
    {
        try
        {
            if (_libraryRecovery is not null) await _libraryRecovery.RecoverInterruptedRestoresAsync();
            _galleryWindow?.RefreshFromQueue();
            if (_tray?.IsInitialized == true) _tray.SetCaptureCount(_queue?.Count ?? 0);
        }
        catch (Exception error) { _log?.LogError(error, "Could not reconcile interrupted library recovery"); }
        finally
        {
            _libraryRecoveryReady = true;
            RequestLibraryMaintenance();
        }
    }

    private void RequestLibraryMaintenance()
    {
        if (Dispatcher.HasShutdownStarted || !_libraryRecoveryReady) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_preparingExit || _storeInstallationPending) return;
            try { _queue?.RequestAutomaticMaintenance(); }
            catch (Exception error) { _log?.LogWarning(error, "Could not schedule recoverable library maintenance"); }
        }));
    }

    private async Task ResumeMaintenanceAfterInitialRecoveryAsync()
    {
        try { await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask); }
        catch (Exception error) { _log?.LogWarning(error, "Initial recovery did not fully complete"); }
        RequestLibraryMaintenance();
    }
}
