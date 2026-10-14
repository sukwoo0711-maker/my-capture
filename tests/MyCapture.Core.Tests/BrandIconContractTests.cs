using System;
using System.IO;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class BrandIconContractTests
{
    [Fact]
    public void IdleTrayUsesTheFullApplicationTileAtEveryResolution()
    {
        Assert.Equal(ReadIcon("app"), ReadIcon("tray-idle"));
    }

    [Fact]
    public void TrayStatesHaveDifferentNative16PixelEntries()
    {
        string[] names = ["tray-idle", "tray-capturing", "tray-busy", "tray-error"];
        for (int i = 0; i < names.Length; i++)
        {
            for (int j = i + 1; j < names.Length; j++)
            {
                Assert.False(Native16(names[i]).SequenceEqual(Native16(names[j])),
                    $"{names[i]} and {names[j]} must differ at 16 px.");
            }
        }
    }

    [Fact]
    public void Native16HasTheDedicatedTwoPixelBracketsAndFullGraphiteTile()
    {
        foreach (string name in new[] { "app", "tray-idle", "tray-capturing", "tray-busy", "tray-error" })
        {
            byte[] dib = Native16(name);
            Assert.Equal(40, BitConverter.ToInt32(dib, 0));
            Assert.Equal(16, BitConverter.ToInt32(dib, 4));
            Assert.Equal(32, BitConverter.ToInt32(dib, 8));
            Assert.Equal((ushort)32, BitConverter.ToUInt16(dib, 14));

            // Interior integer-aligned samples distinguish the dedicated drawing from a
            // downscaled 256px master, whose top bracket begins at 3.5 rather than 3.
            Assert.Equal(0xFFFFF8F4u, Pixel(dib, 3, 3));
            Assert.Equal(0xFFFFF8F4u, Pixel(dib, 4, 6));
            Assert.Equal(0xFFFFF8F4u, Pixel(dib, 12, 3));
            Assert.Equal(0xFFFFF8F4u, Pixel(dib, 3, 12));
            // The enlarged L2 needle now reaches (7,7); sample the adjacent open centre.
            Assert.Equal(0xFF38424Du, Pixel(dib, 7, 6));
            Assert.Equal(0xFF38424Du, Pixel(dib, 1, 7));
            Assert.Equal(0u, Pixel(dib, 0, 0));
        }
    }

    private static uint Pixel(byte[] dib, int x, int y) =>
        BitConverter.ToUInt32(dib, 40 + ((15 - y) * 16 + x) * 4);

    private static byte[] Native16(string name)
    {
        byte[] bytes = ReadIcon(name);
        int count = BitConverter.ToUInt16(bytes, 4);
        int entry = Enumerable.Range(0, count).Select(index => 6 + index * 16)
            .Single(offset => bytes[offset] == 16 && bytes[offset + 1] == 16);
        int length = checked((int)BitConverter.ToUInt32(bytes, entry + 8));
        int start = checked((int)BitConverter.ToUInt32(bytes, entry + 12));
        return bytes.AsSpan(start, length).ToArray();
    }

    private static byte[] ReadIcon(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllBytes(Path.Combine(directory.FullName, "src", "MyCapture.App", "Assets", name + ".ico"));
    }
}
