using System.IO;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.Platform.Shell;

public sealed record RecycledPathReceipt(string OriginalPath, string RecycledPath, string Identity,
    string ShellIdList, bool IsDirectory);

public interface IRecoverableTrash
{
    string GetIdentity(string path);
    RecycledPathReceipt RecycleDirectory(string path);
    void RestoreDirectory(RecycledPathReceipt receipt);
    RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity);
}

/// <summary>Windows Shell recycling with no permanent-delete fallback and identity-bound restoration.</summary>
public sealed class WindowsRecycleBin : IRecoverableTrash
{
    private static readonly object ShellGate = new();
    [ThreadStatic] private static bool _insideShellThread;
    private const uint RecycleFlags = 0x00080000 | 0x00100000 | 0x00000400 | 0x00004000 | 0x00000010 | 0x00000004;
    internal Action<string>? DiagnosticStageForTest { get; set; }

    public string GetIdentity(string path)
    {
        path = ValidatePath(path);
        using SafeFileHandle handle = CreateFileW(path, 0, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out FileInformation info))
            throw new IOException("Could not verify the filesystem identity.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recycle operations cannot follow filesystem links.");
        return $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    public RecycledPathReceipt RecycleDirectory(string path) => Recycle(path, true);
    public RecycledPathReceipt RecycleFile(string path) => Recycle(path, false);
    public void RestoreDirectory(RecycledPathReceipt receipt) => Restore(receipt);
    public void RestoreFile(RecycledPathReceipt receipt) => Restore(receipt);

