using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.Core.Annotations;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Verifies the OCR full-text-search enablers added in response to the external review:
/// the language-pack availability advisory (so the dependency is never silent) and the
/// batch indexing service's control-flow outcomes (so a missing engine degrades cleanly).
/// </summary>
public sealed class OcrIndexingAndAvailabilityTests
{
    // ---- OcrAvailability advisory ----

    [Fact]
    public void Availability_NoLanguagePack_IsSurfacedNotSilent()
    {
        OcrAvailability a = OcrAvailability.Describe(isAvailable: false, supportedLanguages: []);

        Assert.False(a.IsAvailable);
        Assert.Contains("언어 팩", a.Headline);
        // Detail must tell the user OCR/search is off AND that the rest of the app still works.
        Assert.Contains("전문 검색", a.Detail);
        Assert.Contains("나머지 기능", a.Detail);
    }

    [Fact]
    public void Availability_EngineReportsAvailableButNoLanguages_TreatedUnavailable()
    {
        OcrAvailability a = OcrAvailability.Describe(isAvailable: true, supportedLanguages: []);
        Assert.False(a.IsAvailable);
    }

    [Fact]
    public void Availability_WithLanguages_ListsThemAndSaysOffline()
    {
        OcrAvailability a = OcrAvailability.Describe(isAvailable: true, supportedLanguages: ["ko-KR", "en-US"]);

        Assert.True(a.IsAvailable);
        Assert.Contains("ko-KR", a.Detail);
        Assert.Contains("오프라인", a.Detail);
    }

    // ---- OcrIndexingService outcomes ----

    private sealed class FakeOcr : IOcrService
    {
        private readonly bool _available;
        private readonly string _text;

        public FakeOcr(bool available, string text = "recognised text")
        {
            _available = available;
            _text = text;
        }

        public int Calls { get; private set; }

        public bool IsAvailable => _available;

        public IReadOnlyList<string> SupportedLanguages => _available ? ["ko-KR"] : [];

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!_available)
            {
                return Task.FromResult(OcrResult.Unavailable("no engine"));
            }

