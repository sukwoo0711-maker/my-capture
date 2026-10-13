using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>A visible top-level window in virtual-desktop physical pixels.</summary>
/// <param name="ClassName">Registered window class (GetClassName), used to recognise the desktop shell.</param>
/// <param name="Source">Application and folder observed around the frozen frame; null when unknown.</param>
public sealed record WindowCandidate(IntPtr Handle, RectD ScreenBounds, string Title = "",
    string SourcePageUrl = "", uint ProcessId = 0, string ClassName = "",
    MyCapture.Core.Capture.CaptureSourceDetails? Source = null);

/// <summary>
/// Snap candidates captured before the selection overlay appears. EnumWindows returns
/// top-level windows in z-order, so the first rectangle containing the pointer is the
/// same visible window the user meant to target.
/// </summary>
public sealed class WindowCandidateService
{
    /// <summary>
    /// Class of the shell's desktop window, the window <c>GetShellWindow</c> returns. It spans the
    /// whole virtual desktop behind every application window.
    /// </summary>
    internal const string DesktopWindowClass = "Progman";

    /// <summary>
    /// Explorer worker window. When the shell splits the desktop (wallpaper slideshow, animated
    /// wallpaper) it moves the icon view into a top-level WorkerW that also spans the virtual desktop.
    /// </summary>
    internal const string DesktopWorkerClass = "WorkerW";

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
        var className = new StringBuilder(NativeMethods.MaxClassNameLength + 1);

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
                candidates.Add(new WindowCandidate(hwnd, clipped, titles.ReadTitle(hwnd), ProcessId: processId,
                    ClassName: ReadClassName(hwnd, className)));
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
    /// The desktop shell's own caption ("Program Manager") is never a source title.
    /// </summary>
    public static string ResolveSourceTitle(IReadOnlyList<WindowCandidate> candidates, RectD selection) =>
        ResolveSourceWindow(candidates, selection)?.Title ?? string.Empty;

    /// <summary>
    /// The window whose title <see cref="ResolveSourceTitle"/> reports. Its application and folder are
    /// the capture's source as well, so title, application and folder always describe one window.
    /// </summary>
    public static WindowCandidate? ResolveSourceWindow(IReadOnlyList<WindowCandidate> candidates, RectD selection)
    {
        RectD region = selection.Normalized();
        if (region.IsEmpty) return null;
        WindowCandidate? source = null;
        double largestArea = 0;
        foreach (WindowCandidate candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Title) || IsDesktopShell(candidate, candidates)) continue;
            RectD overlap = Intersect(candidate.ScreenBounds, region);
            if (overlap.IsEmpty) continue;
            if (candidate.ScreenBounds.Contains(region.Center)) return candidate;
            double area = overlap.Width * overlap.Height;
            if (area > largestArea)
            {
                largestArea = area;
                source = candidate;
            }
        }
        return source;
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

    /// <summary>
    /// True for the shell's desktop window rather than an application window: the <c>Progman</c>
    /// window (what <c>GetShellWindow</c> returns), or a <c>WorkerW</c> owned by the same process
    /// as a <c>Progman</c> in <paramref name="snapshot"/>. Another process's WorkerW, and every
    /// other shell surface such as the taskbar (<c>Shell_TrayWnd</c>), is an ordinary window.
    /// </summary>
    /// <param name="candidate">The window to classify.</param>
    /// <param name="snapshot">The enumeration <paramref name="candidate"/> came from.</param>
    public static bool IsDesktopShell(WindowCandidate candidate, IReadOnlyList<WindowCandidate> snapshot)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshot);
        // Window class names are case-insensitive in Win32.
        if (string.Equals(candidate.ClassName, DesktopWindowClass, StringComparison.OrdinalIgnoreCase)) return true;
        if (candidate.ProcessId == 0 ||
            !string.Equals(candidate.ClassName, DesktopWorkerClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The selector asks on every pointer move over the desktop: index the list instead of boxing
        // an enumerator.
        for (int index = 0; index < snapshot.Count; index++)
        {
            WindowCandidate other = snapshot[index];
            if (other.ProcessId == candidate.ProcessId &&
                string.Equals(other.ClassName, DesktopWindowClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadClassName(IntPtr hwnd, StringBuilder buffer)
    {
        buffer.Clear();
        int length = NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
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
