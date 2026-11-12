using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Source application icons: one read per application, memory first, a pruned disk cache, a deadline.</summary>
public sealed class SourceIconCacheTests
{
    private static readonly SourceIconCache.FileStamp Stamp = new(638_000_000_000_000_000, 4096);

    [Fact]
    public void KeysNameTheApplicationAndItsVersion()
    {
        CaptureProvenance exe = CaptureProvenance.Create(appName: "Tool", appExecutablePath: @"C:\Apps\Tool.exe");
        CaptureProvenance sameExe = CaptureProvenance.Create(documentPath: @"C:\Work\a.txt", appExecutablePath: @"c:\apps\tool.EXE");
        CaptureProvenance store = CaptureProvenance.Create(appName: "Notepad",
            appExecutablePath: @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1.0.0.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe",
            appUserModelId: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        Assert.Equal(SourceIconCache.Identity(exe), SourceIconCache.Identity(sameExe));
        Assert.Equal("aumid|Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", SourceIconCache.Identity(store));
        Assert.Empty(SourceIconCache.Identity(CaptureProvenance.Create(appName: "Tool")));
        Assert.Empty(SourceIconCache.Identity(CaptureProvenance.Create(appExecutablePath: @"C:\Apps\run.cmd")));

        string key = SourceIconCache.DiskKey(@"C:\Apps\Tool.exe", "", Stamp)!;
        // An update changes the write time or size, and with it the key.
        Assert.NotEqual(key, SourceIconCache.DiskKey(@"C:\Apps\Tool.exe", "", Stamp with { Length = 4097 }));
        Assert.NotEqual(key, SourceIconCache.DiskKey(@"C:\Apps\Tool.exe", "", Stamp with { WriteTicksUtc = 1 }));
        Assert.Equal(key, SourceIconCache.DiskKey(@"c:\apps\tool.exe", "", Stamp));
        Assert.Null(SourceIconCache.DiskKey(@"C:\Apps\Tool.exe", "", null));
        // A packaged application's folder changes with every update; its ID does not.
        Assert.Equal("aumid|A_8wekyb3d8bbwe!App", SourceIconCache.DiskKey(@"C:\x.exe", "A_8wekyb3d8bbwe!App", null));
        string file = SourceIconCache.DiskFileName(key);
        Assert.Matches("^[0-9a-f]{32}\\.png$", file);
        Assert.DoesNotContain("Tool", file, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachApplicationIsReadOnceAndTheLeastRecentlyUsedLeavesMemory() => StaTestHost.Run(() =>
    {
        var reads = new List<string>();
        using var cache = new SourceIconCache(null, readExecutable: path => { lock (reads) reads.Add(path); return Icon(); },
            readPackagedApp: _ => null, stamp: _ => Stamp);
        CaptureProvenance first = App(0);
        int delivered = 0;
        cache.Request(first, _ => delivered++);
        cache.Request(first, _ => delivered++);
        PumpUntil(() => delivered == 2);
        Assert.Single(reads);
        Assert.True(cache.TryGet(first, out ImageSource? icon));
        Assert.True(icon!.IsFrozen);

        cache.Prewarm(Enumerable.Range(1, SourceIconCache.MemoryCapacity).Select(App).Prepend(first));
        PumpUntil(() => cache.TryGet(App(SourceIconCache.MemoryCapacity), out _));
        Assert.Equal(SourceIconCache.MemoryCapacity + 1, reads.Count);
        Assert.Equal(SourceIconCache.MemoryCapacity, cache.MemoryCount);
        // The first was used before all the others, so it is the one that left.
        Assert.False(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(App(1), out _));
    });

    [Fact]
    public void RequestsGoAheadOfPrewarming() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var cache = new SourceIconCache(null, readExecutable: path =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            lock (order) order.Add(Path.GetFileName(path));
            return Icon();
        }, readPackagedApp: _ => null, stamp: _ => Stamp);
        cache.Prewarm([App(1), App(2), App(3)]);
        bool urgent = false;
        cache.Request(App(9), _ => urgent = true);
        gate.Set();
        PumpUntil(() => urgent && cache.MemoryCount == 4);
        // The first prewarm may already be running; the request comes before the rest.
        Assert.True(order.IndexOf("app9.exe") <= 1);
    });

