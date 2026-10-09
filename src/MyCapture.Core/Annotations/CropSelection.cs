using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

public enum CropGrip { New, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>A pending crop in image pixels. It never changes the annotation document.</summary>
public sealed class CropSelection
{
    private PointD _start;
    private RectD _initial;
    private double _minimumX, _minimumY;
    private bool _newDragStarted;
    public CropSelection(int width, int height)
    {
        Bounds = new RectD(0, 0, Math.Max(1, width), Math.Max(1, height));
        Region = Bounds;
    }
    public RectD Bounds { get; }
    public RectD Region { get; private set; }
    public double? Ratio { get; private set; }
    public bool IsDragging { get; private set; }
    public CropGrip Grip { get; private set; }

    public CropGrip HitTest(PointD point, double tolerance) =>
        HitTestEdge(Region, point, tolerance) ?? (Contains(point) ? CropGrip.Move : CropGrip.New);

    /// <summary>Entire edges are draggable; corners take precedence. No hover allocations.</summary>
    public static CropGrip? HitTestEdge(RectD region, PointD point, double tolerance)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !double.IsFinite(tolerance) || tolerance < 0 || region.IsEmpty) return null;
        double tx = Math.Min(tolerance, region.Width / 3);
        double ty = Math.Min(tolerance, region.Height / 3);
        if (point.X < region.Left - tx || point.X > region.Right + tx
            || point.Y < region.Top - ty || point.Y > region.Bottom + ty) return null;
        bool left = Math.Abs(point.X - region.Left) <= tx;
        bool right = Math.Abs(point.X - region.Right) <= tx;
        bool top = Math.Abs(point.Y - region.Top) <= ty;
        bool bottom = Math.Abs(point.Y - region.Bottom) <= ty;
        if (top && left) return CropGrip.TopLeft;
        if (top && right) return CropGrip.TopRight;
        if (bottom && left) return CropGrip.BottomLeft;
        if (bottom && right) return CropGrip.BottomRight;
        if (top) return CropGrip.Top;
        if (bottom) return CropGrip.Bottom;
        if (left) return CropGrip.Left;
        if (right) return CropGrip.Right;
        return null;
    }

    public void SetRegion(RectD region)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
            || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)) return;
        RectD bounded = region.Normalized().ClampTo(Bounds).ToPixelBounds();
        if (bounded.Width < 1 || bounded.Height < 1) return;
        End();
        Ratio = null;
        Region = bounded;
    }

    public bool Contains(PointD p) => p.X >= Region.Left && p.X <= Region.Right && p.Y >= Region.Top && p.Y <= Region.Bottom;
    public void Begin(PointD point, double tolerance, double minimumX = 0, double minimumY = 0)
    {
        Grip = HitTest(point, tolerance);
        _start = Clamp(point);
        _initial = Region;
        _minimumX = Math.Max(0, minimumX); _minimumY = Math.Max(0, minimumY);
        _newDragStarted = false;
        IsDragging = true;
    }

    public void Drag(PointD point, bool shift)
    {
        if (!IsDragging) return;
        PointD p = Clamp(point);
        if (Grip == CropGrip.New && !_newDragStarted)
        {
            if (Math.Abs(p.X - _start.X) < _minimumX && Math.Abs(p.Y - _start.Y) < _minimumY) return;
            _newDragStarted = true;
        }
        if (Grip == CropGrip.Move)
        {
            Region = MoveWithin(_initial, p.X - _start.X, p.Y - _start.Y);
            return;
        }
        bool horizontal = Grip is CropGrip.Left or CropGrip.Right;
        bool vertical = Grip is CropGrip.Top or CropGrip.Bottom;
        bool left = Grip is CropGrip.Left or CropGrip.TopLeft or CropGrip.BottomLeft;
        bool top = Grip is CropGrip.Top or CropGrip.TopLeft or CropGrip.TopRight;
        PointD anchor = Grip == CropGrip.New ? _start : new PointD(
            vertical ? _initial.Center.X : left ? _initial.Right : _initial.Left,
            horizontal ? _initial.Center.Y : top ? _initial.Bottom : _initial.Top);
        double dx = p.X - anchor.X, dy = p.Y - anchor.Y;
        double width = vertical ? _initial.Width : Math.Max(1, Math.Abs(dx));
        double height = horizontal ? _initial.Height : Math.Max(1, Math.Abs(dy));
        double sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        double? ratio = Ratio ?? (shift ? (Grip == CropGrip.New ? 1 : _initial.Width / _initial.Height) : null);
        if (ratio is double aspect)
        {
            if (vertical) width = height * aspect;
            else if (horizontal || width / height > aspect) height = width / aspect;
            else width = height * aspect;
        }
        double availableWidth = vertical ? 2 * Math.Min(anchor.X, Bounds.Right - anchor.X) : sx < 0 ? anchor.X : Bounds.Right - anchor.X;
        double availableHeight = horizontal ? 2 * Math.Min(anchor.Y, Bounds.Bottom - anchor.Y) : sy < 0 ? anchor.Y : Bounds.Bottom - anchor.Y;
        if (ratio.HasValue)
        {
            double scale = Math.Min(1, Math.Min(availableWidth / width, availableHeight / height));
            width *= scale; height *= scale;
        }
        else { width = Math.Min(width, availableWidth); height = Math.Min(height, availableHeight); }
        if (width < 1 || height < 1) return;
        Region = new RectD(vertical ? anchor.X - width / 2 : sx < 0 ? anchor.X - width : anchor.X,
            horizontal ? anchor.Y - height / 2 : sy < 0 ? anchor.Y - height : anchor.Y, width, height);
    }

    public void End() => IsDragging = false;
    public void Nudge(double x, double y) => Region = MoveWithin(Region, x, y);
    public void SetRatio(double? ratio)
    {
        Ratio = ratio is > 0 && double.IsFinite(ratio.Value) ? ratio : null;
        if (Ratio is not double aspect) return;
        double width = Math.Min(Region.Width, Region.Height * aspect);
        Resize(width, width / aspect);
    }
    public void SetWidth(double width) => Resize(width, Ratio is double aspect ? width / aspect : Region.Height);
    public void SetHeight(double height) => Resize(Ratio is double aspect ? height * aspect : Region.Width, height);
    private void Resize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return;
        if (Ratio.HasValue)
        {
            double scale = Math.Min(1, Math.Min(Bounds.Width / width, Bounds.Height / height));
            width *= scale; height *= scale;
        }
        else { width = Math.Min(width, Bounds.Width); height = Math.Min(height, Bounds.Height); }
        Region = new RectD(Math.Clamp(Region.Center.X - width / 2, 0, Bounds.Width - width),
            Math.Clamp(Region.Center.Y - height / 2, 0, Bounds.Height - height), width, height);
    }
    private RectD MoveWithin(RectD region, double x, double y) => new(
        Math.Clamp(region.X + x, 0, Bounds.Width - region.Width), Math.Clamp(region.Y + y, 0, Bounds.Height - region.Height), region.Width, region.Height);
    private PointD Clamp(PointD point) => new(Math.Clamp(point.X, 0, Bounds.Width), Math.Clamp(point.Y, 0, Bounds.Height));
}
