using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryAsyncThumbnailTests
{
    [Fact]
    public void SlowPathInspection_AllowsInput_AndHidePreventsDecode() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId;
        int decodes = 0;
        var loader = new GalleryThumbnailLoader((_, _) => { Interlocked.Increment(ref decodes); return Bitmap(8); });
        var tile = Tile(loader);
        tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher, _ =>
        {
            Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
            return () =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return "fixture";
            };
        });
        try
        {
            Assert.Null(tile.Thumbnail);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            bool inputRan = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputRan = true));
            PumpUntil(() => inputRan);
            Assert.False(tile.PendingThumbnailLoad.IsCompleted);
            tile.SetThumbnailLoadingEnabled(false);
            release.Set();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            tile.PendingThumbnailLoad.GetAwaiter().GetResult();
            Assert.Equal(0, Volatile.Read(ref decodes));
            Assert.Null(tile.Thumbnail);
            Assert.False(tile.IsBroken);
        }
        finally
        {
            release.Set();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            tile.PendingThumbnailLoad.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public async Task PathInspection_IsBounded_AndCancelledWaiterNeverResolves()
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        int resolutions = 0;
        int decodes = 0;
        var loader = new GalleryThumbnailLoader((_, _) => { Interlocked.Increment(ref decodes); return Bitmap(8); });
        string Resolve()
        {
            Interlocked.Increment(ref resolutions);
            entered.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return "fixture";
        }
        Task<BitmapSource?> one = loader.LoadAsync(Resolve, 8, CancellationToken.None);
        Task<BitmapSource?> two = loader.LoadAsync(Resolve, 8, CancellationToken.None);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task<BitmapSource?> three = loader.LoadAsync(Resolve, 8, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => three);
            Assert.Equal(2, Volatile.Read(ref resolutions));
            Assert.Equal(0, Volatile.Read(ref decodes));
        }
        finally { release.Set(); await Task.WhenAll(one, two); }
        Assert.Equal(2, Volatile.Read(ref decodes));
    }

    [Fact]
    public void Reconfigure_WhileOldPathResolves_UsesNewLoaderAndDiscardsOldRequest() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int oldDecodes = 0;
        var oldLoader = new GalleryThumbnailLoader((_, _) => { Interlocked.Increment(ref oldDecodes); return Bitmap(10); });
        var tile = Tile(oldLoader);
        tile.ConfigureAsyncLoading(oldLoader, Dispatcher.CurrentDispatcher, _ => () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return "old";
        });
        Task old = Task.CompletedTask;
        try
        {
            Assert.Null(tile.Thumbnail);
            old = tile.PendingThumbnailLoad;
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var newer = new GalleryThumbnailLoader((path, _) => { Assert.Equal("new", path); return Bitmap(30); });
            tile.ConfigureAsyncLoading(newer, Dispatcher.CurrentDispatcher, _ => () => "new");
            Assert.Null(tile.Thumbnail);
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            tile.PendingThumbnailLoad.GetAwaiter().GetResult();
            Assert.Equal(30, tile.Thumbnail!.PixelWidth);
            release.Set();
            PumpUntil(() => old.IsCompleted);
            old.GetAwaiter().GetResult();
            Assert.Equal(0, Volatile.Read(ref oldDecodes));
            Assert.Equal(30, tile.Thumbnail!.PixelWidth);
        }
        finally
        {
            release.Set();
            PumpUntil(() => old.IsCompleted && tile.PendingThumbnailLoad.IsCompleted);
            old.GetAwaiter().GetResult();
            tile.PendingThumbnailLoad.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void SlowDecode_DoesNotBlockBinding_AndHideDiscardsItsResult() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId;
        var loader = new GalleryThumbnailLoader((_, _) =>
        {
            Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Bitmap(23);
        });
        var tile = Tile(loader);
        var image = new Image();
        image.SetBinding(Image.SourceProperty, new Binding(nameof(GalleryItemViewModel.Thumbnail)) { Source = tile });
        try
        {
            Assert.Null(image.Source);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            tile.SetThumbnailLoadingEnabled(false);
            release.Set();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.Null(image.Source);
            Assert.False(tile.IsBroken);
            Assert.Equal(0, tile.CachedThumbnailBytes);
            tile.SetThumbnailLoadingEnabled(true);
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.NotNull(image.Source);
            Assert.True(((BitmapSource)image.Source).IsFrozen);
        }
        finally { release.Set(); }
    });

    [Fact]
    public void Refresh_NewerDecodeWins_WhenOldDecodeFinishesLast() => StaTestHost.Run(() =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        int calls = 0;
        var loader = new GalleryThumbnailLoader((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
            }
            return Bitmap(call == 1 ? 10 : 30);
        });
        var tile = Tile(loader);
        int publishedOn = 0;
        tile.ThumbnailAccessed = _ => publishedOn = Environment.CurrentManagedThreadId;
        Assert.Null(tile.Thumbnail);
        Task first = tile.PendingThumbnailLoad;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            tile.RefreshThumbnail();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.Equal(30, tile.Thumbnail!.PixelWidth);
            Assert.Equal(Environment.CurrentManagedThreadId, publishedOn);
            releaseFirst.Set();
            PumpUntil(() => first.IsCompleted);
            Assert.Equal(30, tile.Thumbnail!.PixelWidth);
        }
        finally { releaseFirst.Set(); }
    });

    [Fact]
    public async Task DecoderConcurrency_IsBounded_AndCancelledWaiterNeverDecodes()
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        var loader = new GalleryThumbnailLoader((_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Bitmap(8);
        });
        Task<BitmapSource?> one = loader.LoadAsync("one", 8, CancellationToken.None);
        Task<BitmapSource?> two = loader.LoadAsync("two", 8, CancellationToken.None);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Task<BitmapSource?> three = loader.LoadAsync("three", 8, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => three);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { release.Set(); await Task.WhenAll(one, two); }
    }

    private static GalleryItemViewModel Tile(GalleryThumbnailLoader loader)
    {
        var tile = new GalleryItemViewModel(new CaptureRecord(), _ => "fixture", 32);
        tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher);
        return tile;
    }

    private static BitmapSource Bitmap(int width)
    {
        var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgr24, null, new byte[width * 3], width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpUntil(Func<bool> done)
    {
        long start = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(15)) throw new TimeoutException();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
