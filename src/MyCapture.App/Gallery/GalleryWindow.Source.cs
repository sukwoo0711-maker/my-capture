using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

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
}
