using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Interop;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ScreenCaptureReadbackTests
{
    [Fact]
    public void Crop_OwnsOnlySelectedPixels_AndDoesNotDependOnSource() => StaTestHost.Run(() =>
    {
        var source = new WriteableBitmap(8, 6, 120, 120, PixelFormats.Bgra32, null);
        byte[] original = Enumerable.Range(0, 8 * 6 * 4).Select(i => (byte)i).ToArray();
        source.WritePixels(new System.Windows.Int32Rect(0, 0, 8, 6), original, 32, 0);
        var frame = new FrozenFrame(source, new RectD(0, 0, 8, 6), null, 0);
        BitmapSource crop = ScreenCaptureEngine.Crop(frame, new RectD(2, 1, 3, 2));
        Assert.False(crop is CroppedBitmap);
        Assert.True(crop.IsFrozen);
        Assert.Equal(120, crop.DpiX);
        byte[] expected = original.Skip(40).Take(12).Concat(original.Skip(72).Take(12)).ToArray();
        source.WritePixels(new System.Windows.Int32Rect(0, 0, 8, 6), new byte[original.Length], 32, 0);
        byte[] actual = new byte[24];
        crop.CopyPixels(actual, 12, 0);
        Assert.Equal(expected, actual);
    });

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(17, 7)]
    public void Readback_PreservesEveryPixelAndTopDownRows_AndFreezesAcrossThreads(int width, int height) => StaTestHost.Run(() =>
    {
        using var surface = new NativeSurface(width, height);
        BitmapSource bitmap = ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, width, height);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        Assert.Equal(96, bitmap.DpiX);
        Assert.Equal(96, bitmap.DpiY);
        // CopyPixels and format conversion execute on a different thread. Conversion
        // also proves the undefined fourth DIB byte is not interpreted as alpha.
        Task<byte[]> read = Task.Run(() =>
        {
            var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            byte[] bytes = new byte[width * height * 4];
            opaque.CopyPixels(bytes, width * 4, 0);
            return bytes;
        });
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(surface.ExpectedPixels, read.Result);
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidDimensions_AreRejectedBeforeNativeReadback(int width, int height) => StaTestHost.Run(() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenCaptureEngine.ToBitmapSource(IntPtr.Zero, IntPtr.Zero, width, height)));

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(536870911, 2)]
    public void OverflowingLayout_IsRejectedBeforeAllocation(int width, int height) => StaTestHost.Run(() =>
        Assert.Throws<OverflowException>(() => ScreenCaptureEngine.ToBitmapSource(IntPtr.Zero, IntPtr.Zero, width, height)));

    [Fact]
    public void FailedNativeReadbackAndFreshSuccess_DoNotLeakGdiResources() => StaTestHost.Run(() =>
    {
        // All handles are owned by this fixture. Passing a null bitmap triggers
        // GetDIBits failure without invalidating any engine or shared handle.
        using (var warm = new NativeSurface(3, 5))
        {
            Assert.Throws<InvalidOperationException>(() => ScreenCaptureEngine.ToBitmapSource(warm.Dc, IntPtr.Zero, 3, 5));
            _ = ScreenCaptureEngine.ToBitmapSource(warm.Dc, warm.Bitmap, 3, 5);
        }
        using Process process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        Assert.True(before > 0, "GDI resource accounting must be available.");
        for (int attempt = 0; attempt < 40; attempt++)
        {
            using var surface = new NativeSurface(3, 5);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                ScreenCaptureEngine.ToBitmapSource(surface.Dc, IntPtr.Zero, 3, 5));
            Assert.Contains("GetDIBits returned 0 of 5", failure.Message);
            Assert.True(ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, 3, 5).IsFrozen);
        }
        uint after = GetGuiResources(process.Handle, 0);
        Assert.True((long)after - before <= 2, $"Process GDI resources grew from {before} to {after}.");
    });

    [Fact]
    public void Readback_DoesNotAllocateAManagedPixelPlane() => StaTestHost.Run(() =>
    {
        using var surface = new NativeSurface(513, 257);
        _ = ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, 513, 257);
        long before = GC.GetAllocatedBytesForCurrentThread();
        BitmapSource bitmap = ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, 513, 257);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The old byte[] alone exceeded 527 KB. Leave ample room for WPF managed
        // bookkeeping while rejecting even one full-frame managed pixel buffer.
        Assert.True(allocated < 513L * 257 * 4 / 2, $"Readback allocated {allocated} managed bytes.");
        GC.KeepAlive(bitmap);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureCore_DeselectsAndCleansResources_WithCursorOption(bool includeCursor) => StaTestHost.Run(() =>
    {
        using var fixture = new NativeCaptureFixture();
        WaitForFixturePresentation(fixture);
        _ = fixture.Engine.CaptureRegion(fixture.Region, includeCursor);
        using Process process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            BitmapSource bitmap = fixture.Engine.CaptureRegion(fixture.Region, includeCursor);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(320, bitmap.PixelWidth);
            Assert.Equal(240, bitmap.PixelHeight);
            if (!includeCursor)
            {
                byte[] pixel = new byte[4];
                bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                Assert.Equal(fixture.ExpectedBgr, pixel[..3]);
            }
        }
        uint after = GetGuiResources(process.Handle, 0);
        Assert.True((long)after - before <= 2, $"Capture GDI resources grew from {before} to {after}.");
    });

    [Fact]
    public void CaptureRegion_OnMtaWorker_ReturnsFrozenPixelsToStaOwner() => StaTestHost.Run(() =>
    {
        using var fixture = new NativeCaptureFixture();
        WaitForFixturePresentation(fixture);
        Task<(BitmapSource Bitmap, byte[] Reference)> capture = Task.Run(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            byte[] legacy = new byte[320 * 240 * 4];
            fixture.Engine.CaptureRegionInto(fixture.Region, false, legacy, 320 * 4);
            return (fixture.Engine.CaptureRegion(fixture.Region, includeCursor: false), legacy);
        });
        // Keep the owner window responsive while the worker captures it, matching
        // the production coordinator's await rather than blocking its dispatcher.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!capture.IsCompleted && DateTime.UtcNow < deadline) SyntheticCaptureFixture.Pump(10);
        Assert.True(capture.IsCompletedSuccessfully, capture.Exception?.ToString());
        BitmapSource bitmap = capture.Result.Bitmap;
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        byte[] actual = new byte[320 * 240 * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 320 * 4, 0);
        // Compare the actual desktop to the established readback path after the
        // fixture's solid bitmap has reached the compositor.
        Assert.Equal(capture.Result.Reference, actual);
    });

    [Fact]
    public void NativePattern_CreatedAndReadOnMta_PreservesColorsOnSta() => StaTestHost.Run(() =>
    {
        Task<(BitmapSource Bitmap, byte[] Expected)> capture = Task.Run(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            using var surface = new NativeSurface(17, 7);
            return (ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, 17, 7), surface.ExpectedPixels);
        });
        Assert.True(capture.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(capture.Result.Bitmap.IsFrozen);
        byte[] actual = new byte[17 * 7 * 4];
        new FormatConvertedBitmap(capture.Result.Bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(actual, 17 * 4, 0);
        Assert.Equal(capture.Result.Expected, actual);
    });

    [Fact]
    public void NegativeScreenOrigin_RemainsMetadataRatherThanReadbackOffset() => StaTestHost.Run(() =>
    {
        using var surface = new NativeSurface(3, 5);
        BitmapSource bitmap = ScreenCaptureEngine.ToBitmapSource(surface.Dc, surface.Bitmap, 3, 5);
        var frame = new FrozenFrame(bitmap, new RectD(-13, -17, 3, 5), null, 0);
        Assert.Equal(new PointD(0, 0), frame.ToBitmapSpace(new PointD(-13, -17)));
        Assert.Equal(new RectD(1, 2, 2, 3), frame.ToBitmapSpace(new RectD(-12, -15, 2, 3)));
    });

    private static void WaitForFixturePresentation(NativeCaptureFixture fixture)
    {
        byte[] pixels = new byte[320 * 240 * 4];
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        do
        {
            // Use the established recording readback as the readiness oracle,
            // independently of the new WriteableBitmap conversion under test.
            fixture.Engine.CaptureRegionInto(fixture.Region, false, pixels, 320 * 4);
            bool ready = true;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset] != fixture.ExpectedBgr[0] ||
                    pixels[offset + 1] != fixture.ExpectedBgr[1] ||
                    pixels[offset + 2] != fixture.ExpectedBgr[2])
                {
                    ready = false;
                    break;
                }
            }
            if (ready) return;
            // Pump the owner dispatcher only while its presented pixels are not
            // ready. Creating a window alone does not synchronize with composition.
            SyntheticCaptureFixture.Pump(10);
        } while (Stopwatch.GetTimestamp() < deadline);

        Assert.Fail($"The capture fixture did not present its solid background within five seconds; first BGR pixel was {pixels[0]},{pixels[1]},{pixels[2]}.");
    }

    private sealed class NativeCaptureFixture : IDisposable
    {
        private readonly IntPtr _window;
        private readonly NativeSurface _surface;
        internal ScreenCaptureEngine Engine { get; } = new(NullLogger<ScreenCaptureEngine>.Instance);
        internal RectD Region { get; }
        internal byte[] ExpectedBgr { get; } = [166, 90, 18];

        internal NativeCaptureFixture()
        {
            // An explicit bitmap avoids both themed stock-brush colors and a WPF
            // renderer associated with an earlier STA.
            _surface = new NativeSurface(340, 260, solid: true);
            // Topmost tool window, no activation; owned and destroyed by this STA.
            _window = CreateWindowEx(0x08000088, "STATIC", "Capture readback fixture",
                0x9000000E, 100, 100, 340, 260, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            try
            {
                Assert.NotEqual(IntPtr.Zero, _window);
                SendMessage(_window, 0x0172, IntPtr.Zero, _surface.Bitmap); // STM_SETIMAGE / IMAGE_BITMAP
                Assert.True(UpdateWindow(_window));
                Assert.True(NativeMethods.GetWindowRect(_window, out NativeMethods.RECT bounds));
                Region = new RectD(bounds.Left + 10, bounds.Top + 10, 320, 240);
            }
            catch
            {
                DestroyWindow(_window);
                _surface.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { Assert.True(DestroyWindow(_window)); }
            finally { _surface.Dispose(); }
        }
    }

    private sealed class NativeSurface : IDisposable
    {
        internal IntPtr Dc { get; private set; }
        internal IntPtr Bitmap { get; private set; }
        internal byte[] ExpectedPixels { get; }

        internal NativeSurface(int width, int height, bool solid = false)
        {
            ExpectedPixels = new byte[width * height * 4];
            IntPtr desktop = NativeMethods.GetDesktopWindow();
            IntPtr screen = NativeMethods.GetWindowDC(desktop);
            Assert.NotEqual(IntPtr.Zero, screen);
            try
            {
                Dc = NativeMethods.CreateCompatibleDC(screen);
                Assert.NotEqual(IntPtr.Zero, Dc);
                Bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
                Assert.NotEqual(IntPtr.Zero, Bitmap);
                IntPtr previous = NativeMethods.SelectObject(Dc, Bitmap);
                Assert.NotEqual(IntPtr.Zero, previous);
                Assert.NotEqual(new IntPtr(-1), previous);
                try
                {
                    for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        byte r = solid ? (byte)18 : (byte)(17 + x * 11);
                        byte g = solid ? (byte)90 : (byte)(29 + y * 23);
                        byte b = solid ? (byte)166 : (byte)(43 + x * 3 + y * 5);
                        Assert.True(SetPixelV(Dc, x, y, (uint)(r | g << 8 | b << 16)));
                        int offset = (y * width + x) * 4;
                        ExpectedPixels[offset] = b;
                        ExpectedPixels[offset + 1] = g;
                        ExpectedPixels[offset + 2] = r;
                        ExpectedPixels[offset + 3] = 255;
                    }
                }
                finally
                {
                    Assert.Equal(Bitmap, NativeMethods.SelectObject(Dc, previous));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                NativeMethods.ReleaseDC(desktop, screen);
            }
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero) { NativeMethods.DeleteDC(Dc); Dc = IntPtr.Zero; }
            if (Bitmap != IntPtr.Zero) { NativeMethods.DeleteObject(Bitmap); Bitmap = IntPtr.Zero; }
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPixelV(IntPtr dc, int x, int y, uint color);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
