using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace MyCapture.Tests;

/// <summary>
/// Deletes only fixtures allocated by this test process, after confining the path to an owned
/// temporary root and refusing links. (Historical name: tests never use the real Recycle Bin;
/// only the opt-in MYCAPTURE_RECYCLE_CANARY tests exercise it.)
/// </summary>
internal static class TestRecycleBin
{
    private static readonly ConcurrentDictionary<string, byte> Roots = new(StringComparer.OrdinalIgnoreCase);

    internal static DirectoryInfo CreateTempSubdirectory(string prefix)
        => TrackNewDirectory(() => Directory.CreateTempSubdirectory(prefix));

    internal static DirectoryInfo TrackNewDirectory(Func<DirectoryInfo> create)
    {
        DirectoryInfo directory = create();
        if (!string.Equals(directory.Parent?.FullName,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test fixture creation must return an immediate child of the temporary directory.");
        AssertNoLinks(directory);
        Roots.TryAdd(directory.FullName, 0);
        return directory;
    }

    internal static string CreateFilePath(string prefix, string extension) =>
        Path.Combine(CreateTempSubdirectory(prefix).FullName, "fixture" + extension);

    internal static void DeleteFile(string path)
    {
        string canonical = ValidateOwnedPath(path);
        if (!File.Exists(canonical)) return;
        var file = new FileInfo(canonical);
        Dictionary<string, string>? target = SnapshotLinkTarget(file);
        Remove(canonical, directory: false, link: target is not null, recursive: false);
        VerifyRemoved(canonical, file, target);
    }

    internal static void DeleteDirectory(string path, bool recursive = false)
    {
        string canonical = ValidateOwnedPath(path);
        if (!Directory.Exists(canonical)) return;
        var directory = new DirectoryInfo(canonical);
        Dictionary<string, string>? target = SnapshotLinkTarget(directory);
        if (target is null)
        {
            AssertNoLinks(directory);
            if (!recursive && directory.EnumerateFileSystemInfos().Any())
                throw new IOException("Refusing to delete a nonempty directory without recursive consent.");
        }
        Remove(canonical, directory: true, link: target is not null, recursive);
        VerifyRemoved(canonical, directory, target);
    }

    private static string ValidateOwnedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Test fixture deletion requires an absolute path.");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!canonical.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Roots.Keys.Any(root => canonical.Equals(root, StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Refusing to delete content outside a fixture created by this test process.");
        for (DirectoryInfo? parent = Directory.GetParent(canonical); parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete through a linked ancestor.");
            if (parent.FullName.Equals(temp, StringComparison.OrdinalIgnoreCase)) break;
        }
        return canonical;
    }

    private static void AssertNoLinks(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to traverse a linked fixture directory.");
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete a fixture containing links; delete the link itself first.");
            if (entry is DirectoryInfo child) AssertNoLinks(child);
        }
    }

    private static Dictionary<string, string>? SnapshotLinkTarget(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return null;
        FileSystemInfo target = entry.ResolveLinkTarget(returnFinalTarget: false)
            ?? throw new IOException("Cannot verify the fixture link target; preserving the link.");
        ValidateOwnedPath(target.FullName);
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Snapshot(target, snapshot);
        return snapshot;
    }

    private static void Snapshot(FileSystemInfo entry, Dictionary<string, string> result)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cannot safely verify the fixture link target.");
        if (entry is DirectoryInfo directory)
        {
            result[entry.FullName] = "directory";
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos()) Snapshot(child, result);
        }
        else
        {
            using FileStream stream = File.OpenRead(entry.FullName);
            result[entry.FullName] = Convert.ToHexString(SHA256.HashData(stream));
        }
    }

    private static void VerifyRemoved(string path, FileSystemInfo entry, Dictionary<string, string>? target)
    {
        entry.Refresh();
        if (entry.Exists || File.Exists(path) || Directory.Exists(path))
            throw new IOException("The fixture was not deleted.");
        if (target is null) return;
        string root = target.Keys.MinBy(value => value.Length)!;
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Snapshot(target[root] == "directory" ? new DirectoryInfo(root) : new FileInfo(root), actual);
        if (actual.Count != target.Count || target.Any(pair => !actual.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            throw new IOException("The fixture link target changed during deletion.");
    }

    private static void Remove(string path, bool directory, bool link, bool recursive)
    {
        // Fixtures are this process's own scratch files: they are deleted, never sent to the
        // user's Recycle Bin. A link is removed itself (non-recursively); its target is untouched.
        if (!directory) File.Delete(path);
        else Directory.Delete(path, recursive: recursive && !link);
    }
}
