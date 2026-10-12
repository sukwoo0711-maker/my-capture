using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Settings;
using MyCapture.Core.Platform;
using MyCapture.Core.Storage;

namespace MyCapture.App;

public partial class App
{
    private LegacyDataImportWindow? _legacyImportWindow;
    private bool _legacyImportPreparing;
    private IDisposable? _legacyIndexingLease;
    private IDisposable? _legacyPresenterLease;
    private bool _legacySharedProtectionHeld;
    private List<(Window Window, bool Enabled)>? _legacyImportWindowStates;

    private Task RecoverLegacyImportBeforeLoadAsync()
    {
        if (_services is null) throw new InvalidOperationException("Application services are unavailable.");
        AppPaths paths = _services.GetRequiredService<AppPaths>();
        Task recovery = _services.GetRequiredService<ILegacyLibraryMigrationService>().RecoverPendingAsync(paths);
        return LegacyMigrationRecoveryWindow.AwaitAsync(recovery, () => Task.Delay(150), task =>
        {
            var progress = new LegacyMigrationRecoveryWindow(task);
            _ = progress.ShowDialog();
        });
    }

    internal static string DefaultLegacyDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName);

    private void ConfigureLegacyImportWindow(SettingsWindow settings)
    {
        settings.RunLegacyImport = owner => ShowLegacyImport(owner, DefaultLegacyDataRoot);
    }

    private LegacyImportSelection? ShowLegacyImport(Window owner, string source)
    {
        if (_legacyImportWindow is not null || _storeInstallationPending || _preparingExit
            || _services is null || _settings is null || _queue is null || _capturePaths is null) return null;
        var operations = new LegacyImportOperations(
            _services.GetRequiredService<ILegacyLibraryMigrationService>(), _capturePaths,
            PrepareLegacyImportAsync, ReleaseLegacyImport,
            (root, progress, token) => _services.GetRequiredService<ILegacyLibraryMigrationService>()
                .ImportAsync(root, _capturePaths, _queue, _settings, progress, token),
            () => { _galleryWindow?.RefreshFromQueue(); _tray?.SetCaptureCount(_queue.Count); },
            RecoverLegacyImportWhileHeldAsync);
        var window = new LegacyDataImportWindow(operations, source) { Owner = owner };
        _legacyImportWindow = window;
        try { _ = window.ShowDialog(); return window.Selection; }
        finally { _legacyImportWindow = null; }
    }

    private async Task<bool> PrepareLegacyImportAsync(CancellationToken token)
    {
        if (_legacyImportPreparing) return false;
        _legacyImportPreparing = true;
        try
        {
            // Initial recovery can itself publish metadata; finish it before acquiring protection.
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask).WaitAsync(token);
            if (!await PrepareAndHoldStoreUpdateAsync()) { _legacyImportPreparing = false; return false; }
            _legacySharedProtectionHeld = true;
            _legacyImportWindowStates = Windows.Cast<Window>().Where(window => !ReferenceEquals(window, _legacyImportWindow))
                .Select(window => (window, window.IsEnabled)).ToList();
            foreach (var (window, _) in _legacyImportWindowStates) window.IsEnabled = false;
            _automaticIndexingPassCancellation?.Cancel();
            if (_ocrPresenter is not null) _legacyPresenterLease = await _ocrPresenter.AcquireMutationLeaseAsync(token);
            if (_automaticIndexer is not null) _legacyIndexingLease = await _automaticIndexer.AcquireIdleLeaseAsync(token);
            while (_indexingRunning) await Task.Delay(20, token);
            return true;
        }
        catch { ReleaseLegacyImport(); throw; }
    }

    private void ReleaseLegacyImport()
    {
        _legacyPresenterLease?.Dispose();
        _legacyPresenterLease = null;
        _legacyIndexingLease?.Dispose();
        _legacyIndexingLease = null;
        if (_legacyImportWindowStates is { } states)
            foreach (var (window, enabled) in states) window.IsEnabled = enabled;
        _legacyImportWindowStates = null;
        _legacyImportPreparing = false;
        if (_legacySharedProtectionHeld) ReleaseStoreUpdateProtection();
        _legacySharedProtectionHeld = false;
        RequestAutomaticIndexing();
    }

    private async Task RecoverLegacyImportWhileHeldAsync()
    {
        if (!_legacySharedProtectionHeld || _services is null || _queue is null || _capturePaths is null)
            throw new InvalidOperationException("Legacy import protection is unavailable.");
        await _services.GetRequiredService<ILegacyLibraryMigrationService>()
            .RecoverPendingAsync(_capturePaths, CancellationToken.None);
        using (_queue.SuspendEviction()) _queue.Load();
    }

    internal static bool ShouldOfferLegacyImport(bool packaged, bool quietLaunch, bool alreadyOffered) =>
        packaged && !quietLaunch && !alreadyOffered;

    private async Task OfferLegacyImportAfterLaunchAsync(bool quietLaunch)
    {
        if (_services is null || _settings is null) return;
        AppPaths paths = _services.GetRequiredService<AppPaths>();
        string marker = Path.Combine(paths.DataRoot, "legacy-import-offer.json");
        if (!ShouldOfferLegacyImport(AppPackage.IsPackaged, quietLaunch, File.Exists(marker))) return;
        try
        {
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask);
            // Preserve the first-run contract: finish the existing welcome before a new offer.
            while (Windows.Cast<Window>().OfType<WelcomeWindow>().Any(window => window.IsVisible)) await Task.Delay(100);
            LegacyMigrationScan scan = await _services.GetRequiredService<ILegacyLibraryMigrationService>()
                .ScanAsync(DefaultLegacyDataRoot, _capturePaths ?? paths);
            if (scan.Status != LegacyMigrationScanStatus.Ready) return;
            SettingsWindow? settings = EnsureSettingsWindow();
            if (settings is null || _storeInstallationPending || _preparingExit) return;
            // This records an offered dialog, not successful import; Settings always keeps its entry.
            if (!AtomicFile.TryWriteNewExportBytes(marker, Encoding.UTF8.GetBytes("{\"offered\":true}"))) return;
            settings.ShowSettings();
            settings.OpenLegacyImport();
        }
        catch (Exception error) { _log?.LogWarning(error, "Could not offer legacy data import"); }
    }
}
