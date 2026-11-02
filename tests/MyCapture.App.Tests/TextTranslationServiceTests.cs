using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TextTranslationServiceTests
{
    [Fact]
    public async Task TranslatesEachUniqueSegmentOnceInBatchesAndReportsProgressInOrder()
    {
        using var fixture = new ServiceFixture();
        string[] sources = [.. Enumerable.Range(1, 20).Select(index => $"Sentence number {index} is here."), "Sentence number 1 is here."];
        var reports = new List<TranslationProgress>();

        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(sources, new SyncProgress<TranslationProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal([20], fixture.Engine.Batches.Select(batch => batch.Count));
        Assert.Equal(sources.Select(source => "KO " + source), results.Select(result => result.Text));
        Assert.Equal(new TranslationProgress(0, 21), reports[0]);
        Assert.Equal(new TranslationProgress(21, 21), reports[^1]);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.Second.Completed > pair.First.Completed));

        // Switching views asks again; the cache answers without the model.
        await fixture.Service.TranslateAsync(sources, null, CancellationToken.None);
        Assert.Single(fixture.Engine.Batches);
        Assert.Equal(1, fixture.Loads);
    }

    [Fact]
    public async Task ABlockCountsOnceTheModelEndsItsLastSentenceWhileTheCallStillDecodes()
    {
        var log = new List<string>();
        using var fixture = new ServiceFixture(progressInterval: TimeSpan.Zero, load: () => new FakeEngine(source => "KO " + source)
        {
            // Batched decoding ends rows as their outputs end, not in reading order.
            EndOrder = EndsLastLetterFirst,
            Returning = () => log.Add("returned"),
        });

        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(SplitAndSingleSentences,
            new SyncProgress<TranslationProgress>(update => log.Add($"{update.Completed}/{update.Total}")), CancellationToken.None);

        // One call for four sentences of one length: the first block has two of them.
        Assert.Equal([4], fixture.Engine.Batches.Select(batch => batch.Count));
        Assert.Equal(3, results.Count);
        Assert.Equal("KO " + SplitAndSingleSentences[2], results[2].Text);
        // Delta and Charlie end the third and the second block before the first one is complete.
        Assert.Equal(["0/3", "1/3", "2/3", "3/3", "returned"], log);
    }

    [Fact]
    public async Task WhileACallDecodesCountsWaitForTheIntervalAndTheCallEndsWithItsCount()
    {
        var log = new List<string>();
        using var fixture = new ServiceFixture(progressInterval: TimeSpan.FromHours(1), load: () => new FakeEngine(source => "KO " + source)
        {
            EndOrder = EndsLastLetterFirst,
            Returning = () => log.Add("returned"),
        });

        await fixture.Service.TranslateAsync(SplitAndSingleSentences,
            new SyncProgress<TranslationProgress>(update => log.Add($"{update.Completed}/{update.Total}")), CancellationToken.None);

        // The first count shows at once; the next ones of the same call wait, and its end reports the total.
        Assert.Equal(["0/3", "1/3", "returned", "3/3"], log);
    }

    // A paragraph of two sentences that splits in two segments, then two blocks of one sentence; all of one length.
    private static readonly string[] SplitAndSingleSentences =
    [
        Sentence("Alpha") + " " + Sentence("Bravo"),
        Sentence("Charlie"),
        Sentence("Delta"),
    ];

    private static string Sentence(string first) => first + " " + string.Join(" ", Enumerable.Repeat("word", 32)) + ".";

    private static IEnumerable<int> EndsLastLetterFirst(IReadOnlyList<string> batch) =>
        Enumerable.Range(0, batch.Count).OrderByDescending(row => batch[row][0]);

    [Fact]
    public async Task FortyLineScreenTakesLittleTimeAroundTheModel()
    {
        using var fixture = new ServiceFixture();
        string[] screen = [.. TranslationLiveModelTests.ProbeTranslations().Take(40).Select(item => item.Text)];
        await fixture.Service.TranslateAsync(["Warm up the service."], null, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(screen, null, CancellationToken.None);
        watch.Stop();

        Assert.Equal(40, results.Count);
        Assert.Equal(40, fixture.Engine.Batches.Skip(1).Sum(batch => batch.Count));
        // Segmenting, repairing and checking 40 datasheet lines; the model itself takes about 0.8 s.
        Assert.True(watch.ElapsedMilliseconds < 250, $"{watch.ElapsedMilliseconds} ms around the model");
    }

    [Fact]
    public async Task AFewLongSentencesAmongShortLabelsGetACallOfTheirOwn()
    {
        using var fixture = new ServiceFixture();
        string[] labels = [.. Enumerable.Range(1, 20).Select(index => $"Label {index}")];
        string[] sentences = [.. Enumerable.Range(1, 2).Select(index => string.Join(" ",
            Enumerable.Repeat($"Sentence {index} keeps the decoder busy", 6)))];
        string[] sources = [labels[0], sentences[0], .. labels[1..], sentences[1]];

        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(sources, null, CancellationToken.None);

        Assert.Equal([2, 20], fixture.Engine.Batches.Select(batch => batch.Count));
        Assert.Equal(sentences, fixture.Engine.Batches[0]);
        Assert.Equal(sources.Select(source => "KO " + source), results.Select(result => result.Text));
    }

    [Fact]
    public void SimilarLengthsStayInOneCallInTheirOrder()
    {
        string[] segments = ["Save the file now", "Open a new window", "Close this dialog", "Print the page"];

        Assert.Equal(segments, Assert.Single(TextTranslationService.PlanBatches(segments, segment => segment.Length)));
    }

    [Fact]
    public void MoreSegmentsThanOneCallTakesSplitIntoCallsWithinTheBatchSize()
    {
        string[] segments = [.. Enumerable.Range(1, 90).Select(index => $"Line {index:D2} of the screen.")];

        List<List<string>> batches = TextTranslationService.PlanBatches(segments, segment => segment.Length);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, batch => Assert.InRange(batch.Count, 1, TextTranslationService.BatchSize));
        Assert.Equal(segments.Order(StringComparer.Ordinal), batches.SelectMany(batch => batch).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RepairsEachSegmentAndReportsWhatItCouldNotKeep()
    {
        using var fixture = new ServiceFixture(source => source switch
        {
            "Bit 3 OVR: This bit is cleared by writing 1 to the OVRCF bit." => "비트 3 OVR: 이 비트는 OCRCF 비트에 1을 써서 지웁니다.",
            "The BOR threshold is 2.7 V." => "임계값은 2.7V입니다.",
            _ => "KO " + source,
        });

        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(
            ["Bit 3 OVR: This bit is cleared by writing 1 to the OVRCF bit.", "The BOR threshold is 2.7 V."], null,
            CancellationToken.None);

        Assert.Equal("비트 3 OVR: 이 비트는 OVRCF 비트에 1을 써서 지웁니다.", results[0].Text);
        Assert.False(results[0].NeedsReview);
        Assert.Equal("임계값은 2.7 V입니다.", results[1].Text);
        Assert.Equal(["BOR"], results[1].Missing);
        Assert.Empty(results[1].Invented);
    }

    [Fact]
    public async Task LongBlockIsTranslatedInSegmentsAndJoined()
    {
        using var fixture = new ServiceFixture();
        string block = string.Join(" ", Enumerable.Range(1, 8).Select(index =>
            $"The counter {index} stops at the next update event and the CEN bit is cleared by hardware right away."));

        TranslatedText result = Assert.Single(await fixture.Service.TranslateAsync([block], null, CancellationToken.None));

        Assert.True(fixture.Engine.Batches.Single().Count > 1);
        Assert.All(fixture.Engine.Batches.Single(), segment => Assert.True(fixture.Engine.CountTokens(segment) <= TextTranslationService.MaxSegmentTokens));
        Assert.Equal(string.Join(" ", fixture.Engine.Batches.Single().Select(segment => "KO " + segment)), result.Text);
    }

    [Fact]
    public async Task BlankSourcesNeedNoModel()
    {
        using var fixture = new ServiceFixture();

        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(["", "  "], null, CancellationToken.None);

        Assert.All(results, result => Assert.Equal("", result.Text));
        Assert.Equal(0, fixture.Loads);
    }

    [Fact]
    public async Task ReleasesTheModelWhenIdleAndLoadsItAgainOnTheNextRequest()
    {
        using var fixture = new ServiceFixture(idleRelease: TimeSpan.FromMilliseconds(50));
        int released = 0;
        fixture.Service.EngineReleased += () => Interlocked.Increment(ref released);

        await fixture.Service.TranslateAsync(["First sentence to translate."], null, CancellationToken.None);
        FakeEngine first = fixture.Engine;
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(5)));
        Assert.False(fixture.Service.IsEngineLoaded);
        Assert.True(first.Disposed);

        await fixture.Service.TranslateAsync(["Second sentence to translate."], null, CancellationToken.None);
        Assert.Equal(2, fixture.Loads);
    }

    [Fact]
    public async Task ALoadFailureReachesTheCallerAndTheNextRequestTriesAgain()
    {
        int attempts = 0;
        using var fixture = new ServiceFixture(load: () =>
        {
            if (++attempts == 1) throw new InvalidDataException("broken model");
            return new FakeEngine(source => "KO " + source);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.TranslateAsync(["Translate this sentence."], null, CancellationToken.None));
        TranslatedText result = Assert.Single(await fixture.Service.TranslateAsync(["Translate this sentence."], null,
            CancellationToken.None));

        Assert.Equal("KO Translate this sentence.", result.Text);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancellationStopsBeforeTheNextBatch()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ServiceFixture(source =>
        {
            cancellation.Cancel();
            return "KO " + source;
        });
        string[] sources = [.. Enumerable.Range(1, 90).Select(index => $"Line {index} of the screen.")];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.TranslateAsync(sources, null, cancellation.Token));

        Assert.Single(fixture.Engine.Batches);
    }

    [Fact]
    public async Task DeletingTheModelReleasesItAndForgetsCachedTranslations()
    {
        using var fixture = new ServiceFixture();
        await fixture.Service.TranslateAsync(["Keep this sentence."], null, CancellationToken.None);
        FakeEngine first = fixture.Engine;

        await fixture.Service.DeleteModelAsync(CancellationToken.None);
        await fixture.Service.TranslateAsync(["Keep this sentence."], null, CancellationToken.None);

        Assert.True(first.Disposed);
        Assert.Equal(2, fixture.Loads);
        Assert.Single(fixture.Engine.Batches);
    }

    [Fact]
    public async Task WarmingUpLoadsAnInstalledModelOnceForTheTranslation()
    {
        using var fixture = new ServiceFixture(installed: true);

        await fixture.Service.WarmUpAsync(CancellationToken.None);
        Assert.True(fixture.Service.IsEngineLoaded);
        await fixture.Service.WarmUpAsync(CancellationToken.None);
        await fixture.Service.TranslateAsync(["Translate this sentence."], null, CancellationToken.None);

        Assert.Equal(1, fixture.Loads);
    }

    [Fact]
    public async Task WarmingUpNeverLoadsAMissingModel()
    {
        using var fixture = new ServiceFixture();

        await fixture.Service.WarmUpAsync(CancellationToken.None);

        Assert.False(fixture.Service.IsEngineLoaded);
        Assert.Equal(0, fixture.Loads);
    }

    [Fact]
    public async Task AFailedWarmUpIsLeftToTheTranslation()
    {
        int attempts = 0;
        using var fixture = new ServiceFixture(installed: true, load: () =>
        {
            if (++attempts == 1) throw new InvalidDataException("broken model");
            return new FakeEngine(source => "KO " + source);
        });

        await fixture.Service.WarmUpAsync(CancellationToken.None);
        Assert.False(fixture.Service.IsEngineLoaded);
        TranslatedText result = Assert.Single(await fixture.Service.TranslateAsync(["Translate this sentence."], null,
            CancellationToken.None));

        Assert.Equal("KO Translate this sentence.", result.Text);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AWarmedModelThatIsNotUsedLeavesAfterTheQuietTime()
    {
        using var fixture = new ServiceFixture(idleRelease: TimeSpan.FromMilliseconds(50), installed: true);

        await fixture.Service.WarmUpAsync(CancellationToken.None);
        FakeEngine warmed = fixture.Engine;

        Assert.True(SpinWait.SpinUntil(() => !fixture.Service.IsEngineLoaded, TimeSpan.FromSeconds(5)));
        Assert.True(warmed.Disposed);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("translate-service-");
        private readonly HttpClient _http = new(new RejectingHandler());

        internal ServiceFixture(Func<string, string>? map = null, TimeSpan? idleRelease = null, Func<FakeEngine>? load = null,
            bool installed = false, TimeSpan? progressInterval = null)
        {
            AppPaths paths = AppPaths.CreateForRoot(_root);
            TranslationModelFile[]? files = null;
            if (installed)
            {
                // One small file with a real pin stands in for an installed model.
                byte[] model = [1, 2, 3, 4];
                files = [new("model.bin", "https://example.test/models/model.bin", Convert.ToHexString(SHA256.HashData(model)), model.Length)];
                string directory = Path.Combine(paths.TranslationModelsRoot, TranslationModelCatalog.FolderName);
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, files[0].FileName), model);
            }
            var store = new TranslationModelStore(paths, _http, NullLogger.Instance, files);
            Service = new TextTranslationService(store, () =>
            {
                Loads++;
                Engine = load?.Invoke() ?? new FakeEngine(map ?? (source => "KO " + source));
                return Engine;
            }, NullLogger.Instance, idleRelease ?? TimeSpan.FromMinutes(5))
            {
                ProgressInterval = progressInterval ?? TimeSpan.FromMilliseconds(100),
            };
        }

        internal TextTranslationService Service { get; }

        internal FakeEngine Engine { get; private set; } = new(source => source);

        internal int Loads { get; private set; }

        public void Dispose()
        {
            Service.Dispose();
            _http.Dispose();
            OwnedTestDirectory.Delete(_root);
        }
    }

    internal sealed class FakeEngine(Func<string, string> map) : ISentenceTranslator
    {
        internal List<IReadOnlyList<string>> Batches { get; } = [];

        internal bool Disposed { get; private set; }

        /// <summary>The rows a call ends before it returns, in the order the model ends them; none by default.</summary>
        internal Func<IReadOnlyList<string>, IEnumerable<int>>? EndOrder { get; init; }

        /// <summary>Runs as a call returns, after the rows it ended.</summary>
        internal Action? Returning { get; init; }

        public int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;

        public IReadOnlyList<string> Translate(IReadOnlyList<string> sentences, CancellationToken cancellationToken)
        {
            Batches.Add([.. sentences]);
            return [.. sentences.Select(map)];
        }

        public IReadOnlyList<string> Translate(IReadOnlyList<string> sentences, Action<int>? sentenceDone,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<string> translated = Translate(sentences, cancellationToken);
            if (sentenceDone is not null && EndOrder is { } order)
                foreach (int row in order(sentences)) sentenceDone(row);
            Returning?.Invoke();
            return translated;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Reports synchronously so assertions see every update in order.</summary>
    internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
