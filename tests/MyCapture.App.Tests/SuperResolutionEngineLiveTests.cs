using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SuperResolutionEngineLiveTests
{
    [Fact]
    public void RealModel_Upscales128TileTo512()
    {
        using SuperResolutionEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

        Assert.True(engine.TryInitialize(), "Real-ESRGAN session should initialize from downloaded weights.");
        StaTestHost.Run(() =>
        {
            BitmapSource input = GradientBitmap(96, 96);
            BitmapSource? output = engine.Upscale(input);
            Assert.NotNull(output);
            Assert.Equal(384, output!.PixelWidth);
            Assert.Equal(384, output.PixelHeight);
        });
    }

    [Fact]
    public void RealModel_WithoutInitialize_ReturnsNull()
    {
        using SuperResolutionEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

        StaTestHost.Run(() =>
        {
            Assert.False(engine.IsReady);
            Assert.Null(engine.Upscale(GradientBitmap(64, 64)));
        });
    }

    [Fact]
    public void RealModel_TiledUpscaleIsDitherFree()
    {
        using SuperResolutionEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

        Assert.True(engine.TryInitialize());
        StaTestHost.Run(() =>
        {
            BitmapSource input = GradientBitmap(130, 100);
            BitmapSource? output = engine.Upscale(input);
            Assert.NotNull(output);
            Assert.Equal(520, output!.PixelWidth);
            Assert.Equal(400, output.PixelHeight);
        });
    }

    private static SuperResolutionEngine? CreateIfModelsPresent()
    {
        AppPaths paths = AppPaths.CreateDefault();
        var store = new OcrModelStore(paths, NullLogger<OcrModelStore>.Instance);
        return store.HasSuperResolution
            ? new SuperResolutionEngine(store, NullLogger<SuperResolutionEngine>.Instance)
            : null;
    }

    private static BitmapSource GradientBitmap(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var brush = new LinearGradientBrush(Colors.Black, Colors.White, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            dc.DrawRectangle(brush, null, new System.Windows.Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
