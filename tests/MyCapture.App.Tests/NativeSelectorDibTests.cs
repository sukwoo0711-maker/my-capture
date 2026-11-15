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
    public void DragStartOverAHighlightedWindowDimsInPlaceAndMatchesAFreshDisplay() => StaTestHost.Run(() =>
    {
        // A drag starting over a window highlight collapses the large reveal in its first sample. That
        // region is dimmed where it is instead of re-read from the frame; the result must be identical,
        // including under the start chip and other stamps, and for frames whose fourth byte is not 255.
        BitmapSource source = Source(96, 64, padding: 7);
        var dimmer = new SolidColorBrush(Color.FromArgb(128, 30, 70, 110)) { Opacity = 0.5 };
        NativeSelectorStamp chip = Stamp(new Int32Rect(10, 30, 9, 5), [40, 50, 60, 200]);
        NativeSelectorStamp chrome = Stamp(new Int32Rect(70, 2, 12, 6), [90, 10, 20, 255]);
        using var display = new NativeSelectorDib(source, dimmer);
        display.SetStamps([chrome]);
        Int32Rect[] samples = [new(4, 4, 88, 56), new(12, 26, 3, 2), new(12, 26, 17, 8), new(12, 26, 60, 30),
            new(12, 20, 40, 10), new(0, 0, 96, 64), new(30, 30, 5, 5), new(0, 0, 96, 64), Int32Rect.Empty];
        for (int index = 0; index < samples.Length; index++)
        {
            if (index == 1) display.SetAnchorStamp(chip);
            long dimmedBefore = display.PixelsDimmedInPlace;
            display.SetReveal(samples[index]);
            if (index == 1) Assert.True(display.PixelsDimmedInPlace - dimmedBefore > 80 * 50);
            using var expected = new NativeSelectorDib(source, dimmer);
            expected.SetStamps([chrome]);
            if (index >= 1) expected.SetAnchorStamp(chip);
            expected.SetReveal(samples[index]);
            Assert.Equal(Bits(expected), Bits(display));
        }
    });

    [Fact]
    public void DimmingALargeHighlightAcrossCoresMatchesTheSingleThreadedResultByteForByte() => StaTestHost.Run(() =>
    {
        // The first pointer sample of a drag over a maximized window dims millions of pixels, so bands
        // of rows run on the pool above a threshold. Row bands are independent, and the result has to
        // be the same pixels as one thread writes, under the stamps as well, whether the band count
        // divides the height or not.
        var dimmer = new SolidColorBrush(Color.FromArgb(128, 30, 70, 110)) { Opacity = 0.5 };
        // Inside the band that goes to the pool, and translucent so its result depends on the pixels
        // underneath it rather than replacing them.
        NativeSelectorStamp chrome = Stamp(new Int32Rect(600, 400, 40, 9), [40, 50, 60, 200]);
        foreach (int height in new[] { 601, 640 })
        {
            BitmapSource source = Source(1024, height, padding: 7);
            using var display = new NativeSelectorDib(source, dimmer);
            display.SetStamps([chrome]);
            display.SetReveal(new Int32Rect(0, 0, 1024, height));
            long parallelBefore = display.PixelsDimmedInParallel;
            // Collapsing the whole frame onto a small selection dims more than the threshold at once.
            display.SetReveal(new Int32Rect(300, 200, 40, 30));
            Assert.True(display.PixelsDimmedInParallel - parallelBefore > (1 << 18));
            using var expected = new NativeSelectorDib(source, dimmer);
            expected.SetStamps([chrome]);
            expected.SetReveal(new Int32Rect(300, 200, 40, 30));
            Assert.Equal(0L, expected.PixelsDimmedInParallel);
            Assert.Equal(Bits(expected), Bits(display));
        }
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

    private static NativeSelectorStamp Stamp(Int32Rect bounds, byte[] pixel) =>
        new(bounds, Enumerable.Range(0, bounds.Width * bounds.Height).SelectMany(_ => pixel).ToArray());

    private static byte[] Bits(NativeSelectorDib dib)
    {
        var pixels = new byte[dib.ByteLength];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        return pixels;
    }

    private static BitmapSource Source(int width, int height, byte padding = 255)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(x * 3);
            pixels[offset + 1] = (byte)(y * 5);
            pixels[offset + 2] = (byte)(x + y);
            pixels[offset + 3] = padding;
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
