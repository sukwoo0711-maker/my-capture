using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>The element a pin's live translation draws on, over the pin's content and under its outline and messages.</summary>
internal sealed class PinTranslationRevealLayer : FrameworkElement
{
    internal PinTranslationRevealLayer(PinTranslationReveal reveal)
    {
        Reveal = reveal;
        IsHitTestVisible = false;
        Focusable = false;
        // The source and the old view are drawn resampled exactly as the pin's own image is.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    internal PinTranslationReveal Reveal { get; }

    /// <summary>The pin's rounded content area in this element's coordinates.</summary>
    internal Rect Frame { get; set; }

    internal double Radius { get; set; }

    protected override void OnRender(DrawingContext drawingContext) =>
        Reveal.Draw(drawingContext, RenderSize, Frame.IsEmpty ? new Rect(RenderSize) : Frame, Radius);
}

/// <summary>
/// One live change of a pin into its translation, or back (<see cref="TranslationRevealMotion"/>). It draws only over the
/// content the pin shows: before the result the source is shown and translated blocks are drawn over it; once the result
/// is applied the translation is shown and the blocks still to change get their source drawn back over them. Every
/// frame is a function of the clock, so a test can step it. When it ends nothing of it stays: no layer, clock or brush.
/// </summary>
internal sealed class PinTranslationReveal
{
    private sealed class Block(PinRevealBlock info)
    {
        internal PinRevealBlock Info { get; } = info;
        internal double KnownAt { get; set; } = double.NaN;
        internal PinOverlayBlockLayer? Layer { get; set; }
        internal PinOverlayBlockLayer? Previous { get; set; }
        internal double SwapAt { get; set; } = double.NaN;
        // A block the final placement left as it was: its cover lifts from this time.
        internal double LiftAt { get; set; } = double.NaN;
        internal double Band { get; set; } = TranslationRevealMotion.Band;
    }

    private readonly Func<double> _now;
    private readonly Dictionary<int, Block> _blocks = [];
    private readonly TranslationRevealSchedule _schedule = new();
    private double _sweepOpacity;
    private BitmapSource? _source;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _finalApplied;
    private double _finalAt = double.NaN;
    private (ImageSource Image, Rect Destination, double Start, double Duration)? _fadeOld;
    private (Color Color, double At)? _contentCover;
    private double _abortAt = double.NaN;
    private double _finishAt = double.NaN;

    /// <param name="now">Milliseconds on a clock that only moves forward.</param>
    /// <param name="picture">A picture pin whose blocks change one by one; otherwise the whole content fades.</param>
    private PinTranslationReveal(Func<double> now, bool picture, bool glows)
    {
        _now = now;
        IsPicture = picture;
        Glows = glows;
        StartedAt = now();
    }

    /// <summary>A translation the user has just asked for: the sweep starts now.</summary>
    internal static PinTranslationReveal ForTranslation(Func<double> now, bool picture, BitmapSource? source, double meanLuminance) =>
        new(now, picture, glows: true)
        {
            _sweepOpacity = TranslationRevealMotion.SweepOpacity(meanLuminance),
            _source = picture ? source : null,
            _pixelWidth = source?.PixelWidth ?? 0,
            _pixelHeight = source?.PixelHeight ?? 0,
            Sweeping = true,
        };

    /// <summary>A switch between views the pin already has: the old view fades out over the new one.</summary>
    internal static PinTranslationReveal ForSwitch(Func<double> now, ImageSource old, Rect destination, double durationMs)
    {
        var reveal = new PinTranslationReveal(now, picture: false, glows: false) { _finalApplied = true };
        reveal._finalAt = reveal.StartedAt;
        reveal._fadeOld = (old, destination, reveal.StartedAt, durationMs);
        return reveal;
    }

