using System.ComponentModel;
using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Places a WPF HWND on exact virtual-desktop physical pixel bounds.</summary>
public static class PhysicalWindowPositioner
{
    internal const int PlacementAttemptLimit = 3;

    internal const uint DragMoveFlags =
        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;

    public static void PlaceTopmost(IntPtr hwnd, RectD screenBounds)
    {
        PlaceTopmost(hwnd, screenBounds, Win32PhysicalWindowNativeApi.Instance);
    }

    /// <summary>
    /// Moves an already-shown topmost window. Skips the native call when the pixel position
    /// is unchanged, and does not restack, resize, or force a show on every pointer sample.
    /// </summary>
    public static void Move(IntPtr hwnd, RectD screenBounds)
    {
        Move(hwnd, screenBounds, Win32PhysicalWindowNativeApi.Instance);
    }

    internal static void PlaceTopmost(
        IntPtr hwnd,
        RectD screenBounds,
        IPhysicalWindowNativeApi nativeApi)
    {
        Place(
            hwnd,
            screenBounds,
            nativeApi,
            NativeMethods.SWP_SHOWWINDOW,
            compareSize: true,
            skipIfUnchanged: false);
    }

    internal static void Move(
        IntPtr hwnd,
        RectD screenBounds,
        IPhysicalWindowNativeApi nativeApi)
    {
        Place(
            hwnd,
            screenBounds,
            nativeApi,
            DragMoveFlags,
            compareSize: false,
            skipIfUnchanged: true);
    }

    private static void Place(
        IntPtr hwnd,
        RectD screenBounds,
        IPhysicalWindowNativeApi nativeApi,
        uint flags,
        bool compareSize,
        bool skipIfUnchanged)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        }

        ArgumentNullException.ThrowIfNull(nativeApi);

        PhysicalWindowBounds expected = ToExpectedBounds(screenBounds);
        PhysicalWindowBounds actual = default;

        if (skipIfUnchanged)
        {
            if (!nativeApi.GetWindowRect(hwnd, out actual))
            {
                int error = nativeApi.GetLastError();
                throw new Win32Exception(
                    error,
                    "Could not verify the capture overlay's physical-pixel bounds.");
            }

            if (Matches(actual, expected, compareSize))
            {
                return;
            }
        }

        for (int attempt = 1; attempt <= PlacementAttemptLimit; attempt++)
        {
            if (!nativeApi.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    expected.Left,
                    expected.Top,
                    expected.Width,
                    expected.Height,
                    flags))
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

            if (Matches(actual, expected, compareSize))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The capture overlay did not reach its requested physical-pixel bounds after " +
            $"{PlacementAttemptLimit} attempts. Expected {expected}; actual {actual}.");
    }

    private static PhysicalWindowBounds ToExpectedBounds(RectD screenBounds)
    {
        RectD bounds = screenBounds.ToPixelBounds();
        return new PhysicalWindowBounds(
            checked((int)bounds.Left),
            checked((int)bounds.Top),
            Math.Max(1, checked((int)bounds.Width)),
            Math.Max(1, checked((int)bounds.Height)));
    }

    private static bool Matches(PhysicalWindowBounds actual, PhysicalWindowBounds expected, bool compareSize) =>
        actual.Left == expected.Left
        && actual.Top == expected.Top
        && (!compareSize || (actual.Width == expected.Width && actual.Height == expected.Height));
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
