using System.IO;
using System.Net;
using System.Net.Http;
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
    public async Task FortyLineScreenTakesOneModelCallAndLittleTimeAroundIt()
    {
        using var fixture = new ServiceFixture();
        string[] screen = [.. TranslationLiveModelTests.ProbeTranslations().Take(40).Select(item => item.Text)];
        await fixture.Service.TranslateAsync(["Warm up the service."], null, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<TranslatedText> results = await fixture.Service.TranslateAsync(screen, null, CancellationToken.None);
        watch.Stop();

        Assert.Equal(40, results.Count);
        Assert.Equal([1, 40], fixture.Engine.Batches.Select(batch => batch.Count));
        // Segmenting, repairing and checking 40 datasheet lines; the model itself takes about 0.8 s.
        Assert.True(watch.ElapsedMilliseconds < 250, $"{watch.ElapsedMilliseconds} ms around the model");
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

        await fixture.Service.TranslateAsync(["First sentence to translate."], null, CancellationToken.None);
        FakeEngine first = fixture.Engine;
        Assert.True(SpinWait.SpinUntil(() => !fixture.Service.IsEngineLoaded, TimeSpan.FromSeconds(5)));
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

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("translate-service-");
        private readonly HttpClient _http = new(new RejectingHandler());

        internal ServiceFixture(Func<string, string>? map = null, TimeSpan? idleRelease = null, Func<FakeEngine>? load = null)
        {
            var store = new TranslationModelStore(AppPaths.CreateForRoot(_root), _http, NullLogger.Instance);
            Service = new TextTranslationService(store, () =>
            {
                Loads++;
                Engine = load?.Invoke() ?? new FakeEngine(map ?? (source => "KO " + source));
                return Engine;
            }, NullLogger.Instance, idleRelease ?? TimeSpan.FromMinutes(5));
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

        public int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length + 1;

        public IReadOnlyList<string> Translate(IReadOnlyList<string> sentences, CancellationToken cancellationToken)
        {
            Batches.Add([.. sentences]);
            return [.. sentences.Select(map)];
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
