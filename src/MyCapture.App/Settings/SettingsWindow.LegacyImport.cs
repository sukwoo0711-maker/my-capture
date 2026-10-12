using System.Windows;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    internal Func<Window, LegacyImportSelection?>? RunLegacyImport { get; set; }
    private bool _legacyImportPackaged = AppPackage.IsPackaged;
    internal bool LegacyImportPackaged
    {
        get => _legacyImportPackaged;
        set { _legacyImportPackaged = value; InitializeLegacyImport(); }
    }

    private void InitializeLegacyImport() => LegacyImportAction.Visibility =
        _legacyImportPackaged ? Visibility.Visible : Visibility.Collapsed;

    private void OnLegacyImport(object sender, RoutedEventArgs e) => OpenLegacyImport();

    internal void OpenLegacyImport()
    {
        if (!LegacyImportPackaged || RunLegacyImport is null) return;
        LegacyImportSelection? selection = RunLegacyImport(this);
        if (selection?.Library.Status != MyCapture.Core.Storage.LegacyMigrationStatus.Completed) return;
        if (selection.IncludeSettings && selection.Library.SettingsBackupPath is { } settingsPath)
            ImportLegacySettingsDraft(settingsPath);
        else SetStatusMessage(UiText.Format("LegacyImport.Completed", selection.Library.ImportedCount, selection.Library.SkippedCount));
    }

    internal void ImportLegacySettingsDraft(string settingsPath)
    {
        if (_settingsStore is null) return;
        try
        {
            AppSettings current = _currentSettings();
            AppSettings imported = _settingsStore().ImportFrom(settingsPath);
            // Library destinations and retention policy remain owned by the current Store library.
            imported.Queue = current.DeepClone().Queue;
            imported.Export.QuickSaveDirectoryOverride = current.Export.QuickSaveDirectoryOverride;
            imported.StoreRating = current.StoreRating;
            imported.StoreUpdate = current.StoreUpdate;
            imported.GitHub.Token = current.GitHub.Token;
            imported.General.IsFirstRun = current.General.IsFirstRun;
            imported.General.LaunchAtLogin = current.General.LaunchAtLogin;
            imported.Capture.StartGuideShownCount = current.Capture.StartGuideShownCount;
            _draft.ReplaceValues(imported);
            SetStatusMessage(UiText.Get("LegacyImport.SettingsReady"));
            RefreshErrorSummary();
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Could not prepare imported legacy settings draft");
            SetStatusMessage(UiText.Format("LegacyImport.SettingsFailed", ThemedDialog.RecoveryMessage(error)));
        }
    }
}
