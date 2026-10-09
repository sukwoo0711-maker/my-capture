using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

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
        foreach (WindowCandidate candidate in candidates)
        {
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
    /// Maps a detected window to overlay bitmap pixels: the window rectangle clipped to the
    /// frozen desktop, rounded outward to whole pixels. Returns <see langword="null"/> when
    /// nothing usable remains on the desktop.
    /// </summary>
    internal static RectD? ToBitmapRegion(WindowCandidate window, RectD desktopScreenBounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        RectD desktop = desktopScreenBounds.Normalized();
        RectD bounds = window.ScreenBounds.Normalized();
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
