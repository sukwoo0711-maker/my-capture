using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MyCapture.Tests;

/// <summary>Recycles only fixtures allocated by this test process. Never permanently deletes.</summary>
internal static class TestRecycleBin
{
    private static readonly ConcurrentDictionary<string, byte> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ShellLock = new();

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
        Recycle(canonical);
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
                throw new IOException("Refusing to recycle a nonempty directory without recursive consent.");
        }
        Recycle(canonical);
        VerifyRemoved(canonical, directory, target);
    }

    private static string ValidateOwnedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Test recycling requires an absolute path.");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!canonical.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Roots.Keys.Any(root => canonical.Equals(root, StringComparison.OrdinalIgnoreCase)
                || canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Refusing to recycle content outside a fixture created by this test process.");
        for (DirectoryInfo? parent = Directory.GetParent(canonical); parent is not null; parent = parent.Parent)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to recycle through a linked ancestor.");
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
                throw new IOException("Refusing to recycle a fixture containing links; recycle the link itself first.");
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
            throw new IOException("The fixture was not moved to the Recycle Bin.");
        if (target is null) return;
        string root = target.Keys.MinBy(value => value.Length)!;
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Snapshot(target[root] == "directory" ? new DirectoryInfo(root) : new FileInfo(root), actual);
        if (actual.Count != target.Count || target.Any(pair => !actual.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            throw new IOException("The fixture link target changed during recycling.");
    }

    private static void Recycle(string path)
    {
        // FOF_ALLOWUNDO / VisualBasic's SendToRecycleBin can fall back to permanent
        // deletion. Windows 8+ RECYCLEONDELETE explicitly requires the Recycle Bin.
        // NOERRORUI + EARLYFAILURE returns an error instead of asking to destroy data.
        const uint flags = 0x00080000 | 0x00100000 | 0x00000400 | 0x00000010 | 0x00000004;
        lock (ShellLock)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                IFileOperation? operation = null;
                IShellItem? item = null;
                try
                {
                    operation = (IFileOperation)new FileOperation();
                    operation.SetOperationFlags(flags);
                    Guid shellItemId = typeof(IShellItem).GUID;
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref shellItemId, out item));
                    operation.DeleteItem(item, 0);
                    operation.PerformOperations();
                    operation.GetAnyOperationsAborted(out bool aborted);
                    if (aborted) throw new IOException("Test recycling was aborted; no permanent deletion fallback is allowed.");
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error is IOException ? error
                        : new IOException("Could not recycle test content; preserving it without a permanent deletion fallback.", error));
                }
                finally
                {
                    if (item is not null) Marshal.FinalReleaseComObject(item);
                    if (operation is not null) Marshal.FinalReleaseComObject(operation);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindingContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    private class FileOperation { }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint changes);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, nint sink);
        void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
