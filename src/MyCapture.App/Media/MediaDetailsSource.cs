using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;
using MyCapture.Core.Recording;

namespace MyCapture.App.Media;

/// <summary>Cached facts only. Constructing this request does not touch the filesystem or decode media.</summary>
internal sealed record MediaDetailsSource(string? FilePath, int Width = 0, int Height = 0,
    double? DurationMs = null, int CanvasWidth = 0, int CanvasHeight = 0, string? FallbackFilePath = null,
    bool ReadImageDimensions = false);

internal sealed record MediaDetails(MediaDetailsSource Source, string FilePath, long? SizeBytes,
    DateTime? CreatedAt, DateTime? ModifiedAt, int Width, int Height, string Format, bool FileUnavailable)
{
    internal string Resolution => MediaDetailsFormatter.Resolution(Width, Height);
}

/// <summary>Called only on an explicit information action or a tooltip opening, never on card creation.</summary>
internal static class MediaDetailsReader
{
    internal static Task<IReadOnlyList<MediaDetails>> ReadAsync(IReadOnlyList<MediaDetailsSource> sources) =>
        Task.Run<IReadOnlyList<MediaDetails>>(() => sources.Select(Read).ToArray());

    internal static MediaDetails Read(MediaDetailsSource source)
    {
        string path = FullPath(source.FilePath);
        string fallback = FullPath(source.FallbackFilePath);
        bool usingFallback = fallback.Length > 0 && !File.Exists(path) && File.Exists(fallback);
        if (usingFallback) path = fallback;
        int width = source.ReadImageDimensions ? 0 : source.Width, height = source.ReadImageDimensions ? 0 : source.Height;
        string format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (format == "JPG") format = "JPEG";
        if (path.Length == 0) return new(source, path, null, null, null, width, height, format, false);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return new(source, path, null, null, null, width, height, format, true);
            // A library record's duration belongs to its render. If only the original remains, read
            // that file's video-track header instead of attaching the edited duration to it.
            if (usingFallback && format == "MP4" && source.DurationMs is not null)
            {
                double? duration = null;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    duration = Mp4VideoTrackDuration.ReadMilliseconds(stream);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException) { }
                source = source with { DurationMs = duration ?? double.NaN };
            }
            if ((width <= 0 || height <= 0) && format is "PNG" or "JPEG" or "GIF" or "BMP" or "TIFF" or "TIF")
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = frame.PixelWidth; height = frame.PixelHeight;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                    or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException) { }
            }
            return new(source, path, file.Length, file.CreationTime, file.LastWriteTime, width, height, format, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(source, path, null, null, null, width, height, format, true);
        }
    }

    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
    }
}
