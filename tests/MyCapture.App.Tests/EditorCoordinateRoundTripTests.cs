using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorCoordinateRoundTripTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void IntegerPixelEdgesRoundTripExactlyAtNativeAndFittedZoom(double dpi) => StaTestHost.Run(() =>
    {
        foreach (Size viewport in Viewports)
        foreach (PointD cropOrigin in CropOrigins)
        {
            AnnotationEditorSurface surface = Surface(dpi, viewport, cropOrigin);
            Assert.InRange(surface.DipPerPixel, double.Epsilon, 1 / dpi);
            foreach (PointD pixel in new[] { new PointD(0, 0), new PointD(100, 80), new PointD(350, 240),
                new PointD(600, 300), new PointD(-31, -19), new PointD(680, 390) })
            {
                PointD actual = surface.ToImagePoint(surface.ToSurfacePoint(pixel));
                Assert.Equal(pixel, actual);
                Assert.Equal(new RectD(pixel.X, pixel.Y, 1, 1), new RectD(actual.X, actual.Y, 1, 1).ToPixelBounds());
            }
        }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FractionalEdgesOnEitherSideOfAPixelKeepTheirOutwardCoverage(double dpi) => StaTestHost.Run(() =>
    {
        foreach (Size viewport in Viewports)
        foreach (PointD cropOrigin in CropOrigins)
        {
            AnnotationEditorSurface surface = Surface(dpi, viewport, cropOrigin);
            foreach (int boundary in new[] { -80, 0, 100, 600 })
            foreach (double fraction in new[] { -0.25, -0.0001, -0.00000001, 0.00000001, 0.0001, 0.25 })
            {
                var point = new PointD(boundary + fraction, boundary + fraction);
                PointD actual = surface.ToImagePoint(surface.ToSurfacePoint(point));
                Assert.NotEqual((double)boundary, actual.X);
                Assert.NotEqual((double)boundary, actual.Y);
                Assert.Equal(Math.Sign(fraction), Math.Sign(actual.X - boundary));
                Assert.Equal(Math.Sign(fraction), Math.Sign(actual.Y - boundary));
                var end = new PointD(boundary + 3.5, boundary + 3.5);
                RectD covered = RectD.FromCorners(actual, surface.ToImagePoint(surface.ToSurfacePoint(end))).ToPixelBounds();
                int start = fraction < 0 ? boundary - 1 : boundary;
                Assert.Equal(new RectD(start, start, boundary + 4 - start, boundary + 4 - start), covered);
            }
        }
    });

    [Fact]
    public void DirectFractionalRectanglesStillRoundOutwardWithoutTransformCorrection()
    {
        Assert.Equal(new RectD(99, 79, 252, 162),
            RectD.FromCorners(new PointD(100 - 1e-8, 80 - 1e-8), new PointD(350 + 1e-8, 240 + 1e-8)).ToPixelBounds());
        Assert.Equal(new RectD(-101, -81, 102, 82),
            RectD.FromCorners(new PointD(-100 - 1e-8, -80 - 1e-8), new PointD(1e-8, 1e-8)).ToPixelBounds());
    }

    private static readonly Size[] Viewports = [new(1000, 800), new(380, 250), new(123, 87)];
    private static readonly PointD[] CropOrigins = [new(0, 0), new(17, 23), new(-31, -19)];

    private static AnnotationEditorSurface Surface(double dpi, Size viewport, PointD cropOrigin)
    {
        const int width = 640, height = 360;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32,
            null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        var crop = new RectD(cropOrigin.X, cropOrigin.Y, width, height);
        var controller = new AnnotationEditorController(AnnotationDocument.CreateFor(width, height), new UndoStack());
        var surface = new AnnotationEditorSurface(new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 0), crop,
            controller, new AnnotationRenderer(new AnnotationImageStore()));
        VisualTreeHelper.SetRootDpi(surface, new DpiScale(dpi, dpi));
        surface.Measure(viewport);
        surface.Arrange(new Rect(viewport));
        surface.UpdateLayout();
        Assert.Equal(dpi, VisualTreeHelper.GetDpi(surface).DpiScaleX);
        return surface;
    }
}
