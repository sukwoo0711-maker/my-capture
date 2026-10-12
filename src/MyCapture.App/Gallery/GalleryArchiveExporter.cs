using System.IO;
using System.IO.Compression;

namespace MyCapture.App.Gallery;

/// <summary>Publishes a new, media-only archive; never replaces a user's existing file.</summary>
internal static class GalleryArchiveExporter
{
    internal static async Task ExportNewAsync(IReadOnlyList<string> stagedMedia, string destination,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedMedia);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (stagedMedia.Count == 0) throw new ArgumentException("Select at least one capture.", nameof(stagedMedia));
        string[] sources = stagedMedia.ToArray();
        if (sources.Any(path => !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only rendered PNG and MP4 media may be shared.", nameof(stagedMedia));
        string target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Choose a new archive filename.");
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("A destination directory is required.");
        string stage = Path.Combine(parent, $".mycapture-{Guid.NewGuid():N}.zip.partial");
        bool ownsStage = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsStage = true;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (int index = 0; index < sources.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Generic filenames deliberately omit private window titles, OCR and tags.
                        string name = $"MyCapture-{index + 1:000}{Path.GetExtension(sources[index]).ToLowerInvariant()}";
                        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        await using Stream output = entry.Open();
                        await using var input = new FileStream(sources[index], FileMode.Open, FileAccess.Read,
                            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(output, cancellationToken);
                        progress?.Report(index + 1);
                    }
                }
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Atomic publication in the chosen directory, including no-overwrite race protection.
            File.Move(stage, target, overwrite: false);
            ownsStage = false;
        }
        finally
        {
            if (ownsStage)
            {
                try { File.Delete(stage); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}