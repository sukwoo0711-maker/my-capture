using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinNativeWindowTests
{
    [Fact]
    public void ClosingHiddenNativeWindowReleasesTheRealHwndDibAndDc() => StaTestHost.Run(() =>
    {
        BitmapSource picture = PinNativeTestImages.Picture(80, 60, transparent: true);
        Assert.Equal(0, NativePinWindow.LiveWindowCount);
        Assert.Equal(0, NativePinWindow.LiveDibCount);
        Assert.Equal(0, NativePinWindow.LiveDcCount);
        using Process process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        IntPtr handle;
        using (var pin = new NativePinWindow(picture, -20000, -20000))
        {
            handle = pin.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(IsWindow(handle));
            Assert.Equal(1, NativePinWindow.LiveWindowCount);
            Assert.Equal(1, NativePinWindow.LiveDibCount);
            Assert.Equal(1, NativePinWindow.LiveDcCount);
            Assert.Equal(80L * 60 * 4, pin.DisplayBytes);
            Assert.False(pin.IsVisible);
            pin.Close();
            pin.Close();
            Assert.True(pin.IsClosed);
            Assert.Null(pin.Source);
        }
        Assert.False(IsWindow(handle));
        Assert.Equal(0, NativePinWindow.LiveWindowCount);
        Assert.Equal(0, NativePinWindow.LiveDibCount);
        Assert.Equal(0, NativePinWindow.LiveDcCount);
        Assert.Equal(before, GetGuiResources(process.Handle, 0));
    });

    [Fact]
    public void InteriorMoveKeepsTheSameRasterAndPixels() => StaTestHost.Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
        byte[] before = pin.CopyDisplayPixels();
        var rasters = pin.RasterCount;
        var bounds = pin.Bounds;
        pin.MovePhysical(bounds.Left + 200, bounds.Top - 35);
        Assert.Equal(bounds.Left + 200, pin.Bounds.Left);
        Assert.Equal(bounds.Top - 35, pin.Bounds.Top);
        Assert.Equal(rasters, pin.RasterCount);
        Assert.Equal(before, pin.CopyDisplayPixels());
    });

    [Fact]
    public void MoveKeepsTheActualPositionAfterSynchronousWindowPlacementAdjustment() => StaTestHost.Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
        byte[] before = pin.CopyDisplayPixels();
        int rasters = pin.RasterCount;
        IntPtr original = IntPtr.Zero;
        bool adjusted = false;
        Exception? callbackFailure = null;
        // Windows permits an HWND procedure to amend WINDOWPOS during SetWindowPos.
        // This is an actual reentrant placement change, not a replacement of the pin's API.
        NativePinWindowInterop.WindowProcedure callback = (hwnd, message, wparam, lparam) =>
        {
            try
            {
                if (message == 0x0046 && !adjusted) // WM_WINDOWPOSCHANGING
                {
                    var position = Marshal.PtrToStructure<WindowPosition>(lparam);
                    if ((position.Flags & 2) == 0) // SWP_NOMOVE is absent.
                    {
                        position.X += 17;
                        position.Y -= 11;
                        Marshal.StructureToPtr(position, lparam, false);
                        adjusted = true;
                    }
                }
            }
            catch (Exception error) { callbackFailure = error; }
            return CallWindowProcW(original, hwnd, message, wparam, lparam);
        };
        original = NativePinWindowInterop.SetWindowLong(pin.Handle, -4,
            Marshal.GetFunctionPointerForDelegate(callback)); // GWLP_WNDPROC
        Assert.NotEqual(IntPtr.Zero, original);
        try
        {
            pin.MovePhysical(-19800, -20035);
            Assert.Null(callbackFailure);
            Assert.True(adjusted);
            Assert.True(NativePinWindowInterop.GetWindowRect(pin.Handle, out var actual));
            Assert.Equal(-19783, actual.Left);
            Assert.Equal(-20046, actual.Top);
            Assert.Equal(actual.Left, pin.Bounds.Left);
            Assert.Equal(actual.Top, pin.Bounds.Top);
            Assert.Equal(actual.Right - actual.Left, pin.Bounds.Width);
            Assert.Equal(actual.Bottom - actual.Top, pin.Bounds.Height);
            Assert.Equal(rasters, pin.RasterCount);
            Assert.Equal(before, pin.CopyDisplayPixels());
        }
        finally
        {
            _ = NativePinWindowInterop.SetWindowLong(pin.Handle, -4, original);
            GC.KeepAlive(callback);
        }
    });

    [Fact]
    public void ZoomKeepsTheSourcePointUnderThePhysicalAnchor() => StaTestHost.Run(() =>
    {
        using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
        Point anchor = new(pin.Bounds.Left + 20, pin.Bounds.Top + 20);
        pin.SetZoom(1.5, anchor);
        Assert.Equal(1.5, pin.Zoom);
        Assert.Equal(120, pin.PixelWidth);
        Assert.Equal(90, pin.PixelHeight);
        Assert.Equal(30, anchor.X - pin.Bounds.Left);
        Assert.Equal(30, anchor.Y - pin.Bounds.Top);
        Assert.Equal(1, NativePinWindow.LiveDibCount);
    });

    [Fact]
    public void DpiChangeRebuildsTheDisplayButPreservesSourceAndZoom() => StaTestHost.Run(() =>
    {
        BitmapSource source = PinNativeTestImages.Picture(80, 60);
        using var pin = new NativePinWindow(source, -20000, -20000, zoom: 1.5);
        NativePinPixelSource ownedSource = Assert.IsType<NativePinPixelSource>(pin.Source);
        byte[] sourcePixels = PinNativeTestImages.Pixels(source);
        var before = pin.RasterCount;
        pin.SetDpiScale(1.5);
        Assert.Same(ownedSource, pin.Source);
        using (NativePinPixelSource.Lease lease = ownedSource.AcquireLease())
            Assert.Equal(sourcePixels, PinNativeTestMemory.Copy(lease.Pointer, checked(lease.Stride * lease.Height)));
        Assert.Equal(1.5, pin.Zoom);
        Assert.Equal(1.5, pin.DpiScale);
        Assert.Equal(180, pin.PixelWidth);
        Assert.Equal(135, pin.PixelHeight);
        Assert.Equal(180L * 135 * 4, pin.DisplayBytes);
        Assert.True(pin.RasterCount > before);
        Assert.Equal(1, NativePinWindow.LiveDibCount);
    });

    [Fact]
    public void InvalidSourceConstructionDoesNotLeaveNativeHandles() => StaTestHost.Run(() =>
    {
        Assert.Equal(0, NativePinWindow.LiveWindowCount);
        Assert.Equal(0, NativePinWindow.LiveDibCount);
        Assert.Equal(0, NativePinWindow.LiveDcCount);
        Assert.ThrowsAny<ArgumentException>(() => new NativePinWindow((BitmapSource)null!, -20000, -20000));
        Assert.Equal(0, NativePinWindow.LiveWindowCount);
        Assert.Equal(0, NativePinWindow.LiveDibCount);
        Assert.Equal(0, NativePinWindow.LiveDcCount);
    });

    [Fact]
    public void WholeWindowOpacityUsesOneSuccessfulNativeUpdateWithoutRepainting() => StaTestHost.Run(() =>
    {
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60, transparent: true), -20000, -20000);
            pin.Show();
            Assert.True(IsWindow(pin.Handle));
            Assert.True(pin.IsVisible);
            Assert.Null(pin.LastPresentError);
            Assert.Equal(255, pin.LastPresentedAlpha);
            int rasters = pin.RasterCount, presents = pin.PresentCount;
            byte[] before = pin.CopyDisplayPixels();

            pin.SetOpacity(.6);
            Assert.Equal(.6, pin.State.Opacity);
            Assert.Equal(.6, pin.EffectiveOpacity);
            Assert.Equal(153, pin.LastPresentedAlpha);
            Assert.Equal(presents + 1, pin.PresentCount);
            Assert.Equal(rasters, pin.RasterCount);
            Assert.Equal(before, pin.CopyDisplayPixels());
            Assert.Null(pin.LastPresentError);

            pin.SetOpacity(1);
            Assert.Equal(255, pin.LastPresentedAlpha);
            Assert.Equal(presents + 2, pin.PresentCount);
            Assert.Equal(rasters, pin.RasterCount);
            Assert.Equal(before, pin.CopyDisplayPixels());
            Assert.Null(pin.LastPresentError);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; }
    });

    [Fact]
    public void ExistingClickThroughFacadePreservesNativeLayeredSurfaceOnRoundTrip() => StaTestHost.Run(() =>
    {
        const uint layered = 0x00080000, transparent = 0x00000020, noActivate = 0x08000000;
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
            pin.Show();
            uint initialStyle = unchecked((uint)GetWindowLongW(pin.Handle, -20));
            int rasters = pin.RasterCount;
            byte[] before = pin.CopyDisplayPixels();
            Assert.Equal(layered, initialStyle & layered);
            Assert.False(WindowStyleFacade.IsClickThrough(pin.Handle));

            pin.SetClickThrough(true);
            Assert.True(pin.State.IsClickThrough);
            Assert.True(WindowStyleFacade.IsClickThrough(pin.Handle));
            uint throughStyle = unchecked((uint)GetWindowLongW(pin.Handle, -20));
            Assert.Equal(layered | transparent | noActivate, throughStyle & (layered | transparent | noActivate));

            pin.SetClickThrough(false);
            Assert.False(pin.State.IsClickThrough);
            Assert.False(WindowStyleFacade.IsClickThrough(pin.Handle));
            uint interactiveStyle = unchecked((uint)GetWindowLongW(pin.Handle, -20));
            Assert.Equal(initialStyle & ~(transparent | noActivate), interactiveStyle);
            Assert.Equal(layered, interactiveStyle & layered);
            Assert.Equal(rasters, pin.RasterCount);
            Assert.Equal(before, pin.CopyDisplayPixels());
            // The same surface remains usable after both style transitions.
            pin.SetOpacity(.6);
            Assert.Equal(153, pin.LastPresentedAlpha);
            Assert.Null(pin.LastPresentError);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; }
    });

    [Fact]
    public void EnablingClickThroughEndsTheActualNativeGestureAndReleasesCapture() => StaTestHost.Run(() =>
    {
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
            pin.Show();
            Assert.Equal(IntPtr.Zero, NativePinWindowInterop.GetCapture());
            // A synthetic client message to our own offscreen HWND does not move the cursor.
            _ = SendMessageW(pin.Handle, 0x0201, new IntPtr(1), new IntPtr((30 << 16) | 40));
            Assert.Null(pin.LastPresentError);
            Assert.True(pin.IsDragging, "The real HWND must acquire mouse capture before testing its release.");
            Assert.Equal(pin.Handle, NativePinWindowInterop.GetCapture());

            pin.SetClickThrough(true);
            Assert.True(pin.State.IsClickThrough);
            Assert.False(pin.IsDragging);
            Assert.Equal(IntPtr.Zero, NativePinWindowInterop.GetCapture());

            var bounds = pin.Bounds;
            // Even an explicit client message cannot continue the released gesture.
            _ = SendMessageW(pin.Handle, 0x0200, new IntPtr(1), new IntPtr((38 << 16) | 49));
            Assert.Equal(bounds, pin.Bounds);
            Assert.False(pin.IsDragging);
            Assert.Null(pin.LastPresentError);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingDuringRevealNeverMakesTheNativeSurfaceMoreOpaque(bool afterFirstVisibleFrame) => StaTestHost.Run(() =>
    {
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        try
        {
            using var pin = new NativePinWindow(PinNativeTestImages.Picture(80, 60), -20000, -20000);
            IntPtr handle = pin.Handle;
            bool closing = false;
            byte alphaAtClose = 255;
            var alphasAfterClose = new List<byte>();
            void StartExit()
            {
                alphaAtClose = pin.LastPresentedAlpha;
                closing = true;
                pin.CloseWithExit();
            }
            pin.Presented += () =>
            {
                if (closing) alphasAfterClose.Add(pin.LastPresentedAlpha);
                else if (afterFirstVisibleFrame && pin.LastPresentedAlpha > 0) StartExit();
            };
            pin.Show();
            Assert.Equal(0, pin.LastPresentedAlpha);
            if (!afterFirstVisibleFrame) StartExit();

            var watch = Stopwatch.StartNew();
            while (!pin.IsClosed && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
                    (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
            }
            Assert.True(closing);
            Assert.InRange(alphaAtClose, afterFirstVisibleFrame ? 1 : 0, 254);
            Assert.NotEmpty(alphasAfterClose);
            int previous = alphaAtClose;
            foreach (byte alpha in alphasAfterClose)
            {
                Assert.InRange(alpha, 0, previous);
                previous = alpha;
            }
            Assert.Null(pin.LastPresentError);
            Assert.True(pin.IsClosed, "The actual exit timer did not dispose the native window within two seconds.");
            Assert.False(IsWindow(handle));
            Assert.Null(pin.Source);
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            Assert.Equal(0, NativePinWindow.LiveDibCount);
            Assert.Equal(0, NativePinWindow.LiveDcCount);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        internal IntPtr Window, InsertAfter;
        internal int X, Y, Width, Height;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr procedure, IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);
}
