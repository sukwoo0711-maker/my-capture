using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>Which part of the window-detect instruction describes what the pointer does now.</summary>
internal enum CaptureCueState
{
    /// <summary>Neither a click nor a drag is in progress over a detected target.</summary>
    None,

    /// <summary>A click (or a press still inside the click threshold) captures the highlighted target.</summary>
    Click,

    /// <summary>A free-region drag is in progress.</summary>
    Drag,
}

/// <summary>Where the detected-target tab sits against the highlighted region's border.</summary>
internal enum CaptureCueTabPlacement
{
    /// <summary>Outside, its bottom edge on the region's top edge.</summary>
    Above,

    /// <summary>Inside, its top edge on the region's top edge.</summary>
    InsideTop,

    /// <summary>Outside, its top edge on the region's bottom edge.</summary>
    Below,

    /// <summary>Not attached to an edge: moved clear of the top instructions or clamped to the screen.</summary>
    Floating,
}

/// <summary>
/// Pure placement for the tab that names the window or monitor a click will capture. Every value is
/// in overlay DIPs; edges land on the physical-pixel grid so the tab meets the border exactly.
/// </summary>
internal static class CaptureCueLayout
{
    /// <summary>Space kept between the tab and an instruction chip it is moved below.</summary>
    internal const double ChromeGap = 6;

