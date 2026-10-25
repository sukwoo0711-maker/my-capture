using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace MyCapture.App.Tests;

/// <summary>Only deletes directories atomically created and registered by this test process.</summary>
internal static class OwnedTestDirectory
{
    private static readonly ConcurrentDictionary<string, DirectoryInfo> Owned = new(StringComparer.OrdinalIgnoreCase);

    internal static string Create(string prefix)
    {
        DirectoryInfo directory = TestRecycleBin.CreateTempSubdirectory(prefix);
        if (!Owned.TryAdd(directory.FullName, directory))
            throw new IOException("Test directory ownership collision.");
        return directory.FullName;
    }

    internal static void Delete(string path)
    {
        DirectoryInfo directory = GetOwnedDirectory(path);
        directory.Refresh();
        if (directory.Exists)
        {
            AssertNoLinks(directory);
            TestRecycleBin.DeleteDirectory(directory.FullName, recursive: true);
        }
        Owned.TryRemove(directory.FullName, out _);
    }

    /// <summary>Waits for asynchronous native media closure before the caller recycles its fixture.</summary>
    internal static void WaitForMediaFilesReleased(string path)
    {
        DirectoryInfo directory = GetOwnedDirectory(path);
        directory.Refresh();
        AssertNoLinks(directory);
        var elapsed = Stopwatch.StartNew();
        TimeSpan limit = TimeSpan.FromSeconds(5);
        foreach (FileInfo file in directory.EnumerateFiles("*.mp4", SearchOption.AllDirectories))
        {
            while (true)
            {
                try
                {
                    using FileStream probe = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
                    break;
                }
                catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
                {
                    TimeSpan remaining = limit - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        throw new TimeoutException($"Media fixture is still held after {limit.TotalSeconds} seconds: {file.FullName}", ex);
                    RangePatternClip.Pump(remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20));
                }
            }
        }
    }

    private static DirectoryInfo GetOwnedDirectory(string path)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Owned.TryGetValue(canonical, out DirectoryInfo? directory))
            throw new IOException("Refusing to delete a directory this test process did not create.");
        return directory;
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
