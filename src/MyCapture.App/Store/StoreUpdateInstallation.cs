using System.Windows;

namespace MyCapture.App.Store;

/// <summary>The same save/protection/consent boundary for Settings and the notice card.</summary>
internal static class StoreUpdateInstallation
{
    internal static async Task<StoreUpdatePhase> InstallAsync(Window owner, IStorePackageUpdateService service,
        Func<Task<bool>> prepare, Action release, IProgress<StoreUpdateSnapshot> progress, Func<bool>? canInstall = null)
    {
        bool enabled = owner.IsEnabled;
        if (!await prepare()) return StoreUpdatePhase.Blocked;
        try
        {
            if (canInstall?.Invoke() == false) return StoreUpdatePhase.Blocked;
            owner.IsEnabled = false;
            progress.Report(new(StoreUpdatePhase.AwaitingConsent));
            return await service.InstallAsync(progress);
        }
        finally
        {
            try { release(); }
            finally { owner.IsEnabled = enabled; }
        }
    }
}
