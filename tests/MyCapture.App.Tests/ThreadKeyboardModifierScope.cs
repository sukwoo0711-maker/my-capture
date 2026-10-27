using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace MyCapture.App.Tests;

internal sealed class ThreadKeyboardModifierScope : IDisposable
{
    private readonly byte[] _originalState = new byte[256];

    internal ThreadKeyboardModifierScope(ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!GetKeyboardState(_originalState)) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] state = (byte[])_originalState.Clone();
        foreach (int virtualKey in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            state[virtualKey] = 0;
        if (modifiers.HasFlag(ModifierKeys.Shift)) state[0x10] = state[0xA0] = 0x80;
        if (modifiers.HasFlag(ModifierKeys.Control)) state[0x11] = state[0xA2] = 0x80;
        if (modifiers.HasFlag(ModifierKeys.Alt)) state[0x12] = state[0xA4] = 0x80;
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
