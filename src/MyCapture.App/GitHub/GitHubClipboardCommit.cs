using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using MyCapture.App.Editing;
using MyCapture.App.Threading;

namespace MyCapture.App.GitHub;

internal static class GitHubClipboardCommit
{
    // Compare and write under the same clipboard lock, including after the STA queue wait.
    internal static Task<bool> CopyUrlAsync(string url, uint expectedVersion) =>
        ClipboardImageService.RunCopySerializedAsync(() => StaThreadTask.RunAsync(() =>
        {
            using var owner = new HwndSource(new HwndSourceParameters("MyCapture URL clipboard")
            { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            byte[] bytes = Encoding.Unicode.GetBytes(url + '\0');
            nint memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
            if (memory == 0) return false;
            try
            {
                nint pointer = GlobalLock(memory);
                if (pointer == 0) return false;
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(memory); }
                if (!OpenClipboard(owner.Handle)) return false;
                try
                {
                    if (GetVersion() != expectedVersion || !EmptyClipboard()) return false;
                    if (SetClipboardData(13, memory) == 0) return false; // CF_UNICODETEXT
                    memory = 0; // Ownership transferred to Windows.
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally { if (memory != 0) GlobalFree(memory); }
        }, "MyCapture attachment URL writer"));

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")] internal static extern uint GetVersion();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
