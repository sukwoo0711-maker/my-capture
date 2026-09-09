using System.IO;

namespace MyCapture.App.Recording;

/// <summary>Owns two fixed-name renders, never the source or a caller-provided cleanup root.</summary>
internal sealed class VideoExportCalculation : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("MyCapture-video-export-");
    private readonly string _extension;
    private string _selectedName = "standard";
    internal long BaselineBytes { get; private set; }
    internal long ResultBytes { get; private set; }
    internal int TargetReduction { get; private set; }
    internal double ActualReduction => BaselineBytes == 0 ? 0 : 100.0 * (1 - (double)ResultBytes / BaselineBytes);
    internal bool TargetReached => ResultBytes <= TargetBytes(BaselineBytes, TargetReduction);
    internal string Extension => _extension;
    internal string ResultPath => OwnedPath(_selectedName);

    private VideoExportCalculation(bool gif) => _extension = gif ? ".gif" : ".mp4";

    internal static long TargetBytes(long baselineBytes, int reduction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineBytes);
        if (reduction is < 0 or > 90) throw new ArgumentOutOfRangeException(nameof(reduction));
        return (long)(baselineBytes * ((100 - reduction) / 100m));
    }

    internal static int ReducedBitrate(long baselineBytes, double durationMs, int reduction, int standardBitrate)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        if (standardBitrate is < 64_000 or > 24_000_000) throw new ArgumentOutOfRangeException(nameof(standardBitrate));
        long target = TargetBytes(baselineBytes, reduction);
        // Reserve a little room for the container. This is a bitrate choice, never a size promise.
        double desired = target * 8d * 1000 / durationMs * 0.97;
        return (int)Math.Clamp(desired, 64_000, standardBitrate);
    }

    internal static VideoExportCalculation Calculate(
        int reduction, double durationMs, int standardBitrate,
        Action<string, int, bool, CancellationToken> render,
        CancellationToken cancellationToken)
    {
        _ = TargetBytes(1, reduction);
        if (!double.IsFinite(durationMs) || durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        if (standardBitrate is < 64_000 or > 24_000_000) throw new ArgumentOutOfRangeException(nameof(standardBitrate));
        var result = new VideoExportCalculation(false) { TargetReduction = reduction };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            render(result.OwnedPath("standard"), standardBitrate, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.BaselineBytes = result.ResultBytes = result.Length("standard");
            if (reduction > 0)
            {
                int bitrate = ReducedBitrate(result.BaselineBytes, durationMs, reduction, standardBitrate);
                render(result.OwnedPath("reduced"), bitrate, false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                long reducedBytes = result.Length("reduced");
                if (reducedBytes < result.ResultBytes)
                {
                    result._selectedName = "reduced";
                    result.ResultBytes = reducedBytes;
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static VideoExportCalculation CalculateGif(Action<string> render, CancellationToken cancellationToken)
    {
        var result = new VideoExportCalculation(true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            render(result.ResultPath);
            cancellationToken.ThrowIfCancellationRequested();
            result.BaselineBytes = result.ResultBytes = result.Length("standard");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private long Length(string name)
    {
        var file = new FileInfo(OwnedPath(name));
        if (!file.Exists || file.Length <= 0) throw new IOException("The video encoder produced no output.");
        return file.Length;
    }

    private string OwnedPath(string name)
    {
        if (name is not ("standard" or "reduced")) throw new ArgumentException("Unknown owned render.", nameof(name));
        _directory.Refresh();
        if (!_directory.Exists || (_directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The video export directory is unavailable or linked.");
        string path = Path.GetFullPath(Path.Combine(_directory.FullName, name + _extension));
        if (!string.Equals(Path.GetDirectoryName(path), _directory.FullName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The render escaped its owned directory.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The video export file cannot be a link.");
        return path;
    }

    internal void SaveCopy(string destination, string originalSource, CancellationToken cancellationToken)
    {
        string selected = Path.GetFullPath(destination);
        if (string.Equals(selected, Path.GetFullPath(originalSource), StringComparison.OrdinalIgnoreCase))
            throw new IOException(UiText.Get("MediaExport_SourceProtected"));
        if (!string.Equals(Path.GetExtension(selected), _extension, StringComparison.OrdinalIgnoreCase))
            throw new IOException(UiText.Format("MediaExport_Extension", _extension));
        string parent = Path.GetDirectoryName(selected) ?? throw new IOException("Missing export directory.");
        // Hold the immutable source against writes/deletion until the atomic commit is
        // finished. Windows applies this sharing contract to the file itself, so an
        // alternate path through a directory junction or hard link cannot replace it.
        // A lexical comparison alone cannot protect the source from those aliases.
        using var sourceProtection = new FileStream(originalSource, FileMode.Open, FileAccess.Read, FileShare.Read);
        // A unique sibling is staged beside the exact SaveFileDialog choice. Never delete
        // or truncate that destination on cancellation/failure, and never load a video byte[].
        string temporary = Path.Combine(parent, ".MyCapture-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var source = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[64 * 1024];
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, count);
                }
                target.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(selected)) File.Replace(temporary, selected, null, true);
            else File.Move(temporary, selected);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(OwnedPath("standard"));
            File.Delete(OwnedPath("reduced"));
            _directory.Delete(); // Never recursively remove unexpected files.
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
