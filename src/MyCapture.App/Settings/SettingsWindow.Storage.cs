using MyCapture.App.Themes;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Platform;
using MyCapture.Core.Storage;

namespace MyCapture.App.Settings;

/// <summary>
/// The storage page: the automatic-deletion choice, the capture folder's Open folder button and
/// the note that the Store build's default folder is removed with the app.
/// </summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Opens a folder in File Explorer. Tests replace it so no process starts.</summary>
    internal Action<string> OpenFolderInShell { get; set; } = ExplorerLauncher.OpenFolder;

    /// <summary>The library folder used when the override is empty; tests point it at a fixture.</summary>
    internal Func<string> DefaultCapturesRoot { get; set; } = () => AppPaths.CreateDefault().CapturesRoot;

    private bool _syncingRetention;
    private string _loadedRetentionHours = string.Empty;

    /// <summary>
    /// Shows the window on the storage page with the automatic-deletion choice focused, for the
    /// library's "Change" action after the first automatic deletion.
    /// </summary>
    internal void ShowRetentionSetting() => ShowSetting(nameof(MyCapture.Core.Settings.SettingsDraft.ImageRetentionHours));

    /// <summary>
    /// Keeps the list, the selection and the note on the draft. The list is rebuilt only when a
    /// custom hour count appears or goes, and selecting from code never writes back.
    /// </summary>
    private void RefreshRetention()
    {
        KoreanWordWrap.Show(RetentionSummary, SettingsUxText.RetentionSummary(_draft.ImageRetentionHours));
        IReadOnlyList<RetentionChoice> choices = SettingsUxText.RetentionChoices(_draft.ImageRetentionHours, _loadedRetentionHours);
        _syncingRetention = true;
        try
        {
            if (RetentionChoiceBox.ItemsSource is not IReadOnlyList<RetentionChoice> shown || !shown.SequenceEqual(choices))
                RetentionChoiceBox.ItemsSource = choices;
            RetentionChoiceBox.SelectedValue = SettingsUxText.CanonicalHours(_draft.ImageRetentionHours);
        }
        finally
        {
            _syncingRetention = false;
        }
    }

    private void OnRetentionChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRetention || RetentionChoiceBox.SelectedItem is not RetentionChoice choice) return;
        _draft.ImageRetentionHours = choice.Hours;
    }

    // ---- Capture folder ------------------------------------------------------------------

    /// <summary>The folder the capture box names, or the default one when it is empty.</summary>
    private string CapturesFolderShown()
    {
        string typed = _draft.CapturesDirectoryOverride?.Trim() ?? string.Empty;
        return typed.Length == 0 ? DefaultCapturesRoot() : typed;
    }

    /// <summary>The Store build's default folder lives in the package and is removed with it.</summary>
    private void RefreshCaptureFolderNote()
    {
        bool usesDefault;
        try
        {
            usesDefault = string.Equals(Path.GetFullPath(CapturesFolderShown()), Path.GetFullPath(DefaultCapturesRoot()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            usesDefault = false;
        }

        CaptureFolderDefaultNote.Visibility = usesDefault && AppPackage.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenCapturesDirectory(object sender, RoutedEventArgs e)
    {
        string folder = CapturesFolderShown();
        try
        {
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder))
            {
                SetStatusMessage(UiText.Get("Storage_OpenFolderFailed"));
                return;
            }

            OpenFolderInShell(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or Win32Exception or InvalidOperationException or IOException)
        {
            _log.LogWarning(ex, "Could not open the capture folder");
            SetStatusMessage(UiText.Get("Storage_OpenFolderFailed"));
        }
    }
}

/// <summary>
/// Opens folders in File Explorer by passing the path as one argument, never shell-executing it.
/// Only a fully qualified path to an existing directory is accepted: such a path cannot contain
/// quotes (invalid in Windows names) or start with '/', so it can never become an explorer switch.
/// </summary>
internal static class ExplorerLauncher
{
    internal static void OpenFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("The folder path must be fully qualified.", nameof(folder));
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The folder does not exist.");
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
}
