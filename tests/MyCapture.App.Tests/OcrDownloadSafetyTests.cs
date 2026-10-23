using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Windows.Networking.Connectivity;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrDownloadSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task File_RejectsOversizeContentLengthOrUnknownLengthStream(bool knownLength)
    {
        using var fixture = new Fixture();
        var file = new OcrModelFile("bounded.bin", "https://example.test/file", null, 1, MaxBytes: 8);
        fixture.Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(new byte[9]) : new UnknownLengthContent(new byte[9]),
        };
        OcrModelStore store = fixture.CreateStore();
        await Assert.ThrowsAsync<IOException>(() => store.EnsureFileAsync(file, CancellationToken.None));
        Assert.False(File.Exists(store.PathFor(file)));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot, "*.download"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task File_RejectsWrongHashOrTruncationAndCleansTemporaryFile(bool wrongHash)
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent([1, 2, 3, 4]),
        };
        var file = new OcrModelFile("invalid.bin", "https://example.test/file", new string('0', 64), 1,
            ExpectedBytes: wrongHash ? 4 : 5);
        OcrModelStore store = fixture.CreateStore();
        if (wrongHash)
            await Assert.ThrowsAsync<CryptographicException>(() => store.EnsureFileAsync(file, CancellationToken.None));
        else
            await Assert.ThrowsAsync<IOException>(() => store.EnsureFileAsync(file, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot));
    }

    [Theory]
    [InlineData(NetworkCostType.Unrestricted, false, false, false, false)]
    [InlineData(NetworkCostType.Fixed, false, false, false, true)]
    [InlineData(NetworkCostType.Variable, false, false, false, true)]
    [InlineData(NetworkCostType.Unknown, false, false, false, true)]
    [InlineData(NetworkCostType.Unrestricted, true, false, false, true)]
    [InlineData(NetworkCostType.Unrestricted, false, true, false, true)]
    [InlineData(NetworkCostType.Unrestricted, false, false, true, true)]
    public void Environment_DefersCostlyOrBatterySaverBackgroundTransfers(NetworkCostType cost,
        bool roaming, bool overLimit, bool batterySaver, bool expected) =>
        Assert.Equal(expected, WindowsOcrDownloadEnvironment.ShouldDefer(cost, roaming, overLimit, batterySaver));

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 1)]
    public async Task Coordinator_DefersBackgroundButExplicitActionsAlwaysAttemptDownload(
        bool background, bool defer, int expectedCalls)
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var environment = new FakeEnvironment(defer);
        var coordinator = new OcrModelDownloadCoordinator(fixture.CreateStore(), environment);
        Assert.False(await coordinator.EnsureAsync(OcrQuality.Accurate, background, CancellationToken.None));
        Assert.Equal(expectedCalls, fixture.Handler.Calls);
        Assert.Equal(background ? 1 : 0, environment.Calls);
    }

    [Fact]
    public async Task Coordinator_RetriesOnLaterStartupAndSkipsWindowsOnlyQualities()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var environment = new FakeEnvironment(true);
        var coordinator = new OcrModelDownloadCoordinator(fixture.CreateStore(), environment);
        Assert.True(await coordinator.EnsureAsync(OcrQuality.Fast, true, CancellationToken.None));
        Assert.True(await coordinator.EnsureAsync(OcrQuality.Balanced, false, CancellationToken.None));
        Assert.False(await coordinator.EnsureAsync(OcrQuality.Accurate, true, CancellationToken.None));
        Assert.Equal(0, fixture.Handler.Calls);
        environment.Defer = false;
        Assert.False(await coordinator.EnsureAsync(OcrQuality.Accurate, true, CancellationToken.None));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(OcrQuality.Fast, true)]
    [InlineData(OcrQuality.Fast, false)]
    [InlineData(OcrQuality.Balanced, true)]
    [InlineData(OcrQuality.Balanced, false)]
    [InlineData(OcrQuality.Accurate, true)]
    [InlineData(OcrQuality.Accurate, false)]
    [InlineData(OcrQuality.Enhanced, true)]
    [InlineData(OcrQuality.Enhanced, false)]
    public async Task Coordinator_AllQualitiesNeverDownloadSuperResolution(OcrQuality quality, bool background)
    {
        using var fixture = new Fixture();
        var environment = new FakeEnvironment(false);
        OcrModelStore store = fixture.CreateCatalogStore();
        var coordinator = new OcrModelDownloadCoordinator(store, environment);
        bool usesNeural = OcrQualityProfile.UseNeuralModel(quality);

        // With no installed models the rejecting handler sees only the first PP-OCR file.
        Assert.Equal(!usesNeural, await coordinator.EnsureAsync(quality, background, CancellationToken.None));
        Assert.Equal(usesNeural ? 1 : 0, fixture.Handler.Calls);
        Assert.All(fixture.Handler.Requests, uri => Assert.Equal(OcrModelCatalog.Recognition.Uri, uri));

        fixture.WriteCatalogModels();
        fixture.WriteLegacyFiles();
        int requests = fixture.Handler.Calls;
        Assert.True(await coordinator.EnsureAsync(quality, background, CancellationToken.None));
        Assert.Equal(requests, fixture.Handler.Calls);
        fixture.AssertLegacyFilesUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeuralEngine_LegacyRequestFlagsNeverDownloadSuperResolution(bool modelsInstalled)
    {
        using var fixture = new Fixture();
        if (modelsInstalled) fixture.WriteCatalogModels();
        fixture.WriteLegacyFiles();
        var runtime = new OnnxRuntimeInitialization(() =>
            throw new InvalidOperationException("Stop before a native session is created."));
        using var engine = new NeuralOcrEngine(fixture.CreateCatalogStore(),
            NullLogger<NeuralOcrEngine>.Instance, runtime);
        await engine.ModelVerificationTask;

        OcrResult result = await engine.RecognizeAsync(OcrRequest.FromBytes([1], useNeuralModel: true,
            localCorrection: true, useSuperResolution: true), CancellationToken.None);

        Assert.Equal(OcrStatus.Unavailable, result.Status);
        Assert.Equal(modelsInstalled ? 0 : 1, fixture.Handler.Calls);
        Assert.All(fixture.Handler.Requests, uri => Assert.Equal(OcrModelCatalog.Recognition.Uri, uri));
        fixture.AssertLegacyFilesUnchanged();
    }

    [Fact]
    public async Task Telemetry_IsDisabledOnceAcrossConcurrentInitialization()
    {
        int calls = 0;
        var runtime = new OnnxRuntimeInitialization(() => Interlocked.Increment(ref calls));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(runtime.EnsureInitialized)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NeuralEngine_CallsTelemetryGuardBeforeCreatingSession()
    {
        using var fixture = new Fixture();
        foreach (OcrModelFile file in OcrModelCatalog.Required)
            File.WriteAllBytes(Path.Combine(fixture.Paths.OcrModelsRoot, file.FileName), new byte[file.MinimumBytes]);
        int calls = 0;
        using var http = new HttpClient(fixture.Handler);
        var store = new OcrModelStore(fixture.Paths, http, NullLogger.Instance, stream =>
            Convert.FromHexString(OcrModelCatalog.Required.Single(file =>
                file.FileName == Path.GetFileName(((FileStream)stream).Name)).ExpectedSha256!));
        var runtime = new OnnxRuntimeInitialization(() =>
        {
            calls++;
            throw new InvalidOperationException("Stop before a native session is created.");
        });
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance, runtime);
        await engine.ModelVerificationTask;
        Assert.False(engine.TryInitialize());
        Assert.False(engine.TryInitialize());
        Assert.False(engine.IsReady);
        Assert.Equal(1, calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public void AvailabilityCache_ReusesHashButStillEnforcesChangedLimitAndContent()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture.Handler);
        int hashes = 0;
        var store = new OcrModelStore(fixture.Paths, http, NullLogger.Instance, stream =>
        {
            hashes++;
            return SHA256.HashData(stream);
        });
        string path = store.PathFor(fixture.Model);
        File.WriteAllBytes(path, fixture.ModelBytes);
        Assert.True(store.IsPresent(fixture.Model));
        Assert.True(store.IsPresent(fixture.Model));
        Assert.Equal(1, hashes);
        Assert.False(store.IsPresent(fixture.Model with { MaxBytes = 3 }));
        File.WriteAllBytes(path, [4, 3, 2, 1]);
        Assert.False(store.IsPresent(fixture.Model));
        Assert.Equal(2, hashes);
    }

    private sealed class FakeEnvironment(bool defer) : IOcrDownloadEnvironment
    {
        internal bool Defer { get; set; } = defer;
        internal int Calls { get; private set; }
        public bool ShouldDeferBackgroundDownload() { Calls++; return Defer; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("ocr-safe-");
        private readonly HttpClient _http;
        internal byte[] ModelBytes { get; } = [1, 2, 3, 4];
        internal AppPaths Paths { get; }
        internal Handler Handler { get; } = new();
        internal OcrModelFile Model { get; }

        internal Fixture()
        {
            Paths = AppPaths.CreateForRoot(_root);
            Directory.CreateDirectory(Paths.OcrModelsRoot);
            _http = new HttpClient(Handler);
            Model = new OcrModelFile("model.bin", "https://example.test/model",
                Convert.ToHexString(SHA256.HashData(ModelBytes)), 1, ModelBytes.Length);
        }

        internal OcrModelStore CreateStore() => new(Paths, _http, NullLogger.Instance, SHA256.HashData);

        internal OcrModelStore CreateCatalogStore() => new(Paths, _http, NullLogger.Instance, stream =>
            Convert.FromHexString(OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional).Single(file =>
                file.FileName == Path.GetFileName(((FileStream)stream).Name)).ExpectedSha256!));

        internal void WriteCatalogModels()
        {
            foreach (OcrModelFile file in OcrModelCatalog.Required)
                File.WriteAllBytes(Path.Combine(Paths.OcrModelsRoot, file.FileName), new byte[file.MinimumBytes]);
        }

        internal void WriteLegacyFiles()
        {
            foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
                File.WriteAllBytes(Path.Combine(Paths.OcrModelsRoot, name), ModelBytes);
        }

        internal void AssertLegacyFilesUnchanged()
        {
            foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
                Assert.Equal(ModelBytes, File.ReadAllBytes(Path.Combine(Paths.OcrModelsRoot, name)));
        }

        public void Dispose()
        {
            _http.Dispose();
            // Retain synthetic fixtures for the host: its cleanup policy requires a
            // recoverable trash operation, which the legacy test helper does not provide.
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal Func<HttpResponseMessage> Respond { get; set; } = () => new(HttpStatusCode.ServiceUnavailable);
        internal int Calls { get; private set; }
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Respond());
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }
}
