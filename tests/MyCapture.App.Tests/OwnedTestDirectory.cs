using System.Collections.Concurrent;
using System.IO;

namespace MyCapture.App.Tests;

/// <summary>Only deletes directories atomically created and registered by this test process.</summary>
internal static class OwnedTestDirectory
{
    private static readonly ConcurrentDictionary<string, DirectoryInfo> Owned = new(StringComparer.OrdinalIgnoreCase);

    internal static string Create(string prefix)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory(prefix);
        if (!Owned.TryAdd(directory.FullName, directory))
            throw new IOException("Test directory ownership collision.");
        return directory.FullName;
    }

    internal static void Delete(string path)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Owned.TryGetValue(canonical, out DirectoryInfo? directory))
            throw new IOException("Refusing to delete a directory this test process did not create.");
        directory.Refresh();
        if (directory.Exists)
        {
            AssertNoLinks(directory);
            directory.Delete(recursive: true);
        }
        Owned.TryRemove(canonical, out _);
    }

    private static void AssertNoLinks(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to traverse a linked test directory.");
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete linked test content.");
            if (entry is DirectoryInfo child) AssertNoLinks(child);
        }
    }
}
