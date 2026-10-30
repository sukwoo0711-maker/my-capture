using System.Windows;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    internal CaptureSourceActions SourceActions { get; set; } = CaptureSourceActions.Default;

    private async void OnOpenSourceMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile) return;
        e.Handled = true;
        if (!await GallerySourceBadge.TryOpenAsync(tile.SourceProvenance, SourceActions, SetSourceActionStatus))
            ShowSourceDetails(tile);
    }

    private async void OnOpenSourcePageClick(object sender, RoutedEventArgs e)
    {
        string url = SourceUrlForAction(sender);
        if (url.Length == 0) return;
        SetSourceActionStatus(await CaptureSourceActions.Default.OpenAsync(CaptureProvenance.Create(url: url)));
    }

    private void OnCopySourcePageClick(object sender, RoutedEventArgs e)
    {
        string url = SourceUrlForAction(sender);
        if (url.Length == 0) return;
        SetSourceActionStatus(CaptureSourceActions.Default.CopyUrl(url));
    }

    private static string SourceUrlForAction(object sender) =>
        (sender as FrameworkElement)?.DataContext is GalleryItemViewModel tile
            ? tile.SourcePageUrl : string.Empty;

    private async void OnOpenSourceFolderClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        var tile = (GalleryItemViewModel)((FrameworkElement)sender).DataContext;
        SetSourceActionStatus(await CaptureSourceActions.Default.OpenLocationAsync(
            CaptureProvenance.Create(documentPath: path, appName: tile.SourceAppName,
                appExecutablePath: tile.SourceAppPath)));
    }

    private async void OnOpenSourceFileClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        var tile = (GalleryItemViewModel)((FrameworkElement)sender).DataContext;
        SetSourceActionStatus(await CaptureSourceActions.Default.OpenAsync(
            CaptureProvenance.Create(documentPath: path, appName: tile.SourceAppName,
                appExecutablePath: tile.SourceAppPath)));
    }

    private void OnCopySourcePathClick(object sender, RoutedEventArgs e)
    {
        string path = SourcePathForAction(sender);
        if (path.Length == 0) return;
        SetSourceActionStatus(CaptureSourceActions.Default.CopyPath(path));
    }

    private static string SourcePathForAction(object sender) =>
        (sender as FrameworkElement)?.DataContext is GalleryItemViewModel tile
            ? CaptureSourceText.NormalizeLocalPath(tile.SourceDocumentPath) : string.Empty;

    internal void SetSourceActionStatus(string status)
    {
        if (status.Length > 0) LibraryActionStatus.Text = status;
    }

    internal void SearchSource(string query)
    {
        SearchBox.Text = query;
        SearchBox.Focus();
        SearchBox.CaretIndex = query.Length;
    }

    internal enum SourceFolderTargetKind { Missing, Folder, File }

    internal readonly record struct SourceFolderTarget(SourceFolderTargetKind Kind, string Path);

    /// <summary>A folder opens itself; a file opens its folder with the file selected.</summary>
    internal static SourceFolderTarget ResolveFolderTarget(string path)
    {
        var target = CaptureSourceActions.Default.ResolveFolderTarget(path);
        SourceFolderTargetKind kind = target.Kind switch
        {
            CaptureSourceActions.SourceFolderTargetKind.Folder => SourceFolderTargetKind.Folder,
            CaptureSourceActions.SourceFolderTargetKind.File => SourceFolderTargetKind.File,
            _ => SourceFolderTargetKind.Missing,
        };
        return new(kind, target.Path);
    }
}
