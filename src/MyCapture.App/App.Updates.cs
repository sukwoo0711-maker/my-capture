using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

public partial class App
{
    private AppUpdateCoordinator? _updates;
    private UpdateCheckScheduler? _updateScheduler;
    private DispatcherTimer? _updateTimer;

    private void InitializeUpdates()
    {
        if (_settings is null || _log is null) return;
        UpdateChannel channel = UpdateChannelResolver.Resolve(AppPackage.IsPackaged, AppContext.BaseDirectory);
        if (channel == UpdateChannel.None) return;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCapture", "Updates");
        // Finish cleanup before the first download can create a new session in this process.
        Task cleanup = Task.Run(() =>
        {
            try { UpdateSessionCleanup.Cleanup(root); }
            catch (Exception error) { _log?.LogWarning(error, "Could not remove stale update sessions"); }
        });
        _updates = new AppUpdateCoordinator(channel,
            UpdateVersion.FromVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)),
            () => _settings!.Updates, SaveEngagementState, PrepareStoreUpdateSafelyAsync,
            ReleaseStoreUpdateProtection, ExitAfterUpdateHandoff, _log, AppContext.BaseDirectory, root,
            startupCleanup: cleanup);
        _updates.Changed += RefreshUpdateNotice;
        _updateScheduler = new UpdateCheckScheduler(() => _settings!.Updates, channel, () => DateTimeOffset.UtcNow);
        _updateTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += async (_, _) =>
        {
            await _updateScheduler.TryCheckAsync(() => IsStoreRatingDeferred() || _updates.IsBusy,
                () => _updates.CheckAsync(ActiveEngagementWindow(), automatic: true), SaveEngagementState,
                error => _log?.LogWarning(error, "Automatic update check failed"));
            RefreshUpdateNotice();
        };
        _updateTimer.Start();
    }

    private void ExitAfterUpdateHandoff()
    {
        // The shared gate already saved editors and pins; use the normal WPF shutdown lifecycle.
        SavePinSessionForExit();
        _shutdownRequested = true;
        Shutdown(0);
    }

    private async Task InstallKnownUpdateAsync(Window? owner = null)
    {
        if (_updates is null || _updates.IsBusy) return;
        owner ??= ActiveEngagementWindow();
        if (owner is null)
        {
            HandleSettingsRequested();
            owner = ActiveEngagementWindow();
        }
        if (owner is not null) await _updates.InstallAsync(owner);
    }

    private void RefreshUpdateNotice()
    {
        if (ActiveEngagementWindow() is { } window && (!IsStoreRatingDeferred() || _updates?.ShowInstallationNotice == true))
            ShowPendingEngagement(window);
    }

    private bool ShowPendingEngagement(Window window)
    {
        if (!_engagementNotices.TryGetValue(window, out var notice)) return false;
        if (_updates is { ShowInstallationNotice: true } installation)
        {
            HideOtherEngagementNotices(window);
            // Explicit installation owns feedback until it finishes or the user closes it.
            // A still-pending completion notice remains available on the next return.
            if (notice.Kind == EngagementNoticeKind.UpdateCompleted) notice.Hide();
            notice.ShowUpdateAvailable(installation.AvailableVersion, installation.IsBusy,
                installation.StatusText ?? SettingsWindow.StoreUpdateStatusText(installation.Snapshot.Phase));
            return true;
        }
        if (_settings?.StoreUpdate.CompletionNoticeVersion is { } complete)
        {
            HideOtherEngagementNotices(window);
            // Shown once, and consumed only when it really was shown: a surface that has to wait (the library
            // behind a result) consumes it through ClosedByUser when it appears. The next return moves on to
            // an available update or the invitation.
            if (notice.ShowUpdateCompleted(complete))
            {
                _settings.StoreUpdate.CompletionNoticeVersion = null;
                SaveEngagementState();
            }
            return true;
        }
        if (_updates is { HasAvailableUpdate: true } updates
            && _settings?.Updates.DismissedVersion != updates.AvailableKey)
        {
            HideOtherEngagementNotices(window);
            notice.ShowUpdateAvailable(updates.AvailableVersion, updates.IsBusy);
            return true;
        }
        if (notice.Kind == EngagementNoticeKind.UpdateAvailable) notice.Hide();
        return notice.IsShown;
    }

    private void HideOtherEngagementNotices(Window active)
    {
        foreach (var (window, notice) in _engagementNotices)
            if (window != active) notice.Hide();
    }
}
