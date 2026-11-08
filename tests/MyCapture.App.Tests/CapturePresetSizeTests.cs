using System.Windows.Media.Imaging;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePresetSizeTests
{
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.25)]
    [InlineData(true, 1.5)]
    public void PreviewAndCompletedCropKeepThePresetInPhysicalPixels(bool native, double scale) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = PatternedFrame();
        foreach ((System.Drawing.Size preset, PointD anchor, PointD end, RectD expected) in new[]
        {
            (new System.Drawing.Size(160, 90), new PointD(100, 100), new PointD(400, 260), new RectD(100, 100, 160, 90)),
            (new System.Drawing.Size(161, 91), new PointD(100, 100), new PointD(400, 260), new RectD(100, 100, 161, 91)),
            (new System.Drawing.Size(160, 90), new PointD(240, 180), new PointD(20, 40), new RectD(240, 180, 160, 90)),
            (new System.Drawing.Size(160, 90), new PointD(630, 350), new PointD(500, 250), new RectD(480, 270, 160, 90)),
            (new System.Drawing.Size(1280, 720), new PointD(200, 150), new PointD(500, 300), new RectD(0, 0, 640, 360)),
        })
        {
            PointD pointer = anchor;
            using var selector = new PresetSelector(native, frame, () => pointer, scale) { PresetSize = preset };
            CaptureSelectionCompletedEventArgs? completed = null;
            selector.SelectionCompleted += (_, e) => completed = e;
            selector.InitializePointer();
            selector.Press();
            pointer = end;
            selector.Move();
            Assert.Equal(expected, selector.Selection);
            selector.Render(scale);
            Assert.Equal(expected, selector.Selection);
            Assert.Null(completed);
            selector.Release();
            WindowDetectOverlayTests.PumpUntil(() => completed is not null);
            AssertOriginalCrop(frame, expected, completed!);
        }
    });

    [Theory]
    [InlineData(false, 0, 90)]
    [InlineData(false, 160, 0)]
    [InlineData(false, -1, 90)]
    [InlineData(true, 0, 90)]
    [InlineData(true, 160, 0)]
    [InlineData(true, -1, 90)]
    public void InvalidPresetFallsBackToTheOrdinaryDrag(bool native, int width, int height) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = PatternedFrame();
        PointD pointer = new(100, 100);
        using var selector = new PresetSelector(native, frame, () => pointer, 1.25)
        {
            PresetSize = new System.Drawing.Size(width, height),
        };
        CaptureSelectionCompletedEventArgs? completed = null;
        selector.SelectionCompleted += (_, e) => completed = e;
        selector.InitializePointer();
        selector.Press();
        pointer = new PointD(300, 200);
        selector.Move();
        var expected = new RectD(100, 100, 200, 100);
        Assert.Equal(expected, selector.Selection);
        selector.Release();
        WindowDetectOverlayTests.PumpUntil(() => completed is not null);
        AssertOriginalCrop(frame, expected, completed!);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredRatioIsIgnoredDuringPresetAndRestoredWhenPresetIsCleared(bool native) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = PatternedFrame();
        PointD pointer = new(100, 100);
        using var selector = new PresetSelector(native, frame, () => pointer, 1.25);
        selector.SelectRatio(SelectionRatio.Ratio1x1);
        selector.PresetSize = new System.Drawing.Size(160, 90);
        CaptureSelectionCompletedEventArgs? completed = null;
        selector.SelectionCompleted += (_, e) => completed = e;
        selector.InitializePointer();
        selector.Press();
        pointer = new PointD(300, 220);
        selector.Move();
        Assert.Equal(new RectD(100, 100, 160, 90), selector.Selection);
        Assert.Equal(SelectionRatio.Ratio1x1, selector.Ratio);

        selector.PresetSize = null;
        selector.Move();
        var expected = new RectD(100, 100, 200, 200);
        Assert.Equal(expected, selector.Selection);
        Assert.Equal(SelectionRatio.Ratio1x1, selector.Ratio);
        selector.Release();
        WindowDetectOverlayTests.PumpUntil(() => completed is not null);
        AssertOriginalCrop(frame, expected, completed!);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClickingADetectedWindowStillTakesTheWindowBeforeThePreset(bool native) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = PatternedFrame();
        PointD pointer = new(100, 100);
        var target = new WindowCandidate(new IntPtr(31), new RectD(50, 50, 200, 120), "Preset target");
        using var selector = new PresetSelector(native, frame, () => pointer, 1.25)
        {
            PresetSize = new System.Drawing.Size(160, 90),
        };
        selector.Detect([target]);
        CaptureSelectionCompletedEventArgs? completed = null;
        selector.SelectionCompleted += (_, e) => completed = e;
        selector.InitializePointer();
        selector.Press();
        selector.Release();
        WindowDetectOverlayTests.PumpUntil(() => completed is not null);
        AssertOriginalCrop(frame, target.ScreenBounds, completed!);
        Assert.Equal(target.Title, completed!.SourceTitle);
    });

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(false, 1, 50)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 1, 50)]
    public void PositivePresetSmallerThanTheCaptureMinimumPreviewsWithoutCompleting(bool native, int width, int height) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = PatternedFrame();
        PointD pointer = new(100, 100);
        using var selector = new PresetSelector(native, frame, () => pointer, 1.25)
        {
            PresetSize = new System.Drawing.Size(width, height),
        };
        int completed = 0;
        selector.SelectionCompleted += (_, _) => completed++;
        selector.InitializePointer();
        selector.Press();
        pointer = new PointD(300, 220);
        selector.Move();
        var expected = new RectD(100, 100, width, height);
        Assert.Equal(expected, selector.Selection);
        selector.Release();
        WindowDetectOverlayTests.Pump();
        Assert.Equal(0, completed);
        Assert.Equal(expected, selector.Selection);
    });

    private static void AssertOriginalCrop(FrozenFrame frame, RectD expected, CaptureSelectionCompletedEventArgs completed)
    {
        Assert.Same(frame, completed.Frame);
        Assert.Equal(expected, completed.BitmapRegion);
        Assert.Equal((int)expected.Width, completed.SelectedBitmap.PixelWidth);
        Assert.Equal((int)expected.Height, completed.SelectedBitmap.PixelHeight);
        Assert.Equal(Pixels(ScreenCaptureEngine.Crop(frame, expected)), Pixels(completed.SelectedBitmap));
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static FrozenFrame PatternedFrame()
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int y = 0; y < 360; y++)
            for (int x = 0; x < 640; x++)
            {
                int offset = (y * 640 + x) * 4;
                pixels[offset] = (byte)x;
                pixels[offset + 1] = (byte)y;
                pixels[offset + 2] = (byte)(x + y);
            }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 640, 360), null, 0);
    }
}
