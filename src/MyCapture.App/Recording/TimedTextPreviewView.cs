using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>Letterbox-aware preview surface that uses the exact final-output text compositor.</summary>
internal sealed class TimedTextPreviewView : FrameworkElement
{
    private sealed record CachedFrame(string Base64, BitmapSource? Bitmap, long LastUse);
    private long _cacheClock;
    internal long InactiveCacheBudgetBytes { get; set; } = 32 * 1024 * 1024;
    internal int DecodeAttempts { get; private set; }
    internal long InactiveCachedBytes => _decodedFrameCache.Where(p => !_frameLayers.Any(l => l.Id == p.Key && l.IsActiveAt(_sourceTimeMs))).Sum(p => BitmapBytes(p.Value.Bitmap));
    private static long BitmapBytes(BitmapSource? bitmap) => bitmap is null ? 0 : (long)bitmap.PixelWidth * bitmap.PixelHeight * ((bitmap.Format.BitsPerPixel + 7) / 8);

    private readonly Dictionary<Guid, CachedFrame> _decodedFrameCache = new();
    private IReadOnlyList<TimedTextOverlay> _overlays = [];
    private IReadOnlyList<FrameEditLayer> _frameLayers = [];
    private IReadOnlyDictionary<Guid, BitmapSource> _frameLayerBitmaps =
        new Dictionary<Guid, BitmapSource>();
    private double _sourceTimeMs;
    private int _canvasWidth = 1;
    private int _canvasHeight = 1;

    internal TimedTextPreviewView()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    internal void SetCanvas(int width, int height)
    {
        int nextWidth = Math.Max(1, width);
        int nextHeight = Math.Max(1, height);
        if (nextWidth == _canvasWidth && nextHeight == _canvasHeight)
        {
            return;
        }

        _canvasWidth = nextWidth;
        _canvasHeight = nextHeight;
        InvalidateVisual();
    }

    internal void SetOverlays(IReadOnlyList<TimedTextOverlay> overlays)
    {
        _overlays = overlays ?? [];
        InvalidateVisual();
    }

    internal void SetFrameLayers(IReadOnlyList<FrameEditLayer> layers)
    {
        VideoLayerResourceBudget.Validate(layers);
        _frameLayers = layers ?? [];

        RefreshDecodedFrames();
        InvalidateVisual();
    }

    private void RefreshDecodedFrames()
    {
        var current = _frameLayers.Take(VideoEditDocument.MaximumFrameLayerCount)
            .GroupBy(layer => layer.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (Guid id in _decodedFrameCache.Keys.ToArray())
        {
            if (!current.TryGetValue(id, out FrameEditLayer? layer)
                || _decodedFrameCache[id].Base64 != layer.OverlayPngBase64)
            {
                _decodedFrameCache.Remove(id);
            }
        }

        var active = new Dictionary<Guid, BitmapSource>();
        foreach (FrameEditLayer layer in current.Values)
        {
            if (!layer.IsActiveAt(_sourceTimeMs)) { continue; }
            if (!_decodedFrameCache.TryGetValue(layer.Id, out CachedFrame? cached))
            {
                DecodeAttempts++;
                FrameEditLayerRenderer.Decode([layer]).TryGetValue(layer.Id, out BitmapSource? bitmap);
                cached = new CachedFrame(layer.OverlayPngBase64, bitmap, ++_cacheClock);
            }

            _decodedFrameCache[layer.Id] = cached with { LastUse = ++_cacheClock };
            if (cached.Bitmap is { } decoded) { active[layer.Id] = decoded; }
        }

        // Active layers are required for correctness. Only inactive retention has a byte budget;
        // never decode unseen inactive entries just to decide whether to retain them.
        long retained = 0;
        foreach (var pair in _decodedFrameCache.OrderByDescending(p => p.Value.LastUse).ToArray())
        {
            if (current[pair.Key].IsActiveAt(_sourceTimeMs)) { continue; }
            long bytes = BitmapBytes(pair.Value.Bitmap);
            if (retained + bytes > InactiveCacheBudgetBytes) { _decodedFrameCache.Remove(pair.Key); }
            else { retained += bytes; }
        }

        _frameLayerBitmaps = active;
    }
    internal void SetSourceTime(double sourceTimeMs)
    {
        double next = double.IsFinite(sourceTimeMs) ? Math.Max(0, sourceTimeMs) : 0;
        if (Math.Abs(next - _sourceTimeMs) < 0.0001)
        {
            return;
        }

        // The compositor is visually time-invariant while the same overlays are active.
        // Avoid scheduling a WPF render on every playback/scrub tick when there is no
        // visible text transition; the exact source time is still retained for the next
        // overlay or canvas update.
        bool activeSetChanged = false;
        for (int index = 0; index < _overlays.Count; index++)
        {
            TimedTextOverlay overlay = _overlays[index];
            if (overlay.IsActiveAt(_sourceTimeMs) != overlay.IsActiveAt(next))
            {
                activeSetChanged = true;
                break;
            }
        }

        if (!activeSetChanged)
        {
            for (int index = 0; index < _frameLayers.Count; index++)
            {
                FrameEditLayer layer = _frameLayers[index];
                if (layer.IsActiveAt(_sourceTimeMs) != layer.IsActiveAt(next))
                {
                    activeSetChanged = true;
                    break;
                }
            }
        }

        _sourceTimeMs = next;
        if (activeSetChanged)
        {
            RefreshDecodedFrames();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0
            || ActualHeight <= 0
            || (_overlays.Count == 0 && _frameLayers.Count == 0))
        {
            return;
        }

        double scale = Math.Min(ActualWidth / _canvasWidth, ActualHeight / _canvasHeight);
        double contentWidth = _canvasWidth * scale;
        double contentHeight = _canvasHeight * scale;
        double left = (ActualWidth - contentWidth) / 2;
        double top = (ActualHeight - contentHeight) / 2;
        var content = new Rect(left, top, contentWidth, contentHeight);

        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(scale, scale));
        transforms.Children.Add(new TranslateTransform(left, top));
        drawingContext.PushClip(new RectangleGeometry(content));
        drawingContext.PushTransform(transforms);
        FrameEditLayerRenderer.Draw(
            drawingContext,
            _frameLayers,
            _frameLayerBitmaps,
            _sourceTimeMs,
            _canvasWidth,
            _canvasHeight);
        TimedTextOverlayRenderer.Draw(
            drawingContext,
            _overlays,
            _sourceTimeMs,
            _canvasWidth,
            _canvasHeight,
            pixelsPerDip: 1.0);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
