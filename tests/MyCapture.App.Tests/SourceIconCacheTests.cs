using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Source application icons: one read per application, memory first, a disk cache pruned into the
/// Recycle Bin, a deadline.
/// </summary>
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
                File.WriteAllBytes(file, IconBytes(index));
                File.SetLastWriteTimeUtc(file, now.AddMinutes(index));
            }
            File.WriteAllText(Path.Combine(root, "keep.txt"), "not an icon");
            // No recycler is given: this is the application's own path, Windows' Recycle Bin.
            IReadOnlyList<CleanupReceipt> overCount = SourceIconCache.Prune(root, fileLimit: 4, byteLimit: 1_000_000);
            Assert.Equal(new[] { 2, 3, 4, 5 }.Select(i => $"{i:x32}.png"),
                Directory.GetFiles(root, "*.png").Select(Path.GetFileName).Order());
            IReadOnlyList<CleanupReceipt> overSize = SourceIconCache.Prune(root, fileLimit: 100, byteLimit: 2_500);
            Assert.Equal(new[] { 4, 5 }.Select(i => $"{i:x32}.png"),
                Directory.GetFiles(root, "*.png").Select(Path.GetFileName).Order());
            // Only the icons over a limit went, the oldest first; what stays keeps its bytes.
            Assert.Equal(new[] { 0, 1 }.Select(i => $"{i:x32}.png"), overCount.Select(receipt => Path.GetFileName(receipt.OriginalPath)));
            Assert.Equal(new[] { 2, 3 }.Select(i => $"{i:x32}.png"), overSize.Select(receipt => Path.GetFileName(receipt.OriginalPath)));
            Assert.Equal("not an icon", File.ReadAllText(Path.Combine(root, "keep.txt")));
            foreach (int kept in new[] { 4, 5 })
                Assert.Equal(IconBytes(kept), File.ReadAllBytes(Path.Combine(root, $"{kept:x32}.png")));
            // Each icon that went is in the Recycle Bin, and comes back whole.
            var trash = new RecoverableFileRemoval();
            foreach (CleanupReceipt receipt in overCount.Concat(overSize))
            {
                Assert.False(File.Exists(receipt.OriginalPath));
                Assert.True(File.Exists(receipt.RecycledPath));
                trash.RestoreFile(receipt);
            }
            for (int index = 0; index < 6; index++)
                Assert.Equal(IconBytes(index), File.ReadAllBytes(Path.Combine(root, $"{index:x32}.png")));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void PruningMovesTheOldestFirstAndOnlyAFewInOnePass()
    {
        string root = OwnedTestDirectory.Create("mc-srcicons");
        string aside = OwnedTestDirectory.Create("mc-srcicons-aside");
        try
        {
            DateTime now = DateTime.UtcNow;
            int count = SourceIconCache.PruneMoveLimit + 3;
            for (int index = 0; index < count; index++)
            {
                string file = Path.Combine(root, $"{index:x32}.png");
                File.WriteAllBytes(file, IconBytes(index));
                File.SetLastWriteTimeUtc(file, now.AddMinutes(index));
            }
            var moved = new List<string>();
            CleanupReceipt MoveAside(string path)
            {
                string target = Path.Combine(aside, Path.GetFileName(path));
                File.Move(path, target);
                moved.Add(Path.GetFileName(path));
                return new CleanupReceipt(path, target, string.Empty, string.Empty, isDirectory: false);
            }

            // One icon may stay, so all but the newest are over the limit: more than one pass can move.
            Assert.Equal(SourceIconCache.PruneMoveLimit,
                SourceIconCache.Prune(root, fileLimit: 1, byteLimit: 1_000_000, MoveAside).Count);
            Assert.Equal(Enumerable.Range(0, SourceIconCache.PruneMoveLimit).Select(i => $"{i:x32}.png"), moved);
            moved.Clear();
            Assert.Equal(2, SourceIconCache.Prune(root, fileLimit: 1, byteLimit: 1_000_000, MoveAside).Count);
            Assert.Equal(new[] { SourceIconCache.PruneMoveLimit, SourceIconCache.PruneMoveLimit + 1 }.Select(i => $"{i:x32}.png"), moved);
            Assert.Equal($"{count - 1:x32}.png", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
            Assert.Empty(SourceIconCache.Prune(root, fileLimit: 1, byteLimit: 1_000_000, MoveAside));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
            OwnedTestDirectory.Delete(aside);
        }
    }

    [Fact]
    public void AnIconThatCannotBeRecycledStaysAndTheCacheGoesOn() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-srcicons");
        try
        {
            // One more icon than the cache keeps, all of them older than the one about to be stored.
            DateTime old = DateTime.UtcNow.AddDays(-1);
            for (int index = 0; index <= SourceIconCache.DiskFileLimit; index++)
            {
                string file = Path.Combine(root, $"{index:x32}.png");
                File.WriteAllBytes(file, [(byte)index]);
                File.SetLastWriteTimeUtc(file, old.AddSeconds(index));
            }
            // Windows refuses every move, as it does with the Recycle Bin turned off.
            int refused = 0;
            CleanupReceipt Refuse(string path)
            {
                Interlocked.Increment(ref refused);
                throw new IOException("Windows cannot move this item to the Recycle Bin; it was left in place.");
            }
            CaptureProvenance source = App(1);
            using (var first = new SourceIconCache(root, readExecutable: _ => Icon(Colors.SeaGreen), readPackagedApp: _ => null,
                stamp: _ => Stamp, recycle: Refuse))
            {
                bool done = false;
                first.Request(source, _ => done = true);
                // The new icon puts the folder two over the limit; both are tried once it is delivered.
                PumpUntil(() => done && Volatile.Read(ref refused) == 2);
            }
            Assert.Equal(SourceIconCache.DiskFileLimit + 2, Directory.GetFiles(root, "*.png").Length);
            for (int index = 0; index <= SourceIconCache.DiskFileLimit; index++)
                Assert.Equal(new[] { (byte)index }, File.ReadAllBytes(Path.Combine(root, $"{index:x32}.png")));

            // The icon that was stored is read back from the folder, with no shell read.
            using var second = new SourceIconCache(root, readExecutable: _ => throw new InvalidOperationException("read the shell"),
                readPackagedApp: _ => null, stamp: _ => Stamp, recycle: Refuse);
            ImageSource? loaded = null;
            second.Request(source, icon => loaded = icon);
            PumpUntil(() => loaded is not null);
            Assert.Equal(Colors.SeaGreen, CenterPixel(Assert.IsAssignableFrom<BitmapSource>(loaded)));
            Assert.Equal(2, Volatile.Read(ref refused));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void AMoveToTheRecycleBinThatDoesNotReturnHoldsUpNoOtherApplication() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-srcicons");
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            // One more icon than the cache keeps, all of them older than the ones about to be stored.
            DateTime old = DateTime.UtcNow.AddDays(-1);
            for (int index = 0; index <= SourceIconCache.DiskFileLimit; index++)
            {
                string file = Path.Combine(root, $"{index:x32}.png");
                File.WriteAllBytes(file, [(byte)index]);
                File.SetLastWriteTimeUtc(file, old.AddSeconds(index));
            }
            // The first move hangs in the Shell until released; every move is then refused.
            int moves = 0;
            var movers = new List<Thread>();
            CleanupReceipt Recycle(string path)
            {
                lock (movers) movers.Add(Thread.CurrentThread);
                if (Interlocked.Increment(ref moves) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(20));
                }
                throw new IOException("Windows cannot move this item to the Recycle Bin; it was left in place.");
            }
            long clock = 0;
            var reads = new List<string>();
            using var cache = new SourceIconCache(root, readExecutable: path =>
            {
                lock (reads) reads.Add(Path.GetFileName(path));
                return Icon(Colors.SeaGreen);
            }, readPackagedApp: _ => null, stamp: _ => Stamp, now: () => Interlocked.Read(ref clock), recycle: Recycle);

            int first = 0;
            cache.Request(App(1), _ => Interlocked.Increment(ref first));
            // The stored icon puts the folder over the limit; its pruning reaches the hung move.
            PumpUntil(() => Volatile.Read(ref first) == 1 && entered.IsSet);
            // Even past the read deadline, which only abandons a read in progress.
            Interlocked.Add(ref clock, (long)SourceIconCache.ReadDeadline.TotalMilliseconds + 1);

            int second = 0;
            cache.Request(App(2), _ => Interlocked.Increment(ref second));
            PumpUntil(() => Volatile.Read(ref second) == 1, TimeSpan.FromSeconds(5));
            Assert.False(release.IsSet);
            Assert.Equal(1, Volatile.Read(ref moves));
            Assert.Equal(new[] { "app1.exe", "app2.exe" }, reads);
            Thread mover;
            lock (movers) mover = Assert.Single(movers);
            Assert.Equal(ApartmentState.STA, mover.GetApartmentState());

            // Released, the pass tries the second icon over the limit, leaves both, and its thread ends.
            release.Set();
            Assert.True(mover.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, Volatile.Read(ref moves));
            Assert.Equal(SourceIconCache.DiskFileLimit + 3, Directory.GetFiles(root, "*.png").Length);

            // The icon stored during that pass left pruning due: the next new icon tries again, at most a few.
            int third = 0;
            cache.Request(App(3), _ => Interlocked.Increment(ref third));
            PumpUntil(() => Volatile.Read(ref third) == 1 && Volatile.Read(ref moves) == 2 + SourceIconCache.PruneMoveLimit);
            Thread next;
            lock (movers) next = movers[^1];
            Assert.True(next.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(2 + SourceIconCache.PruneMoveLimit, Volatile.Read(ref moves));
            Assert.Equal(1, Volatile.Read(ref first));
            Assert.Equal(1, Volatile.Read(ref second));
            Assert.Equal(new[] { "app1.exe", "app2.exe", "app3.exe" }, reads);
            Assert.Equal(SourceIconCache.DiskFileLimit + 4, Directory.GetFiles(root, "*.png").Length);
            for (int index = 0; index <= SourceIconCache.DiskFileLimit; index++)
                Assert.Equal(new[] { (byte)index }, File.ReadAllBytes(Path.Combine(root, $"{index:x32}.png")));
        }
        finally
        {
            release.Set();
            OwnedTestDirectory.Delete(root);
        }
    });

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

    /// <summary>1,000 bytes that differ from one icon to the next, so a restored file is known by its content.</summary>
    private static byte[] IconBytes(int index) =>
        Enumerable.Range(0, 1000).Select(offset => (byte)(index * 37 + offset)).ToArray();

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

    internal static void PumpUntil(Func<bool> condition, TimeSpan? limit = null)
    {
        var started = Stopwatch.StartNew();
        TimeSpan wait = limit ?? TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (started.Elapsed > wait) throw new TimeoutException();
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
