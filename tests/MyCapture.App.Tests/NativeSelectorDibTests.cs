using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorDibTests
{
    [Fact]
    public void RevealRestoresOriginalPixelsWithoutReplacingTheTopDownDib() => StaTestHost.Run(() =>
    {
        BitmapSource source = Source(32, 24);
        byte[] original = Pixels(source);
        using var dib = new NativeSelectorDib(source, new SolidColorBrush(Color.FromArgb(128, 30, 70, 110)) { Opacity = 0.5 });
        IntPtr bitmap = dib.Bitmap, dc = dib.Dc;
        Assert.True(ReadObject(bitmap) > 0);
        Assert.Equal(bitmap, NativeSelectorInterop.GetCurrentObject(dc, 7));
        Int32Rect[] reveals = [Int32Rect.Empty, new(2, 3, 20, 15), new(3, 3, 20, 15),
            new(-3, -2, 7, 8), new(0, 0, 32, 24), Int32Rect.Empty];
        foreach (Int32Rect reveal in reveals)
        {
            dib.SetReveal(reveal);
            var display = new byte[dib.ByteLength];
            Marshal.Copy(dib.Bits, display, 0, display.Length);
            for (int y = 0; y < 24; y++)
            for (int x = 0; x < 32; x++)
            {
                int offset = (y * 32 + x) * 4;
                bool inside = x >= reveal.X && x < reveal.X + reveal.Width && y >= reveal.Y && y < reveal.Y + reveal.Height;
                for (int channel = 0; channel < 3; channel++)
                {
                    int dim = channel == 0 ? 110 : channel == 1 ? 70 : 30;
                    byte expected = inside ? original[offset + channel] : (byte)((original[offset + channel] * 191 + dim * 64 + 127) / 255);
                    Assert.Equal(expected, display[offset + channel]);
                }
                Assert.Equal((byte)255, display[offset + 3]);
            }
            Assert.Equal(bitmap, dib.Bitmap);
            Assert.Equal(dc, dib.Dc);
            Assert.Equal(original, Pixels(source));
        }
        dib.Dispose();
        dib.Dispose();
        Assert.True(dib.IsDisposed);
        Assert.Equal(IntPtr.Zero, dib.Bits);
        Assert.Equal(0, ReadObject(bitmap));
        Assert.Equal(IntPtr.Zero, NativeSelectorInterop.GetCurrentObject(dc, 7));
        Assert.Throws<ObjectDisposedException>(() => dib.SetReveal(Int32Rect.Empty));
    });

    [Fact]
    public void SessionAllocatesOnceAndCloseDestroysTheActualNativeHandles() => StaTestHost.Run(() =>
    {
        using var overlay = Overlay();
        FrozenFrame frame = Frame();
        overlay.AttachFrame(frame);
        NativeSelectorDib dib = Assert.IsType<NativeSelectorDib>(overlay.DisplayDib);
        IntPtr hwnd = overlay.Handle, bitmap = dib.Bitmap, dc = dib.Dc, pen = overlay.SelectionPen;
        Assert.True(NativeSelectorInterop.IsWindow(hwnd));
        Assert.Equal(7u, NativeSelectorInterop.GetObjectType(bitmap)); // OBJ_BITMAP
        Assert.Equal(10u, NativeSelectorInterop.GetObjectType(dc)); // OBJ_MEMDC
        Assert.True(ReadObject(bitmap) > 0);
        Assert.True(ReadObject(pen) > 0);
        Assert.Equal(bitmap, NativeSelectorInterop.GetCurrentObject(dc, 7));
        int closed = 0;
        overlay.Closed += (_, _) => closed++;
        overlay.BeginSelection(new PointD(2, 3));
        for (int i = 0; i < 30; i++) overlay.MoveSelection(new PointD(20 + i, 30));
        Assert.Equal(1, overlay.DibAllocations);
        Assert.Same(dib, overlay.DisplayDib);
        Assert.Throws<InvalidOperationException>(() => overlay.AttachFrame(frame));
        Assert.Equal(4, overlay.LiveOwnedNativeHandles);
        overlay.Close();
        overlay.Close();
        Assert.Equal(1, closed);
        Assert.True(overlay.IsClosed);
        Assert.False(overlay.IsVisible);
        Assert.Null(overlay.Frame);
        Assert.Null(overlay.DisplayDib);
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.True(dib.IsDisposed);
        Assert.False(NativeSelectorInterop.IsWindow(hwnd));
        Assert.Equal(0, ReadObject(bitmap));
        Assert.Equal(0, ReadObject(pen));
        Assert.Equal(IntPtr.Zero, NativeSelectorInterop.GetCurrentObject(dc, 7));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionPreservesTheFrozenCropContractAndAlwaysCloses(bool subscriberThrows) => StaTestHost.Run(() =>
    {
        using var overlay = Overlay();
        FrozenFrame frame = Frame();
        overlay.AttachFrame(frame);
        IntPtr hwnd = overlay.Handle, bitmap = overlay.DisplayDib!.Bitmap;
        CaptureSelectionCompletedEventArgs? selection = null;
        overlay.SelectionCompleted += (_, value) =>
        {
            selection = value;
            if (subscriberThrows) throw new InvalidOperationException("Test subscriber failure");
        };
        overlay.BeginSelection(new PointD(4, 5));
        overlay.MoveSelection(new PointD(40, 30));
        if (subscriberThrows) Assert.Throws<InvalidOperationException>(() => overlay.EndSelection(new PointD(40, 30)));
        else overlay.EndSelection(new PointD(40, 30));
        Assert.NotNull(selection);
        Assert.Same(frame, selection.Frame);
        Assert.Equal(new RectD(4, 5, 36, 25), selection.BitmapRegion);
        Assert.True(selection.RecordForRepeat);
        byte[] expected = new byte[36 * 25 * 4];
        frame.Bitmap.CopyPixels(new Int32Rect(4, 5, 36, 25), expected, 36 * 4, 0);
        Assert.Equal(expected, Pixels(selection.SelectedBitmap));
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.False(NativeSelectorInterop.IsWindow(hwnd));
        Assert.Equal(0, ReadObject(bitmap));
    });

    [Fact]
    public void RejectedGeometryAndUnattachedCloseReleaseTheWindow() => StaTestHost.Run(() =>
    {
        using var overlay = Overlay();
        IntPtr hwnd = overlay.Handle;
        Assert.Throws<InvalidOperationException>(() => overlay.AttachFrame(new FrozenFrame(Source(8, 8), new RectD(0, 0, 8, 8), null, 0)));
        Assert.Equal(0, overlay.DibAllocations);
        overlay.Close();
        Assert.Equal(0, overlay.LiveOwnedNativeHandles);
        Assert.False(NativeSelectorInterop.IsWindow(hwnd));
    });

    private static NativeCaptureOverlay Overlay() => new(new RectD(-30000, -30000, 64, 48), false, false);
    // GetObjectType may return the encoded handle type after deletion. Request the actual object.
    private static int ReadObject(IntPtr handle) => NativeSelectorInterop.GetObject(handle,
        Marshal.SizeOf<NativeSelectorInterop.BitmapObject>(), out _);
    private static FrozenFrame Frame() => new(Source(64, 48), new RectD(-30000, -30000, 64, 48), null, 0);

    private static BitmapSource Source(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x * 3);
            pixels[offset + 1] = (byte)(y * 5);
            pixels[offset + 2] = (byte)(x + y);
            pixels[offset + 3] = 255;
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
}