            return Task.FromResult(OcrResult.Success(_text, "ko-KR", [], TimeSpan.Zero));
        }
    }

    private sealed class DelayedOcr : IOcrService
    {
        private readonly TaskCompletionSource<OcrResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public IReadOnlyList<string> SupportedLanguages => ["ko-KR"];

        public Task<OcrResult> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
            return _completion.Task;
        }

        internal void Complete(string text) =>
            _completion.TrySetResult(OcrResult.Success(text, "ko-KR", [], TimeSpan.Zero));
    }

    private sealed class NoTextOcr : IOcrService
    {
        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public IReadOnlyList<string> SupportedLanguages => ["ko-KR"];

        public Task<OcrResult> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(OcrResult.NoText("ko-KR", TimeSpan.Zero));
        }
    }

    private sealed class DelayedCancelledResultOcr : IOcrService
    {
        private readonly TaskCompletionSource<OcrResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public IReadOnlyList<string> SupportedLanguages => ["ko-KR"];

        public Task<OcrResult> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            return _completion.Task;
        }

        internal void CompleteCancelled() =>
            _completion.TrySetResult(OcrResult.Cancelled());
    }

    private static GalleryController NewGallery(out CaptureQueue queue, string root)
    {
        AppPaths paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        return new GalleryController(queue, NullLogger<GalleryController>.Instance);
    }

    [Fact]
    public async Task Index_WhenEngineUnavailable_ReturnsUnavailable_WithoutCalling()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ocridx-" + Guid.NewGuid().ToString("N"));
        try
        {
            GalleryController gallery = NewGallery(out CaptureQueue queue, root);
            var ocr = new FakeOcr(available: false);
            var svc = new OcrIndexingService(
                gallery,
                ocr,
                r => queue.GetDirectory(r),
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);

            OcrIndexingOutcome outcome = await svc.IndexMissingAsync();

            Assert.Equal(OcrIndexingOutcome.Unavailable, outcome);
            Assert.Equal(0, ocr.Calls);
            Assert.False(svc.IsAvailable);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public async Task Index_WhenNothingMissing_ReturnsNothingToDo()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ocridx-" + Guid.NewGuid().ToString("N"));
        try
        {
            GalleryController gallery = NewGallery(out CaptureQueue queue, root);
            var ocr = new FakeOcr(available: true);
            var svc = new OcrIndexingService(
                gallery,
                ocr,
                r => queue.GetDirectory(r),
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);

            // Empty queue => nothing missing.
            OcrIndexingOutcome outcome = await svc.IndexMissingAsync();

            Assert.Equal(OcrIndexingOutcome.NothingToDo, outcome);
            Assert.Equal(0, ocr.Calls);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void Coverage_ExposedThroughService()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ocridx-" + Guid.NewGuid().ToString("N"));
        try
        {
            GalleryController gallery = NewGallery(out _, root);
            var svc = new OcrIndexingService(
                gallery,
                new FakeOcr(available: true),
                _ => root,
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);

            OcrCoverage coverage = svc.Coverage;
            Assert.Equal(0, coverage.Total);
            Assert.True(coverage.IsComplete);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void Availability_SingleLanguage_ListsIt()
    {
        OcrAvailability a = OcrAvailability.Describe(isAvailable: true, supportedLanguages: ["en-US"]);
        Assert.True(a.IsAvailable);
        Assert.Contains("en-US", a.Detail);
    }

    // ---- Indexer happy path over a REAL persisted capture ----

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource SolidBitmap(int w, int h)
    {
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        byte[] px = new byte[w * h * 4];
        Array.Fill(px, (byte)0x80);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    [Fact]
    public void Index_PersistedCapture_CachesText_RaisesCoverage_AndBecomesSearchable() => RunSta(() =>
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ocridx-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var queueSettings = new QueueSettings();
            var queue = new CaptureQueue(paths, queueSettings, NullLogger<CaptureQueue>.Instance);
            var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);

            // Persist a real capture so rendered.png exists on disk for the indexer to read.
            var persistence = new MyCapture.App.Editing.CapturePersistenceService(
                queue, paths, () => queueSettings, NullLogger<MyCapture.App.Editing.CapturePersistenceService>.Instance);
            CaptureRecord record = persistence.PersistOriginal(SolidBitmap(40, 30), 1.0, "메모장", "\\\\.\\DISPLAY1");

            // Before indexing: no OCR text, not searchable by the image's words.
            Assert.False(record.HasOcrText);
            Assert.Equal(1, gallery.MeasureOcrCoverage().Missing);
            Assert.Empty(CaptureTextSearch.Search(queue.Records, "송장번호"));

            var ocr = new FakeOcr(available: true, text: "송장번호 INV-2026-0830");
            var svc = new OcrIndexingService(
                gallery, ocr, r => queue.GetDirectory(r), () => new OcrSettings(), NullLogger<OcrIndexingService>.Instance);

            OcrIndexingOutcome outcome = svc.IndexMissingAsync().GetAwaiter().GetResult();

            Assert.Equal(OcrIndexingOutcome.Completed, outcome);
            Assert.True(ocr.Calls >= 1);
            // Text was cached on the record and persisted.
            Assert.True(record.HasOcrText);
            Assert.Contains("INV-2026-0830", record.OcrText);
            // Coverage is now complete and the capture is findable by its image's words.
            Assert.True(gallery.MeasureOcrCoverage().IsComplete);
            IReadOnlyList<CaptureSearchHit> hits = CaptureTextSearch.Search(queue.Records, "INV-2026-0830");
            Assert.Single(hits);
            Assert.True(hits[0].MatchedOcr);

            // Persisted: a reloaded queue still carries the OCR text.
            var reloaded = new CaptureQueue(paths, queueSettings, NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.True(reloaded.Records[0].HasOcrText);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    });

    [Fact]
    public void Index_AlreadyCancelledToken_ReturnsCancelled() => RunSta(() =>
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-ocridx-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var queueSettings = new QueueSettings();
            var queue = new CaptureQueue(paths, queueSettings, NullLogger<CaptureQueue>.Instance);
            var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var persistence = new MyCapture.App.Editing.CapturePersistenceService(
                queue, paths, () => queueSettings, NullLogger<MyCapture.App.Editing.CapturePersistenceService>.Instance);
            _ = persistence.PersistOriginal(SolidBitmap(20, 20), 1.0, "t", "\\\\.\\DISPLAY1");

            var svc = new OcrIndexingService(
                gallery, new FakeOcr(available: true), r => queue.GetDirectory(r),
                () => new OcrSettings(), NullLogger<OcrIndexingService>.Instance);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            OcrIndexingOutcome outcome = svc.IndexMissingAsync(null, cts.Token).GetAwaiter().GetResult();

            Assert.Equal(OcrIndexingOutcome.Cancelled, outcome);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    });

    [Fact]
    public void Index_NoTextResult_IsGenerationScopedDurableAndNotRetried() => RunSta(() =>
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mc-ocr-notext-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var queueSettings = new QueueSettings();
            var queue = new CaptureQueue(paths, queueSettings, NullLogger<CaptureQueue>.Instance);
            var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var persistence = new CapturePersistenceService(
                queue,
                paths,
                () => queueSettings,
                NullLogger<CapturePersistenceService>.Instance);
            CaptureRecord record = persistence.PersistOriginal(
                SolidBitmap(24, 16),
                1.0,
                "no text",
                "DISPLAY1");
            var ocr = new NoTextOcr();
            var firstService = new OcrIndexingService(
                gallery,
                ocr,
                r => queue.GetDirectory(r),
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);

            Assert.Equal(
                OcrIndexingOutcome.Completed,
                firstService.IndexMissingAsync().GetAwaiter().GetResult());
            Assert.Equal(1, ocr.Calls);
            Assert.False(record.HasOcrText);
            Assert.True(record.HasCurrentOcrIndex);
            Assert.Equal(record.ContentRevision, record.OcrContentRevision);
            Assert.True(firstService.Coverage.IsComplete);
            Assert.Equal(0, firstService.Coverage.WithOcrText);
            Assert.Equal(
                OcrIndexingOutcome.NothingToDo,
                firstService.IndexMissingAsync().GetAwaiter().GetResult());
            Assert.Equal(1, ocr.Calls);

            var reloadedQueue = new CaptureQueue(
                paths,
                queueSettings,
                NullLogger<CaptureQueue>.Instance);
            reloadedQueue.Load();
            CaptureRecord durable = Assert.Single(reloadedQueue.Records);
            Assert.True(durable.HasCurrentOcrIndex);
            Assert.False(durable.HasOcrText);
            var reloadedGallery = new GalleryController(
                reloadedQueue,
                NullLogger<GalleryController>.Instance);
            var secondService = new OcrIndexingService(
                reloadedGallery,
                ocr,
                r => reloadedQueue.GetDirectory(r),
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);
            Assert.Equal(
                OcrIndexingOutcome.NothingToDo,
                secondService.IndexMissingAsync().GetAwaiter().GetResult());
            Assert.Equal(1, ocr.Calls);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    });

    [Fact]
    public async Task Index_CancelledResultAfterRecognitionStarts_ReturnsCancelled()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mc-ocr-cancel-result-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var queue = new CaptureQueue(
                paths,
                new QueueSettings(),
                NullLogger<CaptureQueue>.Instance);
            var record = new CaptureRecord
            {
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Width = 1,
                Height = 1,
                TotalBytes = 1,
            };
            record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(
                record.Id,
                record.CreatedAt);
            string directory = queue.GetDirectory(record);
            System.IO.Directory.CreateDirectory(directory);
            await System.IO.File.WriteAllBytesAsync(
                System.IO.Path.Combine(directory, CaptureFileNames.Rendered),
                [0x00]);
            queue.Add(record);
            var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var ocr = new DelayedCancelledResultOcr();
            var service = new OcrIndexingService(
                gallery,
                ocr,
                r => queue.GetDirectory(r),
                () => new OcrSettings(),
                NullLogger<OcrIndexingService>.Instance);
            using var cts = new CancellationTokenSource();

            Task<OcrIndexingOutcome> indexing = service.IndexMissingAsync(null, cts.Token);
            await ocr.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            ocr.CompleteCancelled();

            Assert.Equal(
                OcrIndexingOutcome.Cancelled,
                await indexing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(record.HasCurrentOcrIndex);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public async Task Index_ResultHeldAcrossReedit_IsMarshalledAndRejectedAsStale()
    {
        string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mc-ocr-generation-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        Dispatcher? dispatcher = null;
        CapturePersistenceService? persistence = null;
        CaptureRecord? record = null;
        DelayedOcr? delayed = null;
        OcrIndexingService? service = null;
        Task<OcrIndexingOutcome>? indexing = null;
        Exception? threadFailure = null;
        bool cacheRanOnOwnerDispatcher = false;
        using var ready = new ManualResetEventSlim(initialState: false);
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));
                AppPaths paths = AppPaths.CreateForRoot(root);
                paths.EnsureCreated();
                var queueSettings = new QueueSettings();
                var queue = new CaptureQueue(paths, queueSettings, NullLogger<CaptureQueue>.Instance);
                var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
                persistence = new CapturePersistenceService(
                    queue,
                    paths,
                    () => queueSettings,
                    NullLogger<CapturePersistenceService>.Instance);
                record = persistence.PersistOriginal(
                    SolidBitmap(42, 26),
                    1.0,
                    "OCR generation",
                    "DISPLAY1");
                delayed = new DelayedOcr();
                service = new OcrIndexingService(
                    gallery,
                    delayed,
                    r => queue.GetDirectory(r),
                    () => new OcrSettings(),
                    NullLogger<OcrIndexingService>.Instance,
                    dispatcher);
                service.BeforeCacheOcrForTest = () =>
                    cacheRanOnOwnerDispatcher = dispatcher.CheckAccess();
                indexing = service.IndexMissingAsync();
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                threadFailure = ex;
                ready.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "OCR dispatcher setup timed out.");
            if (threadFailure is not null)
            {
                throw threadFailure;
            }

            Assert.NotNull(dispatcher);
            Assert.NotNull(persistence);
            Assert.NotNull(record);
            Assert.NotNull(delayed);
            Assert.NotNull(service);
            Assert.NotNull(indexing);
            await delayed!.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            dispatcher!.Invoke(() => persistence!.Finalize(
                record!,
                SolidBitmap(42, 26),
                AnnotationDocument.CreateFor(42, 26),
                new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>()));
            Assert.Equal(1, record!.ContentRevision);

            delayed.Complete("stale words from revision zero");
            OcrIndexingOutcome outcome = await indexing!.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(OcrIndexingOutcome.Completed, outcome);
            Assert.True(cacheRanOnOwnerDispatcher);
            Assert.Null(record.OcrText);
            Assert.Null(record.OcrLanguage);
            Assert.Equal(1, service!.Coverage.Missing);
        }
        finally
        {
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "OCR dispatcher did not shut down.");
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }

        if (threadFailure is not null)
        {
            throw threadFailure;
        }
    }
}
