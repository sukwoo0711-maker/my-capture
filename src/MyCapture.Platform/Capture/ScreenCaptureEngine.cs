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
    private static readonly IntPtr HgdiError = new(-1);

    private readonly ILogger<ScreenCaptureEngine> _log;

    public ScreenCaptureEngine(ILogger<ScreenCaptureEngine> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private static bool IsInvalidGdiHandle(IntPtr handle) =>
        handle == IntPtr.Zero || handle == HgdiError;

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
        return CaptureRegionCore(screenBounds, includeCursor, ToBitmapSource);
    }

    /// <summary>
    /// Captures a region into a reusable BGRA32 buffer. Recording uses this to avoid a
    /// <see cref="WriteableBitmap"/> allocation on every frame.
    /// </summary>
    public void CaptureRegionInto(
        RectD screenBounds,
        bool includeCursor,
        byte[] destination,
        int destinationStride)
    {
        ArgumentNullException.ThrowIfNull(destination);

        RectD pixels = screenBounds.ToPixelBounds();
        int width = Math.Max(1, (int)pixels.Width);
        int height = Math.Max(1, (int)pixels.Height);
        if (destinationStride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride));
        }

        if (destination.Length < checked(destinationStride * height))
        {
            throw new ArgumentException("The destination buffer is too small.", nameof(destination));
        }

        _ = CaptureRegionCore(screenBounds, includeCursor, (memoryDc, bitmapHandle, capturedWidth, capturedHeight) =>
        {
            CopyTopDownBgra(memoryDc, bitmapHandle, capturedWidth, capturedHeight, destination, destinationStride);
            return 0;
        });
    }

    private T CaptureRegionCore<T>(
        RectD screenBounds,
        bool includeCursor,
        Func<IntPtr, IntPtr, int, int, T> consume)
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
            if (IsInvalidGdiHandle(previousObject))
            {
                throw new InvalidOperationException(
                    $"Could not select bitmap into memory device context (Win32 error {Marshal.GetLastWin32Error()}).");
            }

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

            // Deselect bitmap before consumer readback as required by GetDIBits Win32 contract.
            IntPtr restored = NativeMethods.SelectObject(memoryDc, previousObject);
            if (IsInvalidGdiHandle(restored))
            {
                throw new InvalidOperationException(
                    $"Could not restore original object to memory device context (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            previousObject = IntPtr.Zero;

            return consume(memoryDc, bitmapHandle, width, height);
        }
        finally
        {
            if (!IsInvalidGdiHandle(previousObject) && memoryDc != IntPtr.Zero)
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
    /// Creates a fixed-region capture surface owned by the calling thread.
    /// </summary>
    public CaptureSession CreateSession(RectD screenBounds, bool includeCursor) =>
        new(this, screenBounds, includeCursor);

    /// <summary>Reusable native resources; never shared with still captures or another recorder.</summary>
    public sealed class CaptureSession : IDisposable
    {
        private readonly ScreenCaptureEngine _owner;
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private readonly RectD _pixels;
        private readonly bool _includeCursor;
        private readonly IntPtr _desktop = NativeMethods.GetDesktopWindow();
        private IntPtr _screenDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private bool _disposed;

        internal CaptureSession(ScreenCaptureEngine owner, RectD bounds, bool includeCursor)
        {
            _owner = owner;
            _pixels = bounds.Normalized().ToPixelBounds();
            _includeCursor = includeCursor;
            Width = Math.Max(1, checked((int)_pixels.Width));
            Height = Math.Max(1, checked((int)_pixels.Height));
            try
            {
                _screenDc = NativeMethods.GetWindowDC(_desktop);
                if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("Could not obtain desktop DC.");
                _memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);
                if (_memoryDc == IntPtr.Zero) throw new InvalidOperationException("Could not create capture DC.");
                _bitmap = NativeMethods.CreateCompatibleBitmap(_screenDc, Width, Height);
                if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not allocate capture bitmap.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int Width { get; }
        public int Height { get; }

        public void CaptureInto(byte[] destination, int stride)
        {
            VerifyThread();
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(destination);
            if (stride < checked(Width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
            if (destination.Length < checked(stride * Height)) throw new ArgumentException("Buffer is too small.", nameof(destination));
            IntPtr previous = NativeMethods.SelectObject(_memoryDc, _bitmap);
            if (IsInvalidGdiHandle(previous)) throw new InvalidOperationException("Could not select capture bitmap.");
            try
            {
                if (!NativeMethods.BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc,
                    (int)_pixels.Left, (int)_pixels.Top, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                    throw new InvalidOperationException($"BitBlt failed (Win32 {Marshal.GetLastWin32Error()}).");
                if (_includeCursor) _owner.DrawCursor(_memoryDc, (int)_pixels.Left, (int)_pixels.Top, Width, Height);
            }
            finally
            {
                // GetDIBits requires deselection on every frame, including error paths.
                if (IsInvalidGdiHandle(NativeMethods.SelectObject(_memoryDc, previous)))
                {
                    Dispose();
                    throw new InvalidOperationException("Could not deselect capture bitmap.");
                }
            }
            CopyTopDownBgra(_memoryDc, _bitmap, Width, Height, destination, stride);
        }

        private void VerifyThread()
        {
            if (_threadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Capture resources belong to their creating thread.");
        }

        public void Dispose()
        {
            VerifyThread();
            if (_disposed) return;
            _disposed = true;
            // Deleting the DC first releases a selection even if restoration failed.
            if (_memoryDc != IntPtr.Zero) { NativeMethods.DeleteDC(_memoryDc); _memoryDc = IntPtr.Zero; }
            if (_bitmap != IntPtr.Zero) { NativeMethods.DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { NativeMethods.ReleaseDC(_desktop, _screenDc); _screenDc = IntPtr.Zero; }
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
    internal static BitmapSource ToBitmapSource(IntPtr memoryDc, IntPtr bitmapHandle, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int stride = checked(width * 4);
        _ = checked(stride * height);

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

        // Desktop alpha is undefined. Bgr32 deliberately ignores that byte, keeping
        // captures opaque without a second pass over the pixels.
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, palette: null);
        bitmap.Lock();
        try
        {
            // GetDIBits has no destination-stride parameter. A 32-bit BI_RGB DIB
            // has DWORD-aligned rows of exactly width * 4 bytes; accepting a larger
            // WPF stride would silently place subsequent rows at the wrong offsets.
            if (bitmap.BackBuffer == IntPtr.Zero || bitmap.BackBufferStride != stride)
            {
                throw new InvalidOperationException("The WPF back buffer does not match the requested DIB layout.");
            }

            // CaptureRegionCore has already deselected bitmapHandle. WPF owns this
            // pointer: use it only while locked, and never free or retain it.
            int scanLines = NativeMethods.GetDIBits(
                memoryDc, bitmapHandle, 0, (uint)height, bitmap.BackBuffer, ref info,
                NativeMethods.DIB_RGB_COLORS);
            if (scanLines != height)
            {
                throw new InvalidOperationException(
                    $"GetDIBits returned {scanLines} of {height} scan lines (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            bitmap.Unlock();
        }

        // Publish only a complete, unlocked frame; failed readbacks stay local.
        bitmap.Freeze();

        return bitmap;
    }

    private static void CopyTopDownBgra(
        IntPtr memoryDc,
        IntPtr bitmapHandle,
        int width,
        int height,
        byte[] destination,
        int destinationStride)
    {
        int packedStride = width * 4;
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        if (destinationStride == packedStride)
        {
            unsafe
            {
                fixed (byte* pinned = destination)
                {
                    int scanLines = NativeMethods.GetDIBits(
                        memoryDc, bitmapHandle, 0, (uint)height, (IntPtr)pinned, ref info,
                        NativeMethods.DIB_RGB_COLORS);
                    if (scanLines != height)
                    {
                        throw new InvalidOperationException(
                            $"GetDIBits returned {scanLines} of {height} scan lines (Win32 error {Marshal.GetLastWin32Error()}).");
                    }
                }
            }
        }
        else
        {
            byte[] packed = new byte[checked(packedStride * height)];
            unsafe
            {
                fixed (byte* pinned = packed)
                {
                    int scanLines = NativeMethods.GetDIBits(
                        memoryDc, bitmapHandle, 0, (uint)height, (IntPtr)pinned, ref info,
                        NativeMethods.DIB_RGB_COLORS);
                    if (scanLines != height)
                    {
                        throw new InvalidOperationException(
                            $"GetDIBits returned {scanLines} of {height} scan lines (Win32 error {Marshal.GetLastWin32Error()}).");
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(packed, y * packedStride, destination, y * destinationStride, packedStride);
            }
        }

        // BitBlt leaves alpha undefined. Opaque pixels match the still-capture Bgr32 conversion.
        for (int y = 0; y < height; y++)
        {
            int row = y * destinationStride;
            for (int x = 3; x < packedStride; x += 4)
            {
                destination[row + x] = 255;
            }
        }
    }
}
