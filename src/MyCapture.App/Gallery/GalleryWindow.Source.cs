using MyCapture.App.Themes;
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

    // The details panel's buttons and the card menu's source items share these handlers; the card
    // menu names its card through CommandParameter.
    private async void OnOpenSourcePageClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile || SourceUrlForAction(sender) is not { Length: > 0 } url) return;
        SetSourceActionStatus(await SourceActions.OpenAsync(
            CaptureProvenance.Create(url: url, appName: tile.SourceAppName, appExecutablePath: tile.SourceAppPath)));
    }

    private void OnCopySourcePageClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile || SourceUrlForAction(sender) is not { Length: > 0 } url) return;
        SetSourceActionStatus(SourceActions.CopyUrl(url));
    }

    private async void OnOpenSourceFolderClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile || SourcePathForAction(sender) is not { Length: > 0 } path) return;
        SetSourceActionStatus(await SourceActions.OpenLocationAsync(
            CaptureProvenance.Create(documentPath: path, appName: tile.SourceAppName,
                appExecutablePath: tile.SourceAppPath)));
    }

    private async void OnOpenSourceFileClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile || SourcePathForAction(sender) is not { Length: > 0 } path) return;
        SetSourceActionStatus(await SourceActions.OpenAsync(
            CaptureProvenance.Create(documentPath: path, appName: tile.SourceAppName,
                appExecutablePath: tile.SourceAppPath)));
    }

    private void OnCopySourcePathClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile || SourcePathForAction(sender) is not { Length: > 0 } path) return;
        SetSourceActionStatus(SourceActions.CopyPath(path));
    }

    private static string SourcePath(GalleryItemViewModel tile) => CaptureSourceText.NormalizeLocalPath(tile.SourceDocumentPath);

    /// <summary>The address a source action targets, for the details buttons and the card menu alike.</summary>
    private static string SourceUrlForAction(object sender) =>
        ResolveTileFromCommand(sender) is { } tile ? tile.SourcePageUrl : string.Empty;

    /// <summary>The path a source action targets, for the details buttons and the card menu alike.</summary>
    private static string SourcePathForAction(object sender) =>
        ResolveTileFromCommand(sender) is { } tile ? SourcePath(tile) : string.Empty;

    internal SourceIconCache SourceIcons { get; set; } = SourceIconCache.Shared;

    private void PrewarmSourceIcons()
    {
        if (!IsVisible) return;
        SourceIcons.Prewarm(_viewModel.Groups.SelectMany(group => group.Items)
            .Where(tile => tile.SourceProvenance.HasAppIdentity).Select(tile => tile.SourceProvenance).ToList());
    }

    internal void SetSourceActionStatus(string status)
    {
        if (status.Length > 0) KoreanWordWrap.Show(LibraryActionStatus, status);
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
