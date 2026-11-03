using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Tests;

/// <summary>Only deletes directories atomically created and registered by this test process.</summary>
internal static class OwnedTestDirectory
{
    // The Shell will not move a folder while something under it is open. It reports 0x80270021 when
    // the holder shares delete access (as a directory watcher does) and 0x80270028 when it does not.
    private const int ShellInUseSharingDelete = unchecked((int)0x80270021), ShellInUse = unchecked((int)0x80270028);
    private static readonly TimeSpan HeldFileLimit = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, DirectoryInfo> Owned = new(StringComparer.OrdinalIgnoreCase);

    internal static string Create(string prefix)
    {
        DirectoryInfo directory = TestRecycleBin.CreateTempSubdirectory(prefix);
        if (!Owned.TryAdd(directory.FullName, directory))
            throw new IOException("Test directory ownership collision.");
        return directory.FullName;
    }

    internal static void Delete(string path) => Delete(path, HeldFileLimit);

    /// <summary>
    /// Native media closure and deferred writes can keep a fixture file open for a moment after the
    /// test body ends, and the Shell then refuses the recycle. Wait for the holder the way
    /// <see cref="WaitForMediaFilesReleased"/> does, and name it if it is still open at the limit.
    /// </summary>
    internal static void Delete(string path, TimeSpan heldFileLimit)
    {
        DirectoryInfo directory = GetOwnedDirectory(path);
        directory.Refresh();
        if (directory.Exists)
        {
            AssertNoLinks(directory);
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    TestRecycleBin.DeleteDirectory(directory.FullName, recursive: true);
                    break;
                }
                catch (IOException ex) when (ex.InnerException is COMException { HResult: ShellInUseSharingDelete or ShellInUse })
                {
                    if (elapsed.Elapsed >= heldFileLimit)
                        throw new IOException(
                            $"{ex.Message} Still open after {heldFileLimit.TotalSeconds:0.#} seconds: {DescribeHolders(directory)}", ex);
                    Pump(TimeSpan.FromMilliseconds(50));
                }
            }
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
                        throw new TimeoutException(
                            $"Media fixture is still held after {limit.TotalSeconds} seconds: {file.FullName} ({DescribeHolders(directory)})", ex);
                    RangePatternClip.Pump(remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20));
                }
            }
        }
    }

    // An unloaded MediaElement closes its file from a dispatcher callback, so keep the owning
    // thread's dispatcher running while waiting; a thread without one just sleeps.
    private static void Pump(TimeSpan duration)
    {
        Dispatcher? dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null || dispatcher.HasShutdownStarted) Thread.Sleep(duration);
        else RangePatternClip.Pump(duration);
    }

    /// <summary>Lists the fixture entries that are still open, naming any other process that holds them.</summary>
    private static string DescribeHolders(DirectoryInfo directory)
    {
        var held = new List<string>();
        try
        {
            // List everything first: a live enumeration holds the directories it is reading open.
            foreach (FileSystemInfo entry in directory.GetFileSystemInfos("*", SearchOption.AllDirectories).Prepend(directory))
            {
                bool open = IsOpen(entry.FullName);
                string[] others = OtherProcessesUsing(entry.FullName);
                if (!open && others.Length == 0) continue;
                string holders = others.Length == 0 ? "this process" : string.Join(", ", others);
                held.Add($"{Path.GetRelativePath(directory.FullName, entry.FullName)} ({(open ? "open" : "attributes open")} by {holders})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            held.Add("listing failed: " + ex.Message);
        }
        return held.Count == 0 ? "no open handle found" : string.Join("; ", held);
    }

    private static bool IsOpen(string path)
    {
        const uint ReadData = 0x1, BackupSemantics = 0x02000000;
        using SafeFileHandle handle = CreateFileW(path, ReadData, FileShare.None, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        return handle.IsInvalid && Marshal.GetLastPInvokeError() is 32 or 33;
    }

    private static string[] OtherProcessesUsing(string path)
    {
        const uint ReadAttributes = 0x80, BackupSemantics = 0x02000000;
        const int ProcessIdsUsingFile = 47, InfoLengthMismatch = unchecked((int)0xC0000004);
        using SafeFileHandle handle = CreateFileW(path, ReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return [];
        for (int size = 1024; size <= 1 << 16; size *= 2)
        {
            byte[] buffer = new byte[size];
            int status = NtQueryInformationFile(handle, out _, buffer, size, ProcessIdsUsingFile);
            if (status == InfoLengthMismatch) continue;
            if (status < 0) return [];
            // FILE_PROCESS_IDS_USING_FILE_INFORMATION: a ULONG count, then pointer-sized ids.
            var names = new List<string>();
            for (int i = 0, count = BitConverter.ToInt32(buffer, 0); i < count; i++)
            {
                int offset = IntPtr.Size * (i + 1);
                int id = IntPtr.Size == 8 ? (int)BitConverter.ToInt64(buffer, offset) : BitConverter.ToInt32(buffer, offset);
                if (id == Environment.ProcessId) continue;
                try
                {
                    using var process = Process.GetProcessById(id);
                    names.Add($"{process.ProcessName} {id}");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    names.Add($"exited process {id}");
                }
            }
            return [.. names];
        }
        return [];
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, FileMode mode,
        uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, byte[] buffer, int length,
        int informationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }
}
