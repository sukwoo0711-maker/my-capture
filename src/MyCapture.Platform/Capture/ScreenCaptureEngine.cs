using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>
/// A captured screen image plus the context needed to interpret its coordinates.
/// </summary>
/// <param name="Bitmap">Frozen bitmap in physical pixels, BGRA32.</param>
/// <param name="ScreenBounds">
/// Where the bitmap came from, in virtual-desktop physical pixels.
/// </param>
/// <param name="Monitor">The source display, when the capture came from exactly one.</param>
/// <param name="ElapsedMilliseconds">
/// Time spent acquiring the frame. Surfaced so the response-time budget can be
/// measured in the field rather than assumed.
/// </param>
public sealed record FrozenFrame(
    BitmapSource Bitmap,
    RectD ScreenBounds,
    MonitorInfo? Monitor,
    double ElapsedMilliseconds)
{
    public int PixelWidth => Bitmap.PixelWidth;

    public int PixelHeight => Bitmap.PixelHeight;

    public double DpiScale => Monitor?.ScaleFactor ?? 1.0;

    /// <summary>
    /// Converts a virtual-desktop point into a pixel offset inside the bitmap.
    /// </summary>
    public PointD ToBitmapSpace(PointD screenPoint) =>
        new(screenPoint.X - ScreenBounds.Left, screenPoint.Y - ScreenBounds.Top);

    /// <summary>
    /// Converts a virtual-desktop rectangle into bitmap pixel coordinates, clamped to
    /// the bitmap.
    /// </summary>
    public RectD ToBitmapSpace(RectD screenRect)
    {
        RectD n = screenRect.Normalized();
        var shifted = new RectD(
            n.Left - ScreenBounds.Left,
            n.Top - ScreenBounds.Top,
            n.Width,
            n.Height);

        return shifted.ClampTo(new RectD(0, 0, PixelWidth, PixelHeight));
    }
}

/// <summary>
/// Acquires screen pixels.
/// </summary>
/// <remarks>
/// <para>
/// GDI <c>BitBlt</c> is used rather than the Desktop Duplication API. Duplication is
/// faster for continuous streams but requires a D3D device, a per-adapter duplication
/// session, and re-acquisition handling on mode changes and desktop switches. This app
/// takes one frame per hotkey press, where BitBlt on a 4K display measures in tens of
/// milliseconds — comfortably inside the response budget — for a fraction of the
/// complexity and failure surface.
/// </para>
/// <para>
/// <c>CAPTUREBLT</c> is always set. Without it, layered and translucent windows are
/// absent from the result, which is the single most common defect in naive screenshot
/// code.
/// </para>
/// <para>
/// Every returned bitmap is frozen. The capture overlay is created after the frame is
/// acquired and the frame is handed across threads to be encoded; an unfrozen
/// <see cref="BitmapSource"/> would be bound to the acquiring thread and throw on
/// first cross-thread access.
/// </para>
/// </remarks>
public sealed class ScreenCaptureEngine
{
    private readonly ILogger<ScreenCaptureEngine> _log;

