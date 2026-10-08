using System.Runtime.InteropServices;

namespace MyCapture.Platform.Shell;

/// <summary>Temporary, message-pump-owned key route for a visible nonactivating surface.
/// The caller accepts only its own shortcuts while hovered, or its explicit activation chord.
/// No key text is retained. Unaccepted input always continues to the foreground application.</summary>
public sealed partial class TransientKeyboardListener : IDisposable
{
    private readonly Func<int, bool> _accept;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private readonly HashSet<int> _consumed = [];
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    public TransientKeyboardListener(Func<int, bool> accept)
    {
        _accept = accept;
        _callback = OnKey;
        _hook = SetWindowsHookEx(13, Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            bool down = message.ToInt64() is 0x100 or 0x104;
            bool up = message.ToInt64() is 0x101 or 0x105;
            if (up && _consumed.Remove(key)) return new IntPtr(1);
            if (down && _consumed.Contains(key)) return new IntPtr(1);
            if (down && _accept(key)) { _consumed.Add(key); return new IntPtr(1); }
        }
        return CallNextHookEx(_hook, code, message, data);
    }
    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { _ = UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _consumed.Clear(); GC.KeepAlive(_callback);
    }
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookEx(int id, IntPtr callback, IntPtr module, uint thread);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);
    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
