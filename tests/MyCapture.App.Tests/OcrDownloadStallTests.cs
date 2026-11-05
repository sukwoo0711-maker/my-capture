using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrDownloadStallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderOrBodyStallEndsAsNetworkFailureAndKeepsCompletedFiles(bool duringBody)
    {
        using var fixture = new Fixture();
        var content = new ScriptedContent(fixture.Payload, blockAfterPrefix: true);
        fixture.Server.Respond = async token =>
        {
            if (!duringBody) await Task.Delay(Timeout.Infinite, token);
            return Ok(content);
        };
        OcrModelStore store = fixture.Store(TimeSpan.FromMilliseconds(200));
        OcrModelFile completed = fixture.SeedCompleted(store);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Download(store));

        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(fixture.Lifetime.IsCancellationRequested, "The caller watchdog must not end the stall test.");
        Assert.Equal(duringBody, content.Waiting.Task.IsCompleted);
        fixture.AssertNoPartialOrTarget(store);
        Assert.True(store.IsPresent(completed));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(completed)));
    }

    [Fact]
    public async Task CallerCancellationDuringBodyRemainsCancellationAndKeepsCompletedFiles()
    {
        using var fixture = new Fixture();
        var content = new ScriptedContent(fixture.Payload, blockAfterPrefix: true);
        fixture.Server.Respond = _ => Task.FromResult(Ok(content));
        OcrModelStore store = fixture.Store(TimeSpan.FromSeconds(5));
        OcrModelFile completed = fixture.SeedCompleted(store);
        Task download = fixture.Download(store);

        await content.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(store.PathFor(fixture.Model) + ".download"));
        fixture.Lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        fixture.AssertNoPartialOrTarget(store);
        Assert.True(store.IsPresent(completed));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(completed)));
    }

    [Fact]
    public async Task SteadyBodyCanOutlastTheStallTimeoutAndStillRequiresExactBytesAndHash()
    {
        using var fixture = new Fixture();
        // Ten delayed chunks take at least one second; the 600 ms deadline is per wait,
        // rather than an overall deadline. This is the same cadence as translation tests.
        fixture.Server.Respond = _ => Task.FromResult(Ok(new ScriptedContent(fixture.Payload,
            chunkSize: fixture.Payload.Length / 10, readDelay: TimeSpan.FromMilliseconds(100))));
        OcrModelStore store = fixture.Store(TimeSpan.FromMilliseconds(600));
        var clock = Stopwatch.StartNew();

        await fixture.Download(store);

        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(600));
        Assert.False(fixture.Lifetime.IsCancellationRequested);
        Assert.True(store.IsPresent(fixture.Model));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(fixture.Model)));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot, "*.download"));
    }

    [Fact]
    public async Task SlowLocalProgressBetweenChunksDoesNotConsumeNetworkStallTimeout()
    {
        using var fixture = new Fixture();
        TimeSpan stallTimeout = TimeSpan.FromMilliseconds(600);
        fixture.Server.Respond = _ => Task.FromResult(Ok(new ScriptedContent(fixture.Payload,
            chunkSize: 400, readDelay: TimeSpan.FromMilliseconds(100))));
        OcrModelStore store = fixture.Store(stallTimeout);
        OcrModelFile completed = fixture.SeedCompleted(store);
        int delays = 0;
        TimeSpan localElapsed = TimeSpan.Zero;
        var received = new List<long>();

        await fixture.DownloadWithProgress(store, bytes =>
        {
            received.Add(bytes);
            if (delays != 0 || bytes <= 0 || bytes >= fixture.Payload.Length) return;
            delays++;
            long started = Stopwatch.GetTimestamp();
            fixture.Lifetime.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(1_200));
            fixture.Lifetime.Token.ThrowIfCancellationRequested();
            localElapsed = Stopwatch.GetElapsedTime(started);
        });

        Assert.Equal(1, delays);
        Assert.True(localElapsed > stallTimeout, $"Local callback lasted {localElapsed.TotalMilliseconds:F1} ms.");
        Assert.False(fixture.Lifetime.IsCancellationRequested);
        Assert.Equal(10, received.Count);
        Assert.Equal(fixture.Payload.Length, received[^1]);
        Assert.True(received.Zip(received.Skip(1), (previous, next) => next > previous).All(value => value));
        Assert.True(store.IsPresent(fixture.Model));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(fixture.Model)));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(completed)));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot, "*.download"));
        await fixture.Download(store);
        Assert.Equal(1, fixture.Server.Requests);
    }

    [Fact]
    public async Task SuccessfulRetryAfterStallUsesTheSameStoreAndReleasesItsGate()
    {
        using var fixture = new Fixture();
        fixture.Server.Respond = _ => Task.FromResult(Ok(new ScriptedContent(fixture.Payload, blockAfterPrefix: true)));
        OcrModelStore store = fixture.Store(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Download(store));
        fixture.AssertNoPartialOrTarget(store);
        fixture.Server.Respond = _ => Task.FromResult(Ok(new ByteArrayContent(fixture.Payload)));

        await fixture.Download(store);

        Assert.Equal(2, fixture.Server.Requests);
        Assert.True(store.IsPresent(fixture.Model));
        Assert.Equal(fixture.Payload, File.ReadAllBytes(store.PathFor(fixture.Model)));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot, "*.download"));
    }

    [Fact]
    public async Task RequiredModelStallReturnsPreparationFailureInsteadOfCallerCancellation()
    {
        using var fixture = new Fixture();
        fixture.Server.ExpectedUri = OcrModelCatalog.Recognition.Uri;
        fixture.Server.Respond = _ => Task.FromResult(Ok(new ScriptedContent(fixture.Payload, blockAfterPrefix: true)));
        OcrModelStore store = fixture.Store(TimeSpan.FromMilliseconds(200));

        bool prepared = await fixture.Track(store.EnsureAsync(fixture.Lifetime.Token));

        Assert.False(prepared);
        Assert.False(fixture.Lifetime.IsCancellationRequested);
        Assert.Equal(1, fixture.Server.Requests);
        Assert.False(File.Exists(store.PathFor(OcrModelCatalog.Recognition)));
        Assert.Empty(Directory.GetFiles(fixture.Paths.OcrModelsRoot, "*.download"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StallTimeoutMustBePositive(int milliseconds)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store(TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("ocr-download-stall-");
        private readonly HttpClient _http;
        private readonly List<Task> _downloads = [];
        internal readonly CancellationTokenSource Lifetime = new(TimeSpan.FromSeconds(10));
        internal byte[] Payload { get; } = Enumerable.Range(0, 4_000).Select(value => (byte)value).ToArray();
        internal AppPaths Paths { get; }
        internal Server Server { get; } = new();
        internal OcrModelFile Model { get; }

        internal Fixture()
        {
            Paths = AppPaths.CreateForRoot(_root);
            Paths.EnsureCreated();
            _http = new HttpClient(Server) { Timeout = Timeout.InfiniteTimeSpan };
            Model = new("stall.bin", "https://example.test/stall.bin", Convert.ToHexString(SHA256.HashData(Payload)),
                MinimumBytes: 1, ExpectedBytes: Payload.Length);
        }

        internal OcrModelStore Store(TimeSpan timeout) =>
            new(Paths, _http, NullLogger.Instance, SHA256.HashData, timeout);

        internal Task Download(OcrModelStore store)
        {
            Task task = store.EnsureFileAsync(Model, Lifetime.Token);
            _downloads.Add(task);
            return task;
        }

        internal Task DownloadWithProgress(OcrModelStore store, Action<long> progress)
        {
            // Exercise the same transfer used by EnsureAsync without downloading the real
            // catalog. EnsureFileAsync intentionally has no public progress parameter.
            System.Reflection.MethodInfo method = typeof(OcrModelStore).GetMethod("DownloadAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.NotNull(method);
            Task task = Assert.IsAssignableFrom<Task>(method.Invoke(store, [Model, Lifetime.Token, progress]));
            _downloads.Add(task);
            return task;
        }

        internal Task<T> Track<T>(Task<T> task)
        {
            _downloads.Add(task);
            return task;
        }

        internal OcrModelFile SeedCompleted(OcrModelStore store)
        {
            Directory.CreateDirectory(Paths.OcrModelsRoot);
            OcrModelFile completed = Model with { FileName = "completed.bin", Uri = "https://example.test/completed.bin" };
            File.WriteAllBytes(store.PathFor(completed), Payload);
            Assert.True(store.IsPresent(completed));
            return completed;
        }

        internal void AssertNoPartialOrTarget(OcrModelStore store)
        {
            Assert.False(File.Exists(store.PathFor(Model)));
            Assert.Empty(Directory.GetFiles(Paths.OcrModelsRoot, "*.download"));
        }

        public void Dispose()
        {
            Lifetime.Cancel();
            try
            {
                // Every owned download observes this token; finish it before recycling fixtures,
                // including when a preceding assertion or the progress wait failed.
                try { Task.WhenAll(_downloads).GetAwaiter().GetResult(); }
                catch (Exception) { /* The test body asserts the operation's actual result. */ }
            }
            finally
            {
                _http.Dispose();
                Lifetime.Dispose();
                OwnedTestDirectory.Delete(_root);
            }
        }
    }

    private sealed class Server : HttpMessageHandler
    {
        internal string ExpectedUri { get; set; } = "https://example.test/stall.bin";
        internal Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => throw new InvalidOperationException("A synthetic response must be configured.");
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(ExpectedUri, request.RequestUri!.AbsoluteUri);
            Requests++;
            return Respond(token);
        }
    }

    private sealed class ScriptedContent(byte[] payload, bool blockAfterPrefix = false,
        int chunkSize = 1_024, TimeSpan readDelay = default) : HttpContent
    {
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(
            new ScriptedStream(payload, blockAfterPrefix, chunkSize, readDelay, Waiting));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => CreateContentReadStreamAsync();
    }

    private sealed class ScriptedStream(byte[] payload, bool blockAfterPrefix, int chunkSize,
        TimeSpan readDelay, TaskCompletionSource waiting) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (blockAfterPrefix && _position != 0)
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (_position == payload.Length) return 0;
            if (readDelay > TimeSpan.Zero) await Task.Delay(readDelay, token);
            int count = Math.Min(buffer.Length, Math.Min(chunkSize, payload.Length - _position));
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
