using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>Source-coordinate selection overlay. The actual artwork is drawn by the export compositor.</summary>
internal sealed class VideoLayerCanvas : FrameworkElement
{
    private VideoEditDocument _document = VideoEditDocument.CreateFor(1, 1, 1);
    private double _time;
    private Point _start;
    private Rect _original;
    private VideoLayerBounds? _originalModelBounds;
    private int _corner = -1;
    private bool _dragging;
    internal Guid? SelectedId { get; private set; }
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? InteractionStarted;
    internal event EventHandler? BoundsChanged;
    internal event EventHandler? InteractionCompleted;
    internal event EventHandler? LayerActivated;

    internal VideoLayerCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, UiText.Get("Text_FA67EC449E04"));
        ToolTip = UiText.Get("Video.EditTextHint");
    }

    internal void SetDocument(VideoEditDocument document) { _document = document; InvalidateVisual(); }
    internal void SetSourceTime(double time)
    {
        bool changed = _document.TextOverlays.Any(layer => layer.IsActiveAt(_time) != layer.IsActiveAt(time))
            || _document.FrameEditLayers.Any(layer => layer.IsActiveAt(_time) != layer.IsActiveAt(time));
        _time = time;
        if (changed) { InvalidateVisual(); }
    }
    internal void Select(Guid? id) { SelectedId = id; InvalidateVisual(); }

    internal Rect ContentRect
    {
        get
        {
            double scale = Math.Min(ActualWidth / _document.CanvasWidth, ActualHeight / _document.CanvasHeight);
            double width = _document.CanvasWidth * scale;
            double height = _document.CanvasHeight * scale;
            return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        }
    }

    private Dictionary<Guid, Rect> ActiveBounds()
    {
        var bounds = new Dictionary<Guid, Rect>();
        foreach (FrameEditLayer layer in _document.FrameEditLayers.Where(layer => layer.IsActiveAt(_time)))
        {
            bounds[layer.Id] = FrameEditLayerRenderer.GetBounds(layer.Bounds, _document.CanvasWidth, _document.CanvasHeight);
        }
        foreach (var pair in TimedTextOverlayRenderer.GetBounds(_document.TextOverlays, _time, _document.CanvasWidth, _document.CanvasHeight))
        {
            bounds[pair.Key] = pair.Value;
        }
        return bounds;
    }

    private Rect ToView(Rect source)
    {
        Rect content = ContentRect;
        double scale = content.Width / _document.CanvasWidth;
        return new Rect(content.X + source.X * scale, content.Y + source.Y * scale, source.Width * scale, source.Height * scale);
    }

    private static Point[] Corners(Rect rect) => [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (SelectedId is not { } id || !ActiveBounds().TryGetValue(id, out Rect bounds)) { return; }
        Rect view = ToView(bounds);
        var pen = new Pen(Brushes.DeepSkyBlue, IsKeyboardFocused ? 2 : 1);
        dc.DrawRectangle(null, pen, view);
        foreach (Point corner in Corners(view))
        {
            dc.DrawRectangle(Brushes.White, pen, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point point = e.GetPosition(this);
        var bounds = ActiveBounds();
        _corner = -1;
        if (SelectedId is { } selected && bounds.TryGetValue(selected, out Rect selection))
        {
            Point[] corners = Corners(ToView(selection));
            _corner = Array.FindIndex(corners, p => Math.Abs(p.X - point.X) <= 9 && Math.Abs(p.Y - point.Y) <= 9);
        }

        if (_corner < 0)
        {
            SelectedId = bounds.Reverse().Where(pair => ToView(pair.Value).Contains(point)).Select(pair => (Guid?)pair.Key).FirstOrDefault();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (SelectedId is { } id && bounds.TryGetValue(id, out _original))
        {
            _start = point;
            _originalModelBounds = GetModelBounds(id);
            _dragging = CaptureMouse();
            if (_dragging) { InteractionStarted?.Invoke(this, EventArgs.Empty); }
        }
        InvalidateVisual();
        if (e.ClickCount >= 2 && SelectedId is not null)
        {
            LayerActivated?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || SelectedId is not { } id || ContentRect.Width <= 0) { return; }
        Vector delta = (e.GetPosition(this) - _start) * (_document.CanvasWidth / ContentRect.Width);
        Rect next = Manipulate(_original, delta, _corner, _document.CanvasWidth, _document.CanvasHeight);
        SetModelBounds(id, new(next.X / _document.CanvasWidth, next.Y / _document.CanvasHeight,
            next.Width / _document.CanvasWidth, next.Height / _document.CanvasHeight));
        BoundsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
    }

    internal static Rect Manipulate(Rect original, Vector delta, int corner, double width, double height)
    {
        if (corner < 0)
        {
            return new Rect(Math.Clamp(original.X + delta.X, 0, Math.Max(0, width - original.Width)),
                Math.Clamp(original.Y + delta.Y, 0, Math.Max(0, height - original.Height)), original.Width, original.Height);
        }
        double minWidth = Math.Min(original.Width, width * 0.01);
        double minHeight = Math.Min(original.Height, height * 0.01);
        double left = corner is 0 or 3 ? Math.Clamp(original.Left + delta.X, 0, original.Right - minWidth) : original.Left;
        double right = corner is 1 or 2 ? Math.Clamp(original.Right + delta.X, original.Left + minWidth, width) : original.Right;
        double top = corner is 0 or 1 ? Math.Clamp(original.Top + delta.Y, 0, original.Bottom - minHeight) : original.Top;
        double bottom = corner is 2 or 3 ? Math.Clamp(original.Bottom + delta.Y, original.Top + minHeight, height) : original.Bottom;
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) { return; }
        EndInteraction();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) { EndInteraction(); }
    }

    private void EndInteraction()
    {
        _dragging = false;
        ReleaseMouseCapture();
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (SelectedId is not { } id) { return; }
        if (e.Key == Key.Escape && _dragging)
        {
            SetModelBounds(id, _originalModelBounds);
            BoundsChanged?.Invoke(this, EventArgs.Empty);
            EndInteraction();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && ActiveBounds().TryGetValue(id, out Rect bounds))
        {
            InteractionStarted?.Invoke(this, EventArgs.Empty);
            var delta = new Vector(e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0);
            Rect next = Manipulate(bounds, delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2 : -1,
                _document.CanvasWidth, _document.CanvasHeight);
            SetModelBounds(id, new(next.X / _document.CanvasWidth, next.Y / _document.CanvasHeight,
                next.Width / _document.CanvasWidth, next.Height / _document.CanvasHeight));
            BoundsChanged?.Invoke(this, EventArgs.Empty);
            InteractionCompleted?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private VideoLayerBounds? GetModelBounds(Guid id) => _document.TextOverlays.FirstOrDefault(layer => layer.Id == id)?.Bounds
        ?? _document.FrameEditLayers.FirstOrDefault(layer => layer.Id == id)?.Bounds;

    private void SetModelBounds(Guid id, VideoLayerBounds? bounds)
    {
        if (_document.TextOverlays.FirstOrDefault(layer => layer.Id == id) is { } text) { text.Bounds = bounds; }
        else if (_document.FrameEditLayers.FirstOrDefault(layer => layer.Id == id) is { } frame) { frame.Bounds = bounds; }
    }
}
