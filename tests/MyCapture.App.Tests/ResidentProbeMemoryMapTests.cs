using System.Globalization;
using System.Runtime.InteropServices;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentProbeMemoryMapTests
{
    private const uint HeapNoSerialize = 0x00000001;

    [Fact]
    public void DefaultTakeWalksOnlyTheProcessHeapAndLeavesPrivateHeapsAlone()
    {
        IntPtr serialized = HeapCreate(0, 0, 0);
        IntPtr unserialized = HeapCreate(HeapNoSerialize, 0, 0);
        Assert.NotEqual(IntPtr.Zero, serialized);
        Assert.NotEqual(IntPtr.Zero, unserialized);
        try
        {
            // Allocate so the private heaps are not empty; they must still be left alone.
            Assert.NotEqual(IntPtr.Zero, HeapAlloc(serialized, 0, 4096));
            Assert.NotEqual(IntPtr.Zero, HeapAlloc(unserialized, 0, 4096));

            ResidentProbeMemoryMap.Snapshot map = ResidentProbeMemoryMap.Take("private heaps", walkForeignHeaps: false);

            ResidentProbeMemoryMap.HeapRow processRow = Assert.Single(map.Heaps, row => row.Note.Contains("process heap", StringComparison.Ordinal));
            Assert.True(processRow.BusyMb + processRow.FreeMb > 0, "the process heap was walked");
            Assert.True(processRow.CommittedMb > 0);
            foreach (IntPtr handle in new[] { serialized, unserialized })
            {
                string text = "0x" + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
                ResidentProbeMemoryMap.HeapRow row = Assert.Single(map.Heaps, candidate => candidate.Handle == text);
                Assert.Equal(ResidentProbeMemoryMap.NotWalkedNote, row.Note);
                Assert.Equal(-1, row.CommittedMb);
                Assert.Equal(0, row.BusyMb);
            }
            Assert.All(map.Heaps.Where(row => !row.Note.Contains("process heap", StringComparison.Ordinal)),
                row => Assert.Equal(ResidentProbeMemoryMap.NotWalkedNote, row.Note));
        }
        finally
        {
            HeapDestroy(unserialized);
            HeapDestroy(serialized);
        }
    }

    [Theory]
    [InlineData(0u, 1, 0, 0)]
    [InlineData(5u, 13, 0, 5)]
    [InlineData(13u, 13, 0, 13)]
    [InlineData(14u, 13, 30, 13)]
    [InlineData(100u, 0, 116, 0)]
    public void HeapListCapacityIsNeverExceeded(uint returned, int capacity, int retryCapacity, int usable)
    {
        Assert.Equal(retryCapacity, ResidentProbeMemoryMap.HeapListRetryCapacity(returned, capacity));
        Assert.Equal(usable, ResidentProbeMemoryMap.UsableHeapCount(returned, capacity));
    }

    [Fact]
    public void AHeapListThatFitsAfterGrowingIsReadWhole()
    {
        int calls = 0;
        (IntPtr[] handles, int count) = ResidentProbeMemoryMap.ReadHeapHandles((capacity, buffer) =>
        {
            calls++;
            if (buffer is null) return 2; // The size query: two heaps, so the first buffer holds ten.
            const int actual = 25;        // The list grew before the handles were copied.
            for (int i = 0; i < Math.Min(buffer.Length, actual); i++) buffer[i] = new IntPtr(0x2000 + i);
            return actual;
        });

        Assert.Equal(3, calls);
        Assert.Equal(25, count);
        Assert.True(handles.Length >= 25);
        Assert.All(Enumerable.Range(0, count), i => Assert.Equal(new IntPtr(0x2000 + i), handles[i]));
    }

    [Fact]
    public void AHeapListThatKeepsOutgrowingTheBufferIsReadFromTheLastFilledBuffer()
    {
        // Like GetProcessHeaps, a call with a buffer that is too small fills what fits and reports more.
        int calls = 0;
        IntPtr[]? lastFilled = null;
        (IntPtr[] handles, int count) = ResidentProbeMemoryMap.ReadHeapHandles((capacity, buffer) =>
        {
            calls++;
            if (buffer is null) return 3;
            for (int i = 0; i < buffer.Length; i++) buffer[i] = new IntPtr(0x1000 + i);
            lastFilled = buffer;
            return capacity + 20;
        });

        Assert.Equal(1 + ResidentProbeMemoryMap.HeapListAttempts, calls);
        Assert.Same(lastFilled, handles);
        Assert.Equal(handles.Length, count);
        Assert.All(handles, handle => Assert.NotEqual(IntPtr.Zero, handle));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr HeapCreate(uint options, UIntPtr initialSize, UIntPtr maximumSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapDestroy(IntPtr heap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr HeapAlloc(IntPtr heap, uint flags, UIntPtr bytes);
}
