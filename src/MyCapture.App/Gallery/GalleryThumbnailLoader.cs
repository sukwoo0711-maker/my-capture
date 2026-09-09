using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>Bounds native image decoders across every gallery request.</summary>
internal sealed class GalleryThumbnailLoader
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private readonly Func<string, int, BitmapSource?> _decode;

    internal GalleryThumbnailLoader(Func<string, int, BitmapSource?>? decode = null) =>
        _decode = decode ?? ImageCodec.TryLoadScaled;

    internal async Task<BitmapSource?> LoadAsync(string path, int size, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapSource? result = _decode(path, size);
                result?.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }
}
