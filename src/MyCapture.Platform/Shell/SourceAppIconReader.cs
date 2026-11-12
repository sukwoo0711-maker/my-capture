using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.Platform.Shell;

/// <summary>
/// Reads the icon a source application shows in Windows: an executable's own icon, or a packaged
/// application's icon by its application user model ID. Blocking shell calls; callers run it on a
/// background STA thread and never on a capture or UI path. The result is frozen.
/// </summary>
public static class SourceAppIconReader
{
    /// <summary>Pixel size read; the controls show it at 16 DIP, so 32 stays sharp up to 200%.</summary>
    public const int PixelSize = 32;

    public static BitmapSource? ReadExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (SHDefExtractIconW(executablePath, 0, 0, out large, out small, (16u << 16) | PixelSize) < 0
                || large == IntPtr.Zero) return null;
            BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(PixelSize, PixelSize));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    public static BitmapSource? ReadPackagedApp(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId) || appUserModelId.IndexOfAny(['\\', '/', '"']) >= 0) return null;
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid id = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appUserModelId, IntPtr.Zero, ref id, out factory) < 0
                || factory is null) return null;
            // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK: the application's icon, never a thumbnail.
            if (factory.GetImage(new NativeSize { Width = PixelSize, Height = PixelSize }, 0x4 | 0x1, out bitmap) < 0
                || bitmap == IntPtr.Zero) return null;
            return FromBitmap(bitmap);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies a 32-bit shell bitmap with its alpha channel. <c>CreateBitmapSourceFromHBitmap</c> drops
    /// the alpha of a DIB section, which would put the icon on a black square.
    /// </summary>
    private static BitmapSource? FromBitmap(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0
            || info.Width <= 0 || info.Width > 256 || info.Height == 0 || Math.Abs(info.Height) > 256) return null;
        int width = info.Width, height = Math.Abs(info.Height);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, pixels, ref header, 0) != height) return null;
        }
        finally { _ = ReleaseDC(IntPtr.Zero, screen); }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        if (width != PixelSize || height != PixelSize)
            source = new TransformedBitmap(source, new ScaleTransform((double)PixelSize / width, (double)PixelSize / height));
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string path, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory factory);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetObjectW(IntPtr handle, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits,
        ref BitmapInfoHeader info, uint usage);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