    internal double StartedAt { get; }
    internal bool IsPicture { get; }
    internal bool Glows { get; }
    internal bool Sweeping { get; private init; }
    internal bool FinalApplied => _finalApplied;
    internal bool Aborting => !double.IsNaN(_abortAt);
    internal bool Ended { get; private set; }

    /// <summary>The share of accent the pin's outline takes now.</summary>
    internal double GlowAmount { get; private set; }

    /// <summary>The time from the start at which the last block's change ended, once it has.</summary>
    internal double? FinishedAfter => double.IsNaN(_finishAt) ? null : _finishAt - StartedAt;

    /// <summary>When the first and the last block began to change, from the start; null before any.</summary>
    internal (double First, double Last)? BlockStartsAfter => _schedule.Starts.Count == 0 ? null
        : (_schedule.Starts.Values.Min() - StartedAt, _schedule.Starts.Values.Max() - StartedAt);

    internal int BlockCount => _blocks.Count;
    internal int PlacedBlockCount => _blocks.Values.Count(block => block.Layer is not null);

    /// <summary>How many blocks the final placement put somewhere else than their early placement (they change over to it).</summary>
    internal int SwappedBlockCount => _blocks.Values.Count(block => block.Previous is not null);

    /// <summary>How many blocks were placed before the result was in.</summary>
    internal int EarlyPlacedCount { get; private set; }

    /// <summary>The picture's blocks to translate are known: they dim to about 0.7 from now.</summary>
    internal void BlocksKnown(int pixelWidth, int pixelHeight, IReadOnlyList<PinRevealBlock> blocks)
    {
        if (!IsPicture || _finalApplied || Aborting || pixelWidth != _pixelWidth || pixelHeight != _pixelHeight) return;
        double now = _now();
        foreach (PinRevealBlock info in blocks)
        {
            if (_blocks.ContainsKey(info.Index)) continue;
            _blocks[info.Index] = new Block(info)
            {
                KnownAt = now,
                Band = TranslationRevealMotion.BandFor(info.LineCount, info.Bounds.Width, pixelWidth),
            };
        }
    }

    /// <summary>Blocks placed before the whole translation is in: they start changing in reading order.</summary>
    internal void BlocksReady(IReadOnlyList<PinOverlayBlockLayer> layers)
    {
        if (!IsPicture || _finalApplied || Aborting) return;
        double now = _now();
        var ready = new List<int>();
        foreach (PinOverlayBlockLayer layer in layers)
        {
            Block block = BlockFor(layer);
            if (block.Layer is not null) continue;
            block.Layer = layer;
            ready.Add(layer.Index);
        }
        EarlyPlacedCount += ready.Count;
        if (ready.Count > 0) _schedule.Ready(ready, now);
    }

    private Block BlockFor(PinOverlayBlockLayer layer)
    {
        if (_blocks.TryGetValue(layer.Index, out Block? block)) return block;
        block = new Block(new PinRevealBlock(layer.Index, layer.Bounds, Colors.Transparent, 1))
        {
            Band = TranslationRevealMotion.BandFor(1, layer.Bounds.Width, _pixelWidth),
        };
        _blocks[layer.Index] = block;
        return block;
    }

