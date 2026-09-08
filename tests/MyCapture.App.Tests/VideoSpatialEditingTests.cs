using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoSpatialEditingTests
{
    [Theory]
    [InlineData(320, 240, 240, 160, 96)]
    [InlineData(320, 240, 240, 160, 144)]
    [InlineData(320, 240, 240, 160, 192)]
    [InlineData(1920, 1080, 320, 200, 96)]
    [InlineData(1920, 1080, 320, 200, 144)]
    [InlineData(1920, 1080, 320, 200, 192)]
    public void ConstrainedParent_PreservesLetterboxCaptionAndSpatialImage(int width, int height, int viewportWidth, int viewportHeight, int dpi) => StaTestHost.Run(() =>
    {
        VideoEditDocument document = MakeDocument(width, height);
        var preview = new TimedTextPreviewView();
        preview.SetCanvas(width, height);
        preview.SetOverlays(document.TextOverlays);
        preview.SetFrameLayers(document.FrameEditLayers);
        preview.SetSourceTime(400);
        // A star row inside a finite parent reproduces the real window's layout contract.
        var parent = new Grid { Background = Brushes.Black };
        parent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        parent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        parent.Children.Add(new Border { Child = preview });
        parent.Measure(new Size(viewportWidth, viewportHeight + 40));
        parent.Arrange(new Rect(0, 0, viewportWidth, viewportHeight + 40));
        parent.UpdateLayout();
        Assert.Equal(viewportWidth, preview.ActualWidth);
        Assert.Equal(viewportHeight, preview.ActualHeight);
        var raster = new RenderTargetBitmap((int)(viewportWidth * dpi / 96.0), (int)((viewportHeight + 40) * dpi / 96.0), dpi, dpi, PixelFormats.Pbgra32);
        raster.Render(parent);
        byte[] pixels = Pixels(raster);
        int white = 0;
        int blue = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180) { white++; }
            if (pixels[i] > 140 && pixels[i + 2] < 80) { blue++; }
        }
        Assert.True(white > 8, $"Caption disappeared at {width}x{height}, {dpi} DPI");
        Assert.True(blue > 20, "Spatial image disappeared");
        double scale = Math.Min((double)viewportWidth / width, (double)viewportHeight / height);
        int left = (int)(((viewportWidth - width * scale) / 2 + width * 0.15 * scale) * dpi / 96);
        int top = (int)(((viewportHeight - height * scale) / 2 + height * 0.15 * scale) * dpi / 96);
        int sample = (top * raster.PixelWidth + left) * 4;
        Assert.True(pixels[sample] > 70 && pixels[sample + 2] < 80, "Image bounds did not map through letterboxing");
    });

    [Fact]
    public void PersistedGeometry_SharedOutputMatchesPreviewAndTimingIsHalfOpen() => StaTestHost.Run(() =>
    {
        VideoEditDocument document = MakeDocument(320, 240);
        document.TextOverlays[0].Bounds = new(0.2, 0.65, 0.6, 0.15);
        string json = JsonSerializer.Serialize(document);
        VideoEditDocument reopened = JsonSerializer.Deserialize<VideoEditDocument>(json)!.NormalizeFor(320, 240, 1000);
        Assert.True(VideoEditorWindow.DocumentsEquivalent(document, reopened));
        var preview = new TimedTextPreviewView();
        preview.SetCanvas(320, 240);
        preview.SetOverlays(reopened.TextOverlays);
        preview.SetFrameLayers(reopened.FrameEditLayers);
        var parent = new Grid { Background = Brushes.Black };
        parent.Children.Add(preview);
        parent.Measure(new Size(320, 240));
        parent.Arrange(new Rect(0, 0, 320, 240));
        foreach (double time in new double[] { 0, 399, 400, 799, 800 })
        {
            preview.SetSourceTime(time);
            parent.UpdateLayout();
            var bitmap = new RenderTargetBitmap(320, 240, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(parent);
            BitmapSource output = VideoFrameRenderPipeline.RenderFrame(Black(320, 240), 320, 240, 320, 240,
                reopened.TextOverlays, reopened.FrameEditLayers, FrameEditLayerRenderer.Decode(reopened.FrameEditLayers), time);
            byte[] expected = Pixels(output);
            byte[] actual = Pixels(bitmap);
            // WPF parent composition can round premultiplied alpha by one channel level.
            Assert.True(expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max() <= 1);
        }
    });

    [Fact]
    public void PreviewCache_DecodesOnDemandCachesFailureAndInvalidatesContentWithoutTimingChurn() => StaTestHost.Run(() =>
    {
        FrameEditLayer layer = MakeDocument(320, 240).FrameEditLayers[0];
        var invalid = new FrameEditLayer { OverlayPngBase64 = "invalid", StartMs = 100, EndMs = 800 };
        var preview = new TimedTextPreviewView { InactiveCacheBudgetBytes = 0 };
        preview.SetFrameLayers([layer, invalid]);
        Assert.Equal(0, preview.DecodeAttempts);
        preview.SetSourceTime(400);
        Assert.Equal(2, preview.DecodeAttempts);
        layer.StartMs = 300;
        preview.SetFrameLayers([layer, invalid]);
        preview.SetSourceTime(500);
        Assert.Equal(2, preview.DecodeAttempts);
        invalid.OverlayPngBase64 = layer.OverlayPngBase64;
        preview.SetFrameLayers([layer, invalid]);
        Assert.Equal(3, preview.DecodeAttempts);
        preview.SetSourceTime(900);
        Assert.Equal(0, preview.InactiveCachedBytes);
        preview.SetFrameLayers([layer, invalid]);
        Assert.Equal(3, preview.DecodeAttempts);
        preview.SetSourceTime(400);
        Assert.Equal(5, preview.DecodeAttempts);
    });

    [Fact]
    public void SpatialManipulation_ClampsMoveAndAllCornerResizesToCanvas()
    {
        Rect rect = new(32, 24, 64, 48);
        Assert.Equal(new Rect(256, 192, 64, 48), VideoLayerCanvas.Manipulate(rect, new Vector(1000, 1000), -1, 320, 240));
        foreach (int corner in new[] { 0, 1, 2, 3 })
        {
            Rect next = VideoLayerCanvas.Manipulate(rect, new Vector(1000, -1000), corner, 320, 240);
            Assert.True(new Rect(0, 0, 320, 240).Contains(next));
            Assert.True(next.Width >= 3.2 && next.Height >= 2.4 - 0.00001);
        }
    }

    [Fact]
    public void PngDecode_RejectsOversizedHeaderBeforeBitmapAllocation() => StaTestHost.Run(() =>
    {
        byte[] png = Convert.FromBase64String(MakeDocument(320, 240).FrameEditLayers[0].OverlayPngBase64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 50000);
        Assert.False(FrameEditLayerRenderer.HasSafePngDimensions(png));
        Assert.Empty(FrameEditLayerRenderer.Decode([new FrameEditLayer { OverlayPngBase64 = Convert.ToBase64String(png) }]));
    });

    private static VideoEditDocument MakeDocument(int width, int height)
    {
        var document = VideoEditDocument.CreateFor(width, height, 1000);
        document.TextOverlays.Add(new TimedTextOverlay { Text = "한글 Caption", StartMs = 400, EndMs = 800 });
        FrameEditLayer layer = VideoLayerAssets.CreateLayer(VideoLayerAssets.CreateShape(false), "shape", 400, 800, width, height);
        layer.Bounds = new(0.1, 0.1, 0.3, 0.3);
        document.FrameEditLayers.Add(layer);
        return document;
    }

    private static BitmapSource Black(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bytes, source.PixelWidth * 4, 0);
        return bytes;
    }
}

