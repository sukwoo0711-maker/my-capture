using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureFolderPersistenceTests
{
    [Fact]
    public Task SynchronousCapturePersistsFolderAndKeepsItsPhysicalDirectory() => RunStaAsync(async () =>
    {
        var (paths, queue, persistence) = Create();
        var folder = await queue.CreateFolderAsync("Sync");
        await queue.SetNewCaptureFolderAsync(folder.Id, true);
        CaptureRecord record = persistence.PersistOriginal(Image(0x20), 1, "fixture", "fixture");
        Assert.Equal(folder.Id, record.FolderId);
        Assert.Equal("myCapture01.png", queue.GetExportFileName(record));
        string directory = queue.GetDirectory(record);
        await queue.MoveCapturesAsync([record.Id], null);
        Assert.Equal(directory, queue.GetDirectory(record));
        Assert.False(record.IsPinned);
        var restored = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        restored.Load();
        Assert.Null(Assert.Single(restored.Records).FolderId);
    });

    [Fact]
    public Task AsyncCaptureJournalsDestinationBeforePublication_AndExportsTheActualRenderedPng() => RunStaAsync(async () =>
    {
        var (paths, queue, persistence) = Create();
        var folder = await queue.CreateFolderAsync("Async");
        await queue.SetNewCaptureFolderAsync(folder.Id, true);
        BitmapSource original = Image(0x20);
        BitmapSource rendered = Image(0xD0);
        CaptureRecord pending = CapturePersistenceService.CreatePendingRecord(original, 1, "fixture", "fixture");
        bool journalChecked = false;
        persistence.BeforeRecordMetadataCommit = _ =>
        {
            using JsonDocument journal = JsonDocument.Parse(File.ReadAllText(
                queue.GetFilePath(pending, CaptureFileNames.OriginalPending)));
            Assert.Equal(folder.Id, journal.RootElement.GetProperty(nameof(CaptureRecord.FolderId)).GetGuid());
            journalChecked = true;
        };
        await persistence.PersistPendingOriginalAsync(pending, original, rendered);
        Assert.True(journalChecked);
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(queue.GetFilePath(pending, CaptureFileNames.Meta)));
        Assert.Equal(folder.Id, metadata.RootElement.GetProperty("folderId").GetGuid());
        var exporter = new GalleryFolderExportService(queue, Path.Combine(paths.DataRoot, "exports"));
        using var prepared = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id)));
        string path = GalleryExportFiles.RelativeChild(prepared.BatchRoot, prepared.Plan.Entries[0].RelativePath);
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(4, decoder.Frames[0].PixelWidth);
        var pixels = new byte[4 * 4 * 4];
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, 16, 0);
        Assert.Equal(0xD0, pixels[0]);
        Assert.Equal(folder.Id, pending.FolderId);
    });

    private static (AppPaths Paths, CaptureQueue Queue, CapturePersistenceService Persistence) Create()
    {
        string data = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ??
            Path.Combine(Path.GetTempPath(), "MyCapture-Folder410-tests");
        var paths = AppPaths.CreateForRoot(Path.Combine(data, "persistence-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var settings = new QueueSettings();
        var queue = new CaptureQueue(paths, settings, NullLogger<CaptureQueue>.Instance);
        return (paths, queue, new CapturePersistenceService(queue, paths, () => settings,
            NullLogger<CapturePersistenceService>.Instance));
    }

    private static BitmapSource Image(byte blue)
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        { pixels[index] = blue; pixels[index + 3] = 255; }
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        bitmap.Freeze();
        return bitmap;
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
