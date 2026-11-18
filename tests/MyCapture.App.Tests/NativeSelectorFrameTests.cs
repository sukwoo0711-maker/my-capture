using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The native selector's actual painted frame: a one-pixel keyline and a two-pixel stroke outside
/// the target, crisp solid fills, inside the target only on a side at the desktop edge, for the drag
/// selection and the hover highlight alike.
/// </summary>
public sealed class NativeSelectorFrameTests
{
    private const int Width = 320, Height = 200;

    [Fact]
    public void TheDragFrameLiesOutsideTheSelectionAndCoversNoCapturedPixel() => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Gradient();
        using NativeCaptureOverlay owner = Owner(frame, () => new PointD(60, 50));
        owner.BeginSelection(new PointD(60, 50));
        owner.MoveSelection(new PointD(260, 150));
        var selection = new Int32Rect(60, 50, 200, 100);
        Assert.Equal(selection, owner.SelectionRectangle);
        Assert.Equal(selection.Width * selection.Height, AssertFrame(owner, frame, selection));
        // Each ring is exactly as wide as the shared style says, left of the selection.
        (Color stroke, Color keyline) = FrameColors();
        byte[] painted = Paint(owner);
        Assert.Equal(keyline, At(painted, 59, 100));
        Assert.Equal(stroke, At(painted, 58, 100));
        Assert.Equal(stroke, At(painted, 57, 100));
        Assert.NotEqual(stroke, At(painted, 56, 100));
        Assert.Equal(CaptureFrameStyle.Outset, 60 - 57);
    });

    [Fact]
    public void ADragFromTheDesktopEdgeKeepsThatSideOfTheFrameInside() => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Gradient();
        using NativeCaptureOverlay owner = Owner(frame, () => new PointD(0, 1));
        owner.BeginSelection(new PointD(0, 1));
        owner.MoveSelection(new PointD(140, 120));
        var selection = new Int32Rect(0, 1, 140, 119);
        Assert.Equal(selection, owner.SelectionRectangle);
        // Left and top would leave the desktop: three columns and three rows of the selection carry
        // the frame there, the stroke on the edge and the keyline next to the picture.
        Assert.Equal((selection.Width - 3) * (selection.Height - 3), AssertFrame(owner, frame, selection));
        (Color stroke, Color keyline) = FrameColors();
        byte[] painted = Paint(owner);
        Assert.Equal(stroke, At(painted, 0, 60));
        Assert.Equal(stroke, At(painted, 1, 60));
        Assert.Equal(keyline, At(painted, 2, 60));
        Assert.Equal(keyline, At(painted, 140, 60));
        Assert.Equal(stroke, At(painted, 142, 60));
    });

    [Fact]
    public void TheHoverFrameFollowsTheSameRulesForAWindowAndForAMaximizedOne() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            FrozenFrame frame = Gradient();
            PointD pointer = new(100, 100);
            var notes = new WindowCandidate(new IntPtr(0x4101), new RectD(40, 30, 150, 110), "Notes");
            var maximized = new WindowCandidate(new IntPtr(0x4102), new RectD(0, 0, Width, Height), "Maximized");
            using NativeCaptureOverlay owner = Owner(frame, () => pointer, notes, maximized);
            owner.UpdatePointer(false, false);
            owner.RefreshHover();
            var window = new Int32Rect(40, 30, 150, 110);
            Assert.Equal(window, owner.DisplayedRegionForTest);
            Assert.Equal(window.Width * window.Height, AssertFrame(owner, frame, window));

            pointer = new PointD(260, 170);
            owner.UpdatePointer(false, false);
            owner.RefreshHover();
            var screen = new Int32Rect(0, 0, Width, Height);
            Assert.Equal(screen, owner.DisplayedRegionForTest);
            // Every side is on the desktop edge: the frame is the outer three pixels all round.
            Assert.Equal((Width - 6) * (Height - 6), AssertFrame(owner, frame, screen));
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    /// <summary>
    /// Checks every pixel of the actual paint against the frame geometry and returns how many
    /// pixels of <paramref name="target"/> show the captured picture untouched.
    /// </summary>
    private static int AssertFrame(NativeCaptureOverlay owner, FrozenFrame frame, Int32Rect target)
    {
        (Color stroke, Color keyline) = FrameColors();
        Assert.NotEqual(stroke, keyline);
        byte[] painted = Paint(owner), display = Pixels(owner.DisplayDib!), source = new byte[Width * Height * 4];
        frame.Bitmap.CopyPixels(source, Width * 4, 0);
        bool leftOut = target.X >= 3, topOut = target.Y >= 3;
        bool rightOut = target.X + target.Width + 3 <= Width, bottomOut = target.Y + target.Height + 3 <= Height;
        int outerLeft = leftOut ? target.X - 3 : target.X, outerTop = topOut ? target.Y - 3 : target.Y;
        int outerRight = target.X + target.Width + (rightOut ? 3 : 0), outerBottom = target.Y + target.Height + (bottomOut ? 3 : 0);
        int captured = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            bool inTarget = x >= target.X && x < target.X + target.Width && y >= target.Y && y < target.Y + target.Height;
            bool inOuter = x >= outerLeft && x < outerRight && y >= outerTop && y < outerBottom;
            // How deep the pixel lies inside the frame's outer edge: 0–1 stroke, 2 keyline.
            int depth = Math.Min(Math.Min(x - outerLeft, outerRight - 1 - x), Math.Min(y - outerTop, outerBottom - 1 - y));
            if (inOuter && depth < 2) Assert.True(stroke == At(painted, x, y), $"Stroke at ({x}, {y}): {At(painted, x, y)}");
            else if (inOuter && depth == 2) Assert.True(keyline == At(painted, x, y), $"Keyline at ({x}, {y}): {At(painted, x, y)}");
            else if (inTarget)
            {
                Assert.True(Same(painted, source, offset), $"Captured pixel covered at ({x}, {y})");
                captured++;
            }
            else Assert.True(Same(painted, display, offset), $"Dimmed pixel changed at ({x}, {y})");
        }
        return captured;
    }

    private static NativeCaptureOverlay Owner(FrozenFrame frame, Func<PointD> pointer, params WindowCandidate[] windows)
    {
        MonitorInfo[] monitors = [new("frame", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            ReadCursor = pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            SourceWindows = windows, DetectWindows = windows.Length > 0,
        };
        owner.AttachFrame(frame);
        owner.PreparePointerFeedback();
        return owner;
    }

    private static (Color Stroke, Color Keyline) FrameColors()
    {
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
        Color Opaque(string key) => ((SolidColorBrush)palette[key]).Color with { A = 255 };
        return (Opaque("CaptureGuide.SelectionBorder"), Opaque("CaptureGuide.CrosshairShadow"));
    }

    private static byte[] Paint(NativeCaptureOverlay owner)
    {
        using var painted = new NativeSelectorDib(owner.Frame!.Bitmap, new SolidColorBrush(Colors.Transparent));
        owner.PaintTo(painted.Dc);
        Assert.True(NativeSelectorInterop.GdiFlush());
        return Pixels(painted);
    }

    private static byte[] Pixels(NativeSelectorDib dib)
    {
        Assert.Equal(Width * 4, dib.Stride);
        byte[] pixels = new byte[dib.ByteLength];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        return pixels;
    }

    private static Color At(byte[] pixels, int x, int y)
    {
        int offset = (y * Width + x) * 4;
        return Color.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static bool Same(byte[] a, byte[] b, int offset) =>
        a[offset] == b[offset] && a[offset + 1] == b[offset + 1] && a[offset + 2] == b[offset + 2];

    // A picture where every pixel differs from its neighbours, so a covered or shifted pixel shows.
    private static FrozenFrame Gradient()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            pixels[offset] = (byte)(x * 7 + y);
            pixels[offset + 1] = (byte)(y * 5 + x);
            pixels[offset + 2] = (byte)(x ^ y);
            pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Width * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, Width, Height), null, 0);
    }
}
