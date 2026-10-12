using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;

namespace MyCapture.App.Settings;

/// <summary>
/// The update tab. MyCapture is distributed only through the Microsoft Store, so every build
/// observes Store deployment in packaged builds and opens the listing in unpackaged builds.
/// </summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Opens the Store listing. Tests replace it so no process starts.</summary>
    internal StoreUpdateLink StoreLink { get; set; } = new();
    internal IStorePackageUpdateService? StoreUpdates { get; set; }
    internal Func<Task<bool>> PrepareStoreUpdate { get; set; } = () => Task.FromResult(false);
    internal Action? PersistStoreUpdateForTest { get; set; }
    internal bool StoreUpdatePackaged { get; set; } = AppPackage.IsPackaged;
    private bool _storeUpdateBusy;
    private bool _storeUpdateAvailable;

    private static UpdateVersion CurrentUpdateVersion => UpdateVersion.FromVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private void InitializeUpdates()
    {
        // The tab is the "About" category (header in XAML); the label carries the version, so
        // name the product without it.
        UpdateVersionLabel.Text = AppIdentity.WindowLabel + " · " + UpdateStrings.Current(CurrentUpdateVersion);
        if (AppPackage.IsPackaged)
        {
            // The Store owns consent, downloading and deployment.
            UpdateDescription.Text = UiText.Get("StoreUpdate_ObservedDescription");
            UpdateAction.Content = UiText.Get("StoreUpdate_Check");
            AutomationProperties.SetHelpText(UpdateAction, UiText.Get("StoreUpdate_CheckHelp"));
        }
        else
        {
            // A development, portable or earlier GitHub install has no update channel of its
            // own any more. It points to the Store version, which keeps its data separately.
            UpdateDescription.Text = UiText.Get("StoreUpdate_UnpackagedDescription")
                + Environment.NewLine + Environment.NewLine + UiText.Get("StoreUpdate_UnpackagedNote");
            UpdateAction.Content = UiText.Get("StoreUpdate_OpenStore");
            AutomationProperties.SetHelpText(UpdateAction, UiText.Get("StoreUpdate_OpenStoreHelp"));
        }

        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        StorePageAction.Visibility = AppPackage.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        if (AppPackage.IsPackaged) ShowStoreUpdate(new(StoreUpdatePhase.Idle));
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (StoreUpdatePackaged)
        {
            await RunStoreUpdateActionAsync();
            return;
        }
        if (StoreLink.Open(_log) is null)
            SetStatusMessage(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri));
    }

    internal async Task RunStoreUpdateActionAsync()
    {
        if (_storeUpdateBusy || !StoreUpdatePackaged) return;
        _storeUpdateBusy = true; UpdateAction.IsEnabled = false;
        try
        {
            StoreUpdates ??= new StorePackageUpdateService(this);
            if (!_storeUpdateAvailable)
            {
                ShowStoreUpdate(new(StoreUpdatePhase.Checking));
                _storeUpdateAvailable = await StoreUpdates.CheckAsync();
                ShowStoreUpdate(new(_storeUpdateAvailable ? StoreUpdatePhase.Available : StoreUpdatePhase.Current));
                UpdateAction.Content = UiText.Get(_storeUpdateAvailable ? "StoreUpdate_Install" : "StoreUpdate_Check");
            }
            else
            {
                if (!await PrepareStoreUpdate()) { ShowStoreUpdate(new(StoreUpdatePhase.Blocked)); return; }
                _currentSettings().StoreUpdate.PendingFromVersion = CurrentUpdateVersion.ToString();
                PersistUpdateObservation();
                ShowStoreUpdate(new(StoreUpdatePhase.AwaitingConsent));
                StoreUpdatePhase result = await StoreUpdates.InstallAsync(new UpdateProgressObserver(this));
                if (result != StoreUpdatePhase.RestartRequired)
                {
                    _currentSettings().StoreUpdate.PendingFromVersion = null;
                    PersistUpdateObservation();
                }
                _storeUpdateAvailable = false;
                UpdateAction.Content = UiText.Get("StoreUpdate_Check");
                ShowStoreUpdate(new(result));
            }
        }
        catch (Exception error)
        {
            _log.LogError(error, "Store update operation failed");
            ShowStoreUpdate(new(StoreUpdatePhase.Failed));
        }
        finally
        {
            _storeUpdateBusy = false; UpdateAction.IsEnabled = true;
            AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        }
    }

    private sealed class UpdateProgressObserver(SettingsWindow owner) : IProgress<StoreUpdateSnapshot>
    {
        public void Report(StoreUpdateSnapshot snapshot)
        {
            void Apply() { if (owner._storeUpdateBusy) owner.ShowStoreUpdate(snapshot); }
            if (owner.Dispatcher.CheckAccess()) Apply();
            else owner.Dispatcher.BeginInvoke((Action)Apply);
        }
    }

    private void PersistUpdateObservation()
    {
        if (PersistStoreUpdateForTest is { } persist) { persist(); return; }
        (_settingsStore?.Invoke() ?? throw new InvalidOperationException("Store update state storage is unavailable."))
            .Save(_currentSettings());
    }

    private void ShowStoreUpdate(StoreUpdateSnapshot snapshot)
    {
        UpdateObservedStatus.Text = UiText.Get("StoreUpdate_State" + snapshot.Phase);
        UpdateProgress.Visibility = snapshot.Phase is StoreUpdatePhase.Checking or StoreUpdatePhase.AwaitingConsent
            or StoreUpdatePhase.Downloading or StoreUpdatePhase.Installing ? Visibility.Visible : Visibility.Collapsed;
        // TotalDownloadProgress is download progress, not a made-up installation percentage.
        UpdateProgress.IsIndeterminate = snapshot.Phase != StoreUpdatePhase.Downloading || snapshot.Progress is null;
        if (snapshot.Progress is { } value && snapshot.Phase == StoreUpdatePhase.Downloading) UpdateProgress.Value = value * 100;
        AutomationProperties.SetName(UpdateProgress, UpdateObservedStatus.Text);
    }

    private void OnStoreUpdatePage(object sender, RoutedEventArgs e)
    {
        if (StoreLink.Open(_log) is null) SetStatusMessage(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri));
    }
}
