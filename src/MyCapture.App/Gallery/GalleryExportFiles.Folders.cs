using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Gallery;

internal static partial class GalleryExportFiles
{
    internal const string FolderBatchPrefix = "MyCapture_FolderExport_";

    internal static string RelativeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new IOException("An export entry must be a portable relative path.");
        string current = root;
        foreach (string segment in relative.Split('/')) current = Child(current, segment);
        return WithinRoot(root, current);
    }

    internal static void EnsureDirectory(string path)
    {
        using DirectoryLease lease = DirectoryLease.Open(path, create: true);
    }

    internal static bool TryCreateNewDirectory(string path)
    {
        using DirectoryLease parents = DirectoryLease.Open(Path.GetDirectoryName(path)!, create: true);
        if (!CreateDirectoryW(NativePath(path), IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) return false;
            throw NativeError("Could not create an export directory.");
        }
        using DirectoryLease created = DirectoryLease.Open(path, create: false);
        return true;
    }

    internal static string CreateUniqueDirectory(string parent, string name)
    {
        for (int sequence = 1; sequence <= 9_999; sequence++)
        {
            string candidate = Child(parent, sequence == 1 ? name : $"{name}-{sequence:00}");
            if (TryCreateNewDirectory(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a new export directory.");
    }

    internal static void CopyToStream(string sourcePath, Stream output, CancellationToken cancellationToken)
    {
        using DirectoryLease parents = DirectoryLease.Open(Path.GetDirectoryName(sourcePath)!, create: false);
        using SafeFileHandle sourceHandle = OpenRegularFile(sourcePath, 0x80000000, OpenExisting);
        using var source = new FileStream(sourceHandle, FileAccess.Read);
        byte[] buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static void WriteStreamNew(string destination, Action<Stream> write, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate even the user-selected final component before entering native path syntax.
        string target = Child(Path.GetDirectoryName(Path.GetFullPath(destination))!, Path.GetFileName(destination));
        string parent = Path.GetDirectoryName(target)!;
        using DirectoryLease parents = DirectoryLease.Open(parent, create: false);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Choose a new archive filename.");
        string stage = Child(parent, $".mycapture-folder-{Guid.NewGuid():N}.zip.partial");
        using SafeFileHandle handle = OpenRegularFile(stage, 0x40000000 | DeleteAccess, CreateNew);
        using var output = new FileStream(handle, FileAccess.Write);
        bool published = false;
        try
        {
            write(output);
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the same validated file handle through atomic publication. Reopening a
            // path for File.Move conflicts with the pinned directory share contract.
            RenameNew(handle, Path.GetFileName(target));
            published = true;
        }
        finally
        {
            if (!published)
            {
                // Remove only our still-open staging object, even if another destination
                // appeared. Never release ownership and then delete by the final path.
                int delete = 1;
                _ = SetFileInformationByHandle(handle, 4, ref delete, sizeof(int));
            }
        }
    }

    private static void RenameNew(SafeFileHandle file, string targetName)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(targetName);
        // FILE_RENAME_INFORMATION: flags, pointer-aligned root, length, UTF-16 name.
        int lengthOffset = (IntPtr.Size == 8 ? 8 : 4) + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(int);
        byte[] information = new byte[nameOffset + name.Length + IntPtr.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(lengthOffset), name.Length);
        name.CopyTo(information, nameOffset);
        // A single name with RootDirectory=null renames within this file's existing
        // directory. Keep its parent pinned and retain ordinary no-replacement semantics.
        // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
        int status = NtSetInformationFile(file, out _, information, (uint)information.Length, 10);
        if (status != 0)
            throw new IOException("Could not publish a completed archive.",
                new System.ComponentModel.Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
    }

    internal static void DeleteFolderBatchBestEffort(string batchRoot, DateTimeOffset? cutoff = null)
    {
        // Only this product-owned temporary namespace is eligible for recursive cleanup.
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(batchRoot));
        if (!name.StartsWith(FolderBatchPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[FolderBatchPrefix.Length..], "N", out _)) return;
        DeleteTreeContentsBestEffort(batchRoot, cutoff);
    }

    private static void DeleteTreeContentsBestEffort(string root, DateTimeOffset? cutoff = null)
    {
        try
        {
            using (DirectoryLease current = DirectoryLease.Open(root, create: false))
            {
                if (cutoff is { } expiry && Directory.GetLastWriteTimeUtc(root) >= expiry.UtcDateTime) return;
                foreach (string file in Directory.EnumerateFiles(root))
                    DeleteBestEffort(WithinRoot(root, file));
                foreach (string directory in Directory.EnumerateDirectories(root))
                    DeleteTreeContentsBestEffort(WithinRoot(root, directory));
            }
            DeleteEmptyDirectoryBestEffort(root);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteEmptyDirectoryBestEffort(string path)
    {
        try
        {
            using DirectoryLease parents = DirectoryLease.Open(Path.GetDirectoryName(path)!, create: false);
            using SafeFileHandle directory = CreateFileW(NativePath(path), ReadAttributes | ListDirectory | DeleteAccess,
                1, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (directory.IsInvalid) return;
            FileAttributes attributes = (FileAttributes)Information(directory).Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) return;
            // Kernel deletion succeeds only for an empty regular directory. No path-based
            // recursive delete, link traversal, or fallback to a user's exported destination.
            int delete = 1;
            _ = SetFileInformationByHandle(directory, 4, ref delete, sizeof(int));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void CleanupFolderBatches(string root, DateTimeOffset cutoff)
    {
        try
        {
            using DirectoryLease parents = DirectoryLease.Open(root, create: false);
            foreach (string directory in Directory.EnumerateDirectories(root, FolderBatchPrefix + "*"))
                DeleteFolderBatchBestEffort(WithinRoot(root, directory), cutoff);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus,
        [In] byte[] information, uint size, int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
}
