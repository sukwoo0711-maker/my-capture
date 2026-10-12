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
        var bitmaps = new Dictionary<string, BitmapSource>
        {
            ["base.png"] = FreezeForDraft(_surface.Frame.Bitmap),
            ["original.png"] = FreezeForDraft(_selectedBitmap),
        };
        int index = 0;
        foreach (var (name, bitmap) in _imageStore.DecodedFor(document.Items.OfType<ImageAnnotation>().Select(x => x.AssetFileName)))
        {
            string file = $"asset-{index++:D4}.png";
            metadata["asset:" + name] = file;
            bitmaps[file] = FreezeForDraft(bitmap);
        }
        return Threading.StaThreadTask.RunAsync<DraftSnapshot?>(() =>
        {
            var files = bitmaps.ToDictionary(x => x.Key, x => EncodeCachedDraftBitmap(x.Value));
            return new DraftSnapshot("image", json, metadata, files);
        }, "MyCapture image draft encoder");
    }
    private static BitmapSource FreezeForDraft(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen) return bitmap;
        var copy = (BitmapSource)bitmap.Clone(); copy.Freeze(); return copy;
    }
    internal static byte[] EncodeDraftBitmap(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    internal static byte[] EncodeCachedDraftBitmap(BitmapSource bitmap) => bitmap.IsFrozen
        ? DraftPngCache.GetValue(bitmap, image => new Lazy<byte[]>(() => EncodeDraftBitmap(image))).Value
        : EncodeDraftBitmap(bitmap);
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
