using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Gallery;

/// <summary>The decoder boundary shared by Explorer drops and the import picker.</summary>
internal static class GalleryImportService
{
    internal static CaptureImportService Create(CaptureQueue queue, ILogger log) => new(queue, PrepareAsync,
        directory => Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(directory)) new WindowsRecycleBin().RecycleDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { log.LogWarning(ex, "Incomplete import remains in its managed directory because recycling failed"); }
        }));

    internal static async Task PrepareAsync(CaptureRecord record, string directory, CancellationToken cancellationToken)
    {
        await StaThreadTask.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = Path.Combine(directory, CaptureImportService.OriginalFileName(record));
            BitmapSource thumbnail;
            if (record.IsVideo)
            {
                VideoMediaInfo info = VideoFrameRenderPipeline.Probe(sourcePath, cancellationToken);
                record.Width = info.Width;
                record.Height = info.Height;
                record.DurationMs = info.DurationMs;
                using (var reader = new MediaFoundationVideoFrameReader(sourcePath))
                {
                    var first = reader.ReadFrameAt(0, cancellationToken);
                    // The editor uses an integer output cadence. For imported VFR media this
                    // is a nominal cadence and estimated count, not a scan of every frame.
                    record.FrameRate = first.SampleDurationMs > 0
                        ? Math.Clamp((int)Math.Round(1000 / first.SampleDurationMs), 1, 120) : 30;
                    record.FrameCount = Math.Max(1, (long)Math.Ceiling(info.DurationMs * record.FrameRate / 1000));
                }
                double scale = Math.Min(1, 320.0 / Math.Max(info.Width, info.Height));
                thumbnail = VideoFrameRenderPipeline.RenderSingleFrame(sourcePath, 0,
                    Math.Max(1, (int)(info.Width * scale)), Math.Max(1, (int)(info.Height * scale)),
                    cancellationToken: cancellationToken);
            }
            else
            {
                using var stream = File.OpenRead(sourcePath);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.None);
                bool jpeg = CaptureImportService.OriginalFileName(record) == CaptureFileNames.ImportedJpeg;
                if (jpeg ? decoder is not JpegBitmapDecoder : decoder is not PngBitmapDecoder)
                    throw new InvalidDataException("The selected image is not PNG or JPEG.");
                BitmapFrame bitmap = decoder.Frames[0];
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 100_000_000)
                    throw new InvalidDataException("The selected image exceeds the decoded pixel budget.");
                record.Width = bitmap.PixelWidth;
                record.Height = bitmap.PixelHeight;
                cancellationToken.ThrowIfCancellationRequested();
                if (jpeg)
                    ImageCodec.SavePng(bitmap, Path.Combine(directory, CaptureFileNames.Original));
                thumbnail = ImageCodec.CreateThumbnail(bitmap, 320);
                // Force the encoding while the decoder's input stream is still open.
                ImageCodec.SaveJpeg(thumbnail, Path.Combine(directory, CaptureFileNames.Thumbnail), ImageCodec.ThumbnailJpegQuality);
                return true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ImageCodec.SaveJpeg(thumbnail, Path.Combine(directory, CaptureFileNames.Thumbnail), ImageCodec.ThumbnailJpegQuality);
            return true;
        }, "MyCapture import decoder");
    }
}
