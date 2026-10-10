using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Draws the layers active at an MP4 frame onto its decoded NV12 samples without composing the
/// video itself in WPF.
/// </summary>
/// <remarks>
/// <para>
/// Composing a whole 1080p frame through <see cref="RenderTargetBitmap"/> costs about 30 ms, nearly
/// all of it drawing the opaque video under a caption. Layers do not depend on the video, and they
/// do not change between their start and end times. This compositor therefore renders only the
/// active layers, once per distinct active set, onto a transparent premultiplied target (about
/// 1 ms, on the calling STA thread like every WPF operation), and blends that onto each frame on
/// plain arrays.
/// </para>
/// <para>
/// Samples outside the layers keep the decoder's values exactly. Each 2x2 block a layer touches is
/// converted to RGB with the arithmetic of <see cref="Nv12FrameConverter"/> (which matches the
/// system converter), blended with the source-over formula WPF's software renderer uses and
/// converted back with <see cref="BgraToNv12Converter"/>; luma of an untouched pixel in such a
/// block is kept. Against WPF composing the full frame on the shared 1080p clip, image layers are
/// bit-exact and text differs by at most three levels on antialiased edges (the caption box and
/// its glyphs are blended onto the video in one step instead of two), about 77 dB.
/// </para>
/// </remarks>
internal sealed class Nv12LayerCompositor
{
    private const int MinimumRowPairsPerBand = 16;

    private readonly int _width;
    private readonly int _height;
    private readonly DrawingVisual _visual = new();
    private RenderTargetBitmap? _target;
    private byte[]? _layers;
    private string? _activeKey;
    private LayerBounds _bounds;

