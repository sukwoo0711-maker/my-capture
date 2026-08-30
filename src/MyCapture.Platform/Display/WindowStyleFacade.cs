using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>
/// A narrow facade over the extended-window-style bits a pinned window toggles to
/// become click-through, without disturbing its topmost or layered state.
/// </summary>
/// <remarks>
/// <para>
/// Click-through is <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c>: mouse messages fall
/// through to whatever is underneath. <c>WS_EX_NOACTIVATE</c> is added so clicking a
/// pin never steals foreground from the app the user is working in — a pin is a
/// reference overlay, not a task. The styles are OR-ed in and cleared out rather than
/// overwritten so WPF's own bits (layered transparency, tool-window) survive the
/// round trip; a bare <c>SetWindowLongPtr</c> of a computed value is what typically
/// breaks a WPF window's rendering.
/// </para>
/// <para>
/// Reversibility is the point: <see cref="SetClickThrough"/> with <c>false</c> removes
/// exactly the transparent/noactivate bits it added, so a global command or context
/// menu can always give a pin back its input, which is why click-through is safe to
/// enable in the first place.
/// </para>
/// </remarks>
public static class WindowStyleFacade
{
    /// <summary>
    /// Turns click-through on or off for <paramref name="hwnd"/>.
    /// </summary>
    /// <returns>The click-through state actually applied.</returns>
    public static bool SetClickThrough(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

        if (enabled)
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT
                       | NativeMethods.WS_EX_LAYERED
                       | NativeMethods.WS_EX_NOACTIVATE;
        }
        else
        {
            // Keep WS_EX_LAYERED: WPF relies on it for AllowsTransparency windows, and
            // leaving it set is harmless. Only the transparent/noactivate bits are removed.
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
            exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        }

        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // Re-assert topmost without moving or resizing, so the style change takes effect
        // and the pin keeps its z-order above ordinary windows.
        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE
                | NativeMethods.SWP_FRAMECHANGED);

        return enabled;
    }

    /// <summary>Whether <paramref name="hwnd"/> currently has the click-through bit set.</summary>
    public static bool IsClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0;
    }

    /// <summary>The current cursor position in physical pixels, or (0,0) if unavailable.</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        return NativeMethods.GetCursorPos(out NativeMethods.POINT pt)
            ? (pt.X, pt.Y)
            : (0, 0);
    }

    /// <summary>
    /// The physical-pixel bounds of <paramref name="hwnd"/>, or all zeros if unavailable.
    /// </summary>
    /// <remarks>
    /// Uses <c>GetWindowRect</c> deliberately. A click-through pin carries
    /// <c>WS_EX_TRANSPARENT</c>, so <c>WindowFromPoint</c> skips it and cannot be used to
    /// find "the pin under the cursor". Testing the cursor against each pin's own bounds
    /// is what lets the global toggle reach a pin that has made itself invisible to hit
    /// testing.
    /// </remarks>
    public static (int Left, int Top, int Right, int Bottom) GetWindowBounds(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return (rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        return (0, 0, 0, 0);
    }
}
