using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyCapture.App.Tests;

internal sealed class ThreadKeyboardModifierScope : IDisposable
{
    private readonly byte[] _originalState = new byte[256];

    internal ThreadKeyboardModifierScope()
    {
        if (!GetKeyboardState(_originalState)) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] state = (byte[])_originalState.Clone();
        foreach (int virtualKey in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            state[virtualKey] = 0;
        if (!SetKeyboardState(state)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (!SetKeyboardState(_originalState)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);
}
