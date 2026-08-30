using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>
/// Everything needed to re-open a stored capture in the annotation editor.
/// </summary>
/// <remarks>
/// The editor was written for the live capture path, which hands it a
/// <see cref="FrozenFrame"/> and a crop rectangle inside it. To re-edit a stored capture
/// we synthesise a frame that <em>is</em> the original at full size and a crop that covers
/// the whole image, so the editor's coordinate maths (crop origin, DPI mapping) works
/// unchanged with a zero offset and 1:1 scale.
/// </remarks>
internal sealed class GalleryReeditContext
{
    internal GalleryReeditContext(
        CaptureRecord record,
        FrozenFrame frame,
        RectD cropRegion,
        BitmapSource originalBitmap,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps)
    {
        Record = record;
        Frame = frame;
        CropRegion = cropRegion;
        OriginalBitmap = originalBitmap;
        Document = document;
        AssetBitmaps = assetBitmaps;
    }

    internal CaptureRecord Record { get; }

    /// <summary>Synthetic frame whose bitmap is the full original at 1:1.</summary>
    internal FrozenFrame Frame { get; }

    /// <summary>Full-image crop rectangle (origin 0,0, original pixel size).</summary>
    internal RectD CropRegion { get; }

    /// <summary>The unmodified original, frozen.</summary>
    internal BitmapSource OriginalBitmap { get; }

    /// <summary>The restored, editable annotation layer.</summary>
    internal AnnotationDocument Document { get; }

    /// <summary>
    /// Decoded, frozen pixels for every <see cref="ImageAnnotation.AssetFileName"/> the
    /// document references and that exists on disk, keyed by that same canonical name.
    /// </summary>
    internal IReadOnlyDictionary<string, BitmapSource> AssetBitmaps { get; }
}

/// <summary>
/// Loads a stored capture back into an editable form.
/// </summary>
/// <remarks>
/// <para>
/// The editable base is always <c>original.png</c>, never <c>rendered.png</c>: re-editing
/// must start from unflattened pixels and the live annotation layer so the user can move
/// or delete an existing arrow, which is the product's central promise. Flattening the
/// rendered image back in would fuse the annotations into the background permanently.
/// </para>
/// <para>
/// Every bitmap is decoded with <see cref="BitmapCacheOption.OnLoad"/> and frozen (via
/// <see cref="ImageCodec.TryLoad"/>) so no file stays locked and the images are safe to
/// hand to the editor thread. Missing or corrupt files degrade rather than crash: a
/// missing original fails the load (there is nothing to edit); a missing layer file or a
/// missing asset drops that annotation but still opens the original.
/// </para>
/// </remarks>
internal sealed class GalleryReeditLoader
{
    private readonly CaptureQueue _queue;
    private readonly ILogger<GalleryReeditLoader> _log;

    internal GalleryReeditLoader(CaptureQueue queue, ILogger<GalleryReeditLoader> log)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Reason a load could not proceed, for a concise Korean status message.</summary>
    internal enum LoadFailure
    {
        None,
        MissingOriginal,
        UndecodableOriginal,
    }

    /// <summary>
    /// Builds a re-edit context for <paramref name="record"/>, or returns
    /// <see langword="null"/> with <paramref name="failure"/> set when the original cannot
    /// be loaded.
    /// </summary>
    internal GalleryReeditContext? TryLoad(CaptureRecord record, out LoadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(record);
        failure = LoadFailure.None;

        string directory = _queue.GetDirectory(record);
        string originalPath = Path.Combine(directory, CaptureFileNames.Original);

        if (!File.Exists(originalPath))
        {
            failure = LoadFailure.MissingOriginal;
            _log.LogWarning("Re-edit load failed: original.png missing for {Id}", record.Id);
            return null;
        }

        BitmapSource? original = ImageCodec.TryLoad(originalPath);
        if (original is null)
        {
            failure = LoadFailure.UndecodableOriginal;
            _log.LogWarning("Re-edit load failed: original.png unreadable for {Id}", record.Id);
            return null;
        }

        AnnotationDocument document = LoadDocument(directory, original);
        IReadOnlyDictionary<string, BitmapSource> assets = LoadAssets(directory, document);

        // Synthesise a frame that is the original itself: full bounds, no monitor, so the
        // editor's crop origin is (0,0) and its DIP-per-pixel mapping is 1:1.
        var frame = new FrozenFrame(
            original,
            new RectD(0, 0, original.PixelWidth, original.PixelHeight),
            Monitor: null,
            ElapsedMilliseconds: 0);
        var cropRegion = new RectD(0, 0, original.PixelWidth, original.PixelHeight);

        return new GalleryReeditContext(record, frame, cropRegion, original, document, assets);
    }

    private AnnotationDocument LoadDocument(string directory, BitmapSource original)
    {
        string layersPath = Path.Combine(directory, CaptureFileNames.Layers);

        string? json = null;
        try
        {
            if (File.Exists(layersPath))
            {
                json = File.ReadAllText(layersPath);
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not read layers.json in {Directory}", directory);
        }

        // A missing or corrupt layer file is treated as "no annotations": the original is
        // intact and re-editing from a clean layer is better than refusing to open.
        AnnotationDocument? document = AnnotationDocument.TryFromJson(json);
        return document ?? AnnotationDocument.CreateFor(original.PixelWidth, original.PixelHeight);
    }

    private IReadOnlyDictionary<string, BitmapSource> LoadAssets(
        string directory,
        AnnotationDocument document)
    {
        var assets = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<ImageAnnotation>();

        foreach (ImageAnnotation image in document.Items.OfType<ImageAnnotation>())
        {
            string name = image.AssetFileName;
            if (string.IsNullOrEmpty(name) || assets.ContainsKey(name))
            {
                continue;
            }

            BitmapSource? bitmap = ImageCodec.TryLoad(Path.Combine(directory, name));
            if (bitmap is null)
            {
                // The sidecar is gone or unreadable: drop just this image so the rest of the
                // capture still opens rather than failing the whole load.
                _log.LogWarning("Re-edit: asset {Asset} missing in {Directory}; dropping it", name, directory);
                dropped.Add(image);
                continue;
            }

            assets[name] = bitmap;
        }

        foreach (ImageAnnotation gone in dropped)
        {
            document.Remove(gone);
        }

        return assets;
    }
}
