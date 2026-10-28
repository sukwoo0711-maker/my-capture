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
    private Dictionary<string, object?>? _legacyProtectedBaseline;
    private static readonly (string Queue, string Draft)[] LegacyQueueFields =
    [
        (nameof(QueueSettings.MaxItems), nameof(SettingsDraft.MaxItems)),
        (nameof(QueueSettings.MaxBytes), nameof(SettingsDraft.MaxGiB)),
        (nameof(QueueSettings.CapturesDirectoryOverride), nameof(SettingsDraft.CapturesDirectoryOverride)),
        (nameof(QueueSettings.ThumbnailLongEdge), nameof(SettingsDraft.ThumbnailLongEdge)),
        (nameof(QueueSettings.ImageRetentionHours), nameof(SettingsDraft.ImageRetentionHours)),
        (nameof(QueueSettings.FolderFilePrefix), nameof(SettingsDraft.FolderFilePrefix)),
        (nameof(QueueSettings.UseCurrentFolderForNewCaptures), nameof(SettingsDraft.UseCurrentFolderForNewCaptures)),
        (nameof(QueueSettings.DefaultCaptureFolderId), nameof(SettingsDraft.UseCurrentFolderForNewCaptures)),
    ];
    private bool _legacyImportPackaged = AppPackage.IsPackaged;
    internal bool LegacyImportPackaged
    {
        get => _legacyImportPackaged;
        set { _legacyImportPackaged = value; InitializeLegacyImport(); }
    }

    private void InitializeLegacyImport()
    {
        Visibility shown = _legacyImportPackaged ? Visibility.Visible : Visibility.Collapsed;
        LegacyImportPanel.Visibility = shown;
        LegacyImportAction.Visibility = shown;
    }

    private void OnLegacyImport(object sender, RoutedEventArgs e) => OpenLegacyImport();

    internal void OpenLegacyImport()
    {
        if (!LegacyImportPackaged || RunLegacyImport is null) return;
        LegacyImportSelection? selection = RunLegacyImport(this);
        if (selection?.Library.Status != MyCapture.Core.Storage.LegacyMigrationStatus.Completed) return;
        if (selection.IncludeSettings && selection.Library.SettingsBackupPath is { } settingsPath)
            ImportLegacySettingsDraft(settingsPath);
        else SetStatusMessage(selection.CompletedAfterCancel
            ? LegacyDataImportWindow.CompletedBeforeCancelText(selection.Library)
            : LegacyDataImportWindow.CompletedText(selection.Library));
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
            // Capture only protected UI fields; unrelated invalid hotkeys cannot erase this baseline.
            _legacyProtectedBaseline = LegacyQueueFields.Select(field => field.Draft)
                .Concat([nameof(SettingsDraft.QuickSaveDirectoryOverride), nameof(SettingsDraft.GitHubToken),
                    nameof(SettingsDraft.GitHubIssueUrl), nameof(SettingsDraft.LaunchAtLogin)])
                .Distinct().ToDictionary(name => name, name => typeof(SettingsDraft).GetProperty(name)!.GetValue(_draft));
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
        if (ReferenceEquals(_legacySettingsDraft, _draft) && _legacyProtectedBaseline is { } baseline)
        {
            AppSettings current = _currentSettings();
            QueueSettings exactQueue = current.DeepClone().Queue;
            // Each scalar preserves its exact live value only while the user leaves it unchanged.
            bool Unchanged(string name) => Equals(typeof(SettingsDraft).GetProperty(name)!.GetValue(_draft), baseline[name]);
            foreach (var (queueProperty, draftProperty) in LegacyQueueFields)
                if (Unchanged(draftProperty))
                {
                    var property = typeof(QueueSettings).GetProperty(queueProperty)!;
                    property.SetValue(candidate.Queue, property.GetValue(exactQueue));
                }
            if (Unchanged(nameof(SettingsDraft.QuickSaveDirectoryOverride)))
                candidate.Export.QuickSaveDirectoryOverride = current.Export.QuickSaveDirectoryOverride;
            // The token and the issue it uploads to stay a pair: an imported issue address never
            // silently receives screenshots under the current account's token.
            if (Unchanged(nameof(SettingsDraft.GitHubToken))) candidate.GitHub.Token = current.GitHub.Token;
            if (Unchanged(nameof(SettingsDraft.GitHubIssueUrl))) candidate.GitHub.IssueUrl = current.GitHub.IssueUrl;
            if (Unchanged(nameof(SettingsDraft.LaunchAtLogin))) candidate.General.LaunchAtLogin = current.General.LaunchAtLogin;
            candidate.StoreRating = current.StoreRating;
            candidate.StoreUpdate = current.StoreUpdate;
            candidate.Updates.LastCheckUtc = current.Updates.LastCheckUtc;
            candidate.Updates.DismissedVersion = current.Updates.DismissedVersion;
            candidate.General.IsFirstRun = current.General.IsFirstRun;
            candidate.Hotkeys.CoreShortcutsAsked = current.Hotkeys.CoreShortcutsAsked;
            candidate.Capture.StartGuideShownCount = current.Capture.StartGuideShownCount;
            candidate.Capture.KeyPanelShownCount = current.Capture.KeyPanelShownCount;
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
        candidate.Updates.LastCheckUtc = current.Updates.LastCheckUtc;
        candidate.Updates.DismissedVersion = current.Updates.DismissedVersion;
        candidate.GitHub.Token = current.GitHub.Token;
        candidate.GitHub.IssueUrl = current.GitHub.IssueUrl;
        candidate.General.IsFirstRun = current.General.IsFirstRun;
        candidate.Hotkeys.CoreShortcutsAsked = current.Hotkeys.CoreShortcutsAsked;
        candidate.General.LaunchAtLogin = current.General.LaunchAtLogin;
        candidate.Capture.StartGuideShownCount = current.Capture.StartGuideShownCount;
        candidate.Capture.KeyPanelShownCount = current.Capture.KeyPanelShownCount;
    }
}
