using System.Diagnostics;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Private commit does not count a page-file section. A native pin's settled display is one, so
/// the probe has to report sections beside private commit, and these tests hold it to that.
/// </summary>
public sealed class PinNativeMemoryAccountingTests
{
    private const long Mib = 1 << 20;

    [Fact]
    public void MapTotalsSplitEveryCommittedByteByRegionType()
    {
        ResidentProbePinMemory.Map map = ResidentProbePinMemory.Take();
        Assert.True(map.MappedBytes > 0);
        Assert.True(map.PrivateRegionBytes > 0);
        Assert.True(map.ImageBytes > 0);
        Assert.Equal(map.CommittedBytes, map.MappedBytes + map.PrivateRegionBytes + map.ImageBytes);
        Assert.True(map.MappedBytes >= map.Regions.Where(region => region.Kind == "mapped/section").Sum(region => region.Bytes));
    }

    [Fact]
    public void AWorkerSectionIsMappedMemoryThatPrivateCommitLeavesOut()
    {
        using Process process = Process.GetCurrentProcess();
        ResidentProbePinMemory.Map before = ResidentProbePinMemory.Take();
        long commitBefore = ResidentProbePinMemory.ProcessCommit(process.Handle);
        const int side = 4096; // 64 MiB of pixels: far above anything else this test allocates.
        ResidentProbePinMemory.Map during;
        long commitDuring;
        using (var buffer = new NativePinSettleBuffer(side, side))
        {
            Assert.Equal(64 * Mib, buffer.ByteLength);
            Assert.Equal(64 * Mib, NativePinSettleBuffer.LiveBytes);
            // Touch every page, as the worker's raster does, so nothing is left to commit later.
            for (int offset = 0; offset < buffer.ByteLength; offset += 4096)
                System.Runtime.InteropServices.Marshal.WriteByte(buffer.Pointer, offset, 0x5A);
            during = ResidentProbePinMemory.Take();
            commitDuring = ResidentProbePinMemory.ProcessCommit(process.Handle);
        }
        ResidentProbePinMemory.Map after = ResidentProbePinMemory.Take();
        Assert.Equal(0, NativePinSettleBuffer.LiveBytes);
        // The section itself is exact. The process total also moves by whatever other threads map
        // or unmap meanwhile (44 KB less than 64 MiB was seen once), hence the 16 MiB either way.
        static int Sections(ResidentProbePinMemory.Map map) =>
            map.Regions.Count(region => region.Kind == "mapped/section" && region.Bytes == 64 * Mib);
        Assert.Equal(Sections(before) + 1, Sections(during));
        Assert.Equal(Sections(before), Sections(after));
        Assert.InRange(during.MappedBytes - before.MappedBytes, 48 * Mib, 80 * Mib);
        Assert.True(commitDuring - commitBefore < 32 * Mib,
            $"Private commit grew by {(commitDuring - commitBefore) / (double)Mib:F1} MiB for a 64 MiB section.");
        Assert.InRange(after.MappedBytes - before.MappedBytes, -16 * Mib, 16 * Mib);
    }

    [Fact]
    public void DisplayCountersKeepPixelsWindowsAllocatedApartFromAnAdoptedSection()
    {
        long privateBefore = NativePinWindowDib.LivePrivateBytes, sectionBefore = NativePinWindowDib.LiveSectionBytes;
        using (var own = new NativePinWindowDib(256, 128))
        {
            Assert.Equal(0, own.SectionBytes);
            Assert.Equal(privateBefore + 256 * 128 * 4, NativePinWindowDib.LivePrivateBytes);
            Assert.Equal(sectionBefore, NativePinWindowDib.LiveSectionBytes);
        }
        Assert.Equal(privateBefore, NativePinWindowDib.LivePrivateBytes);
        var buffer = new NativePinSettleBuffer(256, 128);
        try
        {
            using var adopted = new NativePinWindowDib(buffer);
            Assert.Equal(256 * 128 * 4, adopted.SectionBytes);
            Assert.Equal(privateBefore, NativePinWindowDib.LivePrivateBytes);
            Assert.Equal(sectionBefore + 256 * 128 * 4, NativePinWindowDib.LiveSectionBytes);
            Assert.Equal(0, NativePinSettleBuffer.LiveBytes);
        }
        finally { buffer.Dispose(); }
        Assert.Equal(privateBefore, NativePinWindowDib.LivePrivateBytes);
        Assert.Equal(sectionBefore, NativePinWindowDib.LiveSectionBytes);
    }

    [Fact]
    public void APinReportsWhereItsDisplayAndItsWorkerBufferAreAccounted() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        BitmapSource picture = PinNativeTestImages.Picture(96, 64);
        using var pin = new NativePinWindow(picture, -20000, -20000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            pin.Show();
            // At its own size the display is a DIB that Windows allocated: private commit.
            Assert.Equal(96L * 64 * 4, pin.DisplayPrivateBytes);
            Assert.Equal(0, pin.DisplaySectionBytes);
            Assert.Equal(0, pin.WorkerSectionBytes);
            Assert.Equal(pin.DisplayPrivateBytes, NativePinWindow.LiveDisplayPrivateBytes);

            pin.BeforeSettleRasterForTest = token => { entered.Set(); release.Wait(token); };
            pin.SetZoom(1.5);
            PinNativeSpeedTestWait.Until(() => entered.IsSet);
            // While the worker renders: the live frame, still private, and one worker section.
            Assert.Equal(144L * 96 * 4, pin.DisplayPrivateBytes);
            Assert.Equal(0, pin.DisplaySectionBytes);
            Assert.Equal(144L * 96 * 4, pin.WorkerSectionBytes);
            Assert.Equal(pin.WorkerSectionBytes, NativePinWindow.LiveWorkerBufferBytes);

            release.Set();
            PinNativeSpeedTestWait.Complete(pin.WaitForSettledAsync());
            // Settled: the worker's section is the display, and nothing of the display is private.
            Assert.Equal(0, pin.DisplayPrivateBytes);
            Assert.Equal(144L * 96 * 4, pin.DisplaySectionBytes);
            Assert.Equal(0, pin.WorkerSectionBytes);
            Assert.Equal(0, NativePinWindow.LiveDisplayPrivateBytes);
            Assert.Equal(pin.DisplaySectionBytes, NativePinWindow.LiveDisplaySectionBytes);
            Assert.Equal(0, NativePinWindow.LiveWorkerBufferBytes);
        }
        finally
        {
            release.Set();
            pin.Close();
            PinNativeSpeedTestWait.Complete(pin.DrainAsync());
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
        Assert.Equal(0, NativePinWindow.LiveDisplayPrivateBytes);
        Assert.Equal(0, NativePinWindow.LiveDisplaySectionBytes);
    });
}