    internal Nv12LayerCompositor(int width, int height)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 needs an even frame size.");
        }

        _width = width;
        _height = height;
    }

    /// <summary>Layer renders performed so far; one per distinct active layer set. Tests only.</summary>
    internal int LayerRenderCount { get; private set; }

    /// <summary>
    /// Writes <paramref name="nv12"/> with the layers active at <paramref name="sourceTimeMs"/>
    /// drawn over it into <paramref name="destination"/>. The source buffer is not modified: the
    /// reader may return the same decoded frame for the next request.
    /// </summary>
    internal void Compose(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        byte[] nv12,
        VideoYuvEncoding encoding,
        double sourceTimeMs,
        byte[] destination,
        int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frameLayerBitmaps);
        ArgumentNullException.ThrowIfNull(nv12);
        ArgumentNullException.ThrowIfNull(destination);
        int length = BgraToNv12Converter.RequiredLength(_width, _height);
        if (nv12.Length < length || destination.Length < length)
        {
            throw new ArgumentException("The NV12 buffers are smaller than the frame.", nameof(nv12));
        }

        string key = ActiveKey(request, frameLayerBitmaps, sourceTimeMs);
        if (!string.Equals(key, _activeKey, StringComparison.Ordinal))
        {
            RenderLayers(request, frameLayerBitmaps, sourceTimeMs);
            _activeKey = key;
        }

        Buffer.BlockCopy(nv12, 0, destination, 0, length);
        if (!_bounds.IsEmpty)
        {
            Blend(destination, _layers!, _width, _height, _bounds, encoding, maxThreads);
        }
    }

    /// <summary>
    /// Identifies what <see cref="VideoFrameRenderPipeline.DrawLayers"/> draws at a time: the
    /// active frame layers whose bitmap decoded and the active overlays with visible text, in
    /// order. Nothing else about the layers depends on time.
    /// </summary>
    internal static string ActiveKey(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        double sourceTimeMs)
    {
        var key = new StringBuilder();
        for (int index = 0; index < request.FrameEditLayers.Count; index++)
        {
            FrameEditLayer layer = request.FrameEditLayers[index];
            if (layer is not null && layer.IsActiveAt(sourceTimeMs) && frameLayerBitmaps.ContainsKey(layer.Id))
            {
                key.Append('f').Append(index).Append(';');
            }
        }

        for (int index = 0; index < request.TextOverlays.Count; index++)
        {
            TimedTextOverlay overlay = request.TextOverlays[index];
            if (overlay is not null && overlay.IsActiveAt(sourceTimeMs) && !string.IsNullOrWhiteSpace(overlay.Text))
            {
                key.Append('t').Append(index).Append(';');
            }
        }

        return key.ToString();
    }

    private void RenderLayers(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        double sourceTimeMs)
    {
        using (DrawingContext dc = _visual.RenderOpen())
        {
            VideoFrameRenderPipeline.DrawLayers(
                dc,
                _width,
                _height,
                request.CanvasWidth,
                request.CanvasHeight,
                request.TextOverlays,
                request.FrameEditLayers,
                frameLayerBitmaps,
                sourceTimeMs);
        }

        _target ??= new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        _target.Clear();
        _target.Render(_visual);
        _layers ??= new byte[checked(_width * _height * 4)];
        _target.CopyPixels(_layers, _width * 4, 0);
        _bounds = LayerBounds.Of(_layers, _width, _height);
        LayerRenderCount++;
    }

    /// <summary>
    /// Blends premultiplied BGRA <paramref name="layers"/> onto packed NV12 in place, inside
    /// <paramref name="bounds"/> (aligned to 2x2 blocks). Blocks whose four layer pixels are all
    /// transparent are left untouched.
    /// </summary>
    internal static void Blend(
        byte[] nv12,
        byte[] layers,
        int width,
        int height,
        LayerBounds bounds,
        VideoYuvEncoding encoding,
        int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(nv12);
        ArgumentNullException.ThrowIfNull(layers);
        if (bounds.IsEmpty)
        {
            return;
        }

        if (((bounds.Left | bounds.Top | bounds.Right | bounds.Bottom) & 1) != 0
            || bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height
            || layers.Length < checked(width * height * 4)
            || nv12.Length < BgraToNv12Converter.RequiredLength(width, height))
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Layer bounds must be block aligned and inside the frame.");
        }

        Nv12FrameConverter.Coefficients toRgb = Nv12FrameConverter.Coefficients.For(encoding);
        BgraToNv12Converter.Coefficients toYuv = BgraToNv12Converter.Coefficients.For(encoding);
        int firstPair = bounds.Top / 2;
        int rowPairs = (bounds.Bottom - bounds.Top) / 2;
        int threads = maxThreads > 0 ? maxThreads : Math.Min(Environment.ProcessorCount, 8);
        int bands = Math.Clamp(rowPairs / MinimumRowPairsPerBand, 1, Math.Max(1, threads));
        if (bands == 1)
        {
            BlendRowPairs(nv12, layers, width, height, bounds, toRgb, toYuv, firstPair, firstPair + rowPairs);
            return;
        }

        Parallel.For(
            0,
            bands,
            new ParallelOptions { MaxDegreeOfParallelism = bands },
            band => BlendRowPairs(
                nv12,
                layers,
                width,
                height,
                bounds,
                toRgb,
                toYuv,
                firstPair + (int)((long)rowPairs * band / bands),
                firstPair + (int)((long)rowPairs * (band + 1) / bands)));
    }

    private static void BlendRowPairs(
        byte[] nv12,
        byte[] layers,
        int width,
        int height,
        LayerBounds bounds,
        Nv12FrameConverter.Coefficients toRgb,
        BgraToNv12Converter.Coefficients toYuv,
        int firstPair,
        int endPair)
    {
        int chromaPlane = width * height;
        int layerStride = width * 4;
        Span<int> rgb = stackalloc int[12];
        Span<int> luma = stackalloc int[4];
        Span<int> alpha = stackalloc int[4];
        Span<int> lumaIndex = stackalloc int[4];
        Span<int> layerIndex = stackalloc int[4];
        for (int pair = firstPair; pair < endPair; pair++)
        {
            int topLuma = 2 * pair * width;
            int topLayer = 2 * pair * layerStride;
            int chromaRow = chromaPlane + (pair * width);
            for (int x = bounds.Left; x < bounds.Right; x += 2)
            {
                layerIndex[0] = topLayer + (x * 4);
                layerIndex[1] = layerIndex[0] + 4;
                layerIndex[2] = layerIndex[0] + layerStride;
                layerIndex[3] = layerIndex[2] + 4;
                alpha[0] = layers[layerIndex[0] + 3];
                alpha[1] = layers[layerIndex[1] + 3];
                alpha[2] = layers[layerIndex[2] + 3];
                alpha[3] = layers[layerIndex[3] + 3];
                if ((alpha[0] | alpha[1] | alpha[2] | alpha[3]) == 0)
                {
                    continue;
                }

                lumaIndex[0] = topLuma + x;
                lumaIndex[1] = lumaIndex[0] + 1;
                lumaIndex[2] = lumaIndex[0] + width;
                lumaIndex[3] = lumaIndex[2] + 1;
                int chroma = chromaRow + x;
                int u = nv12[chroma] - 128;
                int v = nv12[chroma + 1] - 128;
                int sumR = 0, sumG = 0, sumB = 0;
                for (int k = 0; k < 4; k++)
                {
                    // Nv12FrameConverter's 8-bit fixed point with replicated chroma, so the blend
                    // starts from exactly the pixels a full-frame composition would see.
                    int l = toRgb.Luma * (nv12[lumaIndex[k]] - toRgb.LumaOffset);
                    int b = Math.Clamp((l + (toRgb.BlueU * u) + 128) >> 8, 0, 255);
                    int g = Math.Clamp((l + 128 - (toRgb.GreenU * u) - (toRgb.GreenV * v)) >> 8, 0, 255);
                    int r = Math.Clamp((l + (toRgb.RedV * v) + 128) >> 8, 0, 255);
                    int a = alpha[k];
                    if (a != 0)
                    {
                        // Premultiplied source-over, rounded like WPF's software renderer.
                        int inverse = 255 - a;
                        int p = layerIndex[k];
                        b = layers[p] + (((b * inverse) + 127) / 255);
                        g = layers[p + 1] + (((g * inverse) + 127) / 255);
                        r = layers[p + 2] + (((r * inverse) + 127) / 255);
                        nv12[lumaIndex[k]] = BgraToNv12Converter.Luma(toYuv, r, g, b);
                    }

                    sumR += r;
                    sumG += g;
                    sumB += b;
                }

                nv12[chroma] = BgraToNv12Converter.ChromaU(toYuv, sumR, sumG, sumB);
                nv12[chroma + 1] = BgraToNv12Converter.ChromaV(toYuv, sumR, sumG, sumB);
            }
        }
    }

    /// <summary>Block-aligned rectangle (exclusive right and bottom) around every non-transparent layer pixel.</summary>
    internal readonly record struct LayerBounds(int Left, int Top, int Right, int Bottom)
    {
        internal bool IsEmpty => Right <= Left || Bottom <= Top;

        internal static LayerBounds Of(byte[] layers, int width, int height)
        {
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (layers[row + (x * 4) + 3] == 0)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = y;
                }
            }

            return right < 0
                ? default
                : new LayerBounds(left & ~1, top & ~1, Math.Min(width, (right + 2) & ~1), Math.Min(height, (bottom + 2) & ~1));
        }
    }
}
