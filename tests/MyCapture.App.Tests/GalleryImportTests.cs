using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryImportTests
{
    [Fact]
    public async Task ExternalDropAcceptsOnlySupportedFileBatchesAndNeverPrivatePayloads()
    {
        await StaThreadTask.RunAsync(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { @"C:\fixture\a.png", @"C:\fixture\b.MP4" });
            Assert.Equal(2, GalleryWindow.ExternalImportPaths(data).Length);
            data.SetData(GalleryFolderDragPayload.Format,
                new GalleryFolderDragPayload(Guid.NewGuid(), [Guid.NewGuid()], null), false);
            Assert.Empty(GalleryWindow.ExternalImportPaths(data));
            var unsupported = new DataObject();
            unsupported.SetData(DataFormats.FileDrop, new[] { @"C:\fixture\a.png", @"C:\fixture\b.exe" });
            Assert.Empty(GalleryWindow.ExternalImportPaths(unsupported));
            Assert.Empty(GalleryWindow.ExternalImportPaths(new DataObject()));
            return true;
        });
    }

    [Theory]
    [InlineData(CaptureMediaKind.Image)]
    [InlineData(CaptureMediaKind.Video)]
    public void ImportedSourceAppearsInDetailsWithoutAnExternalPath(CaptureMediaKind kind)
    {
        var record = new CaptureRecord { ImportedFileName = "example.mp4", MediaKind = kind };
        var tile = new GalleryItemViewModel(record, _ => string.Empty, 320);
        Assert.True(tile.HasSource);
        Assert.Equal(UiText.Get("Library.Import.Source"), tile.SourceAppName);
        Assert.Equal("example.mp4", tile.SourceWindowTitle);
        Assert.False(tile.HasSourceDocumentPath);
        Assert.Empty(tile.SourceAppPath);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpeg")]
    public async Task ImageImportProducesEditablePngAndThumbnailOffCallerThread(string extension)
    {
        string root = NewRoot();
        string source = Path.Combine(root, "image" + extension);
        await StaThreadTask.RunAsync(() =>
        {
            var pixels = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 360 * 4], 640 * 4);
            if (extension == ".png") ImageCodec.SavePng(pixels, source);
            else ImageCodec.SaveJpeg(pixels, source, 90);
            return true;
        });
        byte[] original = File.ReadAllBytes(source);
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        CaptureImportResult result = await service.ImportAsync([source]);
        Assert.Equal(1, result.Imported);
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.Equal((640, 360), (record.Width, record.Height));
        Assert.Equal(original, File.ReadAllBytes(queue.GetFilePath(record, CaptureImportService.OriginalFileName(record))));
        Assert.Equal(original, File.ReadAllBytes(source));
        await StaThreadTask.RunAsync(() =>
        {
            using var imageStream = File.OpenRead(queue.GetFilePath(record, CaptureFileNames.Original));
            var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.IsType<PngBitmapDecoder>(decoder);
            using var thumbStream = File.OpenRead(queue.GetFilePath(record, CaptureFileNames.Thumbnail));
            var thumbnail = BitmapDecoder.Create(thumbStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(320, thumbnail.Frames[0].PixelWidth);
            return true;
        });
    }

    [Fact]
    public async Task CorruptImageDoesNotPublishAndRequestsOnlyManagedCopyCleanup()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "broken.png");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var queue = new CaptureQueue(AppPaths.CreateForRoot(Path.Combine(root, "library")),
            new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        string? cleanup = null;
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync,
            directory => { cleanup = directory; return Task.CompletedTask; });
        CaptureImportResult result = await service.ImportAsync([source]);
        Assert.Equal(1, result.Failed);
        Assert.Empty(queue.Records);
        Assert.True(File.Exists(source));
        Assert.NotNull(cleanup);
        Assert.StartsWith(Path.Combine(root, "library"), cleanup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2, 80, 40, 1, 0, 3, 2)]
    [InlineData(3, 80, 40, 3, 2, 1, 0)]
    [InlineData(4, 80, 40, 2, 3, 0, 1)]
    [InlineData(5, 40, 80, 0, 2, 1, 3)]
    [InlineData(6, 40, 80, 2, 0, 3, 1)]
    [InlineData(7, 40, 80, 3, 1, 2, 0)]
    [InlineData(8, 40, 80, 1, 3, 0, 2)]
    public async Task JpegExifOrientationAppliesToWorkingImageAndThumbnail(int orientation, int width, int height,
        int topLeft, int topRight, int bottomLeft, int bottomRight)
    {
        string root = NewRoot();
        string source = Path.Combine(root, "oriented.jpg");
        Color[] colors = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow];
        await StaThreadTask.RunAsync(() =>
        {
            byte[] pixels = new byte[80 * 40 * 3];
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 80; x++)
                {
                    Color color = colors[(y < 20 ? 0 : 2) + (x < 40 ? 0 : 1)];
                    int offset = (y * 80 + x) * 3;
                    pixels[offset] = color.R;
                    pixels[offset + 1] = color.G;
                    pixels[offset + 2] = color.B;
                }
            var bitmap = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Rgb24, null, pixels, 80 * 3);
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using var output = File.Create(source);
            encoder.Save(output);
            return true;
        });
        byte[] original = File.ReadAllBytes(source);
        var queue = new CaptureQueue(AppPaths.CreateForRoot(Path.Combine(root, "library")),
            new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        Assert.Equal(1, (await service.ImportAsync([source])).Imported);
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.Equal((width, height), (record.Width, record.Height));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.ImportedJpeg)));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), record.OriginalContentHash);
        await StaThreadTask.RunAsync(() =>
        {
            foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Thumbnail })
            {
                using var input = File.OpenRead(queue.GetFilePath(record, name));
                var frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal((width, height), (frame.PixelWidth, frame.PixelHeight));
                var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
                int[] expected = [topLeft, topRight, bottomLeft, bottomRight];
                for (int i = 0; i < 4; i++)
                {
                    byte[] pixel = new byte[3];
                    rgb.CopyPixels(new Int32Rect(i % 2 == 0 ? width / 4 : 3 * width / 4,
                        i < 2 ? height / 4 : 3 * height / 4, 1, 1), pixel, 3, 0);
                    Color color = colors[expected[i]];
                    Assert.InRange(Math.Abs(pixel[0] - color.R), 0, 25);
                    Assert.InRange(Math.Abs(pixel[1] - color.G), 0, 25);
                    Assert.InRange(Math.Abs(pixel[2] - color.B), 0, 25);
                }
            }
            return true;
        });
    }

    [Fact]
    public async Task FourKVideoImportCopiesBytesProbesMediaAndCreatesThumbnail()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "clip.mp4");
        await Task.Run(() => FrameIndexClip.Encode(source, 3840, 2160, 10, FrameIndexClip.Constant(10, 5)));
        var paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        CaptureImportResult result = await service.ImportAsync([source, source]);
        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Duplicates);
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.True(record.IsVideo);
        Assert.Equal((3840, 2160), (record.Width, record.Height));
        Assert.InRange(record.DurationMs, 400, 700);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.VideoSource)));
        Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Thumbnail)));
        var reloaded = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        Assert.True(Assert.Single(reloaded.Records).IsVideo);
    }

    [Fact]
    public async Task ExternalMp4WithLongerAudioImportsAgainstVideoTrackDuration()
    {
        // External H.264/AAC fixture: 30 video frames (1 s), 1.1 s audio, encoded by FFmpeg.
        // ffmpeg -f lavfi -i color=c=red:s=64x48:r=30:d=1 -f lavfi
        // -i sine=frequency=440:sample_rate=48000:duration=1.1 -map 0:v -map 1:a
        // -c:v libx264 -pix_fmt yuv420p -c:a aac -movflags +faststart video-shorter-than-audio.mp4
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "video-shorter-than-audio.mp4");
        byte[] original = File.ReadAllBytes(source);
        await StaThreadTask.RunAsync(() =>
        {
            Assert.InRange(MediaFoundationVideoFrameReader.ReadContainerDurationMs(source), 1090, 1130);
            // Generated recordings retain their original stricter container-tail requirement.
            Assert.Throws<InvalidDataException>(() => VideoFrameRenderPipeline.Probe(source));
            return true;
        });
        var queue = new CaptureQueue(AppPaths.CreateForRoot(Path.Combine(NewRoot(), "library")),
            new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), await service.ImportAsync([source]));
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.Equal((64, 48), (record.Width, record.Height));
        Assert.InRange(record.DurationMs, 999, 1001);
        Assert.Equal(30, record.FrameRate);
        Assert.Equal(30, record.FrameCount);
        Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Thumbnail)));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.VideoSource)));
    }

    private static string NewRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
        string result = Path.Combine(root!.FullName, "artifacts", "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(result);
        return result;
    }
}
