using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Media;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private CancellationTokenSource? _detailsAudioRead;
    private TextBlock? _detailsAudioRow;
    private DetailsAudioRequest? _detailsAudioRequest;
    internal Task PendingDetailsAudioRead { get; private set; } = Task.CompletedTask;
    internal Func<Task>? BeforeDetailsAudioPublishForTest { get; set; }

    private sealed record DetailsAudioRequest(GalleryItemViewModel Tile, long Revision, MediaDetailsSource Source);

    private void OnSelectedDetailAudioContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshSelectedDetailsAudio((TextBlock)sender);

    private void OnSelectedDetailAudioVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshSelectedDetailsAudio((TextBlock)sender);

    private void RefreshSelectedDetailsAudio(TextBlock row)
    {
        CancelSelectedDetailsAudio();
        SetDetailsAudioText(row, string.Empty);
        if (!row.IsVisible || row.DataContext is not GalleryItemViewModel { IsVideo: true } tile) return;
        var cancellation = new CancellationTokenSource();
        _detailsAudioRead = cancellation;
        _detailsAudioRow = row;
        var request = new DetailsAudioRequest(tile, tile.Record.ContentRevision, DetailsSource(tile));
        _detailsAudioRequest = request;
        tile.PropertyChanged += OnSelectedDetailsAudioMetadataChanged;
        SetDetailsAudioText(row, UiText.Get("MediaInfo.Audio") + ": " + UiText.Get("LibraryRecovery_Loading"));
        PendingDetailsAudioRead = ReadSelectedDetailsAudioAsync(row, request, cancellation.Token,
            BeforeDetailsAudioPublishForTest);
    }

    private void OnSelectedDetailsAudioMetadataChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Both an edit's thumbnail refresh and a cached tile's metadata refresh raise this.
        // Renaming/tagging also raises it, but unchanged source facts do not start another probe.
        if (e.PropertyName is not (null or "" or nameof(GalleryItemViewModel.MetadataCaption))) return;
        if (_detailsAudioRow is { } row && _detailsAudioRequest is { } request
            && ReferenceEquals(sender, request.Tile) && !HasCurrentAudioSource(request))
            RefreshSelectedDetailsAudio(row);
    }

    private bool HasCurrentAudioSource(DetailsAudioRequest request) =>
        request.Tile.Record.ContentRevision == request.Revision && DetailsSource(request.Tile) == request.Source;

    private void CancelSelectedDetailsAudio()
    {
        if (_detailsAudioRequest is { } request)
            request.Tile.PropertyChanged -= OnSelectedDetailsAudioMetadataChanged;
        _detailsAudioRead?.Cancel();
        _detailsAudioRead?.Dispose();
        _detailsAudioRead = null;
        _detailsAudioRequest = null;
        _detailsAudioRow = null;
    }

    private static void SetDetailsAudioText(TextBlock row, string text)
    {
        row.Text = text;
        AutomationProperties.SetName(row, text);
    }

    private async Task ReadSelectedDetailsAudioAsync(TextBlock row, DetailsAudioRequest request,
        CancellationToken token, Func<Task>? beforePublish)
    {
        Dispatcher dispatcher = row.Dispatcher;
        try
        {
            IReadOnlyList<MediaDetails> details = await MediaDetailsReader.ReadAsync([request.Source], token).ConfigureAwait(false);
            if (beforePublish is not null) await beforePublish().ConfigureAwait(false);
            if (token.IsCancellationRequested || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !ReferenceEquals(_detailsAudioRequest, request)
                    || !row.IsVisible || !ReferenceEquals(row.DataContext, request.Tile) || !HasCurrentAudioSource(request)) return;
                SetDetailsAudioText(row, UiText.Get("MediaInfo.Audio") + ": " + MediaDetailsWindow.AudioText(details[0]));
            }, DispatcherPriority.DataBind, token).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
