using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryLibraryExtrasTests
{
    [Fact]
    public async Task Controller_TitleAndBatchTagsJoinTheQueueUndoHistory()
    {
        var (queue, controller) = Gallery();
        CaptureRecord record = Add(queue);
        Assert.True(await controller.UpdateOrganizationAsync(record.Id, "New title", "work"));
        await controller.UpdateTagsAsync([record.Id], [new("batch", true)]);
        Assert.Equal(LibraryUndoKind.Tags, await queue.LibraryUndo.UndoAsync());
        Assert.Equal("work", record.Tags);
        Assert.Equal(LibraryUndoKind.Title, await queue.LibraryUndo.UndoAsync());
        Assert.Equal("Original", record.Title);
        Assert.Equal(string.Empty, record.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OcrActivity_IsSharedByAutomaticAndManualCalls_AndSupportsCancellation(bool cancel)
    {
        var (queue, controller) = Gallery();
        CaptureRecord record = Add(queue);
        var ocr = new WaitingOcr();
        var service = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => new OcrSettings(),
            NullLogger<OcrIndexingService>.Instance);
        using var import = queue.Activities.Start(LibraryActivityKind.Import, total: 2);
        Task<OcrIndexingOutcome> work = service.IndexMissingAsync();
        await ocr.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        LibraryActivity activity = Assert.Single(queue.Activities.Running, item => item.Kind == LibraryActivityKind.Ocr);
        Assert.Equal(2, queue.Activities.Running.Count);
        Assert.Equal(0, activity.Completed);
        Assert.Equal(1, activity.Total);
        Assert.True(activity.CanCancel);
        if (cancel) Assert.True(queue.Activities.Cancel(activity.Id));
        else ocr.Complete.TrySetResult(OcrResult.Success("indexed text", "en-US", [], TimeSpan.Zero));
        Assert.Equal(cancel ? OcrIndexingOutcome.Cancelled : OcrIndexingOutcome.Completed,
            await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LibraryActivityKind.Import, Assert.Single(queue.Activities.Running).Kind);
        Assert.Equal(cancel ? null : "indexed text", record.OcrText);
        Assert.Equal(0, queue.LibraryUndo.Count);
    }

    private sealed class WaitingOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OcrResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            return Complete.Task.WaitAsync(cancellationToken);
        }
    }

    private static (CaptureQueue, GalleryController) Gallery()
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(AppContext.BaseDirectory, "library-extras-fixtures", Guid.NewGuid().ToString("N")));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        return (queue, new GalleryController(queue, NullLogger<GalleryController>.Instance));
    }

    private static CaptureRecord Add(CaptureQueue queue)
    {
        var record = new CaptureRecord { RelativeDirectory = Guid.NewGuid().ToString("N"), Title = "Original",
            Width = 10, Height = 10, IsPinned = true };
        queue.Add(record);
        queue.SaveRecordMetaOrThrow(record);
        File.WriteAllText(queue.GetFilePath(record, CaptureFileNames.Original), "fake OCR input");
        return record;
    }
}
