using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureFreeformPreviewTests
{
    [Fact]
    public void WarmPreviewAppendAllocatesNoManagedMemory() => StaTestHost.Run(() =>
    {
        using var preview = Preview(256, 192);
        PointD first = new(20, 20), previous = first;
        // Each warm-up point crosses a real offscreen render/dispatcher boundary, rather
        // than warming only a single pending composition batch.
        for (int index = 0; index < 8; index++)
        {
            PointD next = new(70 + index, index % 2 == 0 ? 70 : 130);
            preview.Append(first, previous, next);
            previous = next;
            _ = Render(preview, 256, 192);
            WindowDetectOverlayTests.Pump();
        }
        long beforePixels = preview.PixelsWritten;
        long allocated = 0;
        for (int index = 0; index < 8; index++)
        {
            PointD next = new(100 + index, index % 2 == 0 ? 70 : 130);
            long before = GC.GetAllocatedBytesForCurrentThread();
            preview.Append(first, previous, next);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            previous = next;
            // Rendering and pumping are deliberately outside the measured Append interval.
            _ = Render(preview, 256, 192);
            WindowDetectOverlayTests.Pump();
        }
        Assert.Equal(0, allocated);
        Assert.True(preview.PixelsWritten > beforePixels);
        Assert.True(preview.LastChangedTileCount > 0);
    });

    [Fact]
    public void TinyMoveAfterWideStrokeDirtiesOnlyTheChangedTileColumn() => StaTestHost.Run(() =>
    {
        using var preview = Preview(1024, 768);
        PointD a = new(30, 30), b = new(930, 30), c = new(930, 690), d = new(30, 690);
        preview.Append(a, a, b);
        preview.Append(a, b, c);
        preview.Append(a, c, d);
        Assert.Equal(192, preview.TileCount);
        Visual[] visuals = preview.Visual.Children.Cast<Visual>().ToArray();
        long before = preview.PixelsWritten;

        // This closes a wide, tall polygon by one pixel along its left edge. Its accumulated
        // path bounds cover almost the desktop, but only the left tile column changes.
        preview.Append(a, d, new PointD(31, 690));

        Assert.InRange(preview.LastChangedTileCount, 1, 12);
        Assert.Equal(64, preview.LastChangedTileBounds.Width);
        Assert.Equal(0, preview.LastChangedTileBounds.Left);
        Assert.InRange(preview.PixelsWritten - before, 1, 700);
        for (int index = 0; index < preview.LastChangedTileCount; index++)
        {
            RectD changed = preview.ChangedTileBounds(index);
            Assert.Equal(0, changed.Left);
            Assert.True(changed.Width <= CaptureFreeformPreview.TileSize);
            Assert.True(changed.Height <= CaptureFreeformPreview.TileSize);
            Assert.False(changed.Contains(new PointD(500, 350)));
        }
        for (int index = 0; index < visuals.Length; index++)
        {
            Assert.Same(visuals[index], preview.Visual.Children[index]);
            DrawingVisual visual = Assert.IsType<DrawingVisual>(visuals[index]);
            Assert.True(visual.ContentBounds.Width <= CaptureFreeformPreview.TileSize);
            Assert.True(visual.ContentBounds.Height <= CaptureFreeformPreview.TileSize);
            Geometry clip = Assert.IsAssignableFrom<Geometry>(visual.Clip);
            Assert.True(clip.Bounds.Width <= CaptureFreeformPreview.TileSize);
        }
    });

    [Fact]
    public void PreviewUsesNonzeroFillAndRetainsTheOpenStrokeWhenWindingCancels() => StaTestHost.Run(() =>
    {
        using var preview = Preview(96, 96);
        PointD a = new(10, 10), b = new(70, 10), c = new(10, 70);
        preview.Append(a, a, b);
        preview.Append(a, b, c);
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(Render(preview, 96, 96), 25, 25));
        // A second pass has winding 2. Returning over one fan triangle leaves winding 1.
        preview.Append(a, b, c);
        preview.Append(a, c, b);
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(Render(preview, 96, 96), 25, 25));
        preview.Append(a, c, b);
        BitmapSource empty = Render(preview, 96, 96);
        Assert.Equal((byte)0, Pixel(empty, 25, 25)[3]);
        Assert.True(Pixel(empty, 30, 10)[3] > 0); // The pointer's drawn outline remains visible.
        Assert.Equal((byte)0, Pixel(empty, 80, 80)[3]);
    });

    [Fact]
    public void ExistingBorderAndInnerGlowPaintOnlyInsideGlowCoverage() => StaTestHost.Run(() =>
    {
        using var preview = new CaptureFreeformPreview(Source(96, 96), 1, 1,
            new Pen(Brushes.White, 1), new Pen(new SolidColorBrush(Color.FromArgb(64, 255, 0, 0)), 3));
        PointD a = new(10, 10), b = new(70, 10), c = new(10, 70);
        preview.Append(a, a, b);
        preview.Append(a, b, c);
        BitmapSource image = Render(preview, 96, 96);
        Assert.Equal((byte)0, Pixel(image, 30, 8)[3]); // No outer glow over the dimmer.
        byte[] glow = Pixel(image, 30, 11);
        Assert.Equal((byte)255, glow[3]);
        Assert.True(glow[2] > 160); // The actual supplied inner-glow colour.
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(image, 30, 20));
    });

    [Fact]
    public void ClearRemovesRevealAndStrokeWithoutReplacingTileVisuals() => StaTestHost.Run(() =>
    {
        using var preview = Preview(96, 96);
        Visual first = preview.Visual.Children[0];
        PointD a = new(10, 10), b = new(70, 10), c = new(10, 70);
        preview.Append(a, a, b);
        preview.Append(a, b, c);
        preview.Clear();
        Assert.Same(first, preview.Visual.Children[0]);
        BitmapSource image = Render(preview, 96, 96);
        byte[] pixels = new byte[96 * 96 * 4];
        image.CopyPixels(pixels, 96 * 4, 0);
        Assert.All(pixels, value => Assert.Equal((byte)0, value));
    });

    [Fact]
    public void PartialEdgeTilesShareStrideAndDisposeReleasesTheAnonymousSection() => StaTestHost.Run(() =>
    {
        using var preview = Preview(131, 99);
        // Both dimensions end in partial tiles. Rendering the last pixel tests the
        // shared section offset and the desktop stride, not only full square tiles.
        PointD a = new(0, 0), b = new(131, 0), c = new(131, 99), d = new(0, 99);
        preview.Append(a, a, b);
        preview.Append(a, b, c);
        preview.Append(a, c, d);
        BitmapSource image = Render(preview, 131, 99);
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(image, 127, 95));
        Assert.Equal((byte)255, Pixel(image, 130, 98)[3]);
        DrawingVisual retainedTile = Assert.IsType<DrawingVisual>(preview.Visual.Children[0]);
        BitmapSource retainedSource = Assert.IsAssignableFrom<BitmapSource>(
            Assert.IsType<ImageDrawing>(retainedTile.Drawing.Children[0]).ImageSource);
        var section = (MemoryMappedFile)typeof(CaptureFreeformPreview).GetField("_section", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        var view = (MemoryMappedViewAccessor)typeof(CaptureFreeformPreview).GetField("_sectionView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        var sectionHandle = section.SafeMemoryMappedFileHandle;
        var viewHandle = view.SafeMemoryMappedViewHandle;
        Assert.False(sectionHandle.IsClosed);
        Assert.False(viewHandle.IsClosed);
        preview.Dispose();
        Assert.True(preview.IsDisposed);
        Assert.True(sectionHandle.IsClosed);
        Assert.True(viewHandle.IsClosed);
        Assert.Empty(preview.Visual.Children.Cast<Visual>());
        // WIC/composition can still retain a tile after its owner is detached. The
        // bitmap's own mapping must outlive our writable view and section handle.
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(retainedSource, 20, 20));
        var retainedImage = new RenderTargetBitmap(131, 99, 96, 96, PixelFormats.Pbgra32);
        retainedImage.Render(retainedTile);
        Assert.Equal(new byte[] { 40, 80, 160, 255 }, Pixel(retainedImage, 20, 20));
        Assert.Throws<ObjectDisposedException>(() => preview.Append(a, d, a));
        Assert.Throws<ObjectDisposedException>(preview.Clear);
        preview.Dispose(); // Releasing resources twice must be harmless.
    });

    private static CaptureFreeformPreview Preview(int width, int height) => new(Source(width, height), 1, 1,
        new Pen(Brushes.White, 1), new Pen(new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)), 3));

    private static BitmapSource Source(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        { pixels[offset] = 40; pixels[offset + 1] = 80; pixels[offset + 2] = 160; pixels[offset + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Render(CaptureFreeformPreview preview, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(preview.Visual);
        return bitmap;
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
}
