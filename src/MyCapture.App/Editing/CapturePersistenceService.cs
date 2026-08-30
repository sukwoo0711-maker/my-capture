using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Persists captures into the queue: the original the instant a region is selected, then
/// the annotated result when editing commits.
/// </summary>
/// <remarks>
/// <para>
/// Two-phase on purpose. The moment the user frames a region we write the untouched pixels
/// and index them, so a crash, a power loss, or the user abandoning the editor can never
/// lose the capture they just took — the single promise the product's queue makes. The
/// second phase replaces the rendered PNG with the flattened annotation result, writes the
/// layer document and any image sidecars, refreshes the thumbnail, and re-indexes.
/// </para>
/// <para>
/// Every file write goes through <see cref="AtomicFile"/> / <see cref="ImageCodec"/> so a
/// half-written file can never stand in for a good one, and each phase ends by saving the
/// index and the per-capture recovery metadata as one logical, ordered step.
/// </para>
/// <para>
/// Bounded and synchronous by contract: the caller invokes this on the UI thread in the
/// path of a capture, and encoding a single frame plus a thumbnail is tens of milliseconds.
/// Nothing here waits on anything unbounded.
/// </para>
/// </remarks>
internal sealed class CapturePersistenceService
{
    private readonly CaptureQueue _queue;
    private readonly AppPaths _paths;
    private readonly Func<QueueSettings> _queueSettings;
    private readonly ILogger<CapturePersistenceService> _log;

    internal CapturePersistenceService(
        CaptureQueue queue,
        AppPaths paths,
        Func<QueueSettings> queueSettings,
        ILogger<CapturePersistenceService> log)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _queueSettings = queueSettings ?? throw new ArgumentNullException(nameof(queueSettings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Writes the untouched selection and indexes it. Returns the created record so the
    /// finalise phase can update the same capture in place.
    /// </summary>
    /// <param name="original">The selected pixels, before any annotation.</param>
    /// <param name="dpiScale">DPI scale of the source monitor.</param>
    /// <param name="sourceWindowTitle">Foreground window title at capture time.</param>
    /// <param name="sourceMonitor">Source monitor device name, for diagnostics.</param>
    internal CaptureRecord PersistOriginal(
        BitmapSource original,
        double dpiScale,
        string sourceWindowTitle,
        string sourceMonitor)
    {
        ArgumentNullException.ThrowIfNull(original);

        var record = new CaptureRecord
        {
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            Width = original.PixelWidth,
            Height = original.PixelHeight,
            DpiScale = dpiScale > 0 ? dpiScale : 1.0,
            SourceWindowTitle = sourceWindowTitle ?? string.Empty,
            SourceMonitor = sourceMonitor ?? string.Empty,
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);

        string directory = _queue.GetDirectory(record);
        Directory.CreateDirectory(directory);

        long bytes = 0;

        // original.png — the unmodified capture.
        bytes += ImageCodec.SavePng(original, Path.Combine(directory, CaptureFileNames.Original));

        // rendered.png — identical to the original until annotations are flattened.
        bytes += ImageCodec.SavePng(original, Path.Combine(directory, CaptureFileNames.Rendered));

        // layers.json — an empty document so the capture is immediately re-editable.
        AnnotationDocument emptyDocument = AnnotationDocument.CreateFor(record.Width, record.Height);
        string layersJson = emptyDocument.ToJson();
        AtomicFile.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), layersJson);
        bytes += ByteLength(layersJson);

        // thumb.jpg — gallery tile.
        bytes += WriteThumbnail(original, directory);

        record.HasAnnotations = false;
        record.TotalBytes = bytes;

        // Index first (so the record is discoverable) then the recovery sidecar (so a lost
        // index can be rebuilt). The meta.json byte cost is small and recovery-only, so it
        // is intentionally not folded into the tracked byte total.
        _queue.Add(record);
        _queue.SaveRecordMeta(record);
        _queue.Save();

        _log.LogInformation(
            "Persisted original {Id} ({Width}x{Height}, {Bytes} bytes)",
            record.Id, record.Width, record.Height, bytes);

        return record;
    }

