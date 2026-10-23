using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Lazy<byte[]>> DraftPngCache = new();
    internal event EventHandler? DraftChanged;
    internal Task<DraftSnapshot?> CaptureDraftAsync()
    {
        if (_completed || _commitInProgress || _redactionInProgress) return Task.FromResult<DraftSnapshot?>(null);
        if (HasUnsettledMosaicWork) return CaptureDraftAfterMosaicsAsync();
        if (!MosaicsReadyForExport) return Task.FromResult<DraftSnapshot?>(null);
        // Everything the editor owns is read here. The document is a deep copy no one else
        // references, so it and these plain values are serialized on the encoder thread.
        AnnotationDocument document = CreateCurrentImageSnapshot();
        string title = _sourceWindowTitle ?? string.Empty;
        int turns = _controller.RotationTurns;
        PointD origin = _controller.CropOrigin;
        RectD? crop = _crop?.Region;
        // Frozen bitmaps are shared and copied once on the encoder thread (and their PNG is
        // cached). A live, mutable bitmap must be read here, so its one copy is made now and
        // that independent copy is encoded as is.
        var bitmaps = new Dictionary<string, (BitmapSource Bitmap, bool Detached)>
        {
            ["base.png"] = PrepareForDraft(_surface.Frame.Bitmap),
            ["original.png"] = PrepareForDraft(_selectedBitmap),
        };
        var assets = new List<(string Name, string File)>();
        int index = 0;
        foreach (var (name, bitmap) in _imageStore.DecodedFor(document.Items.OfType<ImageAnnotation>().Select(x => x.AssetFileName)))
        {
            string file = $"asset-{index++:D4}.png";
            assets.Add((name, file));
            bitmaps[file] = PrepareForDraft(bitmap);
        }
        return Threading.StaThreadTask.RunAsync<DraftSnapshot?>(() =>
        {
            // The same JSON, and metadata in the same order, as when this ran on the UI thread.
            string json = document.ToJson();
            var metadata = new Dictionary<string, string> { ["title"] = title };
            metadata["turns"] = turns.ToString(System.Globalization.CultureInfo.InvariantCulture);
            metadata["origin"] = JsonSerializer.Serialize(origin, JsonDefaults.Compact);
            if (crop is { } region) metadata["crop"] = JsonSerializer.Serialize(region, JsonDefaults.Compact);
            foreach (var (name, file) in assets) metadata["asset:" + name] = file;
            var files = bitmaps.ToDictionary(x => x.Key, x => x.Value.Detached
                ? EncodeIndependentDraftBitmap(x.Value.Bitmap, x.Value.Bitmap)
                : EncodeCachedDraftBitmap(x.Value.Bitmap));
            return new DraftSnapshot("image", json, metadata, files);
        }, "MyCapture image draft encoder");
    }

    private async Task<DraftSnapshot?> CaptureDraftAfterMosaicsAsync()
    {
        await MosaicOperationsSettledAsync().ConfigureAwait(false);
        return await Dispatcher.InvokeAsync(CaptureDraftAsync).Task.Unwrap().ConfigureAwait(false);
    }
    private static (BitmapSource Bitmap, bool Detached) PrepareForDraft(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen) return (bitmap, false);
        var copy = new WriteableBitmap(bitmap); copy.Freeze(); return (copy, true);
    }
    /// <summary>
    /// Encodes a draft PNG from one native copy of <paramref name="bitmap"/>. The encoder runs
    /// on its background STA; a frozen source still shares its WIC lock with the live surface and
    /// PNG WriteSource holds that lock for the whole encode, so the lock is held only while the
    /// pixels are copied, never during compression.
    /// </summary>
    /// <remarks>
    /// The copy is never a <see cref="WriteableBitmap"/> here. Its constructor keeps the source's
    /// lock while it creates this thread's MediaContext, which takes WPF's process-wide
    /// composition lock; the UI thread renders the same source holding the composition lock and
    /// then waits for the source's lock, so the two threads deadlocked for good.
    /// </remarks>
    internal static byte[] EncodeDraftBitmap(BitmapSource bitmap)
    {
        BitmapSource independent = CopyToIndependentBitmap(bitmap);
        return EncodeIndependentDraftBitmap(independent, bitmap);
    }
    /// <summary>
    /// A frozen bitmap with the pixels, format, palette and resolution of
    /// <paramref name="bitmap"/>, as <see cref="WriteableBitmap"/> copied them, sharing nothing
    /// with it. <see cref="BitmapSource.CopyPixels(Int32Rect, IntPtr, int, int)"/> holds only
    /// the source's own lock and <see cref="BitmapSource.Create(int, int, double, double, PixelFormat, BitmapPalette, IntPtr, int, int)"/>
    /// copies the buffer into a new bitmap; neither creates a MediaContext. The unmanaged buffer
    /// is freed at once, so an 8K capture (66 MB) leaves no large object heap garbage behind.
    /// </summary>
    private static BitmapSource CopyToIndependentBitmap(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        PixelFormat format = bitmap.Format;
        int stride = checked((width * format.BitsPerPixel + 7) / 8);
        int size = checked(stride * height);
        IntPtr pixels = Marshal.AllocHGlobal(size);
        try
        {
            bitmap.CopyPixels(new Int32Rect(0, 0, width, height), pixels, size, stride);
            // WriteableBitmap took the palette only for indexed formats.
            bool indexed = format == PixelFormats.Indexed1 || format == PixelFormats.Indexed2
                || format == PixelFormats.Indexed4 || format == PixelFormats.Indexed8;
            BitmapSource copy = BitmapSource.Create(width, height, bitmap.DpiX, bitmap.DpiY, format,
                indexed ? bitmap.Palette : null, pixels, size, stride);
            copy.Freeze();
            return copy;
        }
        finally
        {
            Marshal.FreeHGlobal(pixels);
        }
    }
    private static byte[] EncodeIndependentDraftBitmap(BitmapSource independent, BitmapSource source)
    {
        // Metadata and colour contexts come from the source frame, as before (a wrapper only;
        // BitmapFrame.Create does not copy pixels).
        BitmapFrame contract = BitmapFrame.Create(source);
        var frame = BitmapFrame.Create(independent, contract.Thumbnail,
            contract.Metadata as BitmapMetadata, contract.ColorContexts);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(frame);
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    /// <summary>
    /// A frozen bitmap's PNG is reused across revisions. PublicationOnly never caches an
    /// exception, so one failed encode does not disable draft saving for the session.
    /// </summary>
    internal static byte[] EncodeCachedDraftBitmap(BitmapSource bitmap) => EncodeCachedDraftBitmap(bitmap, EncodeDraftBitmap);
    internal static byte[] EncodeCachedDraftBitmap(BitmapSource bitmap, Func<BitmapSource, byte[]> encode) => bitmap.IsFrozen
        ? DraftPngCache.GetValue(bitmap, image => new Lazy<byte[]>(() => encode(image),
            LazyThreadSafetyMode.PublicationOnly)).Value
        : encode(bitmap);
    internal void RestorePendingDraftCrop(string json)
    {
        _crop = new CropSelection(_canvasWidth, _canvasHeight);
        _crop.SetRegion(JsonSerializer.Deserialize<RectD>(json, JsonDefaults.Compact));
        RefreshCrop();
    }
    internal void RestoreDraftGeometry(BitmapSource current, int turns, string origin)
    {
        _controller.RestoreDraftGeometry(turns, JsonSerializer.Deserialize<PointD>(origin, JsonDefaults.Compact));
        _rotationTurns = _controller.RotationTurns;
        SwapBaseBitmap(current, new RectD(0, 0, current.PixelWidth, current.PixelHeight));
        _dimensionsText.Text = UiText.Format("Text_009548FC2109", _canvasWidth, _canvasHeight);
    }
    internal void ReportDraftFailure() => SetStatus(UiText.Get("EditRecovery_SaveFailed"));
}
