using System.ComponentModel;
using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Places a WPF HWND on exact virtual-desktop physical pixel bounds.</summary>
public static class PhysicalWindowPositioner
{
    internal const int PlacementAttemptLimit = 3;

    public static void PlaceTopmost(IntPtr hwnd, RectD screenBounds)
    {
        PlaceTopmost(hwnd, screenBounds, Win32PhysicalWindowNativeApi.Instance);
    }

    internal static void PlaceTopmost(
        IntPtr hwnd,
        RectD screenBounds,
        IPhysicalWindowNativeApi nativeApi)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        }

        ArgumentNullException.ThrowIfNull(nativeApi);

        RectD bounds = screenBounds.ToPixelBounds();
        var expected = new PhysicalWindowBounds(
            checked((int)bounds.Left),
            checked((int)bounds.Top),
            Math.Max(1, checked((int)bounds.Width)),
            Math.Max(1, checked((int)bounds.Height)));

        PhysicalWindowBounds actual = default;
        for (int attempt = 1; attempt <= PlacementAttemptLimit; attempt++)
        {
            if (!nativeApi.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    expected.Left,
                    expected.Top,
                    expected.Width,
                    expected.Height,
                    NativeMethods.SWP_SHOWWINDOW))
            {
                int error = nativeApi.GetLastError();
                throw new Win32Exception(
                    error,
                    "Could not position the capture overlay on the virtual desktop.");
            }

            if (!nativeApi.GetWindowRect(hwnd, out actual))
            {
                int error = nativeApi.GetLastError();
                throw new Win32Exception(
                    error,
                    "Could not verify the capture overlay's physical-pixel bounds.");
            }

            if (actual.ExactlyMatches(expected))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The capture overlay did not reach its requested physical-pixel bounds after " +
            $"{PlacementAttemptLimit} attempts. Expected {expected}; actual {actual}.");
    }
}

internal readonly record struct PhysicalWindowBounds(int Left, int Top, int Width, int Height)
{
    internal static PhysicalWindowBounds FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, checked(right - left), checked(bottom - top));

    internal bool ExactlyMatches(PhysicalWindowBounds other) =>
        Left == other.Left
        && Top == other.Top
        && Width == other.Width
        && Height == other.Height;

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

internal interface IPhysicalWindowNativeApi
{
    bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    bool GetWindowRect(IntPtr hwnd, out PhysicalWindowBounds bounds);

    int GetLastError();
}

internal sealed class Win32PhysicalWindowNativeApi : IPhysicalWindowNativeApi
{
    internal static Win32PhysicalWindowNativeApi Instance { get; } = new();

    private Win32PhysicalWindowNativeApi()
    {
    }

    public bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags) =>
        NativeMethods.SetWindowPos(hwnd, insertAfter, x, y, width, height, flags);

    public bool GetWindowRect(IntPtr hwnd, out PhysicalWindowBounds bounds)
    {
        bool succeeded = NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect);
        bounds = succeeded
            ? PhysicalWindowBounds.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;
        return succeeded;
    }

    public int GetLastError() => Marshal.GetLastWin32Error();
}
