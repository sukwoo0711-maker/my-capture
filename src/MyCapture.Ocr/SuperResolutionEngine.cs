using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MyCapture.Ocr;

/// <summary>
/// Runs Real-ESRGAN x4plus (RRDBNet) on 128×128 tiles and stitches the 512×512 outputs
/// back into a full image. The model is fixed-shape and slow (~0.7 s/tile on CPU), so
/// inputs are capped before tiling and the result is always a BitmapSource back in the
/// caller's pipeline. A missing or failed model degrades to the input bitmap.
/// </summary>
internal sealed class SuperResolutionEngine : IDisposable
{
    internal const int TileInput = 128;
    internal const int Scale = 4;
    internal const int MaxLongEdge = 2048;

    private readonly OcrModelStore _store;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private InferenceSession? _session;
    private bool _initFailed;

    internal SuperResolutionEngine(OcrModelStore store, ILogger<SuperResolutionEngine> log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _session is not null;
            }
        }
    }

    internal bool TryInitialize()
    {
        lock (_sync)
        {
            if (_session is not null)
            {
                return true;
            }

            if (_initFailed || !_store.HasSuperResolution)
            {
                return false;
            }

            string modelPath = _store.PathFor(OcrModelCatalog.SuperResolution);
            try
            {
                _session = new InferenceSession(modelPath);
                _log.LogInformation("Real-ESRGAN session ready {Model}", modelPath);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Real-ESRGAN session could not be created {Model}", modelPath);
                _initFailed = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Upscales a preprocessed BGRA bitmap 4× by tiling. Returns null when the session is
    /// unavailable so the caller falls back to nearest-neighbour scaling.
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource? Upscale(System.Windows.Media.Imaging.BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_sync)
        {
            if (_session is null)
            {
                return null;
            }
        }

        System.Windows.Media.Imaging.BitmapSource bgra = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        if (!bgra.IsFrozen && bgra.CanFreeze)
        {
            bgra.Freeze();
        }

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        int outWidth = width * Scale;
        int outHeight = height * Scale;
        int outStride = outWidth * 4;
        byte[] output = new byte[outStride * outHeight];

        var session = _session!;
        int tilesX = (width + TileInput - 1) / TileInput;
        int tilesY = (height + TileInput - 1) / TileInput;

        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                int sx = tileX * TileInput;
                int sy = tileY * TileInput;
                int tw = Math.Min(TileInput, width - sx);
                int th = Math.Min(TileInput, height - sy);

                var tensor = new DenseTensor<float>([1, 3, TileInput, TileInput]);
                for (int y = 0; y < th; y++)
                {
                    int srcRow = (sy + y) * stride;
                    for (int x = 0; x < tw; x++)
                    {
                        int i = srcRow + ((sx + x) * 4);
                        if (pixels[i + 3] == 0)
                        {
                            continue;
                        }

                        tensor[0, 0, y, x] = pixels[i + 2] / 255f;
                        tensor[0, 1, y, x] = pixels[i + 1] / 255f;
                        tensor[0, 2, y, x] = pixels[i] / 255f;
                    }
                }

                using var results = session.Run(
                    [NamedOnnxValue.CreateFromTensor("image", tensor)],
                    ["upscaled_image"]);
                var upscaled = (DenseTensor<float>)results[0].Value;

                for (int y = 0; y < th * Scale; y++)
                {
                    int dstRow = ((sy * Scale) + y) * outStride;
                    int dy = sy * Scale + y;
                    for (int x = 0; x < tw * Scale; x++)
                    {
                        int dx = sx * Scale + x;
                        if (dx >= outWidth || dy >= outHeight)
                        {
                            continue;
                        }

                        float r = Math.Clamp(upscaled[0, 0, y, x], 0f, 1f);
                        float g = Math.Clamp(upscaled[0, 1, y, x], 0f, 1f);
                        float b = Math.Clamp(upscaled[0, 2, y, x], 0f, 1f);
                        int o = dstRow + (dx * 4);
                        output[o] = (byte)MathF.Round(b * 255f);
                        output[o + 1] = (byte)MathF.Round(g * 255f);
                        output[o + 2] = (byte)MathF.Round(r * 255f);
                        output[o + 3] = 255;
                    }
                }
            }
        }

        var result = System.Windows.Media.Imaging.BitmapSource.Create(
            outWidth,
            outHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            output,
            outStride);
        result.Freeze();
        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
