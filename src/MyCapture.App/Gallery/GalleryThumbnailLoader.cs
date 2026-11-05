using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>Bounds path inspection and native image decoding across every gallery request.</summary>
internal sealed class GalleryThumbnailLoader
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private readonly Func<string, int, BitmapSource?> _decode;

    internal GalleryThumbnailLoader(Func<string, int, BitmapSource?>? decode = null) =>
        _decode = decode ?? ImageCodec.TryLoadScaled;

    // Capture only the values GetDirectory needs; never read a live mutable record on a worker.
    // The queue still performs its normal root and reparse-point checks for every request.
    internal static Func<string> CreateQueuePathRequest(CaptureQueue queue, CaptureRecord record)
    {
        Guid id = record.Id;
        string relativeDirectory = record.RelativeDirectory;
        return () => Path.Combine(queue.GetDirectory(new CaptureRecord
        {
            Id = id,
            RelativeDirectory = relativeDirectory,
        }), CaptureFileNames.Thumbnail);
    }

    internal Task<BitmapSource?> LoadAsync(string path, int size, CancellationToken cancellationToken) =>
        LoadAsync(() => path, size, cancellationToken);

    internal async Task<BitmapSource?> LoadAsync(Func<string> resolvePath, int size, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = resolvePath();
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
