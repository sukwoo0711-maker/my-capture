using System.IO;
using System.IO.Compression;
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
    [Fact]
    public async Task SuperResolution_DownloadsOneArchiveStoresOneWeightFileAndReusesIt()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore();
        Assert.All(await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.EnsureSuperResolutionAsync(CancellationToken.None))), Assert.True);
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.True(store.HasSuperResolution);
        Assert.Equal(fixture.ModelBytes, File.ReadAllBytes(store.PathFor(fixture.Model)));
        Assert.Equal(fixture.WeightBytes, File.ReadAllBytes(store.SuperResolutionExporterDataPath));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.OcrModelsRoot, OcrModelCatalog.SuperResolutionData.FileName)));
        Assert.Equal(2, Directory.GetFiles(fixture.Paths.OcrModelsRoot).Length);
    }

    [Fact]
    public async Task SuperResolution_AcceptsOldLayoutAndPreservesItsDuplicate()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore();
        File.WriteAllBytes(store.PathFor(fixture.Model), fixture.ModelBytes);
        File.WriteAllBytes(store.SuperResolutionExporterDataPath, fixture.WeightBytes);
        string legacy = Path.Combine(fixture.Paths.OcrModelsRoot, OcrModelCatalog.SuperResolutionData.FileName);
        File.WriteAllBytes(legacy, fixture.WeightBytes);
        Assert.True(store.HasSuperResolution);
        Assert.True(await store.EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(fixture.WeightBytes, File.ReadAllBytes(legacy));
    }

    [Fact]
    public async Task SuperResolution_RepairsMissingExporterFileWithoutTouchingLegacyCopy()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore();
        File.WriteAllBytes(store.PathFor(fixture.Model), fixture.ModelBytes);
        string legacy = Path.Combine(fixture.Paths.OcrModelsRoot, OcrModelCatalog.SuperResolutionData.FileName);
        File.WriteAllBytes(legacy, fixture.WeightBytes);
        Assert.False(store.HasSuperResolution);
        Assert.True(await store.EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Equal(fixture.WeightBytes, File.ReadAllBytes(legacy));
        Assert.Equal(fixture.WeightBytes, File.ReadAllBytes(store.SuperResolutionExporterDataPath));
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

    [Fact]
    public async Task Archive_RejectsOversizeContentLength()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = () =>
        {
            var content = new ByteArrayContent([1]);
            content.Headers.ContentLength = OcrModelCatalog.SuperResolutionArchiveBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        Assert.False(await fixture.CreateStore().EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot));
    }

    [Fact]
    public async Task Archive_RejectsOversizeEntryBeforeExtractingOrPublishing()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore(weights: fixture.Weights with { ExpectedBytes = 4 });
        Assert.False(await store.EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot));
    }

    [Fact]
    public async Task Archive_RejectsSecondMemberHashMismatchWithoutPublishingFirstMember()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore(weights: fixture.Weights with { ExpectedSha256 = new string('0', 64) });
        Assert.False(await store.EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot));
    }

    [Fact]
    public async Task Archive_MalformedZipIsNonFatalAndTemporaryFileIsCleaned()
    {
        using var fixture = new Fixture();
        fixture.Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        Assert.False(await fixture.CreateStore().EnsureSuperResolutionAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot));
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
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Coordinator_EnhancedExplicitActionAlsoDownloadsSuperResolution(bool background, int expectedCalls)
    {
        using var fixture = new Fixture();
        foreach (OcrModelFile file in OcrModelCatalog.Required)
            File.WriteAllBytes(Path.Combine(fixture.Paths.OcrModelsRoot, file.FileName), new byte[file.MinimumBytes]);
        using var http = new HttpClient(fixture.Handler);
        var store = new OcrModelStore(fixture.Paths, http, NullLogger.Instance, stream =>
        {
            OcrModelFile? required = OcrModelCatalog.Required.FirstOrDefault(file =>
                file.FileName == Path.GetFileName(((FileStream)stream).Name));
            return required is null ? SHA256.HashData(stream) : Convert.FromHexString(required.ExpectedSha256!);
        }, fixture.Model, fixture.Weights);
        var coordinator = new OcrModelDownloadCoordinator(store, new FakeEnvironment(false));
        Assert.True(await coordinator.EnsureAsync(OcrQuality.Enhanced, background, CancellationToken.None));
        Assert.Equal(expectedCalls, fixture.Handler.Calls);
        Assert.Equal(!background, store.HasSuperResolution);
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
    public void SuperResolution_CallsTelemetryGuardBeforeCreatingSession()
    {
        using var fixture = new Fixture();
        OcrModelStore store = fixture.CreateStore();
        File.WriteAllBytes(store.PathFor(fixture.Model), fixture.ModelBytes);
        File.WriteAllBytes(store.SuperResolutionExporterDataPath, fixture.WeightBytes);
        int calls = 0;
        var runtime = new OnnxRuntimeInitialization(() =>
        {
            calls++;
            throw new InvalidOperationException("Stop before a native session is created.");
        });
        using var engine = new SuperResolutionEngine(store, NullLogger<SuperResolutionEngine>.Instance, runtime);
        Assert.False(engine.TryInitialize());
        Assert.False(engine.TryInitialize());
        Assert.False(engine.IsReady);
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
        using var superResolution = new SuperResolutionEngine(store, NullLogger<SuperResolutionEngine>.Instance, runtime);
        using var engine = new NeuralOcrEngine(store, superResolution, NullLogger<NeuralOcrEngine>.Instance, runtime);
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
        }, fixture.Model, fixture.Weights);
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
        internal byte[] WeightBytes { get; } = [5, 6, 7, 8, 9, 10, 11, 12];
        internal AppPaths Paths { get; }
        internal Handler Handler { get; } = new();
        internal OcrModelFile Model { get; }
        internal OcrModelFile Weights { get; }

        internal Fixture()
        {
            Paths = AppPaths.CreateForRoot(_root);
            Directory.CreateDirectory(Paths.OcrModelsRoot);
            _http = new HttpClient(Handler);
            Model = ForBytes(OcrModelCatalog.SuperResolution, ModelBytes);
            Weights = ForBytes(OcrModelCatalog.SuperResolutionData, WeightBytes);
            byte[] archiveBytes;
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (Stream entry = archive.CreateEntry("nested/real_esrgan_x4plus.onnx").Open()) entry.Write(ModelBytes);
                    using (Stream entry = archive.CreateEntry("nested/real_esrgan_x4plus.data").Open()) entry.Write(WeightBytes);
                }
                archiveBytes = output.ToArray();
            }
            Handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(archiveBytes),
            };
        }

        internal OcrModelStore CreateStore(OcrModelFile? weights = null) =>
            new(Paths, _http, NullLogger.Instance, SHA256.HashData, Model, weights ?? Weights);

        private static OcrModelFile ForBytes(OcrModelFile file, byte[] bytes) => file with
        {
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)), MinimumBytes = 1, ExpectedBytes = bytes.Length,
        };

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
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
