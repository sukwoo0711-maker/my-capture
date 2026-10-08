using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace MyCapture.App.Gallery;

/// <summary>Recycles a regular export validated by GalleryExportFiles. No permanent fallback.</summary>
internal static class GalleryExportRecycleBin
{
    private static readonly object ShellLock = new();
    internal static void Recycle(string path)
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
                    if (aborted || File.Exists(path)) throw new IOException("Drag-export recycling was aborted; no permanent deletion fallback is allowed.");
                }
                catch (Exception error)
                {
                    failure = ExceptionDispatchInfo.Capture(error is IOException ? error
                        : new IOException("Could not recycle drag export; preserving it without a permanent deletion fallback.", error));
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
