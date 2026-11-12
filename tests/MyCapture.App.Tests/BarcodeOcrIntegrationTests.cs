using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Privacy;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;
using ZXing;

namespace MyCapture.App.Tests;

public sealed class BarcodeOcrIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealDecoder_MergesInReadingOrderAndRemovesOnlyOverlappingWords()
    {
        BitmapSource symbol = BarcodeFixture.Symbol(BarcodeFormat.QR_CODE, "exact payload", 4);
        BitmapSource image = BarcodeFixture.Canvas(600, 450, (symbol, 100, 130));
        OcrLine first = Line("before", 20, 20, 90, 20), last = Line("after", 20, 390, 80, 20);
        OcrWord outside = new("outside", new(360, 150, 80, 20));
        OcrWord noise = new("fake OCR glyphs", new(140, 160, 30, 20));
        OcrResult source = Result(first, new("fake OCR glyphs outside", OcrRect.Union(noise.Bounds, outside.Bounds), [noise, outside]), last);
        source = source.WithNeuralLines(source.Lines);
        var fake = new FixedOcr(source);
        var service = new CodeOcrService(fake);
        OcrResult result = await service.RecognizeAsync(OcrRequest.FromBitmap(image));
        Assert.Same(image, fake.LastRequest!.Bitmap);
        Assert.Equal(new[] { "before", "exact payload", "outside", "after" }, result.Lines.Select(line => line.Text));
        OcrLine code = Assert.Single(result.Lines, line => line.IsCodePayload);
        Assert.Equal(code.Bounds, Assert.Single(code.Words).Bounds);
        Assert.Equal(code.Text, code.Words[0].Text);
        Assert.InRange(code.Bounds.X, 114, 118); // four-module quiet zone is outside the symbol box
        Assert.Same(first, result.Lines[0]); Assert.Same(last, result.Lines[^1]);
        Assert.DoesNotContain("fake OCR", result.Text);
        Assert.Equal(new[] { "before", "outside", "after" }, result.NeuralLines.Select(line => line.Text));
    }

    [Fact]
    public async Task NoCode_ReturnsSameResultAndLineList()
    {
        var expected = Result(Line("unchanged text", 5, 5, 100, 20));
        var service = new CodeOcrService(new FixedOcr(expected));
        OcrResult actual = await service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(500, 400)));
        Assert.Same(expected, actual); Assert.Same(expected.Lines, actual.Lines);
    }

    [Fact]
    public async Task Deadline_DeliversTextWhileDecoderIsStillRunningAndIgnoresLateResult()
    {
        var expected = Result(Line("text only", 1, 1, 50, 10));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CodeOcrService(new FixedOcr(expected), (_, _) =>
        {
            started.SetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.SetResult();
            return [new("too late", new(100, 100, 50, 50), true)];
        }, TimeSpan.FromMilliseconds(40));
        try
        {
            Task<OcrResult> pending = service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            OcrResult result = await pending.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(expected, result); Assert.False(finished.Task.IsCompleted);
            release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("text only", result.Text); Assert.Single(result.Lines);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ConcurrentRecognitions_EachRunOwnScanAndReceiveCodes()
    {
        var expected = Result(Line("text", 1, 1, 50, 10));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var service = new CodeOcrService(new FixedOcr(expected), (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) bothStarted.TrySetResult();
            // Each scan stays in flight until the other one has started as well.
            Assert.True(bothStarted.Task.Wait(TimeSpan.FromSeconds(5)), "The second scan never started.");
            return [new("shared payload", new(100, 50, 80, 80), true)];
        }, TimeSpan.FromSeconds(10));
        Task<OcrResult> first = service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)));
        Task<OcrResult> second = service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)));
        OcrResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.All(results, result => Assert.Equal("shared payload", Assert.Single(result.Lines, line => line.IsCodePayload).Text));
        Assert.False(service.HasOverdueScan);
    }

    [Fact]
    public async Task OverdueScan_BlocksNewScansOnlyUntilItReturns()
    {
        var expected = Result(Line("text only", 1, 1, 50, 10));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var service = new CodeOcrService(new FixedOcr(expected), (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                // Simulates ZXing math that ignores cancellation past the deadline.
                release.Wait(TimeSpan.FromSeconds(10));
                return [new("too late", new(100, 50, 80, 80), true)];
            }
            return [new("fresh payload", new(100, 50, 80, 80), true)];
        }, TimeSpan.FromMilliseconds(40));
        try
        {
            OcrResult timedOut = await service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(expected, timedOut);
            Assert.True(service.HasOverdueScan);

            OcrResult whileBlocked = await service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(expected, whileBlocked);
            Assert.Equal(1, Volatile.Read(ref calls));

            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !service.HasOverdueScan, TimeSpan.FromSeconds(5)), "The overdue scan never released the guard.");
            OcrResult resumed = await service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal("fresh payload", Assert.Single(resumed.Lines, line => line.IsCodePayload).Text);
            Assert.DoesNotContain(resumed.Lines, line => line.Text == "too late");
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DecoderAndRecognizer_RunConcurrentlyOffDispatcher()
    {
        var decodeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocrStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool decoderOnPool = false;
        var text = new FixedOcr(Result(Line("plain", 0, 0, 40, 10)), async () =>
        { ocrStarted.TrySetResult(); await decodeStarted.Task; });
        var service = new CodeOcrService(text, (_, _) =>
        {
            decoderOnPool = Thread.CurrentThread.IsThreadPoolThread;
            decodeStarted.TrySetResult();
            Assert.True(ocrStarted.Task.Wait(TimeSpan.FromSeconds(2)));
            return [];
        }, TimeSpan.FromSeconds(3));
        await service.RecognizeAsync(OcrRequest.FromBitmap(BarcodeFixture.Canvas(300, 200)));
        Assert.True(decoderOnPool);
    }

    [Fact]
    public async Task MultilinePayload_ExtractionAndBothTranslationPathsKeepExactText()
    {
        const string payload = "SeIect the exact j0bs\n\tsecond line  \n\nlast line";
        OcrResult result = CodeOcrResult.Merge(Result(Line("Save the document", 20, 20, 400, 25)),
            [new(payload, new(30, 100, 140, 140), true)]);
        Assert.Equal(5, result.Lines.Count);
        Assert.All(result.Lines.Skip(1), line => Assert.True(line.IsCodePayload));
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        Assert.Contains(payload, extraction.Text(joinLines: false));
        Assert.Contains(payload, extraction.Text(joinLines: true));
        Assert.Equal(payload, string.Join("\n", extraction.Table.Rows.SelectMany(row => row.Cells)
            .Where(cell => cell.IsCodePayload).Select(cell => cell.Text)));
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => "문서를 저장합니다.");
        var blocks = TranslationBlocks.Build(extraction);
        var translated = await TranslationBlocks.TranslateAsync(blocks, translator, null, CancellationToken.None);
        Assert.Equal(new[] { "Save the document" }, translator.Sources);
        Assert.Contains(translated, item => item.Text == payload);

        var pinService = new PinTranslationService(translator);
        var picture = PinContent.FromImage(BarcodeFixture.Canvas(650, 400));
        PinTranslationResult pin = Assert.IsType<PinTranslationResult>(await pinService.TranslateRecognizedAsync(result, picture, CancellationToken.None));
        Assert.DoesNotContain(translator.Sources, text => text.Contains("j0bs", StringComparison.Ordinal));
        Assert.Contains(payload, pin.Content.OriginalText);
        Assert.Contains(PinTranslationOverlay.BuildBlocks(result), block => block.PreservePixels && block.Text == payload);
    }

    [Fact]
    public void CachedWindow_WaitsForPayloadMarkersBeforeTranslating() => Ocr410Fixture.RunSta(() =>
    {
        const string payload = "Keep this payload exactly";
        OcrResult fresh = CodeOcrResult.Merge(Result(Line("Save the document", 20, 20, 200, 25)),
            [new(payload, new(30, 100, 140, 140), true)]);
        var translator = new PinTranslationOverlayTests.FakeTranslator(_ => "문서를 저장합니다.");
        var window = new OcrResultWindow { ShowInTaskbar = false, ShowActivated = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        int refreshes = 0;
        window.GeometryRequested += (_, _) => refreshes++;
        try
        {
            window.ShowResult(OcrResult.Success(fresh.Text, "en-US", [], TimeSpan.Zero), "cached");
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            Ocr410Fixture.Settle();
            Assert.Equal(1, refreshes);
            Assert.Empty(translator.Sources);
            window.ShowResult(fresh, "cached", preserveSelection: true);
            OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
            Assert.Equal(new[] { "Save the document" }, translator.Sources);
            Assert.Contains(window.TranslationPanel.Results, item => item.Translation.Text == payload);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void AccurateMergeAndLatinRecovery_PreservePayload()
    {
        const string payload = "SeIect the 오 word and j0bs";
        OcrResult codes = CodeOcrResult.Merge(OcrResult.NoText("en-US", TimeSpan.Zero),
            [new(payload, new(10, 10, 100, 100), true)]);
        OcrResult second = Result(Line("Select the to word and jobs", 10, 10, 100, 100));
        Assert.False(OcrLatinWordRecovery.HasSuspects(codes));
        Assert.Same(codes, OcrLatinWordRecovery.Recover(codes, second, TimeSpan.Zero));
        OcrResult merged = OcrResultMerger.Merge(codes, OcrResult.NoText("en-US", TimeSpan.Zero), TimeSpan.Zero);
        Assert.Equal(payload, Assert.Single(merged.Lines).Text);
        Assert.True(merged.Lines[0].IsCodePayload);
    }

    [Fact]
    public async Task PrivacyRedaction_EmailPayloadCoversWholeSymbol()
    {
        OcrRect bounds = new(40, 50, 100, 100);
        OcrResult codes = CodeOcrResult.Merge(OcrResult.NoText("en-US", TimeSpan.Zero),
            [new("person@example.com", bounds, true)]);
        var privacy = new PrivacyRedactionService(new FixedOcr(codes), new PrivacyDetector(), () => new OcrSettings());
        var result = await privacy.FindAsync(BarcodeFixture.Canvas(400, 300));
        Assert.Equal(PrivacyRedactionStatus.Success, result.Status);
        var region = Assert.Single(result.Regions);
        Assert.True(region.X <= bounds.X && region.Y <= bounds.Y && region.Width >= bounds.Width && region.Height >= bounds.Height);
    }

    [Fact]
    public void LibraryIndex_RealDecoderMakesPayloadSearchable() => Ocr410Fixture.RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("barcode-index-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root); paths.EnsureCreated();
            var settings = new QueueSettings();
            var queue = new CaptureQueue(paths, settings, NullLogger<CaptureQueue>.Instance);
            var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
            var record = persistence.PersistOriginal(BarcodeFixture.Symbol(BarcodeFormat.QR_CODE, "SEARCH-QR-20261110", 5),
                1.0, "synthetic", "synthetic");
            var service = new OcrIndexingService(gallery, new CodeOcrService(new FixedOcr(OcrResult.NoText("en-US", TimeSpan.Zero))),
                queue.GetDirectory, () => new OcrSettings(), NullLogger<OcrIndexingService>.Instance);
            Assert.Equal(OcrIndexingOutcome.Completed, service.IndexMissingAsync().GetAwaiter().GetResult());
            Assert.Contains("SEARCH-QR-20261110", record.OcrText);
            Assert.Single(CaptureTextSearch.Search(queue.Records, "SEARCH-QR-20261110"));
            output.WriteLine("Library payload persisted and found; cleanup follows.");
        }
        catch (Exception ex)
        {
            output.WriteLine("Library test body failed before cleanup: " + ex);
            throw;
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void ResultWindow_BeforeAndAfterSyntheticEvidence() => Ocr410Fixture.RunSta(() =>
    {
        using var language = UiText.UseLanguage("en");
        string directory = OwnedTestDirectory.Create("barcode-window-evidence-");
        BitmapSource image = BarcodeFixture.Screenshot(800, 500, 5);
        OcrResult before = Result(Line("Synthetic recognition sample", 35, 40, 390, 30),
            Line("Two lines of text around a local code", 35, 420, 480, 30));
        OcrResult after = new CodeOcrService(new FixedOcr(before)).RecognizeAsync(OcrRequest.FromBitmap(image)).GetAwaiter().GetResult();
        Assert.Contains(after.Lines, line => line.IsCodePayload);
        TestDiagnosticArtifacts.SavePng(directory, "source.png", image);
        foreach ((string name, OcrResult result) in new[] { ("before.png", before), ("after.png", after) })
        {
            var window = new OcrResultWindow { ShowInTaskbar = false, ShowActivated = false, Width = 1040, Height = 720 };
            try
            {
                Ocr410Fixture.LoadTheme(window);
                window.ShowResult(result, "Synthetic code recognition");
                typeof(OcrResultWindow).GetMethod("SetSourceImage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [image]);
                typeof(OcrResultWindow).GetMethod("SelectExtractionMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [OcrExtractionMode.Text]);
                window.UpdateLayout(); Ocr410Fixture.Settle();
                var screenshot = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                screenshot.Render(window);
                TestDiagnosticArtifacts.SavePng(directory, name, screenshot);
            }
            finally { window.CloseForExit(); }
        }
        // These synthetic images are retained for handoff, rather than recycled with ephemeral fixtures.
        output.WriteLine($"Synthetic result window evidence: {directory}");
    });

    private static OcrLine Line(string text, double x, double y, double width, double height)
    { OcrRect bounds = new(x, y, width, height); return new(text, bounds, [new(text, bounds)]); }
    private static OcrResult Result(params OcrLine[] lines) => OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)), "en-US", lines, TimeSpan.Zero);

    private sealed class FixedOcr(OcrResult result, Func<Task>? wait = null) : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        internal OcrRequest? LastRequest { get; private set; }
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        { LastRequest = request; if (wait is not null) await wait(); return result; }
    }
}
