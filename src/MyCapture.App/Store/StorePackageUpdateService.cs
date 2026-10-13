using System.Windows;
using System.Windows.Interop;
using Windows.Services.Store;

namespace MyCapture.App.Store;

internal enum StoreUpdatePhase { Idle, Checking, Available, Current, AwaitingConsent, Downloading, Installing, RestartRequired, Canceled, Failed, Blocked }
internal readonly record struct StoreUpdateSnapshot(StoreUpdatePhase Phase, double? Progress = null);

internal interface IStorePackageUpdateService
{
    Task<bool> CheckAsync();
    Task<StoreUpdatePhase> InstallAsync(IProgress<StoreUpdateSnapshot> progress);
}

/// <summary>Only the Store consent API deploys packages; no installer, elevation or silent capability.</summary>
internal sealed class StorePackageUpdateService(Window owner) : IStorePackageUpdateService
{
    private StoreContext? _context;
    private IReadOnlyList<StorePackageUpdate> _updates = [];
    private StoreContext Context
    {
        get
        {
            owner.Dispatcher.VerifyAccess();
            if (_context is not null) return _context;
            _context = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(_context, new WindowInteropHelper(owner).EnsureHandle());
            return _context;
        }
    }
    public async Task<bool> CheckAsync()
    {
        _updates = await Context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask();
        return _updates.Count > 0;
    }
    public async Task<StoreUpdatePhase> InstallAsync(IProgress<StoreUpdateSnapshot> progress)
    {
        if (_updates.Count == 0) throw new InvalidOperationException("Check Store availability before requesting an update.");
        var operation = Context.RequestDownloadAndInstallStorePackageUpdatesAsync(_updates);
        operation.Progress = (_, status) =>
        {
            StoreUpdatePhase? phase = status.PackageUpdateState switch
            {
                StorePackageUpdateState.Downloading => StoreUpdatePhase.Downloading,
                StorePackageUpdateState.Deploying => StoreUpdatePhase.Installing,
                _ => null,
            };
            if (phase is { } value)
                progress.Report(new(value, double.IsFinite(status.TotalDownloadProgress)
                    ? Math.Clamp(status.TotalDownloadProgress, 0, 1) : null));
        };
        StorePackageUpdateResult result = await operation.AsTask();
        return result.OverallState switch
        {
            StorePackageUpdateState.Completed => StoreUpdatePhase.RestartRequired,
            StorePackageUpdateState.Canceled => StoreUpdatePhase.Canceled,
            _ => StoreUpdatePhase.Failed,
        };
    }
}
