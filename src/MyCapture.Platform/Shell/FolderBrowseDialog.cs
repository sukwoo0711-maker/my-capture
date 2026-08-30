using System.Runtime.InteropServices;
using System.Text;

namespace MyCapture.Platform.Shell;

/// <summary>
/// A dependency-free folder picker over the classic <c>SHBrowseForFolder</c> shell API.
/// </summary>
/// <remarks>
/// <para>
/// Chosen over the newer <c>IFileOpenDialog</c> COM route because the classic API is a
/// pair of flat P/Invokes with a stable, well-documented signature — no fragile COM
/// vtable ordering to get exactly right — and over <c>System.Windows.Forms</c> because
/// pulling a second UI framework into a WPF app for one dialog is disproportionate.
/// </para>
/// <para>
/// Returns the selected path, or <see langword="null"/> when the user cancels or the
/// shell call fails. Never throws into the caller.
/// </para>
/// </remarks>
public static class FolderBrowseDialog
{
    public static string? Browse(IntPtr owner, string title, string? initialPath)
    {
        var buffer = new StringBuilder(260);
        var info = new BROWSEINFO
        {
            hwndOwner = owner,
            pszDisplayName = buffer,
            lpszTitle = title ?? string.Empty,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
        };

        // Seed the initial selection through the callback when a valid path is supplied.
        GCHandle initialHandle = default;
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            initialHandle = GCHandle.Alloc(initialPath);
            info.lpfn = BrowseCallbackProc;
            info.lParam = GCHandle.ToIntPtr(initialHandle);
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            pidl = SHBrowseForFolder(ref info);
            if (pidl == IntPtr.Zero)
            {
                return null;
            }

            var pathBuffer = new StringBuilder(260);
            return SHGetPathFromIDList(pidl, pathBuffer) ? pathBuffer.ToString() : null;
        }
        catch (SEHException)
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            if (initialHandle.IsAllocated)
            {
                initialHandle.Free();
            }
        }
    }

    private static int BrowseCallbackProc(IntPtr hwnd, int msg, IntPtr lParam, IntPtr lpData)
    {
        // BFFM_INITIALIZED: point the dialog at the seeded initial path.
        if (msg == BFFM_INITIALIZED && lpData != IntPtr.Zero)
        {
            GCHandle handle = GCHandle.FromIntPtr(lpData);
            if (handle.Target is string path)
            {
                _ = SendMessage(hwnd, BFFM_SETSELECTIONW, new IntPtr(1), path);
            }
        }

        return 0;
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_NEWDIALOGSTYLE = 0x00000040;
    private const uint BIF_EDITBOX = 0x00000010;
    private const int BFFM_INITIALIZED = 1;
    private const uint BFFM_SETSELECTIONW = 0x0467;

    private delegate int BrowseCallback(IntPtr hwnd, int msg, IntPtr lParam, IntPtr lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public StringBuilder pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public BrowseCallback? lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
}
