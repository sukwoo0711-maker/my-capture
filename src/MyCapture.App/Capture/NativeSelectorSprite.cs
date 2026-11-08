using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Capture;

/// <summary>A bounded premultiplied sprite. It never owns a desktop capture.</summary>
internal sealed class NativeSelectorSprite : IDisposable
{
    private IntPtr _previous;
    internal IntPtr Dc { get; private set; }
    internal IntPtr Bitmap { get; private set; }
    internal int Width { get; }
    internal int Height { get; }

    internal NativeSelectorSprite(BitmapSource source)
    {
        Width = source.PixelWidth; Height = source.PixelHeight;
        try
        {
            Dc = NativeSelectorInterop.CreateCompatibleDC(IntPtr.Zero);
            if (Dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new NativeSelectorInterop.BitmapInfo { Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
            Bitmap = NativeSelectorInterop.CreateDIBSection(Dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (Bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = NativeSelectorInterop.SelectObject(Dc, Bitmap);
            if (_previous == IntPtr.Zero || _previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            source.CopyPixels(System.Windows.Int32Rect.Empty, bits, checked(Width * Height * 4), Width * 4);
        }
        catch { Dispose(); throw; }
    }

    internal void Draw(IntPtr destination, int x, int y)
    {
        if (!NativeSelectorInterop.AlphaBlend(destination, x, y, Width, Height, Dc, 0, 0, Width, Height, 0x01ff0000))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_previous != IntPtr.Zero && _previous != new IntPtr(-1) && Dc != IntPtr.Zero)
            _ = NativeSelectorInterop.SelectObject(Dc, _previous);
        if (Bitmap != IntPtr.Zero) _ = NativeSelectorInterop.DeleteObject(Bitmap);
        if (Dc != IntPtr.Zero) _ = NativeSelectorInterop.DeleteDC(Dc);
        Bitmap = Dc = _previous = IntPtr.Zero;
        _ = NativeSelectorInterop.GdiFlush();
    }
}
