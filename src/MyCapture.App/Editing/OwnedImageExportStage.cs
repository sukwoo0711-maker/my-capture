using System.IO;

namespace MyCapture.App.Editing;

/// <summary>A single export in a fresh directory owned by this process, never an arbitrary path.</summary>
internal sealed class OwnedImageExportStage : IDisposable
{
    private static readonly List<OwnedImageExportStage> Retained = [];
    private readonly DirectoryInfo _directory;
    private readonly FileInfo _file;
    private readonly DateTimeOffset _created;
    private bool _fileCreated;

    private OwnedImageExportStage(DirectoryInfo directory, FileInfo file, DateTimeOffset created)
    {
        _directory = directory;
        _file = file;
        _created = created;
    }

    internal string FilePath => _file.FullName;

    internal static OwnedImageExportStage Create(ImageExportResult result, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        // Only literals enter the filename. The result is not permitted to supply a path,
        // alternate stream, separator, extension suffix, or filesystem cleanup pattern.
        string extension = result.Extension switch
        {
            ".png" => ".png",
            ".jpg" => ".jpg",
            _ => throw new ArgumentException("Unsupported staged image extension.", nameof(result)),
        };
        CleanupRetained(DateTimeOffset.UtcNow);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("MyCapture-reduced-export-");
        var file = new FileInfo(Path.Combine(directory.FullName, $"MyCapture_{Guid.NewGuid():N}{extension}"));
        var stage = new OwnedImageExportStage(directory, file, createdAt ?? DateTimeOffset.UtcNow);
        try
        {
            stage.AssertOwnedPath();
            using var stream = new FileStream(file.FullName, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stage._fileCreated = true;
            stream.Write(result.Bytes);
            stream.Flush(true);
            return stage;
        }
        catch
        {
            stage.Dispose();
            throw;
        }
    }

    internal void RetainForShell()
    {
        lock (Retained) Retained.Add(this);
    }

    internal static void CleanupRetained(DateTimeOffset now)
    {
        List<OwnedImageExportStage> expired;
        lock (Retained)
        {
            expired = Retained.Where(stage => stage._created < now.AddDays(-2)).ToList();
            foreach (OwnedImageExportStage stage in expired) Retained.Remove(stage);
        }
        // Ownership comes from CreateTempSubdirectory in this process. We deliberately do
        // not enumerate another process's temporary directories after an application restart.
        foreach (OwnedImageExportStage stage in expired) stage.Dispose();
    }

    private void AssertOwnedPath()
    {
        string prefix = Path.TrimEndingDirectorySeparator(_directory.FullName) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(_file.FullName);
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(path), _directory.FullName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The export is outside its owned directory.");
        _directory.Refresh();
        if ((_directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The export directory cannot be a link.");
        _file.Refresh();
        if (_file.Exists && (_file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The export file cannot be a link.");
    }

    public void Dispose()
    {
        try
        {
            AssertOwnedPath();
            if (_fileCreated) _file.Delete();
            // Nonrecursive deletion refuses to remove any unexpected files in the directory.
            _directory.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
