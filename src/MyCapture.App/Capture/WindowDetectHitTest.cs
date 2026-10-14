using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// Pure geometry behind the region overlay's window auto-detect: which top-level window is
/// under the pointer, what region a click on it selects, and when a press counts as a click.
/// </summary>
/// <remarks>
/// Candidates come from <see cref="WindowCandidateService.GetCandidates"/>, which enumerates
/// visible top-level windows front to back, already excludes this process (so the overlay can
/// never detect itself) and clips each rectangle to the virtual desktop. Only top-level windows
/// are considered; child controls and UI Automation elements are deliberately out of scope.
/// Empty desktop space belongs to the desktop shell window, which maps to one whole monitor.
/// </remarks>
internal static class WindowDetectHitTest
{
    /// <summary>Smallest detected region, matching the overlay's minimum selection.</summary>
    internal const double MinimumRegionPixels = 2;

    /// <summary>
    /// Returns the frontmost candidate whose rectangle contains <paramref name="screenPoint"/>,
    /// or <see langword="null"/> over empty desktop space.
    /// </summary>
    /// <param name="candidates">Top-level windows in z-order, frontmost first.</param>
    /// <param name="screenPoint">Pointer position in virtual-desktop physical pixels.</param>
    /// <param name="excludedHandle">The overlay's own window, ignored even if enumerated.</param>
    internal static WindowCandidate? FindTopmost(
        IReadOnlyList<WindowCandidate> candidates,
        PointD screenPoint,
        IntPtr excludedHandle = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // Runs on every pointer move: index the list instead of boxing an enumerator each time.
        for (int index = 0; index < candidates.Count; index++)
        {
            WindowCandidate candidate = candidates[index];
            if (excludedHandle != IntPtr.Zero && candidate.Handle == excludedHandle) continue;
            RectD bounds = candidate.ScreenBounds.Normalized();
            if (bounds.Width < MinimumRegionPixels || bounds.Height < MinimumRegionPixels) continue;

            // Half-open containment: the pixel just past the right/bottom edge belongs to the
            // neighbouring window, so adjacent windows never both claim a boundary pixel.
            if (screenPoint.X >= bounds.Left && screenPoint.X < bounds.Right &&
                screenPoint.Y >= bounds.Top && screenPoint.Y < bounds.Bottom)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Region a hover or click on <paramref name="window"/> selects, in overlay bitmap pixels.
    /// The desktop shell window spans every display, so it resolves to the full bounds (not the
    /// work area) of the monitor under the pointer; every other window, including the taskbar,
    /// keeps its own rectangle. <paramref name="monitors"/> is read only for the desktop.
    /// </summary>
    /// <param name="window">The frontmost window under the pointer.</param>
    /// <param name="candidates">The enumeration <paramref name="window"/> came from.</param>
    /// <param name="screenPoint">Pointer position in virtual-desktop physical pixels.</param>
    /// <param name="desktopScreenBounds">The frozen virtual desktop in physical pixels.</param>
    /// <param name="monitors">Display layout in the same physical-pixel plane.</param>
    /// <param name="wholeMonitor">True when the region is the pointer's monitor.</param>
    internal static RectD? ResolveRegion(
        WindowCandidate window,
        IReadOnlyList<WindowCandidate> candidates,
        PointD screenPoint,
        RectD desktopScreenBounds,
        Func<IReadOnlyList<MonitorInfo>> monitors,
        out bool wholeMonitor)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitors);
        wholeMonitor = false;
        if (WindowCandidateService.IsDesktopShell(window, candidates) &&
            FindMonitorBounds(monitors(), screenPoint) is { } monitor &&
            ToBitmapRegion(monitor, desktopScreenBounds) is { } monitorRegion)
        {
            wholeMonitor = true;
            return monitorRegion;
        }

        return ToBitmapRegion(window, desktopScreenBounds);
    }

    /// <summary>
    /// Full bounds of the display containing <paramref name="screenPoint"/>, or of the nearest
    /// display when the point lies outside all of them (MONITOR_DEFAULTTONEAREST semantics).
    /// Containment is half-open like window hits, so a shared edge belongs to one display only.
    /// Bounds stay in physical pixels; mixed DPI needs no scaling on the PerMonitorV2 plane.
    /// </summary>
    internal static RectD? FindMonitorBounds(IReadOnlyList<MonitorInfo> monitors, PointD screenPoint)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        RectD? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        // Runs on every pointer move over the desktop: index the list instead of boxing an enumerator.
        for (int index = 0; index < monitors.Count; index++)
        {
            RectD bounds = monitors[index].Bounds.Normalized();
            if (bounds.IsEmpty) continue;
            if (screenPoint.X >= bounds.Left && screenPoint.X < bounds.Right &&
                screenPoint.Y >= bounds.Top && screenPoint.Y < bounds.Bottom)
            {
                return bounds;
            }

            double dx = Math.Max(0, Math.Max(bounds.Left - screenPoint.X, screenPoint.X - bounds.Right));
            double dy = Math.Max(0, Math.Max(bounds.Top - screenPoint.Y, screenPoint.Y - bounds.Bottom));
            double distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = bounds;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Maps a detected window to overlay bitmap pixels: the window rectangle clipped to the
    /// frozen desktop, rounded outward to whole pixels. Returns <see langword="null"/> when
    /// nothing usable remains on the desktop.
    /// </summary>
    internal static RectD? ToBitmapRegion(WindowCandidate window, RectD desktopScreenBounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        return ToBitmapRegion(window.ScreenBounds, desktopScreenBounds);
    }

    private static RectD? ToBitmapRegion(RectD screenBounds, RectD desktopScreenBounds)
    {
        RectD desktop = desktopScreenBounds.Normalized();
        RectD bounds = screenBounds.Normalized();
        double left = Math.Max(bounds.Left, desktop.Left);
        double top = Math.Max(bounds.Top, desktop.Top);
        double right = Math.Min(bounds.Right, desktop.Right);
        double bottom = Math.Min(bounds.Bottom, desktop.Bottom);
        if (right - left < MinimumRegionPixels || bottom - top < MinimumRegionPixels) return null;

        RectD local = new RectD(left - desktop.Left, top - desktop.Top, right - left, bottom - top).ToPixelBounds();
        return local.ClampTo(new RectD(0, 0, desktop.Width, desktop.Height)).ToPixelBounds();
    }

    /// <summary>
    /// True when the pointer travelled less than the system drag threshold on both axes, so the
    /// press is a click on the highlighted window rather than the start of a free-region drag.
    /// </summary>
    internal static bool IsClick(PointD pressed, PointD released, double thresholdX, double thresholdY) =>
        Math.Abs(released.X - pressed.X) < Math.Max(1, thresholdX) &&
        Math.Abs(released.Y - pressed.Y) < Math.Max(1, thresholdY);
}
