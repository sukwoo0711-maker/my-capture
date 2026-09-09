using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ImageExportEncoderTests
{
    [Fact]
    public async Task Original_ReusesLosslessBytesAndPreservesPixels() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(61, 37, noisy: true);
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, 0);
        Assert.Same(original, result.Bytes);
        Assert.Equal(".png", result.Extension);
        Assert.Equal(0, result.ActualReductionPercent);
        Assert.True(result.TargetReached);
        Assert.Null(result.JpegQuality);
        return true;
    });

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(90)]
    public async Task OpaqueImage_ReducesMeasuredBytesWithoutResizing(int percent) => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(257, 193, noisy: true);
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, percent);
        Assert.Equal(".jpg", result.Extension);
        Assert.True(result.TargetReached);
        Assert.True(result.ActualReductionPercent >= percent);
        Assert.InRange(result.JpegQuality!.Value, 1, 100);
        using var stream = new MemoryStream(result.Bytes);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.IsType<JpegBitmapDecoder>(decoder);
        Assert.Equal(source.PixelWidth, decoder.Frames[0].PixelWidth);
        Assert.Equal(source.PixelHeight, decoder.Frames[0].PixelHeight);
        return true;
    });

    [Fact]
    public async Task TinyPng_WhenJpegWouldBeLarger_KeepsOriginalAndReportsUnmetTarget() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(2, 1, noisy: false);
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, 90);
        Assert.Same(original, result.Bytes);
        Assert.Equal(".png", result.Extension);
        Assert.False(result.TargetReached);
        Assert.Equal(0, result.ActualReductionPercent);
        return true;
    });

    [Fact]
    public async Task ActualTransparency_PreservesOriginalAlphaInsteadOfFlattening() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(17, 13, noisy: true, alpha: 128);
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, 50);
        Assert.True(result.PreservedTransparency);
        Assert.False(result.TargetReached);
        Assert.Same(original, result.Bytes);
        Assert.Equal(".png", result.Extension);
        return true;
    });

    [Fact]
    public async Task IndexedTransparency_IsAlsoPreserved() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Indexed8,
            new BitmapPalette([Colors.Transparent, Colors.Red]), new byte[] { 0, 1 }, 2);
        source.Freeze();
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, 50);
        Assert.True(result.PreservedTransparency);
        Assert.Same(original, result.Bytes);
        return true;
    });

    [Fact]
    public async Task CancelledPreview_DoesNotReturnAnExport() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(2, 1, noisy: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ImageExportEncoder.Encode(source,
            ImageCodec.EncodePng(source), 50, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageExportEncoder.Encode(source,
            ImageCodec.EncodePng(source), 91));
        return true;
    });

    private static BitmapSource CreateImage(int width, int height, bool noisy, byte alpha = 255)
    {
        byte[] pixels = new byte[width * height * 4];
        if (noisy) new Random(712).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = alpha;
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    [Theory]
    [InlineData(1800, 20)]
    [InlineData(20, 1800)]
    [InlineData(21, 13)]
    public async Task OutputPreview_BoundsLongEdgeAndDoesNotUpscale(int width, int height) => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = CreateImage(width, height, noisy: true);
        byte[] original = ImageCodec.EncodePng(source);
        ImageExportResult result = ImageExportEncoder.Encode(source, original, 0);
        BitmapSource preview = ImageReductionExportDialog.CreatePreview(result, width, height);
        Assert.True(preview.IsFrozen);
        Assert.Equal(Math.Min(640, Math.Max(width, height)), Math.Max(preview.PixelWidth, preview.PixelHeight));
        Assert.True(preview.PixelWidth <= width);
        Assert.True(preview.PixelHeight <= height);
        return true;
    });

    [Fact]
    public async Task Dialog_DefaultsToOriginalAndInvalidatesPreparedExportAfterTargetChanges() => await StaThreadTask.RunAsync(() =>
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var dialog = new ImageReductionExportDialog(CreateImage(17, 13, noisy: true), "capture.png");
        try
        {
            Assert.Equal(0, dialog.TargetReductionPercent);
            Assert.False(dialog.CanExport);
            Task pending = dialog.UpdatePreviewAsync();
            var frame = new DispatcherFrame();
            _ = pending.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            pending.GetAwaiter().GetResult();
            Assert.True(dialog.CanExport);
            dialog.TargetReductionPercent = 50;
            Assert.False(dialog.CanExport);
        }
        finally { dialog.Close(); }
        return true;
    });
}