    [Fact]
    public void IconsSurviveARestartThroughTheDiskCache() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-srcicons");
        try
        {
            CaptureProvenance source = App(1);
            using (var first = new SourceIconCache(root, readExecutable: _ => Icon(Colors.SeaGreen), readPackagedApp: _ => null, stamp: _ => Stamp))
            {
                bool done = false;
                first.Request(source, _ => done = true);
                PumpUntil(() => done);
            }
            string file = Assert.Single(Directory.GetFiles(root));
            Assert.Equal(SourceIconCache.DiskFileName(SourceIconCache.DiskKey(source.AppExecutablePath, "", Stamp)!), Path.GetFileName(file));

            using var second = new SourceIconCache(root, readExecutable: _ => throw new InvalidOperationException("read the shell"),
                readPackagedApp: _ => null, stamp: _ => Stamp);
            ImageSource? loaded = null;
            second.Request(source, icon => loaded = icon);
            PumpUntil(() => loaded is not null);
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(loaded);
            Assert.True(bitmap.IsFrozen);
            Assert.Equal(32, bitmap.PixelWidth);
            Assert.Equal(Colors.SeaGreen, CenterPixel(bitmap));

            // A newer executable has a new key: the old file is not used for it.
            int shellReads = 0;
            using var updated = new SourceIconCache(root, readExecutable: _ => { Interlocked.Increment(ref shellReads); return Icon(); },
                readPackagedApp: _ => null, stamp: _ => Stamp with { Length = 1 });
            bool refreshed = false;
            updated.Request(source, _ => refreshed = true);
            PumpUntil(() => refreshed);
            Assert.Equal(1, shellReads);
            Assert.Equal(2, Directory.GetFiles(root).Length);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void PruningKeepsTheNewestWithinTheCountAndSize()
    {
        string root = OwnedTestDirectory.Create("mc-srcicons");
        try
        {
            DateTime now = DateTime.UtcNow;
            for (int index = 0; index < 6; index++)
            {
                string file = Path.Combine(root, $"{index:x32}.png");
                File.WriteAllBytes(file, new byte[1000]);
                File.SetLastWriteTimeUtc(file, now.AddMinutes(index));
            }
            File.WriteAllText(Path.Combine(root, "keep.txt"), "not an icon");
            SourceIconCache.Prune(root, fileLimit: 4, byteLimit: 1_000_000);
            Assert.Equal(new[] { 2, 3, 4, 5 }.Select(i => $"{i:x32}.png"),
                Directory.GetFiles(root, "*.png").Select(Path.GetFileName).Order());
            SourceIconCache.Prune(root, fileLimit: 100, byteLimit: 2_500);
            Assert.Equal(new[] { 4, 5 }.Select(i => $"{i:x32}.png"),
                Directory.GetFiles(root, "*.png").Select(Path.GetFileName).Order());
            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void AnIconMadeOnTheIconThreadIsUsableOnTheUiThread() => StaTestHost.Run(() =>
    {
        // The reader returns an unfrozen bitmap; the cache freezes it before it crosses threads.
        using var cache = new SourceIconCache(null, readExecutable: _ => Icon(Colors.Orange, freeze: false),
            readPackagedApp: _ => null, stamp: _ => Stamp);
        ImageSource? delivered = null;
        int thread = -1;
        cache.Request(App(1), icon => { delivered = icon; thread = Environment.CurrentManagedThreadId; });
        PumpUntil(() => delivered is not null);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
        var bitmap = (BitmapSource)delivered!;
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(Colors.Orange, CenterPixel(bitmap));
    });

    [Fact]
    public void AHungReadIsAbandonedAndOtherApplicationsStillLoad() => StaTestHost.Run(() =>
    {
        long clock = 0;
        using var hang = new ManualResetEventSlim(false);
        using var cache = new SourceIconCache(null, readExecutable: path =>
        {
            if (path.EndsWith("app1.exe", StringComparison.Ordinal)) hang.Wait(TimeSpan.FromSeconds(20));
            return Icon();
        }, readPackagedApp: _ => null, stamp: _ => Stamp, now: () => Interlocked.Read(ref clock));
        bool hungDelivered = false;
        cache.Request(App(1), _ => hungDelivered = true);
        var started = Stopwatch.StartNew();
        // Let the worker take the hung read, then pass the deadline.
        while (started.Elapsed < TimeSpan.FromMilliseconds(200)) PumpOnce();
        Interlocked.Add(ref clock, (long)SourceIconCache.ReadDeadline.TotalMilliseconds + 1);
        bool other = false;
        cache.Request(App(2), _ => other = true);
        PumpUntil(() => other);
        Assert.True(cache.TryGet(App(1), out ImageSource? abandoned));
        Assert.Null(abandoned);
        hang.Set();
        while (started.Elapsed < TimeSpan.FromMilliseconds(600)) PumpOnce();
        Assert.False(hungDelivered);
        Assert.True(cache.TryGet(App(1), out abandoned));
        Assert.Null(abandoned);
    });

    [Fact]
    public void PackagedAppsUseTheirIdAndFallBackToTheExecutable() => StaTestHost.Run(() =>
    {
        var asked = new List<string>();
        using var cache = new SourceIconCache(null,
            readExecutable: path => { lock (asked) asked.Add("exe"); return Icon(); },
            readPackagedApp: id => { lock (asked) asked.Add("id"); return null; },
            stamp: _ => Stamp);
        CaptureProvenance store = CaptureProvenance.Create(appName: "Notepad", appExecutablePath: @"C:\Apps\Notepad.exe",
            appUserModelId: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        bool done = false;
        cache.Request(store, _ => done = true);
        PumpUntil(() => done);
        Assert.Equal(new[] { "id", "exe" }, asked);
    });

    internal static CaptureProvenance App(int index) =>
        CaptureProvenance.Create(appName: "App " + index, appExecutablePath: $@"C:\Apps\app{index}.exe");

    internal static BitmapSource Icon(Color? color = null, bool freeze = true)
    {
        int size = 32;
        var pixels = new byte[size * size * 4];
        Color fill = color ?? Colors.SteelBlue;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fill.B;
            pixels[i + 1] = fill.G;
            pixels[i + 2] = fill.R;
            pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        if (freeze) bitmap.Freeze();
        return bitmap;
    }

    private static Color CenterPixel(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    internal static void PumpUntil(Func<bool> condition)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException();
            PumpOnce();
        }
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(5);
    }
}