    /// <summary>
    /// The translation is applied and shown. A picture gets its final blocks: a block placed differently from its preview
    /// changes over to the final one, a block the preview did not have starts changing, and a block left as it was stops
    /// dimming. Any other pin fades its source out over the translation.
    /// </summary>
    internal void ApplyFinal(IReadOnlyList<PinOverlayBlockLayer>? layers, ImageSource? old, Rect destination)
    {
        if (_finalApplied || Aborting) return;
        double now = _now();
        _finalApplied = true;
        _finalAt = now;
        if (!IsPicture || layers is null || _source is null)
        {
            if (old is not null) _fadeOld = (old, destination, now, TranslationRevealMotion.ReshowMs);
            return;
        }
        var placed = new HashSet<int>();
        var ready = new List<int>();
        foreach (PinOverlayBlockLayer layer in layers)
        {
            placed.Add(layer.Index);
            Block block = BlockFor(layer);
            if (block.Layer is null)
            {
                block.Layer = layer;
                ready.Add(layer.Index);
            }
            else if (!Started(block, now)) block.Layer = layer;
            else if (!string.Equals(block.Layer.Signature, layer.Signature, StringComparison.Ordinal))
            {
                block.Previous = block.Layer;
                block.Layer = layer;
                block.SwapAt = now;
            }
            else block.Layer = layer;
        }
        foreach (Block block in _blocks.Values)
        {
            if (placed.Contains(block.Info.Index)) continue;
            if (block.Layer is not null)
            {
                // Previewed but not placed in the end: the source comes back.
                if (Started(block, now))
                {
                    block.Previous = block.Layer;
                    block.SwapAt = now;
                }
                block.Layer = null;
            }
            block.LiftAt = now;
        }
        if (ready.Count > 0) _schedule.Ready(ready, now);
    }

    private bool Started(Block block, double now) =>
        _schedule.Starts.TryGetValue(block.Info.Index, out double start) && start <= now;

    /// <summary>The whole content dims while a text, table or Markdown pin translates.</summary>
    internal void CoverContent(Color color)
    {
        if (IsPicture || _finalApplied || Aborting || _contentCover is not null) return;
        _contentCover = (color, _now());
    }

    /// <summary>The translation stopped before it was shown (failure, cancellation, another state): what changed fades back.</summary>
    internal void Abort()
    {
        if (Aborting || Ended) return;
        _abortAt = _now();
    }

    /// <summary>Advances to the clock's time; true while there is something left to show.</summary>
    internal bool Tick()
    {
        if (Ended) return false;
        double now = _now();
        if (Aborting)
        {
            GlowAmount = 0;
            if (now - _abortAt >= TranslationRevealMotion.RevertMs) End();
            return !Ended;
        }
        if (_finalApplied && double.IsNaN(_finishAt) && LastChangeEnd() is { } end && now >= end) _finishAt = end;
        if (!double.IsNaN(_finishAt))
        {
            double since = now - _finishAt;
            GlowAmount = Glows ? TranslationRevealMotion.Glow(since) : 0;
            if (!Glows || since >= TranslationRevealMotion.GlowMs) End();
        }
        return !Ended;
    }

    /// <summary>The time the last change on screen ends, once the result is applied.</summary>
    private double? LastChangeEnd()
    {
        if (!_finalApplied) return null;
        double end = _finalAt;
        if (Sweeping) end = Math.Max(end, StartedAt + TranslationRevealMotion.SweepMs);
        if (_schedule.LastEnd is { } blocks) end = Math.Max(end, blocks);
        foreach (Block block in _blocks.Values)
        {
            if (!double.IsNaN(block.SwapAt)) end = Math.Max(end, block.SwapAt + TranslationRevealMotion.RevertMs);
            if (!double.IsNaN(block.LiftAt)) end = Math.Max(end, block.LiftAt + TranslationRevealMotion.PendingFadeMs);
        }
        if (_fadeOld is { } fade) end = Math.Max(end, fade.Start + fade.Duration);
        return end;
    }

    internal void End()
    {
        Ended = true;
        GlowAmount = 0;
        _blocks.Clear();
        _source = null;
        _fadeOld = null;
        _contentCover = null;
    }

    /// <summary>Measurement hook: the milliseconds each frame's drawing took on this thread.</summary>
    [ThreadStatic]
    internal static Action<double>? DrawObservedForTest;

    /// <summary>
    /// Draws the frame at the clock's time. <paramref name="frame"/> and <paramref name="radius"/> are the pin's rounded
    /// content area: the few pin-sized primitives (the sweep, the cover, an old view) take its corners as their own geometry,
    /// and nothing is drawn through a pin-sized clip, mask or opacity layer, which WPF renders through an intermediate
    /// surface of that size.
    /// </summary>
    internal void Draw(DrawingContext drawing, Size size, Rect frame, double radius)
    {
        if (DrawObservedForTest is { } observe)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawFrame(drawing, size, frame, radius);
            observe(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return;
        }
        DrawFrame(drawing, size, frame, radius);
    }

