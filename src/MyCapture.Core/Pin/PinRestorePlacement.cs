using MyCapture.Core.Primitives;

namespace MyCapture.Core.Pin;

/// <summary>
/// One display as restore placement sees it, in physical virtual-desktop pixels.
/// </summary>
/// <param name="DeviceName">Adapter device name, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">Full monitor rectangle.</param>
/// <param name="WorkArea">Bounds minus the taskbar and other appbars.</param>
/// <param name="ScaleFactor">DPI scale, 1.5 at 150%.</param>
public readonly record struct PinDisplayArea(string DeviceName, RectD Bounds, RectD WorkArea, double ScaleFactor);

/// <summary>
/// Where a pin was when it closed, in physical virtual-desktop pixels.
/// </summary>
/// <param name="Bounds">The pin window's bounds at close.</param>
/// <param name="DeviceName">The display the pin was on.</param>
/// <param name="DisplayBounds">That display's bounds at close, to detect a moved display.</param>
public readonly record struct PinClosedPlacement(RectD Bounds, string DeviceName, RectD DisplayBounds);

/// <summary>
/// Pure placement for a restored pin: put it back where it closed, and only when the
/// display layout no longer shows it there, move it into a visible work area.
/// </summary>
/// <remarks>
/// <para>
/// A pin is sized in DIP, so its physical size depends on the DPI of the display it lands
/// on. The restored width and height are therefore recomputed from the DIP size and the
/// target display's scale instead of reusing the physical size recorded at close.
/// </para>
/// <para>
/// When the original display still exists but moved (a dock or rearranged monitors), the
/// pin keeps its offset from that display's origin. When the display is gone, the display
/// with the largest overlap, or else the nearest one, becomes the target. A pin that is not
/// fully on the current displays is clamped into the target's work area so it is visible
/// and reachable; one that fits is left exactly where it was, even across two displays.
/// </para>
/// </remarks>
public static class PinRestorePlacement
{
    /// <summary>The physical window bounds to restore to and the display chosen.</summary>
    /// <param name="DisplayIndex">Index into the supplied displays, or -1 when none were supplied.</param>
    /// <param name="Adjusted">Whether the closed position had to change.</param>
    public readonly record struct Result(
        double Left,
        double Top,
        double Width,
        double Height,
        int DisplayIndex,
        bool Adjusted);

    public static Result Resolve(
        PinClosedPlacement closed,
        double widthDip,
        double heightDip,
        IReadOnlyList<PinDisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        RectD saved = closed.Bounds.Normalized();

        if (displays.Count == 0)
        {
            return new Result(saved.Left, saved.Top, saved.Width, saved.Height, -1, Adjusted: false);
        }

        double left = saved.Left;
        double top = saved.Top;
        bool adjusted = false;

        int index = IndexOfDevice(displays, closed.DeviceName);
        if (index >= 0)
        {
            RectD now = displays[index].Bounds.Normalized();
            RectD then = closed.DisplayBounds.Normalized();
            if (!then.IsEmpty && (!SamePixel(now.Left, then.Left) || !SamePixel(now.Top, then.Top)))
            {
                // The same display moved on the virtual desktop: keep the pin's offset from it.
                left = now.Left + (saved.Left - then.Left);
                top = now.Top + (saved.Top - then.Top);
                adjusted = true;
            }
        }
        else
        {
            index = BestDisplayFor(saved, displays);
        }

        PinDisplayArea target = displays[index];
        double scale = target.ScaleFactor > 0 ? target.ScaleFactor : 1.0;
        double width = Math.Max(1.0, widthDip * scale);
        double height = Math.Max(1.0, heightDip * scale);

        if (!IsFullyOnDisplays(new RectD(left, top, width, height), displays))
        {
            RectD work = target.WorkArea.IsEmpty ? target.Bounds.Normalized() : target.WorkArea.Normalized();
            (left, top) = PinGeometry.ClampTopLeftIntoWork(
                left, top, width, height, work.Left, work.Top, work.Width, work.Height);
            adjusted = true;
        }

        return new Result(left, top, width, height, index, adjusted);
    }

    /// <summary>
    /// Whether the rectangle lies on the union of display bounds. Windows displays never
    /// overlap, so summing per-display intersections measures the covered area; a one-pixel
    /// strip of tolerance absorbs DIP-to-pixel rounding.
    /// </summary>
    public static bool IsFullyOnDisplays(RectD rect, IReadOnlyList<PinDisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        RectD r = rect.Normalized();
        double area = r.Width * r.Height;
        if (area <= 0)
        {
            return false;
        }

        double covered = 0;
        foreach (PinDisplayArea display in displays)
        {
            covered += IntersectionArea(r, display.Bounds.Normalized());
        }

        return area - covered <= Math.Max(r.Width, r.Height);
    }

    private static int IndexOfDevice(IReadOnlyList<PinDisplayArea> displays, string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return -1;
        }

        for (int i = 0; i < displays.Count; i++)
        {
            if (string.Equals(displays[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int BestDisplayFor(RectD rect, IReadOnlyList<PinDisplayArea> displays)
    {
        int best = 0;
        double bestOverlap = 0;
        for (int i = 0; i < displays.Count; i++)
        {
            double overlap = IntersectionArea(rect, displays[i].Bounds.Normalized());
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = i;
            }
        }

        if (bestOverlap > 0)
        {
            return best;
        }

        // Entirely off every display: the nearest display to the pin's centre.
        PointD center = rect.Center;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < displays.Count; i++)
        {
            RectD b = displays[i].Bounds.Normalized();
            double dx = Math.Max(Math.Max(b.Left - center.X, 0), center.X - b.Right);
            double dy = Math.Max(Math.Max(b.Top - center.Y, 0), center.Y - b.Bottom);
            double distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static double IntersectionArea(RectD a, RectD b)
    {
        double width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static bool SamePixel(double a, double b) => Math.Abs(a - b) < 0.5;
}
