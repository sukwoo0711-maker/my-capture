using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using MyCapture.Core.Capture;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private void OnOpenSourcePageClick(object sender, RoutedEventArgs e)
    {
        string url = SourceUrlForAction(sender);
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { LibraryActionStatus.Text = UiText.Get("Library.SourcePage.OpenFailed"); }
    }

    private void OnCopySourcePageClick(object sender, RoutedEventArgs e)
    {
        string url = SourceUrlForAction(sender);
        if (url.Length == 0) return;
        try
        {
            Clipboard.SetText(url);
            LibraryActionStatus.Text = UiText.Get("Library.SourcePage.Copied");
        }
        catch (ExternalException)
        { LibraryActionStatus.Text = UiText.Get("Library.SourcePage.CopyFailed"); }
    }

    private static string SourceUrlForAction(object sender) =>
        (sender as FrameworkElement)?.DataContext is GalleryItemViewModel tile
            ? tile.SourcePageUrl : string.Empty;

    private async void OnOpenSourceFolderClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        bool document = (sender as FrameworkElement)?.DataContext is GalleryItemViewModel { IsSourceDocumentFile: true };
        // A disconnected network share can take seconds to answer; never ask on the UI thread.
        SourceFolderTarget target = await Task.Run(() => document
            ? new SourceFolderTarget(SourceDocument.FileExists(path) ? SourceFolderTargetKind.File : SourceFolderTargetKind.Missing, path)
            : ResolveFolderTarget(path));
        // The recorded path came from another application's window. Only an existing, fully
        // qualified path is handed to Explorer: it cannot contain quotes or start with '/', so it
        // can never become a switch beyond the fixed "/select," prefix.
        if (target.Kind == SourceFolderTargetKind.Missing || !Path.IsPathFullyQualified(target.Path))
        {
            LibraryActionStatus.Text = UiText.Get(document
                ? "Library.Source.FileMissing" : "Library.Source.FolderMissing");
            return;
        }

        try
        {
            // Always hand the path to Explorer as an argument. Shell-executing the path itself would
            // run it if it ever named a program instead of a folder.
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(target.Kind == SourceFolderTargetKind.File ? "/select," + target.Path : target.Path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { LibraryActionStatus.Text = UiText.Get("Library.Source.OpenFolderFailed"); }
    }

    private async void OnOpenSourceFileClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        if (!await Task.Run(() => SourceDocument.FileExists(path)))
        {
            LibraryActionStatus.Text = UiText.Get("Library.Source.FileMissing");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { LibraryActionStatus.Text = UiText.Get("Library.Source.OpenFileFailed"); }
    }

    private void OnCopySourcePathClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        try
        {
            Clipboard.SetText(path);
            LibraryActionStatus.Text = UiText.Get("Library.Source.PathCopied");
        }
        catch (ExternalException)
        { LibraryActionStatus.Text = UiText.Get("Library.Source.CopyPathFailed"); }
    }

    private static string SourcePathForAction(object sender) =>
        (sender as FrameworkElement)?.DataContext is GalleryItemViewModel tile
            ? CaptureSourceText.NormalizeLocalPath(tile.SourceDocumentPath) : string.Empty;

    internal enum SourceFolderTargetKind { Missing, Folder, File }

    internal readonly record struct SourceFolderTarget(SourceFolderTargetKind Kind, string Path);

    /// <summary>A folder opens itself; a file opens its folder with the file selected.</summary>
    internal static SourceFolderTarget ResolveFolderTarget(string path)
    {
        string normalized = CaptureSourceText.NormalizeLocalPath(path);
        if (normalized.Length == 0) return new(SourceFolderTargetKind.Missing, string.Empty);
        try
        {
            if (Directory.Exists(normalized)) return new(SourceFolderTargetKind.Folder, normalized);
            if (File.Exists(normalized)) return new(SourceFolderTargetKind.File, normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(SourceFolderTargetKind.Missing, normalized);
    }
}
