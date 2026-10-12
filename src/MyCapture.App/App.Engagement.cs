using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

public partial class App
{
    private readonly Dictionary<Window, EngagementNotice> _engagementNotices = [];
    private Window? ActiveEngagementWindow() => _engagementNotices.Keys.FirstOrDefault(window => window.IsActive && window.IsVisible);

    private void ConfigureEngagementWindow(Window window)
    {
        if (window.Content is not UIElement existing) return;
        var notice = new EngagementNotice { ReviewChosen = () => _storeRating?.AcceptInvitation() };
        window.Content = null;
        var host = new DockPanel(); DockPanel.SetDock(notice, Dock.Top);
        host.Children.Add(notice); host.Children.Add(existing); window.Content = host;
        _engagementNotices.Add(window, notice);
        // Activation can also come from capture restoration. Only input establishes user presence.
        window.PreviewMouseDown += (_, _) => OnEngagementReturn(window);
        window.PreviewKeyDown += (_, _) => OnEngagementReturn(window);
        window.Deactivated += (_, _) => notice.Visibility = Visibility.Collapsed;
        window.Closed += (_, _) => _engagementNotices.Remove(window);
        if (window is SettingsWindow settings)
        {
            settings.PrepareStoreUpdate = PrepareStoreUpdateSafelyAsync;
            settings.FinishStoreUpdate = ReleaseStoreUpdateProtection;
        }
    }

    private void OnEngagementReturn(Window window)
    {
        if (!window.IsActive || !window.IsVisible || IsStoreRatingDeferred()) return;
        if (_settings?.StoreUpdate.CompletionNoticeVersion is { } version)
        {
            _engagementNotices[window].ShowUpdateCompleted(version);
            _settings.StoreUpdate.CompletionNoticeVersion = null;
            SaveEngagementState();
            return;
        }
        _storeRating?.NotifyUserReturned();
    }

    private void ShowStoreInvitation()
    {
        if (ActiveEngagementWindow() is { } window) _engagementNotices[window].ShowInvitation();
    }

    private void InitializeStoreUpdateObservation()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        if (_settings?.StoreUpdate.ObserveLaunch(version, AppPackage.IsPackaged) == true) SaveEngagementState();
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
        if (_storeInstallationPending) return false;
        _storeInstallationPending = true;
        _galleryEnabledBeforeUpdate = _galleryWindow?.IsEnabled == true;
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = false;
        try
        {
            if (await PrepareForStoreUpdateAsync()) return true;
            ReleaseStoreUpdateProtection();
            return false;
        }
        catch { ReleaseStoreUpdateProtection(); throw; }
    }

    private void ReleaseStoreUpdateProtection()
    {
        _storeInstallationPending = false;
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = _galleryEnabledBeforeUpdate;
    }
}
