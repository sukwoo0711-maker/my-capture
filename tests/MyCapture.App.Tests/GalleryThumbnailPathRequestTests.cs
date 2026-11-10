using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryThumbnailPathRequestTests
{
    [Fact]
    public async Task QueueRequest_KeepsOriginalPathAfterLiveRecordChanges()
    {
        using var fixture = new QueueFixture();
        var record = new CaptureRecord { RelativeDirectory = Path.Combine("2026-11", "original") };
        string expected = fixture.Queue.GetFilePath(record, CaptureFileNames.Thumbnail);
        Func<string> request = GalleryThumbnailLoader.CreateQueuePathRequest(fixture.Queue, record);
        record.Id = Guid.NewGuid();
        record.RelativeDirectory = @"..\outside";
        string? decodedPath = null;
        int calls = 0;
        var loader = new GalleryThumbnailLoader((path, _) =>
        {
            decodedPath = path;
            Interlocked.Increment(ref calls);
            return null;
        });

        await loader.LoadAsync(request, 32, CancellationToken.None);

        Assert.Equal(expected, decodedPath);
        Assert.Equal(1, calls);
        Assert.Equal(@"..\outside", record.RelativeDirectory);
    }

    [Fact]
    public async Task QueueRequest_RejectsCapturedTraversalWithOriginalIdBeforeDecode()
    {
        using var fixture = new QueueFixture();
        var record = new CaptureRecord { RelativeDirectory = @"..\outside" };
        InvalidDataException expected = Assert.Throws<InvalidDataException>(() => fixture.Queue.GetDirectory(record));
        Func<string> request = GalleryThumbnailLoader.CreateQueuePathRequest(fixture.Queue, record);
        record.Id = Guid.NewGuid();
        record.RelativeDirectory = Path.Combine("2026-11", "now-valid");
        int calls = 0;
        var loader = new GalleryThumbnailLoader((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return null;
        });

        InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.LoadAsync(request, 32, CancellationToken.None));

        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task QueueRequest_RechecksJunctionCreatedAfterCaptureBeforeDecode()
    {
        using var fixture = new QueueFixture();
        var record = new CaptureRecord { RelativeDirectory = "linked-capture" };
        Func<string> request = GalleryThumbnailLoader.CreateQueuePathRequest(fixture.Queue, record);
        string outside = Path.Combine(fixture.Root, "outside-captures");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, CaptureFileNames.Thumbnail);
        byte[] sentinelBytes = [0x4D, 0x43, 0x71, 0x82];
        File.WriteAllBytes(sentinel, sentinelBytes);
        string link = Path.Combine(fixture.Paths.CapturesRoot, record.RelativeDirectory);
        try
        {
            UpdatePathsTests.CreateJunction(link, outside);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            InvalidDataException expected = Assert.Throws<InvalidDataException>(() => fixture.Queue.GetDirectory(record));
            int calls = 0;
            var loader = new GalleryThumbnailLoader((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return null;
            });

            InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
                () => loader.LoadAsync(request, 32, CancellationToken.None));

            Assert.Equal(expected.Message, actual.Message);
            Assert.Equal(0, calls);
            Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            // Recycle the link itself before its owned parent; never traverse its target.
            TestRecycleBin.DeleteDirectory(link);
        }
        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void QueueFactory_RejectsTraversalAsBrokenTileWithoutDecoding() => StaTestHost.Run(() =>
    {
        using var fixture = new QueueFixture();
        var record = new CaptureRecord { RelativeDirectory = @"..\outside" };
        int decoderCalls = 0, legacyCalls = 0;
        var loader = new GalleryThumbnailLoader((_, _) =>
        {
            Interlocked.Increment(ref decoderCalls);
            return Bitmap();
        });
        var tile = new GalleryItemViewModel(record, _ =>
        {
            legacyCalls++;
            return "unused-legacy";
        }, 32);
        tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher,
            item => GalleryThumbnailLoader.CreateQueuePathRequest(fixture.Queue, item));
        try
        {
            Assert.Null(tile.Thumbnail);
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.True(tile.PendingThumbnailLoad.IsCompletedSuccessfully);
            Assert.Null(tile.Thumbnail);
            Assert.True(tile.IsBroken);
            Assert.Equal(0, decoderCalls);
            Assert.Equal(0, legacyCalls);
            Assert.Equal(0, tile.CachedThumbnailBytes);
        }
        finally
        {
            tile.ReleaseThumbnail();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
        }
    });

    [Fact]
    public void LegacyResolver_ReceivesOriginalRecordOnOwnerDispatcher() => StaTestHost.Run(() =>
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        var record = new CaptureRecord();
        CaptureRecord? resolvedRecord = null;
        int resolverThread = 0, resolverCalls = 0, decoderThread = 0;
        string? decodedPath = null;
        BitmapSource bitmap = Bitmap();
        var tile = new GalleryItemViewModel(record, item =>
        {
            resolvedRecord = item;
            resolverThread = Environment.CurrentManagedThreadId;
            resolverCalls++;
            return "legacy-thumbnail";
        }, 32);
        var loader = new GalleryThumbnailLoader((path, _) =>
        {
            decodedPath = path;
            decoderThread = Environment.CurrentManagedThreadId;
            return bitmap;
        });
        tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher);
        try
        {
            Assert.Null(tile.Thumbnail);
            Assert.Equal(1, resolverCalls);
            Assert.Same(record, resolvedRecord);
            Assert.Equal(ownerThread, resolverThread);
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.True(tile.PendingThumbnailLoad.IsCompletedSuccessfully);
            Assert.Same(bitmap, tile.Thumbnail);
            Assert.Equal("legacy-thumbnail", decodedPath);
            Assert.NotEqual(ownerThread, decoderThread);
            Assert.False(tile.IsBroken);
        }
        finally
        {
            tile.ReleaseThumbnail();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
        }
    });

    [Fact]
    public void QueueFactory_CapturesOnOwnerAndResolvesOnDecoderWorker() => StaTestHost.Run(() =>
    {
        using var fixture = new QueueFixture();
        int ownerThread = Environment.CurrentManagedThreadId;
        var record = new CaptureRecord { RelativeDirectory = Path.Combine("2026-11", "factory") };
        string expected = fixture.Queue.GetFilePath(record, CaptureFileNames.Thumbnail);
        CaptureRecord? factoryRecord = null;
        int factoryThread = 0, resolverThread = 0, decoderThread = 0, legacyCalls = 0;
        string? decodedPath = null;
        BitmapSource bitmap = Bitmap();
        var tile = new GalleryItemViewModel(record, _ =>
        {
            legacyCalls++;
            return "unused-legacy";
        }, 32);
        var loader = new GalleryThumbnailLoader((path, _) =>
        {
            decodedPath = path;
            decoderThread = Environment.CurrentManagedThreadId;
            return bitmap;
        });
        tile.ConfigureAsyncLoading(loader, Dispatcher.CurrentDispatcher, item =>
        {
            factoryRecord = item;
            factoryThread = Environment.CurrentManagedThreadId;
            Func<string> request = GalleryThumbnailLoader.CreateQueuePathRequest(fixture.Queue, item);
            return () =>
            {
                resolverThread = Environment.CurrentManagedThreadId;
                return request();
            };
        });
        try
        {
            Assert.Null(tile.Thumbnail);
            Assert.Same(record, factoryRecord);
            Assert.Equal(ownerThread, factoryThread);
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
            Assert.True(tile.PendingThumbnailLoad.IsCompletedSuccessfully);
            Assert.Same(bitmap, tile.Thumbnail);
            Assert.Equal(expected, decodedPath);
            Assert.NotEqual(ownerThread, resolverThread);
            Assert.Equal(resolverThread, decoderThread);
            Assert.Equal(0, legacyCalls);
            Assert.False(tile.IsBroken);
        }
        finally
        {
            tile.ReleaseThumbnail();
            PumpUntil(() => tile.PendingThumbnailLoad.IsCompleted);
        }
    });

    private static BitmapSource Bitmap()
    {
        BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpUntil(Func<bool> done)
    {
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15))
                throw new TimeoutException("Thumbnail request did not finish.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class QueueFixture : IDisposable
    {
        internal string Root { get; } = TestRecycleBin.CreateTempSubdirectory("mc-thumbnail-path-").FullName;
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }

        internal QueueFixture()
        {
            Paths = AppPaths.CreateForRoot(Root);
            Paths.EnsureCreated();
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        }

        public void Dispose() => TestRecycleBin.DeleteDirectory(Root, recursive: true);
    }
}