    /// <summary>
    /// Whether a frame drawn now can differ from the one before: something is sweeping, fading, changing or glowing. While
    /// the translation runs and nothing moves (an OCR of a large picture, a model download) the pin needs no frames.
    /// </summary>
    internal bool NeedsFrames()
    {
        if (Ended) return false;
        if (Aborting || _finalApplied) return true;
        double now = _now();
        if (Sweeping && now < StartedAt + TranslationRevealMotion.SweepMs) return true;
        if (_contentCover is { } cover && now < cover.At + TranslationRevealMotion.PendingFadeMs) return true;
        if (_fadeOld is { } old && now < old.Start + old.Duration) return true;
        if (_schedule.LastEnd is { } blocksEnd && now < blocksEnd) return true;
        foreach (Block block in _blocks.Values)
        {
            if (!double.IsNaN(block.KnownAt) && now < block.KnownAt + TranslationRevealMotion.PendingFadeMs) return true;
            if (!double.IsNaN(block.SwapAt) && now < block.SwapAt + TranslationRevealMotion.RevertMs) return true;
            if (!double.IsNaN(block.LiftAt) && now < block.LiftAt + TranslationRevealMotion.PendingFadeMs) return true;
        }
        return false;
    }

    private void DrawFrame(DrawingContext drawing, Size size, Rect frame, double radius)
    {
        if (Ended || size.Width <= 0 || size.Height <= 0) return;
        double now = _now();
        // A stopped change keeps the frame it had and fades out, each primitive through its own alpha.
        double t = Aborting ? Math.Min(now, _abortAt) : now;
        double alpha = Aborting ? 1 - TranslationRevealMotion.Fade(now - _abortAt, TranslationRevealMotion.RevertMs) : 1;
        if (alpha <= 0) return;
        if (_fadeOld is { } old)
        {
            double share = TranslationRevealMotion.Fade(t - old.Start, old.Duration);
            if (share < 1)
            {
                double keep = (1 - share) * alpha;
                // The old view fills the rounded content area through its brush, placed where it was shown.
                var view = new ImageBrush(old.Image)
                {
                    Stretch = Stretch.Fill,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = old.Destination,
                    TileMode = TileMode.None,
                    Opacity = keep,
                };
                RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality);
                view.Freeze();
                drawing.DrawRoundedRectangle(view, null, frame, radius, radius);
                if (_contentCover is { } cover)
                    DrawRounded(drawing, cover.Color, TranslationRevealMotion.PendingCoverAt(t - cover.At) * keep, frame, radius);
            }
        }
        else if (_contentCover is { } cover && !_finalApplied)
            DrawRounded(drawing, cover.Color, TranslationRevealMotion.PendingCoverAt(t - cover.At) * alpha, frame, radius);
        if (IsPicture && _pixelWidth > 0 && _pixelHeight > 0 && _blocks.Count > 0) DrawBlocks(drawing, size, t, alpha);
        if (Sweeping) DrawSweep(drawing, size, t, frame, radius, alpha);
    }

    private static void DrawRounded(DrawingContext drawing, Color color, double amount, Rect frame, double radius)
    {
        if (amount <= 0 || color.A == 0) return;
        drawing.DrawRoundedRectangle(Solid(color, amount), null, frame, radius, radius);
    }

    private void DrawBlocks(DrawingContext drawing, Size size, double t, double alpha)
    {
        double scaleX = size.Width / _pixelWidth, scaleY = size.Height / _pixelHeight;
        drawing.PushTransform(new ScaleTransform(scaleX, scaleY));
        foreach (Block block in _blocks.Values.OrderBy(block => block.Info.Index))
        {
            // A fading block fades on its own: the opacity layer is the block's size, never the pin's.
            if (alpha < 1) drawing.PushOpacity(alpha);
            DrawBlock(drawing, block, t, scaleY);
            if (alpha < 1) drawing.Pop();
        }
        drawing.Pop();
    }

