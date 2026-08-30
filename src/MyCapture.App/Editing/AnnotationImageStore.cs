using System.IO;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Holds the decoded pixels of images inserted during one editing session.
/// </summary>
/// <remarks>
/// <para>
/// The domain's <see cref="MyCapture.Core.Annotations.ImageAnnotation"/> references a
/// sidecar file by name and never carries the pixels. During editing, though, the
/// renderer needs the actual bitmap, and task 8 needs to know which source file each
/// asset came from so it can copy the bytes into the capture directory. This store is
/// that in-memory bridge.
/// </para>
/// <para>
/// Images are decoded with <see cref="BitmapCacheOption.OnLoad"/> and the stream closed
/// immediately, so the source file is never left locked — a user must be able to delete
/// or move the file they just inserted.
/// </para>
/// </remarks>
internal sealed class AnnotationImageStore
{
    private readonly Dictionary<string, BitmapSource> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> without locking it and registers it under a
    /// fresh, path-separator-free asset name.
    /// </summary>
    /// <returns>
    /// The decoded bitmap and the generated asset file name to store on the annotation,
    /// or <see langword="null"/> when the file could not be decoded as an image.
    /// </returns>
    internal (BitmapSource Bitmap, string AssetFileName)? LoadFromFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            BitmapSource decoded = DecodeWithoutLocking(sourcePath);
            string extension = SafeExtension(sourcePath);
            string assetName = $"image-{++_counter:D2}{extension}";

            _decoded[assetName] = decoded;
            _sources[assetName] = Path.GetFullPath(sourcePath);
            return (decoded, assetName);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            // A file the user pointed at that is not a decodable image is a user error,
            // not a crash: report nothing here and let the caller ignore the pick.
            return null;
        }
    }

    internal BitmapSource? Get(string assetFileName) =>
        _decoded.TryGetValue(assetFileName, out BitmapSource? bitmap) ? bitmap : null;

    /// <summary>
    /// Registers already-decoded pixels under their canonical asset names, for re-editing a
    /// stored capture whose sidecars were loaded from disk.
    /// </summary>
    /// <remarks>
    /// The names are the persisted <c>asset-XX.png</c> names, so the renderer resolves them
    /// during editing and the persistence layer's canonicalisation maps them straight back
    /// to the same files on commit.
    /// </remarks>
    internal void Seed(IReadOnlyDictionary<string, BitmapSource> decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);

        foreach ((string name, BitmapSource bitmap) in decoded)
        {
            if (!string.IsNullOrEmpty(name) && bitmap is not null)
            {
                _decoded[name] = bitmap;
            }
        }
    }

    /// <summary>
    /// Builds a store whose only job is to resolve the supplied decoded bitmaps by name.
    /// </summary>
    /// <remarks>
    /// Used to back a renderer at flatten time: the flattened output must resolve every
    /// image asset from the pixels decoded during editing, never from a source file that
    /// may already be gone.
    /// </remarks>
    internal static AnnotationImageStore FromDecoded(IReadOnlyDictionary<string, BitmapSource> decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);

        var store = new AnnotationImageStore();
        foreach ((string name, BitmapSource bitmap) in decoded)
        {
            if (!string.IsNullOrEmpty(name) && bitmap is not null)
            {
                store._decoded[name] = bitmap;
            }
        }

        return store;
    }

    /// <summary>
    /// The decoded, in-memory bitmap for each asset actually used by
    /// <paramref name="usedAssetNames"/>.
    /// </summary>
    /// <remarks>
    /// This is what lets the flattener and the persistence layer write the sidecar bytes
    /// without re-reading the source file: the pixels were decoded on insert and frozen,
    /// so they survive the user deleting or moving the original. Only used assets are
    /// reported so an abandoned pick is neither drawn nor persisted.
    /// </remarks>
    internal IReadOnlyDictionary<string, BitmapSource> DecodedFor(IEnumerable<string> usedAssetNames)
    {
        ArgumentNullException.ThrowIfNull(usedAssetNames);

        var result = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in usedAssetNames)
        {
            if (!string.IsNullOrEmpty(name) && _decoded.TryGetValue(name, out BitmapSource? bitmap))
            {
                result[name] = bitmap;
            }
        }

        return result;
    }

    /// <summary>
    /// The source path each asset was decoded from, filtered to the assets actually used
    /// by <paramref name="usedAssetNames"/> so unused picks are not reported.
    /// </summary>
    internal IReadOnlyDictionary<string, string> SourcesFor(IEnumerable<string> usedAssetNames)
    {
        ArgumentNullException.ThrowIfNull(usedAssetNames);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in usedAssetNames)
        {
            if (!string.IsNullOrEmpty(name) && _sources.TryGetValue(name, out string? source))
            {
                result[name] = source;
            }
        }

        return result;
    }

    private static BitmapSource DecodeWithoutLocking(string path)
    {
        // OnLoad forces the whole image to decode before the constructor returns, so the
        // stream can be — and is — disposed immediately. Without it BitmapImage keeps the
        // file handle open lazily and the source file stays locked for the session.
        //
        // IgnoreImageCache is deliberately not set: with a StreamSource (no UriSource) it
        // sends BitmapImage.FinalizeCreation into ImagingCache.RemoveFromCache with a null
        // Uri, which throws. OnLoad alone already guarantees the stream can be released.
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        bitmap.Freeze();
        return bitmap;
    }

    private static string SafeExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? ".png" : ext.ToLowerInvariant();
    }
}
