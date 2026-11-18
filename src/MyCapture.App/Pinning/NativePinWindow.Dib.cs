using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyCapture.App.Pinning;

/// <summary>A single top-down premultiplied BGRA display DIB, owned by its creating STA.</summary>
internal sealed class NativePinWindowDib : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private IntPtr _previous, _section;
    private static int _liveBitmaps, _liveDcs;
    private static int _liveSections;
    private static long _liveSectionBytes, _livePrivateBytes;
    private bool _countedPrivate;
    internal static int LiveBitmapCount => Volatile.Read(ref _liveBitmaps);
    internal static int LiveDcCount => Volatile.Read(ref _liveDcs);
    internal static int LiveSectionCount => Volatile.Read(ref _liveSections);
    internal static long LiveSectionBytes => Interlocked.Read(ref _liveSectionBytes);
    /// <summary>
    /// Pixels of the DIBs that Windows allocated itself. They are part of the process's private
    /// commit; a DIB over an adopted section (<see cref="LiveSectionBytes"/>) is not.
    /// </summary>
    internal static long LivePrivateBytes => Interlocked.Read(ref _livePrivateBytes);
    internal IntPtr Dc { get; private set; }
    internal IntPtr Bitmap { get; private set; }
    internal IntPtr Bits { get; private set; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal int ByteLength { get; }
    internal long SectionBytes => _section != IntPtr.Zero ? ByteLength : 0;

    internal NativePinWindowDib(int width, int height) : this(width, height, null) { }

    internal NativePinWindowDib(NativePinSettleBuffer completed)
        : this(completed.Width, completed.Height, completed) { }

    private NativePinWindowDib(int width, int height, NativePinSettleBuffer? completed)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Stride = checked(width * 4); ByteLength = checked(Stride * height);
        try
        {
            Dc = NativePinWindowInterop.CreateCompatibleDC(IntPtr.Zero);
            if (Dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Interlocked.Increment(ref _liveDcs);
            var info = new NativePinWindowInterop.BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            Bitmap = NativePinWindowInterop.CreateDIBSection(Dc, ref info, 0, out IntPtr bits, completed?.Section ?? IntPtr.Zero, 0);
            Bits = bits;
            if (Bitmap != IntPtr.Zero) Interlocked.Increment(ref _liveBitmaps);
            if (Bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = NativePinWindowInterop.SelectObject(Dc, Bitmap);
            if (_previous == IntPtr.Zero || _previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (completed is not null)
            {
                _section = completed.TransferToDisplay();
                Interlocked.Increment(ref _liveSections);
                Interlocked.Add(ref _liveSectionBytes, ByteLength);
            }
            else
            {
                _countedPrivate = true;
                Interlocked.Add(ref _livePrivateBytes, ByteLength);
            }
        }
        catch { Dispose(); throw; }
    }

    internal byte[] CopyPixels()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(Bits == IntPtr.Zero, this);
        var result = new byte[ByteLength];
        Marshal.Copy(Bits, result, 0, result.Length);
        return result;
    }

    public void Dispose()
    {
        VerifyThread();
        if (Dc != IntPtr.Zero && _previous != IntPtr.Zero && _previous != new IntPtr(-1))
        {
            _ = NativePinWindowInterop.SelectObject(Dc, _previous);
            _previous = IntPtr.Zero;
        }
        // Deleting the memory DC also releases its selection if restoration failed. The bitmap
        // is deleted afterwards, so no live DC can still have the display DIB selected.
        if (Dc != IntPtr.Zero)
        {
            if (!NativePinWindowInterop.DeleteDC(Dc)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Dc = IntPtr.Zero;
            Interlocked.Decrement(ref _liveDcs);
        }
        if (Bitmap != IntPtr.Zero)
        {
            if (!NativePinWindowInterop.DeleteObject(Bitmap)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Bitmap = IntPtr.Zero; Bits = IntPtr.Zero;
            Interlocked.Decrement(ref _liveBitmaps);
            if (_countedPrivate)
            {
                _countedPrivate = false;
                Interlocked.Add(ref _livePrivateBytes, -ByteLength);
            }
        }
        if (_section != IntPtr.Zero)
        {
            NativePinSettleBuffer.ReleaseSection(_section);
            _section = IntPtr.Zero;
            Interlocked.Decrement(ref _liveSections);
            Interlocked.Add(ref _liveSectionBytes, -ByteLength);
        }
    }

    private void VerifyThread()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("The display DIB must be used and closed on its owner thread.");
    }
}