    private void DrawBlock(DrawingContext drawing, Block block, double t, double scaleY)
    {
        Rect area = block.Layer is { } placed ? Rect.Union(placed.Bounds, block.Info.Bounds) : block.Info.Bounds;
        double cover = double.IsNaN(block.KnownAt) ? 0 : TranslationRevealMotion.PendingCoverAt(t - block.KnownAt);
        if (!double.IsNaN(block.LiftAt)) cover *= 1 - TranslationRevealMotion.Fade(t - block.LiftAt, TranslationRevealMotion.PendingFadeMs);
        Color background = block.Info.Background;
        if (block.Layer is { } layer && _schedule.Starts.TryGetValue(block.Info.Index, out double start))
        {
            TranslationBlockFrame frame = TranslationRevealMotion.Block(t - start, block.Band);
            if (!frame.Done || !_finalApplied || block.Previous is not null)
            {
                // Over the shown translation, the block's source comes back until its change ends.
                if (_finalApplied && !frame.Done) DrawSource(drawing, area);
                if (frame.Progress <= 0) DrawCover(drawing, block.Info.Bounds, background, cover);
                else if (!frame.Done) DrawEdge(drawing, layer, area, block.Info.Bounds, background, cover, frame, scaleY);
                else if (!_finalApplied) DrawLayer(drawing, layer, 0);
            }
        }
        else if (block.Layer is null && cover > 0)
        {
            if (_finalApplied && block.Previous is null && double.IsNaN(block.LiftAt)) return;
            DrawCover(drawing, block.Info.Bounds, background, cover);
        }
        if (block.Previous is { } previous && !double.IsNaN(block.SwapAt))
        {
            double share = TranslationRevealMotion.Fade(t - block.SwapAt, TranslationRevealMotion.RevertMs);
            if (share < 1)
            {
                drawing.PushOpacity(1 - share);
                DrawLayer(drawing, previous, 0);
                drawing.Pop();
            }
        }
    }

    private void DrawSource(DrawingContext drawing, Rect area)
    {
        if (_source is null) return;
        drawing.PushClip(new RectangleGeometry(area));
        drawing.DrawImage(_source, new Rect(0, 0, _pixelWidth, _pixelHeight));
        drawing.Pop();
    }

    private static void DrawLayer(DrawingContext drawing, PinOverlayBlockLayer layer, double rise)
    {
        drawing.DrawDrawing(layer.Surface);
        if (layer.Text is null) return;
        if (rise > 0) drawing.PushTransform(new TranslateTransform(0, rise));
        drawing.DrawDrawing(layer.Text);
        if (rise > 0) drawing.Pop();
    }

    private static void DrawCover(DrawingContext drawing, Rect bounds, Color background, double amount)
    {
        if (amount <= 0 || background.A == 0) return;
        drawing.DrawRectangle(Solid(background, amount), null, bounds);
    }

