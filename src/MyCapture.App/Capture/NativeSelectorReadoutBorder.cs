using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyCapture.App.Capture;

/// <summary>A drawing-only outline leaves the shared 9 by 5 text padding unchanged.</summary>
internal sealed class NativeSelectorReadoutBorder(Brush outline) : Border
{
    private readonly Pen _outline = new(outline, 1);
    internal bool ShowsHoverTab { get; set; }

    internal void SetScale(double scale)
    {
        double thickness = 1 / scale;
        if (_outline.Thickness == thickness) return;
        _outline.Thickness = thickness;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!ShowsHoverTab) dc.DrawRoundedRectangle(Background, _outline, new Rect(RenderSize), 7, 7);
    }
}
