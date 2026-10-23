using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrModelPreparationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(OcrQuality.Fast)]
    [InlineData(OcrQuality.Balanced)]
    public async Task LeavingNeuralQuality_CancelsAndKeepsTokenUsableUntilWorkerFinishes(OcrQuality quality)
    {
        var preparation = new OcrModelPreparation();
        var started = Signal();
        var inspectAfterCancel = Signal();
        CancellationToken observed = default;
        Task worker = preparation.StartAsync(async token =>
        {
            observed = token;
            started.SetResult();
            await inspectAfterCancel.Task;
            Assert.True(token.IsCancellationRequested);
            // Cancellation is independent of disposal: a worker can still use the source's
            // wait handle and register cleanup while unwinding its download finally block.
            Assert.True(token.WaitHandle.WaitOne(0));
            using CancellationTokenRegistration registration = token.Register(() => { });
        });
        try
        {
            await started.Task.WaitAsync(Wait);
            preparation.CancelForQuality(quality);
            Assert.True(observed.IsCancellationRequested);
        }
        finally { inspectAfterCancel.TrySetResult(); }
        await worker.WaitAsync(Wait);
    }

    [Theory]
    [InlineData(OcrQuality.Accurate)]
    [InlineData(OcrQuality.Enhanced)]
    public async Task NeuralQuality_KeepsRunningPreparation(OcrQuality quality)
    {
        var preparation = new OcrModelPreparation();
        var started = Signal();
        var release = Signal();
        CancellationToken observed = default;
        Task worker = preparation.StartAsync(async token =>
        {
            observed = token;
            started.SetResult();
            await release.Task;
        });
        try
        {
            await started.Task.WaitAsync(Wait);
            preparation.CancelForQuality(quality);
            Assert.False(observed.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        await worker.WaitAsync(Wait);
        Assert.False(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task SecondStart_SharesTheRunningToken_AndCancelStopsBoth_ThenANewStartIsFresh()
    {
        var preparation = new OcrModelPreparation();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var release = Signal();
        CancellationToken first = default, second = default;
        Task firstWorker = preparation.StartAsync(async token =>
        {
            first = token;
            firstStarted.SetResult();
            await release.Task;
        });
        Task? secondWorker = null;
        try
        {
            await firstStarted.Task.WaitAsync(Wait);
            secondWorker = preparation.StartAsync(async token =>
            {
                second = token;
                secondStarted.SetResult();
                await release.Task;
                Assert.True(token.WaitHandle.WaitOne(0));
            });
            await secondStarted.Task.WaitAsync(Wait);
            // A second start (startup, then Settings) must not discard the first one's download.
            Assert.Equal(first, second);
            Assert.False(first.IsCancellationRequested);

            preparation.CancelForQuality(OcrQuality.Fast);
            Assert.True(first.IsCancellationRequested);
            Assert.True(second.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        await firstWorker.WaitAsync(Wait);
        await secondWorker!.WaitAsync(Wait);

        CancellationToken fresh = default;
        await preparation.StartAsync(token =>
        {
            fresh = token;
            return Task.CompletedTask;
        }).WaitAsync(Wait);
        Assert.NotEqual(first, fresh);
        Assert.False(fresh.IsCancellationRequested);
    }

    [Fact]
    public async Task Close_CancelsRunningPreparation_AndIgnoresLaterStarts()
    {
        var preparation = new OcrModelPreparation();
        var started = Signal();
        var release = Signal();
        CancellationToken observed = default;
        Task worker = preparation.StartAsync(async token =>
        {
            observed = token;
            started.SetResult();
            await release.Task;
        });
        try
        {
            await started.Task.WaitAsync(Wait);
            preparation.Close(); // The same seam used by OnExit.
            Assert.True(observed.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        await worker.WaitAsync(Wait);

        int laterRuns = 0;
        await preparation.StartAsync(_ =>
        {
            laterRuns++;
            return Task.CompletedTask;
        }).WaitAsync(Wait);
        Assert.Equal(0, laterRuns);
    }

    [Fact]
    public async Task CancelBeforeWorkerUsesToken_DoesNotDisposeIt()
    {
        var preparation = new OcrModelPreparation();
        var release = Signal();
        Task worker = preparation.StartAsync(async token =>
        {
            await release.Task;
            Assert.True(token.IsCancellationRequested);
            Assert.True(token.WaitHandle.WaitOne(0));
        });
        preparation.Cancel();
        release.SetResult();
        await worker.WaitAsync(Wait);
    }

    [Fact]
    public async Task LeavingAccurate_StopsDownload_KeepsCompletedModelAndRemovesPartialFile()
    {
        string root = OwnedTestDirectory.Create("ocr-prepare-cancel-");
        var content = new StalledModelContent();
        using var http = new HttpClient(new StalledHandler(content));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            Directory.CreateDirectory(paths.OcrModelsRoot);
            OcrModelFile completed = OcrModelCatalog.Recognition;
            string completedPath = Path.Combine(paths.OcrModelsRoot, completed.FileName);
            byte[] completedBytes = new byte[completed.MinimumBytes];
            completedBytes[0] = 7;
            File.WriteAllBytes(completedPath, completedBytes);
            // Synthetic weights: the hasher reports each catalog file's pinned hash.
            var store = new OcrModelStore(paths, http, NullLogger.Instance, stream =>
                Convert.FromHexString(OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional).Single(file =>
                    file.FileName == Path.GetFileName(((FileStream)stream).Name)).ExpectedSha256!));
            var coordinator = new OcrModelDownloadCoordinator(store, new NeverDefer());
            var preparation = new OcrModelPreparation();
            string partial = Path.Combine(paths.OcrModelsRoot, OcrModelCatalog.Dictionary.FileName + ".download");

            Task worker = preparation.StartAsync(token =>
                coordinator.EnsureAsync(OcrQuality.Accurate, isBackground: false, token));
            await content.Stalled.Task.WaitAsync(Wait);
            Assert.True(File.Exists(partial));

            preparation.CancelForQuality(OcrQuality.Fast);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(Wait));
            Assert.False(File.Exists(partial));
            Assert.Empty(Directory.GetFiles(paths.OcrModelsRoot, "*.download"));
            Assert.False(File.Exists(Path.Combine(paths.OcrModelsRoot, OcrModelCatalog.Dictionary.FileName)));
            Assert.Equal(completedBytes, File.ReadAllBytes(completedPath));
            Assert.Equal(1, content.Opened);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NeverDefer : IOcrDownloadEnvironment
    {
        public bool ShouldDeferBackgroundDownload() => false;
        public bool? IsBatterySaverOn => false;
    }

    private sealed class StalledHandler(StalledModelContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    /// <summary>Delivers one chunk, then waits for cancellation like a stalled transfer.</summary>
    private sealed class StalledModelContent : HttpContent
    {
        internal TaskCompletionSource Stalled { get; } = Signal();
        internal int Opened { get; private set; }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Opened++;
            return Task.FromResult<Stream>(new StalledStream(Stalled));
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("The download reads the response stream.");
    }

    private sealed class StalledStream(TaskCompletionSource stalled) : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                _delivered = true;
                buffer.Span[..16].Fill(1);
                return 16;
            }
            stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
