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
        UpdateDescription.Text = UpdateStrings.Description;
        UpdateVersionLabel.Text = UpdateStrings.Current(CurrentUpdateVersion);
        UpdateAction.Content = UpdateStrings.Check;
        UpdateCancel.Content = UpdateStrings.Cancel;
        AutomationProperties.SetName(UpdateAction, UpdateStrings.Check);
        AutomationProperties.SetName(UpdateCancel, UpdateStrings.Cancel);
    }

    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (_updates.IsBusy || _installingUpdate) return;
        if (_stagedUpdate is not null)
        {
            if (!UpdateInstaller.SupportsCurrentInstallation)
            {
                UpdateStatus.Text = UpdateStrings.UnsupportedLocation;
                return;
            }
            if (_settingsEdited || CanExitForUpdate?.Invoke() != true || ExitForUpdate is null)
            {
                UpdateStatus.Text = UpdateStrings.Busy;
                return;
            }
            _installingUpdate = true;
            UpdateAction.IsEnabled = false;
            UpdateStatus.Text = UpdateStrings.Starting;
            try
            {
                bool launched = await new UpdateInstaller().InstallAsync(_stagedUpdate,
                    () => !_settingsEdited && CanExitForUpdate?.Invoke() == true,
                    ExitForUpdate, CancellationToken.None);
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
                UpdateAction.Content = UpdateStrings.Check;
                UpdateAction.IsEnabled = true;
            }
            return;
        }
        UpdateAction.IsEnabled = false;
        UpdateCancel.IsEnabled = true;
        UpdateStatus.Text = UpdateStrings.Checking;
        UpdateDownloadProgress.IsIndeterminate = true;
        int generation = ++_updateProgressGeneration;
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
            _stagedUpdate = result.VerifiedPackage;
            UpdateStatus.Text = result.Succeeded ? UpdateStrings.Ready : UpdateStrings.Error(result.ErrorKind);
            UpdateAction.Content = result.Succeeded ? UpdateStrings.Install : UpdateStrings.Check;
            AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
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
