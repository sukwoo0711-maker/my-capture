using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Recording;

[Flags]
internal enum TimelineRenderLayer
{
    None = 0,
    Static = 1,
    Range = 2,
    Transient = 4,
    All = Static | Range | Transient,
}

/// <summary>
/// Lightweight timeline surface backed by three fixed <see cref="DrawingVisual"/> instances.
/// Static ticks/text, range/trim geometry, and transient playhead state can be invalidated
/// independently without allocating WPF Shape or TextBlock objects on every pointer event.
/// </summary>
internal sealed class TimelineRenderSurface : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _staticVisual = new();
    private readonly DrawingVisual _rangeVisual = new();
    private readonly DrawingVisual _transientVisual = new();
    private readonly Action<DrawingContext, TimelineRenderLayer, Size> _drawLayer;
    private TimelineRenderLayer _dirtyLayers;

    internal TimelineRenderSurface(
        double height,
        Action<DrawingContext, TimelineRenderLayer, Size> drawLayer)
    {
        Height = height;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
        _drawLayer = drawLayer ?? throw new ArgumentNullException(nameof(drawLayer));
        _visuals = new VisualCollection(this)
        {
            _staticVisual,
            _rangeVisual,
            _transientVisual,
        };

        SizeChanged += (_, _) => InvalidateLayers(TimelineRenderLayer.All);
    }

    internal event EventHandler? LayersInvalidated;

    internal int FixedVisualCount => _visuals.Count;

    internal long StaticDrawCount { get; private set; }

    internal long RangeDrawCount { get; private set; }

    internal long TransientDrawCount { get; private set; }

    internal TimelineRenderLayer DirtyLayers => _dirtyLayers;

    internal void InvalidateLayers(TimelineRenderLayer layers)
    {
        TimelineRenderLayer added = layers & ~_dirtyLayers;
        _dirtyLayers |= layers;
        if (added != TimelineRenderLayer.None)
        {
            LayersInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RenderDirtyLayers()
    {
        TimelineRenderLayer dirty = _dirtyLayers;
        if (dirty == TimelineRenderLayer.None)
        {
            return;
        }

        _dirtyLayers = TimelineRenderLayer.None;
        Size size = RenderSize;
        if ((dirty & TimelineRenderLayer.Static) != 0)
        {
            RenderLayer(_staticVisual, TimelineRenderLayer.Static, size);
            StaticDrawCount++;
        }

        if ((dirty & TimelineRenderLayer.Range) != 0)
        {
            RenderLayer(_rangeVisual, TimelineRenderLayer.Range, size);
            RangeDrawCount++;
        }

        if ((dirty & TimelineRenderLayer.Transient) != 0)
        {
            RenderLayer(_transientVisual, TimelineRenderLayer.Transient, size);
            TransientDrawCount++;
        }
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    private void RenderLayer(DrawingVisual visual, TimelineRenderLayer layer, Size size)
    {
        using DrawingContext dc = visual.RenderOpen();
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(size)));
        _drawLayer(dc, layer, size);
        dc.Pop();
    }
}
