using Microsoft.Extensions.Logging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using MyCapture.App.Updates;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private readonly UpdateSession _updates = new(new GitHubUpdateService());
    private VerifiedUpdatePackage? _stagedUpdate;
    private UpdateTarget? _updateTarget;
    private bool _installingUpdate;
    private bool _settingsEdited;
    private int _updateProgressGeneration;
    internal Func<bool>? CanExitForUpdate { get; set; }
    internal Action? ExitForUpdate { get; set; }
    private static UpdateVersion CurrentUpdateVersion => UpdateVersion.FromVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private void InitializeUpdates()
    {
        UpdateTab.Header = UpdateStrings.Title;
        UpdateVersionLabel.Text = MyCapture.Core.Platform.AppIdentity.Label + " · " + UpdateStrings.Current(CurrentUpdateVersion);
        UpdateCancel.Content = UpdateStrings.Cancel;
        AutomationProperties.SetName(UpdateCancel, UpdateStrings.Cancel);
        try
        {
            _updateTarget = UpdateTarget.Resolve(AppContext.BaseDirectory, UpdateTarget.DefaultRoot);
            UpdateDescription.Text = UpdateStrings.TargetDescription(_updateTarget);
            ResetUpdateAction();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update target is unsafe");
            UpdateDescription.Text = UpdateStrings.Failed;
            UpdateAction.IsEnabled = false;
        }
    }

    private void ResetUpdateAction()
    {
        UpdateAction.Content = _stagedUpdate is not null ? UpdateStrings.Install :
            _updateTarget?.IsPortableMigration == true ? UpdateStrings.InstallPortable : UpdateStrings.Check;
        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
    }

    private async Task InstallStagedUpdateAsync(int generation)
    {
        if (_stagedUpdate is null || _updateTarget is null || !IsVisible || _allowClose || generation != _updateProgressGeneration) return;
        if (_settingsEdited || CanExitForUpdate?.Invoke() != true || ExitForUpdate is null)
        {
            UpdateStatus.Text = UpdateStrings.Busy;
            ResetUpdateAction();
            return;
        }
        _installingUpdate = true;
        UpdateAction.IsEnabled = false;
        UpdateStatus.Text = UpdateStrings.Starting;
        try
        {
            bool launched = await new UpdateInstaller().InstallAsync(_stagedUpdate,
                () => IsVisible && !_allowClose && generation == _updateProgressGeneration &&
                    !_settingsEdited && CanExitForUpdate?.Invoke() == true,
                ExitForUpdate, CancellationToken.None, _updateTarget);
            if (launched) { _stagedUpdate = null; return; }
            UpdateStatus.Text = UpdateStrings.Busy;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update handoff failed");
            UpdateStatus.Text = UpdateStrings.Failed;
        }
        finally
        {
            _installingUpdate = false;
            _stagedUpdate?.Cleanup();
            _stagedUpdate = null;
            ResetUpdateAction();
            UpdateAction.IsEnabled = true;
        }
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (_updates.IsBusy || _installingUpdate || _updateTarget is null) return;
        int generation = ++_updateProgressGeneration;
        if (_stagedUpdate is not null)
        {
            await InstallStagedUpdateAsync(generation);
            return;
        }
        UpdateAction.IsEnabled = false;
        UpdateCancel.IsEnabled = true;
        UpdateStatus.Text = UpdateStrings.Checking;
        UpdateDownloadProgress.IsIndeterminate = true;
        var progress = new Progress<UpdateProgress>(value =>
        {
            if (!_updates.IsBusy || generation != _updateProgressGeneration) return;
            UpdateStatus.Text = UpdateStrings.Download(value);
            UpdateDownloadProgress.IsIndeterminate = value.Percent is null;
            UpdateDownloadProgress.Value = value.Percent ?? 0;
        });
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCapture", "Updates");
            var result = await _updates.DownloadAsync(CurrentUpdateVersion, root, progress);
            if (result is null) return;
            if (_allowClose || !IsVisible || generation != _updateProgressGeneration)
            {
                result.VerifiedPackage?.Cleanup();
                return;
            }
            _stagedUpdate = result.VerifiedPackage;
            UpdateStatus.Text = result.Succeeded ? UpdateStrings.Ready : UpdateStrings.Error(result.ErrorKind);
            ResetUpdateAction();
            UpdateCancel.IsEnabled = false;
            // The same user action checks, downloads, verifies, installs and restarts. Only
            // unfinished work defers the final step and leaves an explicit retry action.
            if (_stagedUpdate is not null) await InstallStagedUpdateAsync(generation);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update download failed");
            UpdateStatus.Text = UpdateStrings.Failed;
        }
        finally
        {
            UpdateAction.IsEnabled = true;
            UpdateCancel.IsEnabled = false;
            UpdateDownloadProgress.IsIndeterminate = false;
            UpdateDownloadProgress.Value = _stagedUpdate is null ? 0 : 100;
        }
    }
    private void OnUpdateCancel(object sender, RoutedEventArgs e) => _updates.Cancel();
}
