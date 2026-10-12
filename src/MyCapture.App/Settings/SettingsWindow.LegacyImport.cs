using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    internal Func<Window, LegacyImportSelection?>? RunLegacyImport { get; set; }
    private SettingsDraft? _legacySettingsDraft;
    private AppSettings? _legacyNormalizedBaseline;
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
            PreserveLegacyTargetSettings(imported, current);
            _draft.ReplaceValues(imported);
            _legacySettingsDraft = _draft;
            _legacyNormalizedBaseline = _draft.HasErrors ? null : _draft.ToAppSettings();
            SetStatusMessage(UiText.Get("LegacyImport.SettingsReady"));
            RefreshErrorSummary();
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Could not prepare imported legacy settings draft");
            SetStatusMessage(UiText.Format("LegacyImport.SettingsFailed", ThemedDialog.RecoveryMessage(error)));
        }
    }

    internal AppSettings LegacyApplyCandidate(AppSettings candidate)
    {
        if (ReferenceEquals(_legacySettingsDraft, _draft) && _legacyNormalizedBaseline is { } baseline)
        {
            AppSettings current = _currentSettings();
            QueueSettings exactQueue = current.DeepClone().Queue;
            // Each scalar preserves its exact live value only while the user leaves it unchanged.
            foreach (var property in typeof(QueueSettings).GetProperties())
                if (Equals(property.GetValue(candidate.Queue), property.GetValue(baseline.Queue)))
                    property.SetValue(candidate.Queue, property.GetValue(exactQueue));
            if (candidate.Export.QuickSaveDirectoryOverride == baseline.Export.QuickSaveDirectoryOverride)
                candidate.Export.QuickSaveDirectoryOverride = current.Export.QuickSaveDirectoryOverride;
            if (candidate.GitHub.Token == baseline.GitHub.Token) candidate.GitHub.Token = current.GitHub.Token;
            if (candidate.General.LaunchAtLogin == baseline.General.LaunchAtLogin) candidate.General.LaunchAtLogin = current.General.LaunchAtLogin;
            candidate.StoreRating = current.StoreRating;
            candidate.StoreUpdate = current.StoreUpdate;
            candidate.General.IsFirstRun = current.General.IsFirstRun;
            candidate.Capture.StartGuideShownCount = current.Capture.StartGuideShownCount;
        }
        return candidate;
    }

    private static void PreserveLegacyTargetSettings(AppSettings candidate, AppSettings current)
    {
        // Keep exact queue limits too: the editable GiB display rounds values for normal drafts.
        candidate.Queue = current.DeepClone().Queue;
        candidate.Export.QuickSaveDirectoryOverride = current.Export.QuickSaveDirectoryOverride;
        candidate.StoreRating = current.StoreRating;
        candidate.StoreUpdate = current.StoreUpdate;
        candidate.GitHub.Token = current.GitHub.Token;
        candidate.General.IsFirstRun = current.General.IsFirstRun;
        candidate.General.LaunchAtLogin = current.General.LaunchAtLogin;
        candidate.Capture.StartGuideShownCount = current.Capture.StartGuideShownCount;
    }
}
