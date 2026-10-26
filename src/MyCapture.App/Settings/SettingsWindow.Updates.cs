using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>
/// The update tab observes the shell's shared update operation. Standalone windows retain
/// the Store service seam so the consent and progress behavior can be exercised in isolation.
/// </summary>
internal sealed partial class SettingsWindow
{
    internal AppUpdateCoordinator? UpdateCoordinator { get; private set; }

    internal void ConfigureUpdates(AppUpdateCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if (UpdateCoordinator is { } previous) previous.Changed -= RefreshUpdates;
        UpdateCoordinator = coordinator;
        coordinator.Changed += RefreshUpdates;
        Closed -= OnUpdatesClosed;
        Closed += OnUpdatesClosed;
        InitializeUpdateChannel(coordinator.Channel);
        // Only builds that check for new versions disclose the daily check.
        PrivacyUpdateText.Visibility = coordinator.Channel == UpdateChannel.None ? Visibility.Collapsed : Visibility.Visible;
        RefreshUpdates();
    }

    private void OnUpdatesClosed(object? sender, EventArgs e)
    {
        if (UpdateCoordinator is { } coordinator) coordinator.Changed -= RefreshUpdates;
        UpdateCoordinator = null;
    }

    private void RefreshUpdates()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshUpdates); return; }
        if (UpdateCoordinator is not { } coordinator || coordinator.Channel == UpdateChannel.None) return;
        _lastStoreCheck = coordinator.LastCheckUtc;
        SetStoreUpdateAction(coordinator.HasAvailableUpdate);
        UpdateAction.IsEnabled = !coordinator.IsBusy;
        ShowStoreUpdate(coordinator.Snapshot);
        if (coordinator.StatusText is { } text) UpdateObservedStatus.Text = text;
        AutomationProperties.SetName(UpdateProgress, UpdateObservedStatus.Text);
    }

    /// <summary>Opens the Store listing. Tests replace it so no process starts.</summary>
    internal StoreUpdateLink StoreLink { get; set; } = new();
    internal IStorePackageUpdateService? StoreUpdates { get; set; }
    internal Func<Task<bool>> PrepareStoreUpdate { get; set; } = () => Task.FromResult(false);
    internal Action FinishStoreUpdate { get; set; } = () => { };
    internal bool StoreUpdatePackaged { get; set; } = AppPackage.IsPackaged;
    private bool _storeUpdateBusy;
    private bool _storeUpdateAvailable;
    private long _storeUpdateOperation;
    private bool _storeUpdateProgressActive;

    /// <summary>When this window last asked the Store for an update; shown beside the result.</summary>
    private DateTimeOffset? _lastStoreCheck;

    /// <summary>The time a finished check is stamped with. Tests pin it.</summary>
    internal Func<DateTimeOffset> StoreCheckClock { get; set; } = () => DateTimeOffset.Now;

    private static UpdateVersion CurrentUpdateVersion => ApplicationVersion.Current;

    private void InitializeUpdates()
    {
        InitializeLegacyImport();
        // The tab is the "About" category (header in XAML); the label carries the version, so
        // name the product without it.
        UpdateVersionLabel.Text = AppIdentity.WindowLabel + " · " + UpdateStrings.Current(CurrentUpdateVersion);
        InitializeUpdateChannel(AppPackage.IsPackaged ? UpdateChannel.Store : UpdateChannel.None);
    }

    private void InitializeUpdateChannel(UpdateChannel channel)
    {
        bool enabled = channel != UpdateChannel.None;
        bool packaged = channel == UpdateChannel.Store;
        AutoCheckUpdatesCheckBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            UpdateDescription.Text = UiText.Get(packaged ? "StoreUpdate_ObservedDescription" : "Update_UnpackagedDescription");
            SetStoreUpdateAction(false);
        }
        else
        {
            // Development output has no release channel and retains its Store listing action.
            UpdateDescription.Text = UiText.Get("StoreUpdate_UnpackagedDescription")
                + Environment.NewLine + Environment.NewLine + UiText.Get("StoreUpdate_UnpackagedNote");
            UpdateAction.Content = UiText.Get("StoreUpdate_OpenStore");
            AutomationProperties.SetHelpText(UpdateAction, UiText.Get("StoreUpdate_OpenStoreHelp"));
        }

        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        StorePageAction.Visibility = enabled && !packaged ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) ShowStoreUpdate(new(StoreUpdatePhase.Idle));
        else
        {
            UpdateObservedStatus.Text = string.Empty;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (UpdateCoordinator is { Channel: not UpdateChannel.None })
        {
            await RunUpdateActionAsync();
            return;
        }
        if (StoreUpdatePackaged)
        {
            await RunStoreUpdateActionAsync();
            return;
        }
        if (StoreLink.Open(_log) is null)
            SetStatusMessage(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri));
    }

    internal async Task RunUpdateActionAsync()
    {
        if (UpdateCoordinator is not { } coordinator) { await RunStoreUpdateActionAsync(); return; }
        if (coordinator.IsBusy || coordinator.Channel == UpdateChannel.None) return;
        SetStatusMessage(string.Empty);
        if (coordinator.HasAvailableUpdate) await coordinator.InstallAsync(this);
        else await coordinator.CheckAsync(this);
    }

    internal async Task RunStoreUpdateActionAsync()
    {
        if (_storeUpdateBusy || !StoreUpdatePackaged) return;
        long operation = ++_storeUpdateOperation;
        _storeUpdateBusy = true; UpdateAction.IsEnabled = false;
        SetStatusMessage(string.Empty);
        try
        {
            StoreUpdates ??= new StorePackageUpdateService(this);
            if (!_storeUpdateAvailable)
            {
                ShowStoreUpdate(new(StoreUpdatePhase.Checking));
                _storeUpdateAvailable = await StoreUpdates.CheckAsync();
                _lastStoreCheck = StoreCheckClock();
                ShowStoreUpdate(new(_storeUpdateAvailable ? StoreUpdatePhase.Available : StoreUpdatePhase.Current));
                SetStoreUpdateAction(_storeUpdateAvailable);
            }
            else
            {
                _storeUpdateProgressActive = true;
                StoreUpdatePhase result = await StoreUpdateInstallation.InstallAsync(this, StoreUpdates,
                    PrepareStoreUpdate, FinishStoreUpdate, new UpdateProgressObserver(this, operation));
                _storeUpdateProgressActive = false;
                if (result == StoreUpdatePhase.Blocked) { ShowStoreUpdate(new(result)); return; }
                _storeUpdateAvailable = false;
                SetStoreUpdateAction(false);
                ShowStoreUpdate(new(result));
            }
        }
        catch (Exception error)
        {
            _storeUpdateProgressActive = false;
            _storeUpdateAvailable = false;
            SetStoreUpdateAction(false);
            _log.LogError(error, "Store update operation failed");
            ShowStoreUpdate(new(StoreUpdatePhase.Failed));
        }
        finally
        {
            _storeUpdateProgressActive = false;
            _storeUpdateBusy = false; UpdateAction.IsEnabled = true;
            AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        }
    }

    private void SetStoreUpdateAction(bool install)
    {
        UpdateAction.Content = UiText.Get(install ? "StoreUpdate_Install" : "StoreUpdate_Check");
        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        string help = UpdateCoordinator?.Channel switch
        {
            UpdateChannel.Portable when install => "Update_OpenReleaseHelp",
            UpdateChannel.GitHubInstall when install => "Update_InstallHelp",
            UpdateChannel.GitHubInstall or UpdateChannel.Portable => "Update_CheckHelp",
            _ => install ? "StoreUpdate_InstallHelp" : "StoreUpdate_CheckHelp",
        };
        AutomationProperties.SetHelpText(UpdateAction, UiText.Get(help));
    }

    private sealed class UpdateProgressObserver(SettingsWindow owner, long operation) : IProgress<StoreUpdateSnapshot>
    {
        public void Report(StoreUpdateSnapshot snapshot)
        {
            void Apply()
            {
                if (owner._storeUpdateProgressActive && owner._storeUpdateOperation == operation)
                    owner.ShowStoreUpdate(snapshot);
            }
            if (owner.Dispatcher.CheckAccess()) Apply();
            else owner.Dispatcher.BeginInvoke((Action)Apply);
        }
    }

    private void ShowStoreUpdate(StoreUpdateSnapshot snapshot)
    {
        UpdateObservedStatus.Text = UpdateCheckSummary(snapshot.Phase, _lastStoreCheck);
        bool githubChannel = UpdateCoordinator?.Channel is UpdateChannel.GitHubInstall or UpdateChannel.Portable;
        bool storeChannel = UpdateCoordinator?.Channel == UpdateChannel.Store || UpdateCoordinator is null && StoreUpdatePackaged;
        StorePageAction.Visibility = githubChannel || storeChannel && snapshot.Phase == StoreUpdatePhase.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = snapshot.Phase is StoreUpdatePhase.Checking or StoreUpdatePhase.AwaitingConsent
            or StoreUpdatePhase.Downloading or StoreUpdatePhase.Installing ? Visibility.Visible : Visibility.Collapsed;
        // TotalDownloadProgress is download progress, not a made-up installation percentage.
        UpdateProgress.IsIndeterminate = snapshot.Phase != StoreUpdatePhase.Downloading || snapshot.Progress is null;
        if (snapshot.Progress is { } value && snapshot.Phase == StoreUpdatePhase.Downloading) UpdateProgress.Value = value * 100;
        AutomationProperties.SetName(UpdateProgress, UpdateObservedStatus.Text);
    }

    /// <summary>
    /// The update line: a check's result carries its time, "마지막 확인 14:02 · 최신 버전"; every
    /// other phase shows its state alone.
    /// </summary>
    internal static string UpdateCheckSummary(StoreUpdatePhase phase, DateTimeOffset? lastCheck)
    {
        if (lastCheck is not { } checkedAt || phase is not (StoreUpdatePhase.Current or StoreUpdatePhase.Available))
            return StoreUpdateStatusText(phase);
        string state = phase == StoreUpdatePhase.Current ? UiText.Get("About_UpToDate") : StoreUpdateStatusText(phase);
        return UiText.Format("About_LastChecked", checkedAt.ToLocalTime().DateTime, state);
    }

    internal static string StoreUpdateStatusText(StoreUpdatePhase phase) => phase switch
    {
        StoreUpdatePhase.Idle => UiText.Get("StoreUpdate_StateIdle"),
        StoreUpdatePhase.Checking => UiText.Get("StoreUpdate_StateChecking"),
        StoreUpdatePhase.Available => UiText.Get("StoreUpdate_StateAvailable"),
        StoreUpdatePhase.Current => UiText.Get("StoreUpdate_StateCurrent"),
        StoreUpdatePhase.AwaitingConsent => UiText.Get("StoreUpdate_StateAwaitingConsent"),
        StoreUpdatePhase.Downloading => UiText.Get("StoreUpdate_StateDownloading"),
        StoreUpdatePhase.Installing => UiText.Get("StoreUpdate_StateInstalling"),
        StoreUpdatePhase.RestartRequired => UiText.Get("StoreUpdate_StateRestartRequired"),
        StoreUpdatePhase.Canceled => UiText.Get("StoreUpdate_StateCanceled"),
        StoreUpdatePhase.Failed => UiText.Get("StoreUpdate_StateFailed"),
        StoreUpdatePhase.Blocked => UiText.Get("StoreUpdate_StateBlocked"),
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private void OnStoreUpdatePage(object sender, RoutedEventArgs e)
    {
        if (StoreLink.Open(_log) is null) SetStatusMessage(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri));
    }
}
