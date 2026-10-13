using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Decodes the frame thumbnails of one <see cref="FilmstripLayout"/> on a background thread.
/// </summary>
/// <remarks>
/// <para>
/// One Media Foundation reader is opened and walked forward once: the layout's sample times are
/// non-decreasing, so no tile ever seeks backwards. Each decoded BGR32 frame is scaled down to
/// the tile's device-pixel size and kept only as that small frozen <see cref="BitmapSource"/>,
/// which the timeline draws from any thread. Memory is bounded by the tile size and
/// <see cref="FilmstripLayout.MaxTileCount"/>.
/// </para>
/// <para>
/// Tiles reach the UI in batches (never more often than every <see cref="PublishBatchSize"/>
/// tiles, and always once at the end) through the timeline's dispatcher, so a strip redraws a
/// few times, not once per frame. A decode failure of any kind is logged and leaves the tiles
/// that were not published empty: the editor keeps working without thumbnails.
/// </para>
/// </remarks>
internal sealed class TimelineFilmstrip : IDisposable
{
    /// <summary>Tiles decoded between two mid-way publishes.</summary>
    internal const int PublishBatchSize = 8;

    private readonly string _sourcePath;
    private readonly Dispatcher _dispatcher;
    private readonly Action<TimelineFilmstrip> _tilesPublished;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly BitmapSource?[] _tiles;
    private readonly Task _decode;
    private int _publishedCount;
    private bool _disposed;

    private TimelineFilmstrip(
        string sourcePath,
        FilmstripLayout layout,
        int pixelWidth,
        int pixelHeight,
        Dispatcher dispatcher,
        Action<TimelineFilmstrip> tilesPublished,
        ILogger log)
    {
        _sourcePath = sourcePath;
        Layout = layout;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _dispatcher = dispatcher;
        _tilesPublished = tilesPublished;
        _log = log;
        _tiles = new BitmapSource?[layout.TileCount];
        // The tiles are WPF bitmaps, which give the decoding thread a Dispatcher. A thread-pool
        // thread would keep it until the pool retires the thread; the STA worker shuts it down.
        _decode = StaThreadTask.RunAsync(
            () =>
            {
                Decode();
                return true;
            },
            "MyCapture filmstrip decoder");
    }

    internal FilmstripLayout Layout { get; }

    /// <summary>Device-pixel width of every tile bitmap.</summary>
    internal int PixelWidth { get; }

    /// <summary>Device-pixel height of every tile bitmap.</summary>
    internal int PixelHeight { get; }

    internal int TileCount => Layout.TileCount;

    /// <summary>Tiles the UI may draw: a prefix of the strip, published in order.</summary>
    internal int PublishedCount => Volatile.Read(ref _publishedCount);

    /// <summary>True once decoding ended, with every tile or after a failure.</summary>
    internal bool IsCompleted => _decode.IsCompleted;

    /// <summary>Awaits the end of decoding; diagnostics and tests only.</summary>
    internal Task CompletionForTest => _decode;

    /// <summary>
    /// Starts decoding <paramref name="layout"/> from <paramref name="sourcePath"/>. Tiles are
    /// <paramref name="layout"/>'s tile size times the DPI scale, in whole device pixels.
    /// <paramref name="tilesPublished"/> runs on <paramref name="dispatcher"/> after each batch.
    /// </summary>
    internal static TimelineFilmstrip Start(
        string sourcePath,
        FilmstripLayout layout,
        DpiScale dpi,
        Dispatcher dispatcher,
        Action<TimelineFilmstrip> tilesPublished,
        ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(tilesPublished);
        ArgumentNullException.ThrowIfNull(log);
        int pixelWidth = Math.Max(1, (int)Math.Round(layout.TileWidth * Math.Max(0.5, dpi.DpiScaleX)));
        int pixelHeight = Math.Max(1, (int)Math.Round(layout.TileHeight * Math.Max(0.5, dpi.DpiScaleY)));
        return new TimelineFilmstrip(sourcePath, layout, pixelWidth, pixelHeight, dispatcher, tilesPublished, log);
    }

    /// <summary>The published tile at <paramref name="index"/>, or null while it is still decoding.</summary>
    internal BitmapSource? Tile(int index) =>
        index >= 0 && index < PublishedCount ? _tiles[index] : null;

    private void Decode()
    {
        CancellationToken token = _cancellation.Token;
        MediaFoundationVideoFrameReader? reader = null;
        int decoded = 0;
        try
        {
            reader = new MediaFoundationVideoFrameReader(_sourcePath);
            for (int index = 0; index < Layout.TileCount; index++)
            {
                token.ThrowIfCancellationRequested();
                DecodedVideoFrame frame = reader.ReadFrameAt(Layout.SampleTimeMs(index), token);
                if (frame.Format != DecodedVideoPixelFormat.Bgr32)
                {
                    throw new InvalidOperationException(
                        $"The filmstrip decoder returned {frame.Format} frames; BGR32 was expected.");
                }

                _tiles[index] = ToTile(frame);
                decoded = index + 1;
                if (decoded - PublishedCount >= PublishBatchSize && decoded < Layout.TileCount)
                {
                    Publish(decoded);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Any failure (a missing or unreadable file, an unsupported codec, a decoder
            // error) only costs the thumbnails; the strip stays usable without them.
            _log.LogWarning(
                ex,
                "Filmstrip thumbnails of {Path} stopped after {Decoded} of {Count} tiles",
                _sourcePath,
                decoded,
                Layout.TileCount);
        }
        finally
        {
            reader?.Dispose();
        }

        if (decoded > PublishedCount && !token.IsCancellationRequested)
        {
            Publish(decoded);
        }
    }

    private void Publish(int count)
    {
        Volatile.Write(ref _publishedCount, count);
        // Normal priority runs before a pending composition render, so the strip's redraw
        // joins a render the user's pointer already requested instead of adding one.
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!_disposed)
            {
                _tilesPublished(this);
            }
        });
    }

    /// <summary>
    /// Downscales one decoded frame to the tile size: the full frame becomes a BitmapSource,
    /// WIC scales it, and only the small copy is kept and frozen for cross-thread drawing.
    /// </summary>
    private BitmapSource ToTile(DecodedVideoFrame frame)
    {
        BitmapSource full = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            frame.Pixels,
            frame.Stride);
        var scaled = new TransformedBitmap(
            full,
            new ScaleTransform((double)PixelWidth / frame.Width, (double)PixelHeight / frame.Height));
        int width = Math.Max(1, Math.Min(PixelWidth, scaled.PixelWidth));
        int height = Math.Max(1, Math.Min(PixelHeight, scaled.PixelHeight));
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        scaled.CopyPixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        BitmapSource tile = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        tile.Freeze();
        return tile;
    }

    /// <summary>Stops decoding; the decode thread releases the reader as soon as it notices.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        // The decode thread only polls the token, which stays valid after disposal.
        _cancellation.Dispose();
    }
}