    /// <summary>
    /// Places a <paramref name="tab"/> against <paramref name="target"/>: above the region, then inside
    /// its top edge, then below it. A whole monitor (<paramref name="insideOnly"/>) keeps its tab inside
    /// so it never lands on a neighbouring display or on space no display shows. Candidates that would
    /// cover a top instruction chip in <paramref name="avoid"/> are skipped; inside the region the tab
    /// moves down below the chips instead. The result always stays inside <paramref name="viewport"/>.
    /// </summary>
    /// <param name="target">Highlighted region, already snapped to physical pixels.</param>
    /// <param name="tab">Tab size; it is rounded up to whole physical pixels.</param>
    /// <param name="viewport">Overlay size.</param>
    /// <param name="avoid">Top instruction chips the tab must not cover.</param>
    /// <param name="insideOnly">True for a whole monitor.</param>
    /// <param name="pixelX">DIPs per physical pixel, horizontally.</param>
    /// <param name="pixelY">DIPs per physical pixel, vertically.</param>
    /// <param name="frame">
    /// A floating chip's spacing around a frame drawn outside the region (see
    /// <see cref="TabSpacing"/>); the default attaches the tab to the region's own edge.
    /// </param>
    internal static (Rect Bounds, CaptureCueTabPlacement Placement) PlaceTab(
        Rect target, Size tab, Size viewport, IReadOnlyList<Rect> avoid, bool insideOnly,
        double pixelX = 1, double pixelY = 1, TabSpacing frame = default)
    {
        ArgumentNullException.ThrowIfNull(avoid);
        pixelX = pixelX > 0 ? pixelX : 1;
        pixelY = pixelY > 0 ? pixelY : 1;
        double width = Math.Min(Up(tab.Width, pixelX), Down(viewport.Width, pixelX));
        double height = Math.Min(Up(tab.Height, pixelY), Down(viewport.Height, pixelY));
        Rect screen = new(0, 0, viewport.Width, viewport.Height);
        Rect visible = Rect.Intersect(target, screen);
        if (visible.IsEmpty) visible = new Rect(Math.Clamp(target.X, 0, viewport.Width), Math.Clamp(target.Y, 0, viewport.Height), 0, 0);
        // A display narrower than the tab gets a shortened tab rather than one spilling onto its neighbour.
        if (insideOnly) width = Math.Min(width, Math.Max(pixelX, Down(visible.Width - 2 * frame.Inset, pixelX)));

        // Outside the region the chip lines up with the frame's outer edge; inside, it keeps the
        // inset from the region's corner. Either way it stays on screen and, when it fits, within
        // the region's width.
        double OnRow(double left)
        {
            double value = left;
            if (width <= visible.Width) value = Math.Min(value, visible.Right - width);
            return Snap(Math.Clamp(value, 0, Math.Max(0, viewport.Width - width)), pixelX);
        }
        double outsideX = OnRow(visible.Left - frame.Outset), insideX = OnRow(visible.Left + frame.Inset);

        bool OnScreen(double y) => y >= -0.0001 && y + height <= viewport.Height + 0.0001;
        bool Clear(Rect candidate) => !avoid.Any(chrome => Overlaps(chrome, candidate));
        Rect At(double x, double y) => new(x, Snap(y, pixelY), width, height);

        if (!insideOnly)
        {
            Rect above = At(outsideX, visible.Top - frame.Outset - frame.Gap - height);
            if (OnScreen(above.Top) && Clear(above)) return (above, CaptureCueTabPlacement.Above);
        }

        // Inside: only a region that holds the whole tab, so a small window is not buried under it.
        bool fitsInside = width + 2 * frame.Inset <= visible.Width + 0.0001 && height + 2 * frame.Inset <= visible.Height + 0.0001;
        if (fitsInside || insideOnly)
        {
            Rect inside = At(insideX, visible.Top + frame.Inset);
            if (OnScreen(inside.Top) && Clear(inside)) return (inside, CaptureCueTabPlacement.InsideTop);
            // Under the chips, but still inside the region (a monitor smaller than the tab: on screen).
            Rect lowered = BelowChrome(inside);
            bool holds = !fitsInside || lowered.Bottom <= visible.Bottom - frame.Inset + 0.0001;
            if (holds && OnScreen(lowered.Top)) return (lowered, CaptureCueTabPlacement.Floating);
        }

        if (!insideOnly)
        {
            Rect below = At(outsideX, visible.Bottom + frame.Outset + frame.Gap);
            if (OnScreen(below.Top) && Clear(below)) return (below, CaptureCueTabPlacement.Below);
        }

        // Nothing fits cleanly: stay on screen at the region's top edge.
        Rect clamped = At(frame.IsDefault ? outsideX : insideX,
            Math.Clamp(visible.Top + frame.Inset, 0, Math.Max(0, viewport.Height - height)));
        return (clamped, CaptureCueTabPlacement.Floating);

        // Steps down past every chip it touches; chips are stacked, so one ordered pass suffices.
        Rect BelowChrome(Rect candidate)
        {
            foreach (Rect chrome in avoid.OrderBy(rect => rect.Top))
            {
                if (Overlaps(chrome, candidate))
                    candidate = new Rect(candidate.X, Up(chrome.Bottom + ChromeGap, pixelY), candidate.Width, candidate.Height);
            }
            return candidate;
        }
    }

    /// <summary>
    /// Spacing for a chip that floats beside a frame drawn outside the region rather than attached
    /// to the region's edge. All values are overlay DIPs.
    /// </summary>
    /// <param name="Outset">How far the frame reaches outside the region.</param>
    /// <param name="Gap">Space between the frame's outer edge and a chip placed outside it.</param>
    /// <param name="Inset">Space between the region's top-left corner and a chip flipped inside it.</param>
    internal readonly record struct TabSpacing(double Outset, double Gap, double Inset)
    {
        internal bool IsDefault => Outset == 0 && Gap == 0 && Inset == 0;
    }

    /// <summary>Positive-area intersection; rectangles that only share an edge do not overlap.</summary>
    internal static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right - 0.0001 && b.Left < a.Right - 0.0001 && a.Top < b.Bottom - 0.0001 && b.Top < a.Bottom - 0.0001;

    private static double Snap(double value, double pixel) => Math.Round(value / pixel) * pixel;

    private static double Up(double value, double pixel) => Math.Ceiling(value / pixel - 0.0001) * pixel;

    private static double Down(double value, double pixel) => Math.Floor(value / pixel + 0.0001) * pixel;
}
