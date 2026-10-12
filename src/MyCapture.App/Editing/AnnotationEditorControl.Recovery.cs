using System.IO;
using System.Text.Json;
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
        AnnotationDocument document = CreateCurrentImageSnapshot();
        string json = document.ToJson();
        var metadata = new Dictionary<string, string> { ["title"] = _sourceWindowTitle ?? string.Empty };
        metadata["turns"] = _controller.RotationTurns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["origin"] = JsonSerializer.Serialize(_controller.CropOrigin, JsonDefaults.Compact);
        if (_crop is not null) metadata["crop"] = JsonSerializer.Serialize(_crop.Region, JsonDefaults.Compact);
        // Frozen bitmaps are shared and copied once on the encoder thread (and their PNG is
        // cached). A live, mutable bitmap must be read here, so its one copy is made now and
        // that independent copy is encoded as is.
        var bitmaps = new Dictionary<string, (BitmapSource Bitmap, bool Detached)>
        {
            ["base.png"] = PrepareForDraft(_surface.Frame.Bitmap),
            ["original.png"] = PrepareForDraft(_selectedBitmap),
        };
        int index = 0;
        foreach (var (name, bitmap) in _imageStore.DecodedFor(document.Items.OfType<ImageAnnotation>().Select(x => x.AssetFileName)))
        {
            string file = $"asset-{index++:D4}.png";
            metadata["asset:" + name] = file;
            bitmaps[file] = PrepareForDraft(bitmap);
        }
        return Threading.StaThreadTask.RunAsync<DraftSnapshot?>(() =>
        {
            var files = bitmaps.ToDictionary(x => x.Key, x => x.Value.Detached
                ? EncodeIndependentDraftBitmap(x.Value.Bitmap, x.Value.Bitmap)
                : EncodeCachedDraftBitmap(x.Value.Bitmap));
            return new DraftSnapshot("image", json, metadata, files);
        }, "MyCapture image draft encoder");
    }
    private static (BitmapSource Bitmap, bool Detached) PrepareForDraft(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen) return (bitmap, false);
        var copy = new WriteableBitmap(bitmap); copy.Freeze(); return (copy, true);
    }
    /// <summary>
    /// Encodes a draft PNG from one native copy of <paramref name="bitmap"/>. The encoder runs
    /// on its background STA; a frozen source still shares its WIC lock with the live surface and
    /// PNG WriteSource holds that lock for the whole encode, so the lock is held only while
    /// <see cref="WriteableBitmap"/> copies the pixels, never during compression.
    /// </summary>
    internal static byte[] EncodeDraftBitmap(BitmapSource bitmap)
    {
        var independent = new WriteableBitmap(bitmap);
        independent.Freeze();
        return EncodeIndependentDraftBitmap(independent, bitmap);
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
