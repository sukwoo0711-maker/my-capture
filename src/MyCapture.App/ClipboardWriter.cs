using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace MyCapture.App;

/// <summary>The single boundary for writes to the Windows clipboard.</summary>
internal static class ClipboardWriter
{
    private sealed record Override(Action<IDataObject, bool> SetDataObject, Action<string> SetText,
        Func<uint> GetVersion, Func<string, uint, bool> TrySetTextAtVersion);

    private static Override? _testOverride;

    internal static bool IsTestOverrideInstalled => Volatile.Read(ref _testOverride) is not null;

    // Installed by a test/tool module initializer before it can invoke product code.
    // There is deliberately no reset: another test must never restore the real clipboard.
    internal static void InstallTestOverride(Action<IDataObject, bool> setDataObject, Action<string> setText,
        Func<uint> getVersion, Func<string, uint, bool> trySetTextAtVersion)
    {
        ArgumentNullException.ThrowIfNull(setDataObject);
        ArgumentNullException.ThrowIfNull(setText);
        ArgumentNullException.ThrowIfNull(getVersion);
        ArgumentNullException.ThrowIfNull(trySetTextAtVersion);
        if (Interlocked.CompareExchange(ref _testOverride,
                new Override(setDataObject, setText, getVersion, trySetTextAtVersion), null) is not null)
            throw new InvalidOperationException("The clipboard test override is already installed for this process.");
    }

    internal static void SetDataObject(IDataObject data, bool copy)
    {
        Override? writer = Volatile.Read(ref _testOverride);
        if (writer is null) Clipboard.SetDataObject(data, copy);
        else writer.SetDataObject(data, copy);
    }

    internal static void SetText(string text)
    {
        Override? writer = Volatile.Read(ref _testOverride);
        if (writer is null) Clipboard.SetText(text);
        else writer.SetText(text);
    }

    internal static uint GetVersion() => Volatile.Read(ref _testOverride)?.GetVersion() ?? GetSystemVersion();

    // The URL writer compares and writes under one native lock. Keep that transaction
    // intact instead of substituting WPF SetText, which would reopen the clipboard.
    internal static bool TrySetTextAtVersion(string text, uint expectedVersion)
    {
        Override? writer = Volatile.Read(ref _testOverride);
        if (writer is not null) return writer.TrySetTextAtVersion(text, expectedVersion);

        using var owner = new HwndSource(new HwndSourceParameters("MyCapture URL clipboard")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
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
                if (GetSystemVersion() != expectedVersion || !EmptyClipboard()) return false;
                if (SetClipboardData(13, memory) == 0) return false; // CF_UNICODETEXT
                memory = 0; // Ownership transferred to Windows.
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")] private static extern uint GetSystemVersion();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