    public ScreenCaptureEngine(ILogger<ScreenCaptureEngine> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Initialises the GDI and WPF imaging path with a one-pixel capture.
    /// </summary>
    /// <returns>Elapsed warm-up time in milliseconds.</returns>
    /// <remarks>
    /// The first call into GDI plus <see cref="WriteableBitmap"/> pays JIT, DLL-load
    /// and WPF imaging initialisation costs. On the test workstation that made the
    /// first 3440x1440 capture take 827ms while subsequent captures took 21-51ms.
    /// A one-pixel capture performs the same initialisation without allocating a
    /// full frame, and is started in the background as soon as the tray app launches.
    /// </remarks>
    public double Prewarm()
    {
        var stopwatch = Stopwatch.StartNew();
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        _ = CaptureRegion(
            new RectD(monitor.Bounds.Left, monitor.Bounds.Top, 1, 1),
            includeCursor: false);
        stopwatch.Stop();

        _log.LogDebug("Capture pipeline prewarmed in {Elapsed:0.0}ms", stopwatch.Elapsed.TotalMilliseconds);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Captures one display.
    /// </summary>
    public FrozenFrame CaptureMonitor(MonitorInfo monitor, bool includeCursor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var stopwatch = Stopwatch.StartNew();
        BitmapSource bitmap = CaptureRegion(monitor.Bounds, includeCursor);
        stopwatch.Stop();

        _log.LogDebug(
            "Captured {Device} {Width}x{Height} at {Dpi}dpi in {Elapsed:0.0}ms",
            monitor.DeviceName, bitmap.PixelWidth, bitmap.PixelHeight, monitor.Dpi,
            stopwatch.Elapsed.TotalMilliseconds);

        return new FrozenFrame(bitmap, monitor.Bounds, monitor, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Captures every display as one bitmap.
    /// </summary>
    /// <remarks>
    /// Used by free-region selection, recording, pin and scrolling features. The bitmap is a
    /// physical-pixel plane, so mixed-DPI monitors remain unscaled and a selection can cross
    /// their boundary without being split or clipped.
    /// </remarks>
    public FrozenFrame CaptureVirtualDesktop(bool includeCursor)
    {
        var stopwatch = Stopwatch.StartNew();
        RectD bounds = MonitorEnumerator.GetVirtualDesktopBounds();
        BitmapSource bitmap = CaptureRegion(bounds, includeCursor);
        stopwatch.Stop();

        return new FrozenFrame(bitmap, bounds, null, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Captures an arbitrary virtual-desktop rectangle.
    /// </summary>
    public BitmapSource CaptureRegion(RectD screenBounds, bool includeCursor)
    {
        RectD pixels = screenBounds.ToPixelBounds();

        int width = Math.Max(1, (int)pixels.Width);
        int height = Math.Max(1, (int)pixels.Height);
        int originX = (int)pixels.Left;
        int originY = (int)pixels.Top;

        IntPtr desktopWindow = NativeMethods.GetDesktopWindow();
        IntPtr screenDc = NativeMethods.GetWindowDC(desktopWindow);

        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not obtain a device context for the desktop.");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create a memory device context.");
            }

            bitmapHandle = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmapHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Could not allocate a {width}x{height} bitmap for the capture.");
            }

            previousObject = NativeMethods.SelectObject(memoryDc, bitmapHandle);

            bool copied = NativeMethods.BitBlt(
                memoryDc, 0, 0, width, height,
                screenDc, originX, originY,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!copied)
            {
                throw new InvalidOperationException(
                    $"BitBlt failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            if (includeCursor)
            {
                DrawCursor(memoryDc, originX, originY, width, height);
            }

            return ToBitmapSource(memoryDc, bitmapHandle, width, height);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previousObject);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(desktopWindow, screenDc);
        }
    }

    /// <summary>
    /// Crops a frozen frame.
    /// </summary>
    /// <remarks>
    /// Selection crops from the already-captured frame rather than re-capturing the
    /// selected region. Re-capturing would pick up whatever moved on screen while the
    /// user was dragging, so the saved image would not match what they framed.
    /// </remarks>
    public static BitmapSource Crop(FrozenFrame frame, RectD bitmapRegion)
    {
        ArgumentNullException.ThrowIfNull(frame);

        RectD clamped = bitmapRegion
            .ToPixelBounds()
            .ClampTo(new RectD(0, 0, frame.PixelWidth, frame.PixelHeight));

        int x = (int)clamped.Left;
        int y = (int)clamped.Top;
        int w = Math.Max(1, (int)clamped.Width);
        int h = Math.Max(1, (int)clamped.Height);

        var cropped = new CroppedBitmap(frame.Bitmap, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    private void DrawCursor(IntPtr targetDc, int originX, int originY, int width, int height)
    {
        var info = new NativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>(),
        };

        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0)
        {
            return;
        }

        // CopyIcon is required: the handle from GetCursorInfo is owned by the system
        // and GetIconInfo on it is not reliable across cursor changes.
        IntPtr cursor = NativeMethods.CopyIcon(info.hCursor);
        if (cursor == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!NativeMethods.GetIconInfo(cursor, out NativeMethods.ICONINFO iconInfo))
            {
                return;
            }

            // Bitmaps returned by GetIconInfo are caller-owned.
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(iconInfo.hbmMask);
            }

            if (iconInfo.hbmColor != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(iconInfo.hbmColor);
            }

            // The hotspot, not the cursor's top-left, is what sits at the pointer
            // position; ignoring it offsets the drawn cursor by up to its own size.
            int drawX = info.ptScreenPos.X - originX - iconInfo.xHotspot;
            int drawY = info.ptScreenPos.Y - originY - iconInfo.yHotspot;

            if (drawX > width || drawY > height)
            {
                return;
            }

            NativeMethods.DrawIconEx(
                targetDc, drawX, drawY, cursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing cursor is cosmetic; losing the capture is not.
            _log.LogDebug(ex, "Could not composite the mouse cursor into the capture");
        }
        finally
        {
            NativeMethods.DestroyIcon(cursor);
        }
    }

    /// <summary>
    /// Copies a GDI bitmap into a frozen WPF bitmap.
    /// </summary>
    /// <remarks>
    /// <c>Imaging.CreateBitmapSourceFromHBitmap</c> is avoided deliberately: it leaks
    /// unless the HBITMAP is deleted at exactly the right moment and it forces an
    /// extra internal copy. Reading the DIB bits directly is both cheaper and
    /// predictable about ownership.
    /// </remarks>
    private static BitmapSource ToBitmapSource(IntPtr memoryDc, IntPtr bitmapHandle, int width, int height)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,

                // Negative height requests a top-down DIB, matching the row order
                // WriteableBitmap expects. Requesting bottom-up would mean copying the
                // buffer a second time just to flip it.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        int stride = width * 4;
        int byteCount = stride * height;
        byte[] buffer = new byte[byteCount];

        unsafe
        {
            fixed (byte* pinned = buffer)
            {
                int scanLines = NativeMethods.GetDIBits(
                    memoryDc, bitmapHandle, 0, (uint)height, (IntPtr)pinned, ref info,
                    NativeMethods.DIB_RGB_COLORS);

                if (scanLines == 0)
                {
                    throw new InvalidOperationException(
                        $"GetDIBits returned no scan lines (Win32 error {Marshal.GetLastWin32Error()}).");
                }
            }
        }

        // The desktop has no meaningful alpha, and BitBlt leaves the alpha byte
        // undefined. Declaring Bgr32 rather than Bgra32 avoids interpreting that
        // garbage as transparency, which shows up as a capture that looks correct in
        // one viewer and semi-transparent in another.
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, palette: null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        bitmap.Freeze();

        return bitmap;
    }
}
