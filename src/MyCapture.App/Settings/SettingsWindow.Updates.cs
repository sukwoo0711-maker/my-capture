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

    private static UpdateVersion CurrentUpdateVersion => UpdateVersion.FromVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private void InitializeUpdates()
    {
        InitializeLegacyImport();
        // The tab is the "About" category (header in XAML); the label carries the version, so
        // name the product without it.
        UpdateVersionLabel.Text = AppIdentity.WindowLabel + " · " + UpdateStrings.Current(CurrentUpdateVersion);
        if (AppPackage.IsPackaged)
        {
            // The Store owns consent, downloading and deployment.
            UpdateDescription.Text = UiText.Get("StoreUpdate_ObservedDescription");
            SetStoreUpdateAction(false);
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
        // The listing button repeats what Check and Rate already open; it appears only after a
        // failed check, as the way to reach the Store anyway.
        StorePageAction.Visibility = Visibility.Collapsed;
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
        long operation = ++_storeUpdateOperation;
        _storeUpdateBusy = true; UpdateAction.IsEnabled = false;
        SetStatusMessage(string.Empty);
        bool protectionAcquired = false;
        bool enabledBeforeInstallation = IsEnabled;
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
                if (!await PrepareStoreUpdate()) { ShowStoreUpdate(new(StoreUpdatePhase.Blocked)); return; }
                protectionAcquired = true;
                enabledBeforeInstallation = IsEnabled;
                IsEnabled = false;
                ShowStoreUpdate(new(StoreUpdatePhase.AwaitingConsent));
                _storeUpdateProgressActive = true;
                StoreUpdatePhase result = await StoreUpdates.InstallAsync(new UpdateProgressObserver(this, operation));
                _storeUpdateProgressActive = false;
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
            try { if (protectionAcquired) FinishStoreUpdate(); }
            finally
            {
                if (protectionAcquired) IsEnabled = enabledBeforeInstallation;
                _storeUpdateBusy = false; UpdateAction.IsEnabled = true;
                AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
            }
        }
    }

    private void SetStoreUpdateAction(bool install)
    {
        UpdateAction.Content = UiText.Get(install ? "StoreUpdate_Install" : "StoreUpdate_Check");
        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
        AutomationProperties.SetHelpText(UpdateAction, UiText.Get(install ? "StoreUpdate_InstallHelp" : "StoreUpdate_CheckHelp"));
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
        StorePageAction.Visibility = StoreUpdatePackaged && snapshot.Phase == StoreUpdatePhase.Failed
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
