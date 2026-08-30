using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
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
}
