using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdatePathsTests
{
    [Theory]
    [InlineData("../outside.exe")]
    [InlineData(@"..\outside.exe")]
    [InlineData(@"C:\outside.exe")]
    [InlineData("setup.exe:payload")]
    [InlineData(".")]
    [InlineData("..")]
    public void ChildRejectsTraversalRootedAndAlternateStreamNames(string name)
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        try { Assert.Throws<IOException>(() => UpdatePaths.Child(root, name)); }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"C:relative")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\staging")]
    [InlineData(@"\\?\C:\staging")]
    public void RootRejectsAmbiguousOrNonLocalPaths(string root) =>
        Assert.Throws<IOException>(() => UpdatePaths.CanonicalRoot(root));

    [Fact]
    public void NumericVersionDeterminesInstallerName()
    {
        Assert.Equal("MyCapture-1.8.0-win-x64-setup.exe", UpdatePaths.InstallerName(new UpdateVersion(1, 8, 0)));
        Assert.Throws<IOException>(() => UpdatePaths.InstallerName(new UpdateVersion(1, 8, 0, "../../other")));
    }

    [Fact]
    public void CanonicalChildRemainsWithinItsParent()
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        try
        {
            string child = UpdatePaths.Child(root + Path.DirectorySeparatorChar, "update-session.json");
            Assert.Equal(root, Path.GetDirectoryName(child));
            Assert.Equal(Path.Combine(root, "update-session.json"), child);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void CleanupRejectsUnownedSiblingAndPreservesItsFile()
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        string sibling = OwnedTestDirectory.Create("mycapture-preserve-");
        try
        {
            string nested = Path.Combine(sibling, "unowned");
            Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "keep.txt");
            File.WriteAllText(file, "keep");
            Assert.Throws<IOException>(() => OwnedTestDirectory.Delete(nested));
            OwnedTestDirectory.Delete(root);
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { OwnedTestDirectory.Delete(sibling); }
    }

    [Fact]
    public void MissingDescendantBelowLinkIsRejectedBeforeCreation()
    {
        string root = OwnedTestDirectory.Create("mycapture-paths-");
        string target = OwnedTestDirectory.Create("mycapture-link-target-");
        string link = Path.Combine(root, "linked");
        try
        {
            CreateJunction(link, target);
            string descendant = Path.Combine(link, "must-not-be-created", "staging");
            Assert.Throws<IOException>(() => UpdatePaths.CanonicalRoot(descendant));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Throws<IOException>(() => OwnedTestDirectory.Delete(root));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(root);
            OwnedTestDirectory.Delete(target);
        }
    }

    // A mount-point reparse fixture exercises the guard without symlink privileges or a shell.
    internal static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
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
