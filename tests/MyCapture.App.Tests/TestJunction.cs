using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MyCapture.App.Tests;

/// <summary>Creates directory junctions for path-safety tests.</summary>
internal static class TestJunction
{
    // A mount-point reparse fixture exercises the guard without symlink privileges or a shell.
    internal static void Create(string link, string target)
    {
        target = MyCapture.Core.Storage.ValidatedFilePaths.DirectoryRoot(target);
        link = MyCapture.Core.Storage.ValidatedFilePaths.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT
            writer.Write(checked((ushort)(8 + substitute.Length + print.Length + 4)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)print.Length));
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write(print);
            writer.Write((ushort)0);
        }
        using SafeFileHandle handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] bytes = buffer.ToArray();
        if (!DeviceIoControl(handle, 0x000900A4, bytes, (uint)bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