    private RecycledPathReceipt Recycle(string path, bool directory)
    {
        path = ValidatePath(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path)) throw new IOException("The recycle target does not exist.");
        RejectLinks(path, directory);
        string identity = GetIdentity(path);
        return OnSta(() =>
        {
            IFileOperation? operation = null;
            IShellItem? item = null;
            try
            {
                operation = (IFileOperation)new FileOperation();
                operation.SetOperationFlags(RecycleFlags);
                DiagnosticStageForTest?.Invoke("recycle:queued");
                item = ItemFromPath(path);
                var sink = new ProgressSink();
                operation.DeleteItem(item, sink);
                operation.PerformOperations();
                DiagnosticStageForTest?.Invoke("recycle:performed");
                operation.GetAnyOperationsAborted(out bool aborted);
                if (aborted || sink.Result < 0 || sink.RecycledPath is null || sink.IdList is null)
                    throw new IOException("The Shell did not confirm a recoverable Recycle Bin move.");
                var receipt = new RecycledPathReceipt(path, sink.RecycledPath, identity, sink.IdList, directory);
                if (Exists(path) || !Exists(receipt.RecycledPath) || GetIdentity(receipt.RecycledPath) != identity)
                    throw new IOException("The Recycle Bin item did not match the original filesystem object.");
                return receipt;
            }
            finally
            {
                Release(item);
                Release(operation);
            }
        });
    }

    private void Restore(RecycledPathReceipt receipt)
    {
        string original = ValidatePath(receipt.OriginalPath);
        if (Exists(original)) throw new IOException("The original location is occupied; nothing was overwritten.");
        if (!Exists(receipt.RecycledPath) || GetIdentity(receipt.RecycledPath) != receipt.Identity)
            throw new IOException("The recorded Recycle Bin item is no longer available.");
        string parent = Path.GetDirectoryName(original)!;
        ValidatePath(parent);
        Directory.CreateDirectory(parent);
        OnSta(() =>
        {
            IShellItem? item = null;
            IShellFolder? desktop = null, folder = null;
            IContextMenu? context = null;
            nint menu = 0, root = 0, child = 0, childArray = 0;
            try
            {
                // The delete callback can return a filesystem PIDL, whose MoveItem moves
                // only $R content and leaves $I metadata behind. Resolve the exact item in
                // the Recycle Bin namespace and invoke its canonical restore operation.
                DiagnosticStageForTest?.Invoke("restore:namespace-lookup-start");
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktop));
                uint eaten = 0, attributes = 0;
                desktop.ParseDisplayName(0, 0, "::{645FF040-5081-101B-9F08-00AA002F954E}", ref eaten, out root, ref attributes);
                Guid folderId = typeof(IShellFolder).GUID;
                desktop.BindToObject(root, 0, ref folderId, out folder);
                folder.ParseDisplayName(0, 0, receipt.RecycledPath, ref eaten, out child, ref attributes);
                Guid itemId = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemWithParent(root, folder, child, ref itemId, out item));
                if (!string.Equals(GetPath(item), receipt.RecycledPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(ReadDeletedOriginalPath(receipt.RecycledPath), original, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The exact Shell Recycle Bin identity did not match.");
                DiagnosticStageForTest?.Invoke("restore:namespace-lookup-end");
                Guid contextId = typeof(IContextMenu).GUID;
                childArray = Marshal.AllocHGlobal(nint.Size);
                Marshal.WriteIntPtr(childArray, child);
                folder.GetUIObjectOf(0, 1, childArray, ref contextId, 0, out nint pointer);
                DiagnosticStageForTest?.Invoke("restore:context-bound");
                try { context = (IContextMenu)Marshal.GetObjectForIUnknown(pointer); }
                finally { Marshal.Release(pointer); }
                menu = CreatePopupMenu();
                Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7FFF, 1));
                DiagnosticStageForTest?.Invoke("restore:context-menu-ready");
                if (Exists(original)) throw new IOException("The original location became occupied; nothing was overwritten.");
                var command = new InvokeCommandInfo
                {
                    Size = Marshal.SizeOf<InvokeCommandInfo>(), Mask = 0x00000400 | 0x00000100,
                    Verb = "undelete", Show = 0,
                };
                DiagnosticStageForTest?.Invoke("restore:undelete-start");
                Marshal.ThrowExceptionForHR(context.InvokeCommand(ref command));
                DiagnosticStageForTest?.Invoke("restore:undelete-end");
                string recycledName = Path.GetFileName(receipt.RecycledPath);
                string metadata = Path.Combine(Path.GetDirectoryName(receipt.RecycledPath)!, "$I" + recycledName[2..]);
                if (!Exists(original) || GetIdentity(original) != receipt.Identity || Exists(receipt.RecycledPath)
                    || !recycledName.StartsWith("$R", StringComparison.Ordinal) || File.Exists(metadata))
                    throw new IOException("The Shell could not restore the exact recorded item.");
                return true;
            }
            finally
            {
                if (menu != 0) DestroyMenu(menu);
                Release(context); Release(item); Release(folder); Release(desktop);
                if (childArray != 0) Marshal.FreeHGlobal(childArray);
                if (child != 0) Marshal.FreeCoTaskMem(child);
                if (root != 0) Marshal.FreeCoTaskMem(root);
            }
        });
    }

    public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity)
        => FindRecycledPath(originalPath, identity, true);

    private RecycledPathReceipt? FindRecycledPath(string originalPath, string identity, bool directory, string? recycledPath = null)
    {
        originalPath = ValidatePath(originalPath);
        return OnSta(() =>
        {
            IShellFolder? desktop = null, folder = null;
            IEnumIdList? items = null;
            nint root = 0;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktop));
                uint eaten = 0, attributes = 0;
                desktop.ParseDisplayName(0, 0, "::{645FF040-5081-101B-9F08-00AA002F954E}", ref eaten, out root, ref attributes);
                Guid folderId = typeof(IShellFolder).GUID;
                desktop.BindToObject(root, 0, ref folderId, out folder);
                folder.EnumObjects(0, 0x20 | 0x40 | 0x80, out items);
                DiagnosticStageForTest?.Invoke("lookup:recyclebin-enumerator-ready");
                if (items is null) return null;
                var budget = Stopwatch.StartNew();
                while (items.Next(1, out nint child, out uint fetched) == 0 && fetched == 1)
                {
                    IShellItem? candidate = null;
                    try
                    {
                        if (budget.Elapsed > TimeSpan.FromSeconds(5))
                            throw new TimeoutException("Recycle Bin lookup exceeded its bounded recovery budget.");
                        Guid itemId = typeof(IShellItem).GUID;
                        Marshal.ThrowExceptionForHR(SHCreateItemWithParent(root, folder, child, ref itemId, out candidate));
                        string path = GetPath(candidate);
                        if (recycledPath is not null && !string.Equals(path, recycledPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals(ReadDeletedOriginalPath(path), originalPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if ((directory ? !Directory.Exists(path) : !File.Exists(path)) || GetIdentity(path) != identity) continue;
                        DiagnosticStageForTest?.Invoke("lookup:exact-child-identity-matched");
                        return new RecycledPathReceipt(originalPath, path, identity, GetIdList(candidate), directory);
                    }
                    catch (Exception ex) when (ex is IOException or COMException) { }
                    finally { Release(candidate); Marshal.FreeCoTaskMem(child); }
                }
                return null;
            }
            finally { Release(items); Release(folder); Release(desktop); if (root != 0) Marshal.FreeCoTaskMem(root); }
        });
    }

    // Read-only validation of Windows' v1/v2 Recycle Bin identity metadata. Never edit
    // or delete $I/$R files; only the namespace's restore operation may consume them.
    private static string ReadDeletedOriginalPath(string recycledPath)
    {
        string name = Path.GetFileName(recycledPath);
        if (!name.StartsWith("$R", StringComparison.Ordinal)) return "";
        string metadata = Path.Combine(Path.GetDirectoryName(recycledPath)!, "$I" + name[2..]);
        using var reader = new BinaryReader(File.OpenRead(metadata), System.Text.Encoding.Unicode);
        long version = reader.ReadInt64();
        _ = reader.ReadInt64(); // original byte count
        _ = reader.ReadInt64(); // deletion FILETIME
        int characters = version switch { 1 => 260, 2 => reader.ReadInt32(), _ => 0 };
        if (characters is < 1 or > 32768) throw new IOException("Unknown Recycle Bin metadata format.");
        return new string(reader.ReadChars(characters)).TrimEnd('\0');
    }

    private static string ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("A fully qualified filesystem path is required.");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path == Path.GetPathRoot(path)) throw new IOException("A volume root cannot be recycled.");
        for (DirectoryInfo? parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recycle operations cannot traverse linked ancestors.");
        return path;
    }

    private static void RejectLinks(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked content cannot be recycled.");
        if (!directory) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(path)) RejectLinks(child, Directory.Exists(child));
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static T OnSta<T>(Func<T> run)
    {
        if (_insideShellThread) return run();
        lock (ShellGate)
        {
            T result = default!;
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                _insideShellThread = true;
                try { result = run(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
                finally { _insideShellThread = false; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start(); thread.Join();
            failure?.Throw();
            return result;
        }
    }

    private static IShellItem ItemFromPath(string path)
    {
        Guid id = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref id, out IShellItem item));
        return item;
    }
    private static IShellItem ItemFromIdList(string encoded)
    {
        byte[] bytes = Convert.FromBase64String(encoded);
        nint list = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, list, bytes.Length);
            Guid id = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(list, ref id, out IShellItem item));
            return item;
        }
        finally { Marshal.FreeCoTaskMem(list); }
    }
    private static string GetIdList(object item)
    {
        Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out nint list));
        try
        {
            int size = checked((int)ILGetSize(list));
            var bytes = new byte[size]; Marshal.Copy(list, bytes, 0, size);
            return Convert.ToBase64String(bytes);
        }
        finally { Marshal.FreeCoTaskMem(list); }
    }
    private static string GetPath(IShellItem item)
    {
        item.GetDisplayName(0x80058000, out nint name);
        try { return Marshal.PtrToStringUni(name) ?? throw new IOException("Missing recycled filesystem path."); }
        finally { Marshal.FreeCoTaskMem(name); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ProgressSink(string? restoreTarget = null) : IFileOperationProgressSink
    {
        public int Result { get; private set; } = unchecked((int)0x80004005);
        public string? RecycledPath { get; private set; }
        public string? IdList { get; private set; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name)
            => restoreTarget is not null && (Exists(restoreTarget) || (flags & 4) != 0) ? unchecked((int)0x80070050) : 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created)
            { Result = result; return 0; }
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreDeleteItem(uint flags, IShellItem item) => 0;
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created)
        {
            Result = result;
            if (result >= 0 && created is not null)
            {
                try { RecycledPath = GetPath(created); IdList = GetIdList(created); }
                catch (Exception) { Result = unchecked((int)0x80004005); }
            }
            return 0;
        }
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromIDList(nint list, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] private static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object item, out nint list);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(nint list);
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder folder);
    [DllImport("shell32.dll")] private static extern int SHCreateItemWithParent(nint parentList, IShellFolder parent, nint child, ref Guid id, out IShellItem item);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct InvokeCommandInfo
    {
        public int Size; public uint Mask; public nint Owner;
        [MarshalAs(UnmanagedType.LPStr)] public string Verb;
        [MarshalAs(UnmanagedType.LPStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? Directory;
        public int Show; public uint Hotkey; public nint Icon;
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, nint reserved, nint name, uint count);
    }
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(nint owner, nint context, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out nint list, ref uint attributes);
        void EnumObjects(nint owner, uint flags, out IEnumIdList? items);
        void BindToObject(nint list, nint context, ref Guid id, out IShellFolder folder);
        void BindToStorage(nint list, nint context, ref Guid id, out nint storage);
        [PreserveSig] int CompareIds(nint parameter, nint first, nint second);
        void CreateViewObject(nint owner, ref Guid id, out nint view);
        void GetAttributesOf(uint count, nint lists, ref uint attributes);
        void GetUIObjectOf(nint owner, uint count, nint lists, ref Guid id, nint reserved, out nint value);
    }
    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIdList
    {
        [PreserveSig] int Next(uint count, out nint list, out uint fetched);
        [PreserveSig] int Skip(uint count);
        void Reset(); void Clone(out IEnumIdList copy);
    }
    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")] private class FileOperation { }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid id, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer(); [PreserveSig] int PauseTimer(); [PreserveSig] int ResumeTimer();
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(nint dialog); void SetProperties(nint changes); void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item); void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink); void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink); void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink); void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink sink); void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, nint sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
