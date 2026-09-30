using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>A visible top-level window in virtual-desktop physical pixels.</summary>
public sealed record WindowCandidate(IntPtr Handle, RectD ScreenBounds, string Title = "",
    string SourcePageUrl = "", uint ProcessId = 0);

/// <summary>
/// Snap candidates captured before the selection overlay appears. EnumWindows returns
/// top-level windows in z-order, so the first rectangle containing the pointer is the
/// same visible window the user meant to target.
/// </summary>
public sealed class WindowCandidateService
{
    private readonly ILogger<WindowCandidateService> _log;

    public WindowCandidateService(ILogger<WindowCandidateService> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<WindowCandidate> GetCandidates(RectD monitorBounds)
    {
        RectD monitor = monitorBounds.Normalized();
        var candidates = new List<WindowCandidate>();
        uint ownProcessId = unchecked((uint)Environment.ProcessId);
        var titles = new WindowTitleService();

        bool Callback(IntPtr hwnd, IntPtr data)
        {
            if (!NativeMethods.IsWindowVisible(hwnd) ||
                NativeMethods.IsIconic(hwnd) ||
                IsPointerTransparentHelper(hwnd))
            {
                return true;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == ownProcessId || IsCloaked(hwnd))
            {
                return true;
            }

            if (!TryGetBounds(hwnd, out RectD bounds) || !Intersects(bounds, monitor))
            {
                return true;
            }

            RectD clipped = Intersect(bounds, monitor);
            if (clipped.Width >= 2 && clipped.Height >= 2)
            {
                candidates.Add(new WindowCandidate(hwnd, clipped, titles.ReadTitle(hwnd), ProcessId: processId));
            }

            return true;
        }

        if (!NativeMethods.EnumWindows(Callback, IntPtr.Zero))
        {
            _log.LogDebug(
                "EnumWindows did not complete: Win32 {Error}",
                Marshal.GetLastWin32Error());
        }

        return candidates;
    }

    /// <summary>
    /// Uses titles frozen before the overlay takes focus. Prefer the frontmost titled
    /// window at the selection centre; for a centre over desktop space use the largest
    /// intersecting window. Never query the live foreground window after selection.
    /// </summary>
    public static string ResolveSourceTitle(IReadOnlyList<WindowCandidate> candidates, RectD selection)
    {
        RectD region = selection.Normalized();
        if (region.IsEmpty) return string.Empty;
        string title = string.Empty;
        double largestArea = 0;
        foreach (WindowCandidate candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Title)) continue;
            RectD overlap = Intersect(candidate.ScreenBounds, region);
            if (overlap.IsEmpty) continue;
            if (candidate.ScreenBounds.Contains(region.Center)) return candidate.Title;
            double area = overlap.Width * overlap.Height;
            if (area > largestArea)
            {
                largestArea = area;
                title = candidate.Title;
            }
        }
        return title;
    }

    /// <summary>URL attribution requires the frontmost window at the selection centre, even if untitled.</summary>
    public static string ResolveSourcePageUrl(IReadOnlyList<WindowCandidate> candidates, RectD selection)
    {
        RectD region = selection.Normalized();
        if (region.IsEmpty) return string.Empty;
        WindowCandidate? source = candidates.FirstOrDefault(candidate => candidate.ScreenBounds.Contains(region.Center));
        if (source is null || source.Title != ResolveSourceTitle(candidates, region)) return string.Empty;
        return MyCapture.Core.Capture.SourcePageUrl.Normalize(source.SourcePageUrl);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int result = NativeMethods.DwmGetWindowAttributeInt32(
            hwnd,
            NativeMethods.DWMWA_CLOAKED,
            out int cloaked,
            sizeof(int));
        return result == 0 && cloaked != 0;
    }

    private static bool IsPointerTransparentHelper(IntPtr hwnd)
    {
        long extendedStyle = NativeMethods.GetWindowLongPtr(
            hwnd,
            NativeMethods.GWL_EXSTYLE).ToInt64();
        return (extendedStyle & (NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE)) != 0;
    }

    private static bool TryGetBounds(IntPtr hwnd, out RectD bounds)
    {
        NativeMethods.RECT rect;
        int result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out rect,
            (uint)Marshal.SizeOf<NativeMethods.RECT>());

        if (result != 0 && !NativeMethods.GetWindowRect(hwnd, out rect))
        {
            bounds = RectD.Empty;
            return false;
        }

        bounds = new RectD(rect.Left, rect.Top, rect.Width, rect.Height).Normalized();
        return !bounds.IsEmpty;
    }

    private static bool Intersects(RectD a, RectD b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static RectD Intersect(RectD a, RectD b)
    {
        double left = Math.Max(a.Left, b.Left);
        double top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right);
        double bottom = Math.Min(a.Bottom, b.Bottom);
        return new RectD(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
