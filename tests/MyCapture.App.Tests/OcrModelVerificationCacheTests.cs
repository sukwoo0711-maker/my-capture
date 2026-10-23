using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class OcrModelVerificationCacheTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void IsPresent_UnchangedVerifiedFileIsHashedOnce()
    {
        using var workspace = new ModelWorkspace();
        byte[] payload = Payload(4096);
        OcrModelFile file = workspace.WriteFile("verified.bin", payload);
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });

        Assert.True(store.IsPresent(file));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(store.IsPresent(file));
        }

        Assert.Equal(1, hashes);
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsPresent_ChangedLengthOrTimestampRechecksAndRejectsCorruption(bool changeLength)
    {
        using var workspace = new ModelWorkspace();
        byte[] original = Payload(4096);
        OcrModelFile file = workspace.WriteFile("changed.bin", original);
        string path = workspace.PathFor(file);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));

        byte[] corrupt = changeLength ? Payload(original.Length + 1) : (byte[])original.Clone();
        corrupt[0] ^= 0xff;
        File.WriteAllBytes(path, corrupt);
        File.SetLastWriteTimeUtc(path, changeLength ? timestamp : timestamp.AddMinutes(1));

        Assert.False(store.IsPresent(file));
        Assert.Equal(2, hashes);
        Assert.False(store.IsPresent(file));
        Assert.Equal(3, hashes);
    }

    [Fact]
    public void IsPresent_TimestampChangeRechecksEvenWhenBytesAreIdentical()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("touched.bin", Payload(4096));
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));
        string path = workspace.PathFor(file);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

        Assert.True(store.IsPresent(file));
        Assert.True(store.IsPresent(file));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public void IsPresent_SameMetadataAtAnotherPathDoesNotShareVerification()
    {
        using var workspace = new ModelWorkspace();
        byte[] payload = Payload(4096);
        OcrModelFile first = workspace.WriteFile("first.bin", payload);
        byte[] corrupt = (byte[])payload.Clone();
        corrupt[0] ^= 0xff;
        OcrModelFile second = workspace.WriteFile("second.bin", corrupt) with
        {
            ExpectedSha256 = first.ExpectedSha256,
        };
        File.SetLastWriteTimeUtc(workspace.PathFor(second), File.GetLastWriteTimeUtc(workspace.PathFor(first)));
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });

        Assert.True(store.IsPresent(first));
        Assert.False(store.IsPresent(second));
        Assert.True(store.IsPresent(first));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public void IsPresent_MissingFileInvalidatesPreviouslyVerifiedMetadata()
    {
        using var workspace = new ModelWorkspace();
        byte[] payload = Payload(4096);
        OcrModelFile file = workspace.WriteFile("missing.bin", payload);
        string path = workspace.PathFor(file);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));

        File.Move(path, path + ".previous");
        Assert.False(store.IsPresent(file));
        payload[0] ^= 0xff;
        File.WriteAllBytes(path, payload);
        File.SetLastWriteTimeUtc(path, timestamp);

        Assert.False(store.IsPresent(file));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public void IsPresent_CachedHashStillEnforcesExpectedHashAndMinimumLength()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("contract.bin", Payload(4096));
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));
        Assert.True(store.IsPresent(file with { ExpectedSha256 = file.ExpectedSha256!.ToLowerInvariant() }));
        Assert.Equal(1, hashes);

        Assert.False(store.IsPresent(file with { ExpectedSha256 = new string('0', 64) }));
        Assert.True(store.IsPresent(file));
        Assert.Equal(3, hashes);

        Assert.False(store.IsPresent(file with { MinimumBytes = 4097 }));
        Assert.True(store.IsPresent(file));
        Assert.Equal(4, hashes);
    }

    [Fact]
    public void IsPresent_UnpinnedCheckDoesNotMarkFileHashVerified()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("unpinned.bin", Payload(4096));
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });

        Assert.True(store.IsPresent(file with { ExpectedSha256 = null }));
        Assert.Equal(0, hashes);
        Assert.True(store.IsPresent(file));
        Assert.Equal(1, hashes);
    }

    [Fact]
    public void IsPresent_HashExceptionInvalidatesPreviousVerification()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("retry.bin", Payload(4096));
        string path = workspace.PathFor(file);
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(path);
        int hashes = 0;
        bool fail = false;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            if (fail)
            {
                throw new IOException("Injected hash read failure.");
            }
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));
        File.SetLastWriteTimeUtc(path, originalTimestamp.AddMinutes(1));
        fail = true;
        Assert.Throws<IOException>(() => store.IsPresent(file));

        File.SetLastWriteTimeUtc(path, originalTimestamp);
        fail = false;
        Assert.True(store.IsPresent(file));
        Assert.Equal(3, hashes);
    }

    [Fact]
    public void IsPresent_MetadataChangedDuringHashIsNotCachedAsVerified()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("changed-during-hash.bin", Payload(4096));
        string path = workspace.PathFor(file);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            byte[] hash = SHA256.HashData(stream);
            if (Interlocked.Increment(ref hashes) == 1)
            {
                // Release the read handle to simulate a writer between hashing and metadata recheck.
                stream.Dispose();
                File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
            }
            return hash;
        });

        Assert.False(store.IsPresent(file));
        Assert.True(store.IsPresent(file));
        Assert.True(store.IsPresent(file));
        Assert.Equal(2, hashes);
    }

    [Fact]
    public async Task IsPresent_ConcurrentChecksLeaveAnUnchangedFileVerified()
    {
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("concurrent.bin", Payload(4096));
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.IsPresent(file))));

        Assert.All(results, result => Assert.True(result));
        int verifiedHashes = hashes;
        Assert.Equal(1, verifiedHashes);
        Assert.True(store.IsPresent(file));
        Assert.Equal(verifiedHashes, hashes);
    }

    [Fact]
    public async Task EnsureFile_ReplacementInvalidatesOldVerificationRepopulatedDuringDownload()
    {
        using var workspace = new ModelWorkspace();
        byte[] original = Payload(4096);
        OcrModelFile oldFile = workspace.WriteFile("replacement.bin", original);
        byte[] replacement = (byte[])original.Clone();
        replacement[0] ^= 0xff;
        OcrModelFile newFile = oldFile with
        {
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(replacement)),
        };
        string path = workspace.PathFor(oldFile);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        using var handler = new ControlledDownloadHandler(replacement);
        using var http = new HttpClient(handler);
        int hashes = 0;
        var store = new OcrModelStore(workspace.Paths, http, NullLogger.Instance, stream =>
        {
            Interlocked.Increment(ref hashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(oldFile));

        Task preparation = store.EnsureFileAsync(newFile, CancellationToken.None);
        try
        {
            await handler.Requested.Task.WaitAsync(Timeout);
            Assert.True(store.IsPresent(oldFile));
            // The stale file is checked once before and once inside the download gate.
            Assert.Equal(4, hashes);
            handler.Release.TrySetResult();
            await preparation.WaitAsync(Timeout);
            File.SetLastWriteTimeUtc(path, timestamp);

            Assert.False(store.IsPresent(oldFile));
            Assert.Equal(5, hashes);
            Assert.True(store.IsPresent(newFile));
            Assert.Equal(6, hashes);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            handler.Release.TrySetResult();
            await preparation.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Constructor_PreverifiesAllInstalledModelsOffThreadWithoutBlockingOrDownloading()
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels();
        using var releaseHash = new ManualResetEventSlim();
        var hashEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashes = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            hashEntered.TrySetResult(Environment.CurrentManagedThreadId);
            if (!releaseHash.Wait(Timeout))
            {
                throw new TimeoutException("Test did not release model verification.");
            }
            string name = Path.GetFileName(((FileStream)stream).Name);
            hashes.AddOrUpdate(name, 1, (_, count) => count + 1);
            return SyntheticCatalogHash(stream);
        });
        NeuralOcrEngine? engine = null;
        Task<(int ThreadId, NeuralOcrEngine Value)> construction = OnDedicatedThread(
            () => new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance));
        try
        {
            (int constructingThread, NeuralOcrEngine constructed) = await construction.WaitAsync(Timeout);
            engine = constructed;
            int verifyingThread = await hashEntered.Task.WaitAsync(Timeout);

            Assert.NotEqual(constructingThread, verifyingThread);
            Assert.False(engine.ModelVerificationTask.IsCompleted);
            releaseHash.Set();
            await engine.ModelVerificationTask.WaitAsync(Timeout);

            Assert.True(engine.CanRun);
            Assert.True(store.HasRequiredModels);
            Assert.Equal(CatalogModels.Length, hashes.Count);
            Assert.All(CatalogModels, file => Assert.Equal(1, hashes[file.FileName]));
            Assert.Equal(0, workspace.Handler.Calls);
            Assert.False(engine.IsReady);
        }
        finally
        {
            releaseHash.Set();
            try
            {
                engine ??= (await construction.WaitAsync(Timeout)).Value;
                await engine.ModelVerificationTask.WaitAsync(Timeout);
            }
            finally
            {
                engine?.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("throws")]
    public async Task Preverification_FirstModelFailureDoesNotSkipOtherInstalledModels(string failure)
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels(omitRecognition: failure == "missing");
        var hashes = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            string name = Path.GetFileName(((FileStream)stream).Name);
            hashes.AddOrUpdate(name, 1, (_, count) => count + 1);
            if (name == OcrModelCatalog.Recognition.FileName)
            {
                if (failure == "throws")
                {
                    throw new IOException("Injected recognition read failure.");
                }
                return new byte[32];
            }
            return SyntheticCatalogHash(stream);
        });
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);

        await engine.ModelVerificationTask.WaitAsync(Timeout);

        Assert.True(engine.ModelVerificationTask.IsCompletedSuccessfully);
        foreach (OcrModelFile file in CatalogModels.Where(file => file != OcrModelCatalog.Recognition))
        {
            Assert.True(store.IsPresent(file));
            Assert.Equal(1, hashes[file.FileName]);
        }
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Fact]
    public async Task Preverification_ObservesVerificationAndLoggerFailures()
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels();
        var logger = new ThrowingLogger();
        int hashes = 0;
        OcrModelStore store = workspace.CreateStore(_ =>
        {
            Interlocked.Increment(ref hashes);
            throw new IOException("Injected model read failure.");
        }, logger);
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);

        await engine.ModelVerificationTask.WaitAsync(Timeout);

        Assert.True(engine.ModelVerificationTask.IsCompletedSuccessfully);
        Assert.Equal(CatalogModels.Length, hashes);
        Assert.Equal(CatalogModels.Length, logger.Calls);
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("file")]
    public async Task Ensure_VerificationRunsOffCallingThreadAndReturnsBeforeHashCompletes(string operation)
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels();
        using var releaseHash = new ManualResetEventSlim();
        var hashEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashThreads = new ConcurrentBag<int>();
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            int threadId = Environment.CurrentManagedThreadId;
            hashThreads.Add(threadId);
            hashEntered.TrySetResult(threadId);
            if (!releaseHash.Wait(Timeout))
            {
                throw new TimeoutException("Test did not release preparation verification.");
            }
            return SyntheticCatalogHash(stream);
        });
        Task<bool>? preparation = null;
        Task<(int ThreadId, Task<bool> Value)> invocation = OnDedicatedThread(
            () => Ensure(store, operation, CancellationToken.None));
        try
        {
            (int callingThread, Task<bool> returned) = await invocation.WaitAsync(Timeout);
            preparation = returned;
            int verifyingThread = await hashEntered.Task.WaitAsync(Timeout);

            Assert.NotEqual(callingThread, verifyingThread);
            Assert.False(preparation.IsCompleted);
            releaseHash.Set();
            Assert.True(await preparation.WaitAsync(Timeout));
            Assert.All(hashThreads, thread => Assert.NotEqual(callingThread, thread));
            Assert.Equal(0, workspace.Handler.Calls);
        }
        finally
        {
            releaseHash.Set();
            preparation ??= (await invocation.WaitAsync(Timeout)).Value;
            await preparation.WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData("required")]
    [InlineData("file")]
    public async Task Ensure_AlreadyPresentModelsStillSucceedWithAnAlreadyCanceledToken(string operation)
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels();
        OcrModelStore store = workspace.CreateStore(SyntheticCatalogHash);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(await Ensure(store, operation, cancellation.Token));
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Theory]
    [InlineData("present", true)]
    [InlineData("missing", false)]
    [InlineData("corrupt", false)]
    public async Task Availability_PreservesRequiredModelResultsBeforeAndAfterPreverification(
        string state,
        bool expected)
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels(omitRecognition: state == "missing");
        OcrModelStore store = workspace.CreateStore(stream =>
            state == "corrupt" && Path.GetFileName(((FileStream)stream).Name) == OcrModelCatalog.Recognition.FileName
                ? new byte[32]
                : SyntheticCatalogHash(stream));

        Assert.Equal(expected, store.HasRequiredModels);
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
        Assert.Equal(expected, engine.CanRun);
        await engine.ModelVerificationTask.WaitAsync(Timeout);
        Assert.Equal(expected, engine.CanRun);
        Assert.Equal(expected, store.HasRequiredModels);
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Fact]
    public async Task Preverification_RemovesRetiredSuperResolutionFilesWithoutHashingThem()
    {
        using var workspace = new ModelWorkspace();
        workspace.WriteSyntheticCatalogModels();
        byte[] legacyBytes = [1, 2, 3, 4];
        foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
            File.WriteAllBytes(Path.Combine(workspace.Paths.OcrModelsRoot, name), legacyBytes);
        var hashes = new ConcurrentBag<string>();
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            hashes.Add(Path.GetFileName(((FileStream)stream).Name));
            return SyntheticCatalogHash(stream);
        });
        using var engine = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
        await engine.ModelVerificationTask.WaitAsync(Timeout);

        Assert.Equal(CatalogModels.Length, hashes.Count);
        Assert.All(hashes, name => Assert.DoesNotContain(name, OcrModelCatalog.LegacySuperResolutionFiles));
        foreach (string name in OcrModelCatalog.LegacySuperResolutionFiles)
            Assert.False(File.Exists(Path.Combine(workspace.Paths.OcrModelsRoot, name)));
        Assert.True(store.HasRequiredModels);
        Assert.Equal(0, workspace.Handler.Calls);
    }

    [Fact]
    public void Measurement_RepeatedAvailabilityChecksAvoidReadingAndHashingModelPayload()
    {
        const int bytes = 16 * 1024 * 1024;
        const int iterations = 20;
        using var workspace = new ModelWorkspace();
        OcrModelFile file = workspace.WriteFile("measurement.bin", Payload(bytes));
        string path = workspace.PathFor(file);
        int baselineHashes = 0;
        var baseline = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var info = new FileInfo(path);
            Assert.True(info.Exists);
            Assert.True(info.Length >= file.MinimumBytes);
            using FileStream stream = File.OpenRead(path);
            Assert.Equal(file.ExpectedSha256, Convert.ToHexString(SHA256.HashData(stream)));
            baselineHashes++;
        }
        baseline.Stop();

        int cachedHashes = 0;
        OcrModelStore store = workspace.CreateStore(stream =>
        {
            Interlocked.Increment(ref cachedHashes);
            return SHA256.HashData(stream);
        });
        Assert.True(store.IsPresent(file));
        int initialHashes = cachedHashes;
        var cached = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            Assert.True(store.IsPresent(file));
        }
        cached.Stop();

        Assert.Equal(iterations, baselineHashes);
        Assert.Equal(1, initialHashes);
        Assert.Equal(0, cachedHashes - initialHashes);
        output.WriteLine(
            "16 MiB model, {0} checks: baseline={1:F3} ms, hashes={2}, bytes hashed={3}; warm cache={4:F3} ms, hashes={5}, bytes hashed=0.",
            iterations, baseline.Elapsed.TotalMilliseconds, baselineHashes, (long)bytes * iterations,
            cached.Elapsed.TotalMilliseconds, cachedHashes - initialHashes);
    }

    private static readonly OcrModelFile[] CatalogModels =
        [.. OcrModelCatalog.Required, .. OcrModelCatalog.Optional];

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31 + 7);
        }
        return bytes;
    }

    // Catalog fixtures exercise scheduling and availability without downloading model weights.
    // Real SHA-256 is used by the standalone file/cache tests and the measurement above.
    private static byte[] SyntheticCatalogHash(Stream stream)
    {
        string name = Path.GetFileName(((FileStream)stream).Name);
        return Convert.FromHexString(CatalogModels.Single(file => file.FileName == name).ExpectedSha256!);
    }

    private static async Task<bool> Ensure(OcrModelStore store, string operation, CancellationToken token)
    {
        switch (operation)
        {
            case "required":
                return await store.EnsureAsync(token);
            case "file":
                await store.EnsureFileAsync(OcrModelCatalog.Recognition, token);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static Task<(int ThreadId, T Value)> OnDedicatedThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<(int, T)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult((Environment.CurrentManagedThreadId, action()));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return completion.Task;
    }

    private sealed class ModelWorkspace : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("ocr-cache-");
        private readonly HttpClient _http;

        internal ModelWorkspace()
        {
            Paths = AppPaths.CreateForRoot(_root);
            Directory.CreateDirectory(Paths.OcrModelsRoot);
            _http = new HttpClient(Handler);
        }

        internal AppPaths Paths { get; }
        internal RejectDownloadHandler Handler { get; } = new();

        internal OcrModelStore CreateStore(Func<Stream, byte[]> computeHash, ILogger? logger = null) =>
            new(Paths, _http, logger ?? NullLogger.Instance, computeHash);

        internal string PathFor(OcrModelFile file) => Path.Combine(Paths.OcrModelsRoot, file.FileName);

        internal OcrModelFile WriteFile(string name, byte[] payload)
        {
            File.WriteAllBytes(Path.Combine(Paths.OcrModelsRoot, name), payload);
            return new OcrModelFile(name, $"https://example.test/{name}", Convert.ToHexString(SHA256.HashData(payload)), 16);
        }

        internal void WriteSyntheticCatalogModels(bool omitRecognition = false)
        {
            foreach (OcrModelFile file in CatalogModels)
            {
                if (omitRecognition && file == OcrModelCatalog.Recognition)
                {
                    continue;
                }
                File.WriteAllBytes(PathFor(file), new byte[file.MinimumBytes]);
            }

        }

        public void Dispose()
        {
            _http.Dispose();
            OwnedTestDirectory.Delete(_root);
        }
    }

    private sealed class RejectDownloadHandler : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _calls);
            throw new IOException("Injected logging failure.");
        }
    }

    private sealed class ControlledDownloadHandler(byte[] payload) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Requested.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        }
    }
}
