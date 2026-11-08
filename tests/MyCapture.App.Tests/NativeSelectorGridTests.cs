using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorGridTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void GuidesReuseTheirNativeSpritesSuppressOverTheBarAndReleaseEveryHandle(double scale) => StaTestHost.Run(() =>
    {
        var frame = WindowDetectOverlayTests.Frame();
        PointD pointer = new(320, 180);
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, (uint)Math.Round(96 * scale), true)];
        using var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
        };
        owner.AttachFrame(frame);
        int baseHandles = owner.LiveOwnedNativeHandles;
        Assert.Empty(owner.VisualFeedbackHandlesForTest);
        using var chrome = new NativeSelectorChrome(owner, false, false);
        IReadOnlyList<IntPtr> handles = owner.VisualFeedbackHandlesForTest;
        Assert.NotEmpty(handles);
        Assert.Equal(0, handles.Count % 2);
        Assert.Equal(baseHandles + handles.Count, owner.LiveOwnedNativeHandles);
        for (int index = 0; index < handles.Count; index += 2)
        {
            Assert.NotEqual(IntPtr.Zero, GetCurrentObject(handles[index], 7));
            Assert.True(ReadBitmap(handles[index + 1]) > 0);
        }
        Assert.Null(owner.HorizontalGuideBoundsForTest);
        Assert.Null(owner.VerticalGuideBoundsForTest);
        Assert.Equal(DisplayPixels(owner), PaintPixels(owner));

        owner.UpdatePointer(false, false);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        Int32Rect horizontal = Assert.IsType<Int32Rect>(owner.HorizontalGuideBoundsForTest);
        Int32Rect vertical = Assert.IsType<Int32Rect>(owner.VerticalGuideBoundsForTest);
        Assert.Equal(0, horizontal.X);
        Assert.Equal(frame.PixelWidth, horizontal.Width);
        Assert.Equal(0, vertical.Y);
        Assert.Equal(frame.PixelHeight, vertical.Height);
        Assert.InRange(pointer.Y, horizontal.Y, horizontal.Y + horizontal.Height);
        Assert.InRange(pointer.X, vertical.X, vertical.X + vertical.Width);
        byte[] ready = PaintPixels(owner);
        Assert.NotEqual(DisplayPixels(owner), ready);
        for (int index = 0; index < 20; index++)
        {
            owner.UpdatePointer(false, false);
            chrome.Update(pointer, owner.DisplayedRegionForTest);
            Assert.Same(handles, owner.VisualFeedbackHandlesForTest);
            Assert.Equal(baseHandles + handles.Count, owner.LiveOwnedNativeHandles);
            Assert.Equal(ready, PaintPixels(owner));
        }

        RectD bar = chrome.BoundsForTest(chrome.Windows[0]);
        pointer = new PointD(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
        // Entering a separate HWND stops the owner's WM_MOUSEMOVE messages. Exercise the actual
        // WPF bar event route using the fake cursor, without manually refreshing native input.
        chrome.Windows[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert.Null(owner.HorizontalGuideBoundsForTest);
        Assert.Null(owner.VerticalGuideBoundsForTest);
        Assert.Equal(DisplayPixels(owner), PaintPixels(owner));
        Assert.Same(handles, owner.VisualFeedbackHandlesForTest);

        pointer = new PointD(320, 180);
        chrome.Windows[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 1) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert.Equal(horizontal, owner.HorizontalGuideBoundsForTest);
        Assert.Equal(vertical, owner.VerticalGuideBoundsForTest);
        Assert.Equal(ready, PaintPixels(owner));
        Assert.Same(handles, owner.VisualFeedbackHandlesForTest);

        chrome.Dispose();
        owner.Close();
        Assert.Empty(owner.VisualFeedbackHandlesForTest);
        Assert.Equal(0, owner.LiveOwnedNativeHandles);
        for (int index = 0; index < handles.Count; index += 2)
        {
            Assert.Equal(IntPtr.Zero, GetCurrentObject(handles[index], 7));
            Assert.Equal(0, ReadBitmap(handles[index + 1]));
        }
    });

    private static byte[] PaintPixels(NativeCaptureOverlay owner)
    {
        using var painted = new NativeSelectorDib(owner.Frame!.Bitmap, new SolidColorBrush(Colors.Transparent));
        owner.PaintTo(painted.Dc);
        Assert.True(NativeSelectorInterop.GdiFlush());
        return ReadPixels(painted);
    }

    private static byte[] DisplayPixels(NativeCaptureOverlay owner) => ReadPixels(owner.DisplayDib!);
    private static byte[] ReadPixels(NativeSelectorDib dib)
    {
        byte[] pixels = new byte[dib.ByteLength];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        // Bgr32's unused byte is not a rendered channel and GDI may alter it during blending.
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 0;
        return pixels;
    }

    private static int ReadBitmap(IntPtr handle) => NativeSelectorInterop.GetObject(handle,
        Marshal.SizeOf<NativeSelectorInterop.BitmapObject>(), out _);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetCurrentObject(IntPtr dc, uint objectType);
}
