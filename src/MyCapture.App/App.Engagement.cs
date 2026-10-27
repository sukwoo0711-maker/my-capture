using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

public partial class App
{
    private readonly Dictionary<Window, IEngagementNotice> _engagementNotices = [];
    private Window? ActiveEngagementWindow() => _engagementNotices.Keys.FirstOrDefault(window => window.IsActive && window.IsVisible);

    /// <summary>Clears the settings window's Apply row.</summary>
    private const double SettingsFooterInset = 76;

    private void ConfigureEngagementWindow(Window window)
    {
        if (window.Content is not UIElement) return;
        // The library shows its notices on the status bar; the settings window keeps the card.
        IEngagementNotice notice = window is GalleryWindow library ? new LibraryEngagementNotice(library) : new EngagementNotice();
        notice.ReviewChosen = () => _storeRating?.AcceptInvitation();
        notice.UpdateChosen = () => _ = InstallKnownUpdateAsync(window);
        notice.ClosedByUser = kind =>
        {
            if (kind == EngagementNoticeKind.UpdateAvailable) _updates?.DismissAvailable();
            if (kind == EngagementNoticeKind.UpdateCompleted && _settings is not null)
            {
                _settings.StoreUpdate.CompletionNoticeVersion = null;
                SaveEngagementState();
            }
        };
        // An overlay, so nothing under the pointer moves when it appears. It stays until closed
        // or acted on; leaving the window does not take it away.
        if (notice is EngagementNotice card) EngagementNoticeHost.Attach(window, card, SettingsFooterInset);
        _engagementNotices.Add(window, notice);
        // A return counts once the window has stayed active for a moment, never at the instant a
        // click activates it, so the card cannot land under that click.
        _ = EngagementNoticeHost.WatchReturns(window, () => OnEngagementReturn(window));
        window.Closed += (_, _) => _engagementNotices.Remove(window);
        if (window is SettingsWindow settings)
        {
            settings.PrepareStoreUpdate = PrepareStoreUpdateSafelyAsync;
            settings.FinishStoreUpdate = ReleaseStoreUpdateProtection;
            if (_updates is not null) settings.ConfigureUpdates(_updates);
        }
    }

    private void OnEngagementReturn(Window window)
    {
        if (!window.IsActive || !window.IsVisible || IsStoreRatingDeferred()) return;
        if (ShowPendingEngagement(window)) return;
        _storeRating?.NotifyUserReturned();
    }

    private void ShowStoreInvitation()
    {
        if (ActiveEngagementWindow() is { } window && !ShowPendingEngagement(window))
        {
            HideOtherEngagementNotices(window);
            _engagementNotices[window].ShowInvitation();
        }
    }

    private void InitializeStoreUpdateObservation()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        bool owned = UpdateChannelResolver.Resolve(AppPackage.IsPackaged, AppContext.BaseDirectory) == UpdateChannel.GitHubInstall;
        if (_settings?.StoreUpdate.ObserveLaunch(version, AppPackage.IsPackaged, owned) == true) SaveEngagementState();
    }

    private void SaveEngagementState()
    {
        if (_services is null || _settings is null || _settings.General.IsFirstRun) return;
        try { _services.GetRequiredService<SettingsStore>().Save(_settings); }
        catch (Exception error) { _log?.LogError(error, "Could not persist engagement state"); }
    }

    // The shutdown worker supplies the shared save/discard/cancel gate during integration.
    internal Func<Task<bool>>? PrepareStoreUpdateProtection { get; set; }
    private Task<bool> PrepareStoreUpdateSafelyAsync() => PrepareStoreUpdateProtection?.Invoke() ?? Task.FromResult(false);

    private bool _storeInstallationPending;
    private bool _galleryEnabledBeforeUpdate;

    private async Task<bool> PrepareAndHoldStoreUpdateAsync()
    {
        if (!await PrepareAndHoldAsync(PrepareForStoreUpdateAsync)) return false;
        _pinSession?.SaveNow();
        return true;
    }

    /// <summary>The import holds the same protection, but asks with its own wording.</summary>
    private Task<bool> PrepareAndHoldForLegacyImportAsync() =>
        PrepareAndHoldAsync(() => PrepareForAppExitAsync(AppExitReason.LegacyImport));

    private async Task<bool> PrepareAndHoldAsync(Func<Task<bool>> prepare)
    {
        if (_storeInstallationPending) return false;
        _storeInstallationPending = true;
        _galleryEnabledBeforeUpdate = _galleryWindow?.IsEnabled == true;
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = false;
        try
        {
            if (await prepare()) return true;
            ReleaseStoreUpdateProtection();
            return false;
        }
        catch { ReleaseStoreUpdateProtection(); throw; }
    }

    private void ReleaseStoreUpdateProtection()
    {
        _storeInstallationPending = false;
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = _galleryEnabledBeforeUpdate;
        RequestLibraryMaintenance();
    }
}
