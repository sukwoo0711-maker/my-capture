using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Media;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _mediaDetailsOpening;
    private MediaDetailsToolTipLoader? _mediaDetailsTooltips;

    private async void OnMediaDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_mediaDetailsOpening || ResolveTileFromCommand(sender) is not { } tile) return;
        e.Handled = true;
        IReadOnlyList<MediaDetailsSource> sources = DetailsSources(tile);
        _mediaDetailsOpening = true;
        try { await MediaDetailsWindow.ShowAsync(this, sources); }
        finally { _mediaDetailsOpening = false; }
    }

    // CommandTargets has the same selected-card rule as all other context-menu commands.
    internal IReadOnlyList<MediaDetailsSource> DetailsSources(GalleryItemViewModel tile) =>
        CommandTargets(tile).Select(DetailsSource).ToArray();

    private MediaDetailsSource DetailsSource(GalleryItemViewModel tile) => new(
        _queue.GetFilePath(tile.Record, tile.IsVideo ? CaptureFileNames.VideoRendered : CaptureFileNames.Rendered),
        tile.Record.Width, tile.Record.Height, tile.IsVideo ? tile.Record.DurationMs : null,
        FallbackFilePath: _queue.GetFilePath(tile.Record, tile.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Original),
        // Image record dimensions describe the original; cropping/rotation can resize its render.
        ReadImageDimensions: tile.IsImage);

    private async void OnMediaDetailsToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryItemViewModel tile } target) return;
        // Updating the tooltip's content leaves the existing time/caption and card layout untouched.
        var tooltip = new ToolTip { Content = UiText.Get("LibraryRecovery_Loading") };
        target.SetCurrentValue(ToolTipProperty, tooltip);
        _mediaDetailsTooltips ??= new MediaDetailsToolTipLoader(this);
        await _mediaDetailsTooltips.LoadAsync(target, tile, tooltip, DetailsSource(tile), details =>
        {
            string facts = MediaDetailsWindow.TooltipText(details);
            tooltip.Content = tile.MetadataCaption.Length > 0 ? tile.MetadataCaption + Environment.NewLine + facts : facts;
        });
    }
}