    /// <summary>The light band: its rectangle cut to the rounded content area, filled with a gradient placed on the band.</summary>
    private void DrawSweep(DrawingContext drawing, Size size, double t, Rect frame, double radius, double alpha)
    {
        if (TranslationRevealMotion.Sweep(t - StartedAt, _sweepOpacity) is not { } sweep) return;
        var band = new Rect(sweep.Left * size.Width, 0, sweep.Width * size.Width, size.Height);
        Rect visible = Rect.Intersect(band, frame);
        if (visible.IsEmpty || visible.Width <= 0) return;
        byte peak = (byte)Math.Round(255 * Math.Clamp(sweep.Opacity * alpha, 0, 1));
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(band.Left, 0),
            EndPoint = new Point(band.Right, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(peak, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        brush.Freeze();
        Geometry shape = radius > 0
            ? new CombinedGeometry(GeometryCombineMode.Intersect, new RectangleGeometry(band), new RectangleGeometry(frame, radius, radius))
            : new RectangleGeometry(visible);
        shape.Freeze();
        drawing.DrawGeometry(brush, null, shape);
    }

    /// <summary>
    /// A block part way through its change: the translation up to the moving edge, the source after it, and a soft edge of
    /// the block's own background where they meet: the translation fades into it over the band before the edge, the source
    /// comes out of it over half a band after. Only rectangle clips and gradient fills: an opacity mask or layer would draw
    /// through an intermediate surface of a new size every frame, which the graphics driver keeps afterwards.
    /// </summary>
    private static void DrawEdge(DrawingContext drawing, PinOverlayBlockLayer layer, Rect area, Rect sourceBounds, Color background,
        double cover, TranslationBlockFrame frame, double scaleY)
    {
        double solid = area.Left + (area.Width * frame.SolidEnd), edge = area.Left + (area.Width * frame.Boundary);
        double band = Math.Max(edge - solid, 0.001);
        Rect translated = Rect.Intersect(area, new Rect(area.Left, area.Top, Math.Max(0, edge - area.Left), area.Height));
        if (!translated.IsEmpty && translated.Width > 0)
        {
            drawing.PushClip(new RectangleGeometry(translated));
            DrawLayer(drawing, layer, frame.Rise / scaleY);
            drawing.Pop();
        }
        Rect rest = Rect.Intersect(sourceBounds, new Rect(edge, sourceBounds.Top, Math.Max(0, sourceBounds.Right - edge), sourceBounds.Height));
        if (cover > 0 && !rest.IsEmpty && rest.Width > 0) DrawCover(drawing, rest, background, cover);
        if (background.A == 0) return;
        Rect before = Rect.Intersect(area, new Rect(solid, area.Top, band, area.Height));
        if (!before.IsEmpty && before.Width > 0) drawing.DrawRectangle(Fade(background, solid, edge, rising: true), null, before);
        double after = band / 2;
        Rect beyond = Rect.Intersect(area, new Rect(edge, area.Top, after, area.Height));
        if (!beyond.IsEmpty && beyond.Width > 0) drawing.DrawRectangle(Fade(background, edge, edge + after, rising: false), null, beyond);
    }

    /// <summary>The block background from clear to solid between two x positions (or solid to clear).</summary>
    private static LinearGradientBrush Fade(Color background, double from, double to, bool rising)
    {
        Color clear = Color.FromArgb(0, background.R, background.G, background.B);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(from, 0),
            EndPoint = new Point(Math.Max(to, from + 0.001), 0),
            GradientStops =
            {
                new GradientStop(rising ? clear : background, 0),
                new GradientStop(rising ? background : clear, 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(Color color, double amount)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(amount, 0, 1) * color.A / 255d),
            color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The mean luminance (0..1) and colour of a picture from a few evenly spaced rows, cheap enough for the press itself.
    /// </summary>
    internal static (double Luminance, Color Color) Sample(BitmapSource? image)
    {
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0) return (1, Colors.White);
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgr32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        int rows = Math.Min(16, height);
        var row = new byte[stride];
        double r = 0, g = 0, b = 0;
        long count = 0;
        int step = Math.Max(1, width / 256);
        for (int index = 0; index < rows; index++)
        {
            int y = (int)((index + 0.5) * height / rows);
            bgra.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
            for (int x = 0; x < width; x += step)
            {
                int offset = x * 4;
                b += row[offset];
                g += row[offset + 1];
                r += row[offset + 2];
                count++;
            }
        }
        if (count == 0) return (1, Colors.White);
        r /= count;
        g /= count;
        b /= count;
        double luminance = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255;
        return (luminance, Color.FromRgb((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b)));
    }
}
