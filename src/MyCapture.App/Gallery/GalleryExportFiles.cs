using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Gallery;

/// <summary>Opens drag-export files without following links or allowing parent replacement.</summary>
internal static class GalleryExportFiles
{
    private const uint ListDirectory = 0x1, ReadAttributes = 0x80, DeleteAccess = 0x10000;
    private const uint OpenExisting = 3, CreateNew = 1;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;

    internal static string Child(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("Drag export names must be a single unambiguous path component.");
        return WithinRoot(parent, Path.Combine(parent, name));
    }

    internal static string WithinRoot(string root, string candidate)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(candidate);
        // Extended Windows paths retain dot segments through GetFullPath.
        if (full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment is "." or ".."))
            throw new IOException("Drag export paths cannot contain unresolved traversal components.");
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Drag export path escaped its owning directory.");
        return full;
    }

    internal static void Copy(string sourcePath, string destination, CancellationToken cancellationToken)
    {
        // Pin every existing directory without sharing writes or deletes. This blocks both
        // parent replacement and an in-place reparse change while the copy is in progress.
        using DirectoryLease sourceParents = DirectoryLease.Open(Path.GetDirectoryName(sourcePath)!, create: false);
        using DirectoryLease destinationParents = DirectoryLease.Open(Path.GetDirectoryName(destination)!, create: true);
        bool created = false;
        try
        {
            using SafeFileHandle sourceHandle = OpenRegularFile(sourcePath, 0x80000000, OpenExisting);
            using var source = new FileStream(sourceHandle, FileAccess.Read);
            using SafeFileHandle destinationHandle = OpenRegularFile(destination, 0x40000000, CreateNew);
            created = true;
            using var target = new FileStream(destinationHandle, FileAccess.Write);
            byte[] buffer = new byte[81920];
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            target.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) DeleteBestEffort(destination);
            throw;
        }
    }

    internal static void ValidateSource(string path)
    {
        using DirectoryLease parents = DirectoryLease.Open(Path.GetDirectoryName(path)!, create: false);
        using SafeFileHandle file = OpenRegularFile(path, ReadAttributes, OpenExisting);
    }

    internal static void DeleteBestEffort(string path, DateTimeOffset? cutoff = null)
    {
        try
        {
            using DirectoryLease parents = DirectoryLease.Open(Path.GetDirectoryName(path)!, create: false);
            using SafeFileHandle file = OpenRegularFile(path, ReadAttributes | DeleteAccess, OpenExisting);
            FileInformation info = Information(file);
            long timestamp = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
            if (cutoff is not null && DateTime.FromFileTimeUtc(timestamp) >= cutoff.Value.UtcDateTime) return;
            // Delete the validated file object itself, not a path that could name a new object.
            int delete = 1;
            if (!SetFileInformationByHandle(file, 4 /* FileDispositionInfo */, ref delete, sizeof(int)))
                throw NativeError("Could not remove a drag export.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Cleanup(string root, DateTimeOffset cutoff)
    {
        try
        {
            using DirectoryLease parents = DirectoryLease.Open(root, create: false);
            foreach (string file in Directory.EnumerateFiles(root, "MyCapture_*.*"))
            {
                string extension = Path.GetExtension(file);
                if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    DeleteBestEffort(WithinRoot(root, file), cutoff);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static SafeFileHandle OpenRegularFile(string path, uint access, uint disposition)
    {
        SafeFileHandle handle = CreateFileW(NativePath(path), access, 3 /* read/write, never delete */, IntPtr.Zero,
            disposition, OpenReparsePoint, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw NativeError("Could not open a drag-export file.");
            FileAttributes attributes = (FileAttributes)Information(handle).Attributes;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Drag exports cannot follow a file link.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation info))
            throw NativeError("Could not validate a drag-export handle.");
        return info;
    }

    private static IOException NativeError(string message) =>
        new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private static string NativePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    internal sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        internal static DirectoryLease Open(string directory, bool create)
        {
            var lease = new DirectoryLease();
            try
            {
                string full = Path.GetFullPath(directory);
                string current = Path.GetPathRoot(full) ?? throw new IOException("An export needs a rooted directory.");
                // Lock from the volume/share root down. A junction is rejected at its own
                // handle before attempting to create or open anything below it.
                lease.Pin(current);
                foreach (string segment in full[current.Length..].Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Child(current, segment);
                    if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                    lease.Pin(current);
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private void Pin(string path)
        {
            // An attributes-only handle does not enforce sharing restrictions. Requesting
            // directory read access makes the no-write/no-delete share contract effective.
            SafeFileHandle handle = CreateFileW(NativePath(path), ReadAttributes | ListDirectory, 1 /* share read only */, IntPtr.Zero, OpenExisting,
                OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid) throw NativeError("Could not open a drag-export directory.");
                FileAttributes attributes = (FileAttributes)Information(handle).Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Drag exports cannot follow a directory link.");
                _handles.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref int information, int size);
}
