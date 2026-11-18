using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyCapture.App.Capture;

/// <summary>
/// The size read-out and hover tab chip: a flat surface whose window DWM rounds on Windows 11. Where
/// DWM cannot (Windows 10, or a chip stamped into the selector's own bitmap) it draws a one-pixel
/// edge itself; the edge is drawing only, so it never moves the text.
/// </summary>
internal sealed class NativeSelectorReadoutBorder(Brush outline) : Border
{
    private readonly Pen _outline = new(outline, 1);
    private double _scale = 1;
    private bool _outlined = true;
    internal bool ShowsHoverTab { get; set; }

    /// <summary>True while the chip draws its own edge (no DWM border around its window).</summary>
    internal bool Outlined
    {
        get => _outlined;
        set { if (_outlined == value) return; _outlined = value; InvalidateVisual(); }
    }

    internal void SetScale(double scale)
    {
        if (!(scale > 0) || _scale == scale) return;
        _scale = scale;
        _outline.Thickness = 1 / scale;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Background, null, bounds);
        if (!_outlined || bounds.Width <= 0 || bounds.Height <= 0) return;
        // One physical pixel along the inside of the chip's edge.
        double half = _outline.Thickness / 2;
        dc.DrawRectangle(null, _outline, new Rect(half, half,
            Math.Max(0, bounds.Width - _outline.Thickness), Math.Max(0, bounds.Height - _outline.Thickness)));
    }
}
