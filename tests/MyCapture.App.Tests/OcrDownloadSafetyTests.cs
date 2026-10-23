using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Windows.Networking.Connectivity;
using Windows.System.Power;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrDownloadSafetyTests
{
    [Theory]
    [InlineData(EnergySaverStatus.Disabled, false)]
    [InlineData(EnergySaverStatus.Off, false)]
    [InlineData(EnergySaverStatus.On, true)]
    [InlineData((EnergySaverStatus)999, null)]
    public void EnergySaverMapping_OnlyDefersWhenOnOrUnknown(EnergySaverStatus status, bool? expected) =>
        Assert.Equal(expected, WindowsOcrDownloadEnvironment.MapEnergySaverStatus(status));

    // The welcome path is explicit and rechecks battery saver; the startup path is a background
    // transfer and checks the connection as well. App.StartOcrModelDownload uses these arguments.
    [Theory]
    [InlineData(false, true, EnergySaverStatus.Disabled, 1)]
    [InlineData(true, false, EnergySaverStatus.Disabled, 1)]
    [InlineData(false, true, EnergySaverStatus.Off, 1)]
    [InlineData(true, false, EnergySaverStatus.Off, 1)]
    [InlineData(false, true, EnergySaverStatus.On, 0)]
    [InlineData(true, false, EnergySaverStatus.On, 0)]
    public async Task WelcomeAndStartup_PrepareUnlessBatterySaverIsOn(bool background, bool deferForBatterySaver,
        EnergySaverStatus status, int expectedRequests)
    {
        using var fixture = new Fixture();
        var environment = new WindowsOcrDownloadEnvironment(() => status,
            () => new ConnectionCostState(NetworkCostType.Unrestricted, Roaming: false, OverDataLimit: false));
        var coordinator = new OcrModelDownloadCoordinator(fixture.CreateStore(), environment);
        Assert.False(await coordinator.EnsureAsync(OcrQuality.Accurate, background,
            CancellationToken.None, deferForBatterySaver));
        // The synthetic server rejects the request; reaching it proves preparation was not deferred.
        Assert.Equal(expectedRequests, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WelcomeAndStartup_DeferWhenBatterySaverCannotBeRead(bool background, bool deferForBatterySaver)
    {
        using var fixture = new Fixture();
        var environment = new WindowsOcrDownloadEnvironment(
            () => throw new InvalidOperationException("Power status unavailable"),
            () => new ConnectionCostState(NetworkCostType.Unrestricted, Roaming: false, OverDataLimit: false));
        Assert.Null(environment.IsBatterySaverOn);
        var coordinator = new OcrModelDownloadCoordinator(fixture.CreateStore(), environment);
        Assert.False(await coordinator.EnsureAsync(OcrQuality.Accurate, background,
            CancellationToken.None, deferForBatterySaver));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(EnergySaverStatus.Disabled, NetworkCostType.Unrestricted, false)]
    [InlineData(EnergySaverStatus.Disabled, NetworkCostType.Fixed, true)]
    [InlineData(EnergySaverStatus.Disabled, NetworkCostType.Variable, true)]
    [InlineData(EnergySaverStatus.Off, NetworkCostType.Unrestricted, false)]
    [InlineData(EnergySaverStatus.On, NetworkCostType.Unrestricted, true)]
    public void Environment_DefersBackgroundOnlyForCostlyNetworkOrBatterySaver(EnergySaverStatus status,
        NetworkCostType cost, bool expected)
    {
        var environment = new WindowsOcrDownloadEnvironment(() => status,
            () => new ConnectionCostState(cost, Roaming: false, OverDataLimit: false));
        Assert.Equal(expected, environment.ShouldDeferBackgroundDownload());
    }

    [Fact]
    public void Environment_DefersBackgroundWithoutConnectionProfileOrWhenTheReadFails()
    {
        Assert.True(new WindowsOcrDownloadEnvironment(() => EnergySaverStatus.Disabled, () => null)
            .ShouldDeferBackgroundDownload());
        Assert.True(new WindowsOcrDownloadEnvironment(() => EnergySaverStatus.Disabled,
            () => throw new InvalidOperationException("Network status unavailable")).ShouldDeferBackgroundDownload());
    }

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
        fixture.AssertLegacyFilesRemoved();
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

        // The download coordinator never touches the retired super-resolution files.
        internal void AssertLegacyFilesUnchanged()
        {
            foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
                Assert.Equal(ModelBytes, File.ReadAllBytes(Path.Combine(Paths.OcrModelsRoot, name)));
        }

        // Background verification removes the retired super-resolution files; nothing reads them.
        internal void AssertLegacyFilesRemoved()
        {
            foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
                Assert.False(File.Exists(Path.Combine(Paths.OcrModelsRoot, name)));
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
