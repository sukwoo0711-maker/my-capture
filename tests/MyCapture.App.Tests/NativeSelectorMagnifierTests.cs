using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorMagnifierTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CornerMasksAreRetainedTopOnlyGeometryWithoutImageClipping(double scale) => StaTestHost.Run(() =>
    {
        PointD pointer = new(320, 180);
        MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, (uint)Math.Round(96 * scale), true)];
        using var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(Frame());
        using var chrome = new NativeSelectorChrome(owner, true, false);
        var panel = Assert.IsType<Border>(chrome.Windows.Last().Content);
        var image = Assert.IsType<Image>(panel.Child);
        NativeSelectorMagnifier magnifier = Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest);
        DrawingGroup drawing = magnifier.DrawingForTest;
        GeometryDrawing mask = Assert.Single(drawing.Children.OfType<GeometryDrawing>(),
            item => item.Geometry is StreamGeometry);
        Geometry corners = mask.Geometry;
        Assert.True(corners.IsFrozen);
        Assert.Equal(6, NativeSelectorMagnifier.ImageRadius);
        double width = magnifier.ImageSizeForTest.Width;
        Assert.Equal(0, corners.Bounds.Left, 8);
        Assert.Equal(0, corners.Bounds.Top, 8);
        Assert.Equal(width, corners.Bounds.Width, 8);
        Assert.Equal(6, corners.Bounds.Height, 8);
        Assert.True(corners.FillContains(new Point(0.25, 0.25)));
        Assert.True(corners.FillContains(new Point(width - 0.25, 0.25)));
        Assert.False(corners.FillContains(new Point(3, 3)));
        Assert.False(corners.FillContains(new Point(width - 3, 3)));
        Assert.False(corners.FillContains(new Point(0.25, magnifier.ImageSizeForTest.Height - 0.25)));
        Assert.Null(image.Clip);
        Assert.Null(panel.Clip);
        Assert.Null(magnifier.Clip);
        Assert.Null(drawing.ClipGeometry);

        pointer = new PointD(330, 190);
        chrome.Update(pointer, Int32Rect.Empty);
        GeometryDrawing nextMask = Assert.Single(magnifier.DrawingForTest.Children.OfType<GeometryDrawing>(),
            item => item.Geometry is StreamGeometry);
        Assert.Same(corners, nextMask.Geometry);
    });

    [Fact]
    public void ShownMagnifierAttachesAndRendersItsActualAdornerThenDetachesIt() => StaTestHost.Run(() =>
    {
        MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, 96, true)];
        using var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => new PointD(320, 180), ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(Frame());
        using var chrome = new NativeSelectorChrome(owner, true, false);
        var image = Assert.IsType<Image>(Assert.IsType<Border>(chrome.Windows.Last().Content).Child);
        NativeSelectorMagnifier magnifier = Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest);
        Assert.Equal(0, magnifier.RenderCountForTest);
        chrome.Show();
        WindowDetectOverlayTests.PumpUntil(() => owner.IsChromeReady);
        AdornerLayer layer = Assert.IsType<AdornerLayer>(AdornerLayer.GetAdornerLayer(image));
        Assert.Contains(magnifier, layer.GetAdorners(image) ?? []);
        WindowDetectOverlayTests.PumpUntil(() => magnifier.RenderCountForTest > 0);
        Assert.True(magnifier.RenderCountForTest > 0);
        // DrawingForTest deliberately is not read: only the live WPF rendering path may render here.
        chrome.Detach();
        Assert.DoesNotContain(magnifier, layer.GetAdorners(image) ?? []);
    });

    [Fact]
    public void PointerBurstsDeferLabelLayoutAndCoalesceIntoOneMagnifierRender() => StaTestHost.Run(() =>
    {
        PointD pointer = new(320, 180);
        MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, 96, true)];
        using var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(Frame());
        using var chrome = new NativeSelectorChrome(owner, true, false);
        NativeSelectorMagnifier magnifier = Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest);
        Assert.NotNull(magnifier.DrawingForTest);
        int renders = magnifier.RenderCountForTest, layouts = magnifier.LabelLayoutCountForTest;
        for (int index = 1; index <= 30; index++)
        {
            pointer = new PointD(320 + index, 180 + index);
            chrome.Update(pointer, Int32Rect.Empty);
            Assert.Equal(renders, magnifier.RenderCountForTest);
            Assert.Equal(layouts, magnifier.LabelLayoutCountForTest);
        }
        Assert.NotNull(magnifier.DrawingForTest);
        Assert.Equal(renders + 1, magnifier.RenderCountForTest);
        Assert.InRange(magnifier.LabelLayoutCountForTest - layouts, 1, 2);
        renders = magnifier.RenderCountForTest;
        layouts = magnifier.LabelLayoutCountForTest;
        for (int index = 0; index < 20; index++) chrome.Update(pointer, Int32Rect.Empty);
        Assert.NotNull(magnifier.DrawingForTest);
        Assert.Equal(renders, magnifier.RenderCountForTest);
        Assert.Equal(layouts, magnifier.LabelLayoutCountForTest);
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void GridAndSelectedCellFollowThePhysicalSampleAtCenterAndEdges(double scale) => StaTestHost.Run(() =>
    {
        PointD pointer = new(320, 180);
        MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, (uint)Math.Round(96 * scale), true)];
        using var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(Frame());
        using var chrome = new NativeSelectorChrome(owner, true, false);
        NativeSelectorMagnifier magnifier = Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest);
        foreach (PointD sample in new PointD[] { new(320, 180), new(0, 0), new(639, 359) })
        {
            pointer = sample;
            chrome.Update(pointer, Int32Rect.Empty);
            Assert.NotNull(magnifier.DrawingForTest); // Flush the same coalesced drawing used by OnRender.
            Size image = magnifier.ImageSizeForTest;
            Assert.True(image.Width * scale >= 150 && image.Height * scale >= 150);
            double cellWidth = image.Width / 15, cellHeight = image.Height / 15;
            Assert.Equal(Math.Round(cellWidth * scale), cellWidth * scale, 8);
            Assert.Equal(Math.Round(cellHeight * scale), cellHeight * scale, 8);
            IReadOnlyList<LineGeometry> lines = magnifier.GridLinesForTest;
            Assert.Equal(28, lines.Count);
            for (int index = 1; index < 15; index++)
            {
                Assert.Equal(new Point(index * cellWidth, 0), lines[index - 1].StartPoint);
                Assert.Equal(new Point(index * cellWidth, image.Height), lines[index - 1].EndPoint);
                Assert.Equal(new Point(0, index * cellHeight), lines[index + 13].StartPoint);
                Assert.Equal(new Point(image.Width, index * cellHeight), lines[index + 13].EndPoint);
            }
            int cropX = Math.Clamp((int)pointer.X - 7, 0, 625);
            int cropY = Math.Clamp((int)pointer.Y - 7, 0, 345);
            var expectedCell = new Rect((pointer.X - cropX) * cellWidth,
                (pointer.Y - cropY) * cellHeight, cellWidth, cellHeight);
            Assert.Equal(expectedCell, magnifier.CellBoundsForTest);
            Rect cell = magnifier.CellBoundsForTest;
            double left = cell.Left * scale, top = cell.Top * scale;
            double right = cell.Right * scale, bottom = cell.Bottom * scale;
            // At 150%, 14*(100/15)+(100/15) is 100.00000000000001 DIPs. The grid's
            // contract is whole device pixels, so verify that contract before testing containment.
            foreach (double edge in new[] { left, top, right, bottom })
                Assert.Equal(Math.Round(edge), edge, 8);
            var physicalCell = new Rect(new Point(Math.Round(left), Math.Round(top)),
                new Point(Math.Round(right), Math.Round(bottom)));
            Assert.Equal(10, physicalCell.Width);
            Assert.Equal(10, physicalCell.Height);
            var physicalImage = new Rect(0, 0, Math.Round(image.Width * scale), Math.Round(image.Height * scale));
            Assert.True(physicalImage.Contains(physicalCell),
                $"Cell {cell} at scale {scale}: physical cell {physicalCell}, image {physicalImage}.");
            Assert.True(magnifier.GridFillCount > 0);
            Assert.Equal(TypeScale.Caption, NativeSelectorMagnifier.ColorFontSize);
            Assert.Equal(TypeScale.Micro, NativeSelectorMagnifier.CoordinateFontSize);
            Assert.False(magnifier.ColorBoundsForTest.IntersectsWith(magnifier.CoordinateBoundsForTest));
            var footer = new Rect(0, image.Height, image.Width, magnifier.FooterHeightForTest);
            Assert.True(footer.Contains(magnifier.ColorBoundsForTest));
            Assert.True(footer.Contains(magnifier.CoordinateBoundsForTest));
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedMagnifierCopiesTheExactSmallRegionForEachSourceFormat(bool use24Bit) => StaTestHost.Run(() =>
    {
        PointD pointer = new(320, 180);
        FrozenFrame frame = Frame(use24Bit);
        MonitorInfo[] monitors = [new("fixture", Bounds, Bounds, 96, true)];
        using var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        var image = Assert.IsType<Image>(Assert.IsType<Border>(chrome.Windows.Last().Content).Child);
        var bitmap = Assert.IsType<WriteableBitmap>(image.Source);
        var obtained = new byte[15 * 15 * 4];
        foreach (PointD sample in new PointD[] { new(320, 180), new(0, 0), new(639, 359) })
        {
            pointer = sample;
            chrome.Update(pointer, Int32Rect.Empty);
            _ = chrome.MagnifierForTest!.DrawingForTest;
            Assert.Same(bitmap, image.Source);
            bitmap.CopyPixels(obtained, 15 * 4, 0);
            int cropX = Math.Clamp((int)pointer.X - 7, 0, 625);
            int cropY = Math.Clamp((int)pointer.Y - 7, 0, 345);
            for (int row = 0; row < 15; row++)
            for (int column = 0; column < 15; column++)
            {
                int offset = (row * 15 + column) * 4;
                Assert.Equal((byte)((cropX + column) % 256), obtained[offset]);
                Assert.Equal((byte)((cropY + row) % 256), obtained[offset + 1]);
                Assert.Equal((byte)((cropX + column + cropY + row) % 256), obtained[offset + 2]);
            }
        }
    });

    [Theory]
    [InlineData(5, 7)]
    [InlineData(20, 5)]
    [InlineData(1, 1)]
    public void SmallFramesFillTheImageAndUseTheirActualSampleGrid(int width, int height) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Frame(width: width, height: height);
        RectD bounds = frame.ScreenBounds;
        PointD pointer = new(width - 1, height - 1);
        MonitorInfo[] monitors = [new("small-fixture", bounds, bounds, 96, true)];
        using var owner = new NativeCaptureOverlay(bounds, false, true)
        {
            ReadCursor = () => pointer, ReadMonitors = () => monitors, PlaceCursor = _ => false,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Update(pointer, Int32Rect.Empty);
        NativeSelectorMagnifier magnifier = Assert.IsType<NativeSelectorMagnifier>(chrome.MagnifierForTest);
        Assert.NotNull(magnifier.DrawingForTest);
        var image = Assert.IsType<Image>(Assert.IsType<Border>(chrome.Windows.Last().Content).Child);
        var bitmap = Assert.IsType<WriteableBitmap>(image.Source);
        int columns = Math.Min(15, width), rows = Math.Min(15, height);
        Assert.Equal(columns, bitmap.PixelWidth);
        Assert.Equal(rows, bitmap.PixelHeight);
        var pixels = new byte[columns * rows * 4];
        bitmap.CopyPixels(pixels, columns * 4, 0);
        int cropX = width - columns, cropY = height - rows;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int offset = (y * columns + x) * 4;
            Assert.Equal((byte)(cropX + x), pixels[offset]);
            Assert.Equal((byte)(cropY + y), pixels[offset + 1]);
            Assert.Equal((byte)(cropX + x + cropY + y), pixels[offset + 2]);
        }
        Size size = magnifier.ImageSizeForTest;
        double cellWidth = size.Width / columns, cellHeight = size.Height / rows;
        Assert.Equal(new Rect((columns - 1) * cellWidth, (rows - 1) * cellHeight, cellWidth, cellHeight),
            magnifier.CellBoundsForTest);
        Assert.Equal(columns - 1 + rows - 1,
            magnifier.GridLinesForTest.Count(line => line.StartPoint != line.EndPoint));
    });

    private static FrozenFrame Frame(bool use24Bit = false, int width = 640, int height = 360)
    {
        int bytesPerPixel = use24Bit ? 3 : 4;
        int stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * bytesPerPixel;
            pixels[offset] = (byte)(x % 256);
            pixels[offset + 1] = (byte)(y % 256);
            pixels[offset + 2] = (byte)((x + y) % 256);
            if (!use24Bit) pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96,
            use24Bit ? PixelFormats.Bgr24 : PixelFormats.Bgr32, null, pixels, stride);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 0);
    }
}