    /// <summary>
    /// Replaces the rendered PNG with the flattened annotation result and finalises every
    /// derived file, then re-indexes and updates the tracked byte count.
    /// </summary>
    /// <param name="record">The record returned by <see cref="PersistOriginal"/>.</param>
    /// <param name="flattened">The annotations flattened onto the original at 1:1 pixels.</param>
    /// <param name="document">The annotation layer to persist.</param>
    /// <param name="assetBitmaps">
    /// Decoded, in-memory bitmaps for the image assets used by <paramref name="document"/>,
    /// keyed by their in-session asset name. The bytes are written from these, never from
    /// the source file, so a deleted or moved original cannot lose the asset.
    /// </param>
    internal void Finalize(
        CaptureRecord record,
        BitmapSource flattened,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(flattened);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assetBitmaps);

        string directory = _queue.GetDirectory(record);
        Directory.CreateDirectory(directory);

        long bytes = 0;

        // original.png is unchanged; re-measure it so the byte total stays accurate.
        bytes += SafeFileLength(Path.Combine(directory, CaptureFileNames.Original));

        // Canonicalise image sidecars to asset-XX.png and rewrite the document to reference
        // them, so the persisted layer never depends on the in-session names or source paths.
        CanonicalizeAssets(directory, document, assetBitmaps, ref bytes);

        // rendered.png — the flattened result.
        bytes += ImageCodec.SavePng(flattened, Path.Combine(directory, CaptureFileNames.Rendered));

        // layers.json — the editable layer.
        document.NormalizeZIndices();
        string layersJson = document.ToJson();
        AtomicFile.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), layersJson);
        bytes += ByteLength(layersJson);

        // thumb.jpg — regenerated from the flattened result so the tile shows annotations.
        bytes += WriteThumbnail(flattened, directory);

        record.HasAnnotations = !document.IsEmpty;
        record.UpdatedAt = DateTimeOffset.Now;

        _queue.UpdateByteCount(record.Id, bytes);
        _queue.SaveRecordMeta(record);
        _queue.Save();

        _log.LogInformation(
            "Finalised capture {Id}: {ItemCount} annotation(s), {Bytes} bytes",
            record.Id, document.Items.Count, bytes);
    }

    /// <summary>
    /// Copies each used asset's decoded pixels into the capture directory as
    /// <c>asset-XX.png</c> and rewrites the document's references to match.
    /// </summary>
    private void CanonicalizeAssets(
        string directory,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps,
        ref long bytes)
    {
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 1;

        foreach (ImageAnnotation image in document.Items.OfType<ImageAnnotation>())
        {
            string sessionName = image.AssetFileName;
            if (string.IsNullOrEmpty(sessionName))
            {
                continue;
            }

            if (remap.TryGetValue(sessionName, out string? already))
            {
                // The same inserted image used twice shares one sidecar.
                image.AssetFileName = already;
                continue;
            }

            if (!assetBitmaps.TryGetValue(sessionName, out BitmapSource? bitmap))
            {
                // No decoded pixels for this asset (should not happen for a used asset);
                // leave the reference untouched so the item is not silently dropped.
                _log.LogWarning("No in-memory bitmap for asset {Asset}; sidecar not written", sessionName);
                continue;
            }

            string canonicalName =
                $"{CaptureFileNames.AssetPrefix}{index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}.png";
            index++;

            long assetBytes = ImageCodec.SavePng(bitmap, Path.Combine(directory, canonicalName));
            bytes += assetBytes;

            remap[sessionName] = canonicalName;
            image.AssetFileName = canonicalName;
        }
    }

    private long WriteThumbnail(BitmapSource source, string directory)
    {
        int longEdge = Math.Max(16, _queueSettings().ThumbnailLongEdge);
        BitmapSource thumb = ImageCodec.CreateThumbnail(source, longEdge);
        return ImageCodec.SaveJpeg(
            thumb, Path.Combine(directory, CaptureFileNames.Thumbnail), ImageCodec.ThumbnailJpegQuality);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long ByteLength(string text) =>
        System.Text.Encoding.UTF8.GetByteCount(text);
}
