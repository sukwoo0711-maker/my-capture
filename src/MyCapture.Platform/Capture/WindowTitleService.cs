using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>A resolved top-level window with both visible frame and client-area bounds.</summary>
public sealed record WindowUnderCursor(
    IntPtr Handle,
    RectD ScreenBounds,
    string Title,
    RectD ClientBounds = default)
{
    /// <summary>The chrome-free viewport preferred by scrolling capture.</summary>
    public RectD ScrollBounds => ClientBounds.IsEmpty ? ScreenBounds : ClientBounds;
}

/// <summary>Resolves the top-level window at a physical virtual-desktop point.</summary>
public sealed class WindowTitleService
{
    public WindowUnderCursor? ResolveAt(PointD screenPoint)
    {
        var point = new NativeMethods.POINT
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y),
        };

        IntPtr hit = NativeMethods.WindowFromPoint(point);
        if (hit == IntPtr.Zero)
        {
            return null;
        }

        IntPtr root = NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = hit;
        }

        if (!TryGetFrameBounds(root, out RectD bounds) || bounds.IsEmpty)
        {
            return null;
        }

        _ = TryGetClientBounds(root, out RectD clientBounds);
        return new WindowUnderCursor(root, bounds, ReadTitle(root), clientBounds);
    }

    public string ReadTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(length + 1);
        int copied = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool TryGetFrameBounds(IntPtr hwnd, out RectD bounds)
    {
        int result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT rect,
            (uint)Marshal.SizeOf<NativeMethods.RECT>());

        if (result != 0 && !NativeMethods.GetWindowRect(hwnd, out rect))
        {
            bounds = RectD.Empty;
            return false;
        }

        bounds = new RectD(rect.Left, rect.Top, rect.Width, rect.Height).Normalized().ToPixelBounds();
        return !bounds.IsEmpty;
    }

    private static bool TryGetClientBounds(IntPtr hwnd, out RectD bounds)
    {
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT client))
        {
            bounds = RectD.Empty;
            return false;
        }

        var topLeft = new NativeMethods.POINT { X = client.Left, Y = client.Top };
        var bottomRight = new NativeMethods.POINT { X = client.Right, Y = client.Bottom };
        if (!NativeMethods.ClientToScreen(hwnd, ref topLeft)
            || !NativeMethods.ClientToScreen(hwnd, ref bottomRight))
        {
            bounds = RectD.Empty;
            return false;
        }

        bounds = new RectD(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y)
            .Normalized()
            .ToPixelBounds();
        return !bounds.IsEmpty;
    }
}
