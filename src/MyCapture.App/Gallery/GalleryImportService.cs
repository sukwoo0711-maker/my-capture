using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
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
                // Only the unpublished managed copy reaches this callback; the source is untouched.
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log.LogWarning(ex, "Incomplete import remains in its managed directory because cleanup failed"); }
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
                VideoMediaInfo info = ProbeImportedVideo(sourcePath, cancellationToken);
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
                BitmapSource bitmap = decoder.Frames[0];
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 100_000_000)
                    throw new InvalidDataException("The selected image exceeds the decoded pixel budget.");
                if (jpeg) bitmap = OrientJpeg(decoder.Frames[0]);
                record.Width = bitmap.PixelWidth;
                record.Height = bitmap.PixelHeight;
                cancellationToken.ThrowIfCancellationRequested();
                if (jpeg)
                    ImageCodec.SavePng(bitmap, Path.Combine(directory, CaptureFileNames.Original));
                // Gallery exports consume rendered.png even before the first edit. Keep the
                // imported original intact and seed the rendered copy from the normalized PNG.
                File.Copy(Path.Combine(directory, CaptureFileNames.Original),
                    Path.Combine(directory, CaptureFileNames.Rendered));
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

    private static VideoMediaInfo ProbeImportedVideo(string sourcePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(sourcePath);
        double? trackDuration = Mp4VideoTrackDuration.ReadMilliseconds(stream, cancellationToken);
        // Fragmented files can omit a track duration. Retain the existing strict validation then.
        if (trackDuration is not double duration) return VideoFrameRenderPipeline.Probe(sourcePath, cancellationToken);
        using var reader = new MediaFoundationVideoFrameReader(sourcePath);
        _ = reader.ReadFrameAt(0, cancellationToken);
        var final = reader.ReadFrameAt(Math.Max(0, duration - 1), cancellationToken);
        VideoFrameRenderPipeline.ValidateDecodedTail(duration, final);
        return new VideoMediaInfo(reader.Width, reader.Height, duration);
    }

    private static BitmapSource OrientJpeg(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata
            || metadata.GetQuery("/app1/ifd/{ushort=274}") is not ushort orientation) return frame;
        // EXIF maps stored pixels to display pixels, including the two diagonal reflections.
        Matrix matrix = orientation switch
        {
            2 => new(-1, 0, 0, 1, 0, 0),
            3 => new(-1, 0, 0, -1, 0, 0),
            4 => new(1, 0, 0, -1, 0, 0),
            5 => new(0, 1, 1, 0, 0, 0),
            6 => new(0, 1, -1, 0, 0, 0),
            7 => new(0, -1, -1, 0, 0, 0),
            8 => new(0, -1, 1, 0, 0, 0),
            _ => Matrix.Identity,
        };
        return matrix.IsIdentity ? frame : new TransformedBitmap(frame, new MatrixTransform(matrix));
    }
}
