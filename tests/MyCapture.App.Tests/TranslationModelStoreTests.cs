using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TranslationModelStoreTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Download_StoresEveryVerifiedFileWithMonotonicProgressToTheTotal()
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        var progress = new ProgressLog();
        Assert.False(store.IsInstalled);
        Assert.Equal(0L, store.InstalledBytes);

        await store.DownloadAsync(progress, CancellationToken.None).WaitAsync(Wait);

        Assert.True(store.IsInstalled);
        Assert.True(await store.IsInstalledAsync(CancellationToken.None).WaitAsync(Wait));
        Assert.Equal(store.TotalBytes, store.InstalledBytes);
        foreach (TranslationModelFile file in workspace.Files)
            Assert.Equal(workspace.Payloads[file.FileName], File.ReadAllBytes(store.PathFor(file)));
        Assert.Equal(workspace.Files.Count, Directory.GetFiles(store.ModelDirectory).Length);
        Assert.Equal(workspace.Files.Select(file => file.FileName), workspace.Server.Requests);
        AssertProgress(progress.Reports, store.TotalBytes, firstReceived: 0);
    }

    [Fact]
    public async Task SecondDownload_SendsNoRequestsAndReportsTheTotal()
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        await store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);
        int requests = workspace.Server.Requests.Length;
        var progress = new ProgressLog();

        await store.DownloadAsync(progress, CancellationToken.None).WaitAsync(Wait);
        // A fresh store, as after a restart, verifies the files on disk and sends nothing either.
        await workspace.CreateStore().DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(requests, workspace.Server.Requests.Length);
        Assert.Equal(new TranslationDownloadProgress(store.TotalBytes, store.TotalBytes), Assert.Single(progress.Reports));
    }

    [Fact]
    public async Task ConcurrentDownloads_WaitForEachOtherAndRequestEachFileOnce()
    {
        using var workspace = new Workspace();
        var firstRequested = Signal();
        var release = Signal();
        workspace.Server.Respond = async (name, token) =>
        {
            if (name == workspace.Files[0].FileName && firstRequested.TrySetResult())
                await release.Task.WaitAsync(token);
            return workspace.Server.Serve(name);
        };
        TranslationModelStore store = workspace.CreateStore();

        Task first = store.DownloadAsync(null, CancellationToken.None);
        Task? second = null;
        try
        {
            await firstRequested.Task.WaitAsync(Wait);
            second = store.DownloadAsync(null, CancellationToken.None);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(first, second).WaitAsync(Wait);

        Assert.Equal(workspace.Files.Select(file => file.FileName), workspace.Server.Requests);
        Assert.True(store.IsInstalled);
    }

    [Fact]
    public async Task HashMismatch_IsCorruptAndLeavesNothingForThatFile()
    {
        using var workspace = new Workspace();
        TranslationModelFile damaged = workspace.Files[1];
        workspace.Server.Respond = (name, _) => Task.FromResult(name == damaged.FileName
            ? Ok(new ByteArrayContent(Flipped(workspace.Payloads[name])))
            : workspace.Server.Serve(name));
        TranslationModelStore store = workspace.CreateStore();

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.Corrupt, error.Error);
        Assert.False(File.Exists(store.PathFor(damaged)));
        Assert.Empty(Directory.GetFiles(store.ModelDirectory, "*.download"));
        Assert.Equal(workspace.Payloads[workspace.Files[0].FileName], File.ReadAllBytes(store.PathFor(workspace.Files[0])));
        Assert.DoesNotContain(workspace.Files[2].FileName, workspace.Server.Requests);
        Assert.False(store.IsInstalled);
    }

    [Theory]
    [InlineData("declared-longer")]
    [InlineData("declared-shorter")]
    [InlineData("streamed-longer")]
    [InlineData("streamed-shorter")]
    public async Task WrongSize_IsCorruptAndLeavesNothingForThatFile(string shape)
    {
        using var workspace = new Workspace();
        TranslationModelFile target = workspace.Files[0];
        byte[] payload = workspace.Payloads[target.FileName];
        byte[] longer = [.. payload, 0x5A];
        // Bodies with a prefix never end, so a store that read past the point of refusal would
        // stall instead of failing at once.
        ScriptedContent content = shape switch
        {
            "declared-longer" => new ScriptedContent(payload, payload.Length + 1, prefix: 0),
            "declared-shorter" => new ScriptedContent(payload, payload.Length - 1, prefix: 0),
            "streamed-longer" => new ScriptedContent(longer, contentLength: null, prefix: longer.Length),
            _ => new ScriptedContent(payload[..^1], contentLength: null),
        };
        workspace.Server.Respond = (name, _) =>
            Task.FromResult(name == target.FileName ? Ok(content) : workspace.Server.Serve(name));
        TranslationModelStore store = workspace.CreateStore();

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.Corrupt, error.Error);
        Assert.False(content.PrefixSent.Task.IsCompleted);
        Assert.False(File.Exists(store.PathFor(target)));
        Assert.Empty(Directory.GetFiles(store.ModelDirectory, "*.download"));
        Assert.Equal(target.FileName, Assert.Single(workspace.Server.Requests));
    }

    [Fact]
    public async Task CancelInsideAFile_RemovesThePartialFileAndKeepsCompletedFiles()
    {
        using var workspace = new Workspace();
        TranslationModelFile middle = workspace.Files[1];
        byte[] payload = workspace.Payloads[middle.FileName];
        var content = new ScriptedContent(payload, payload.Length, prefix: 1024, after: AfterPrefix.Block);
        workspace.Server.Respond = (name, _) =>
            Task.FromResult(name == middle.FileName ? Ok(content) : workspace.Server.Serve(name));
        TranslationModelStore store = workspace.CreateStore();
        using var cancellation = new CancellationTokenSource();

        Task download = store.DownloadAsync(null, cancellation.Token);
        try
        {
            await content.PrefixSent.Task.WaitAsync(Wait);
            Assert.True(File.Exists(store.PathFor(middle) + ".download"));
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(Wait));
        Assert.Empty(Directory.GetFiles(store.ModelDirectory, "*.download"));
        Assert.Equal(workspace.Payloads[workspace.Files[0].FileName], File.ReadAllBytes(store.PathFor(workspace.Files[0])));
        Assert.False(File.Exists(store.PathFor(middle)));
        Assert.False(File.Exists(store.PathFor(workspace.Files[2])));
        Assert.False(store.IsInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stall_IsANetworkErrorAfterTheStallTimeout(bool duringBody)
    {
        using var workspace = new Workspace();
        TranslationModelFile target = workspace.Files[0];
        byte[] payload = workspace.Payloads[target.FileName];
        workspace.Server.Respond = async (name, token) =>
        {
            if (name != target.FileName) return workspace.Server.Serve(name);
            if (!duringBody) await Task.Delay(Timeout.Infinite, token);
            return Ok(new ScriptedContent(payload, payload.Length, prefix: 1024, after: AfterPrefix.Block));
        };
        TranslationModelStore store = workspace.CreateStore(stallTimeout: TimeSpan.FromMilliseconds(200));

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.Network, error.Error);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(File.Exists(store.PathFor(target)));
        Assert.Empty(Directory.GetFiles(store.ModelDirectory, "*.download"));
    }

    [Fact]
    public async Task SlowButSteadyTransfer_OutlastsTheStallTimeout()
    {
        using var workspace = new Workspace(4_000);
        byte[] payload = workspace.Payloads[workspace.Files[0].FileName];
        workspace.Server.Respond = (_, _) => Task.FromResult(Ok(new ScriptedContent(payload, payload.Length,
            chunk: 400, readDelay: TimeSpan.FromMilliseconds(100))));
        TranslationModelStore store = workspace.CreateStore(stallTimeout: TimeSpan.FromMilliseconds(600));
        var clock = Stopwatch.StartNew();

        await store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);

        // Eleven reads 100 ms apart outlast the stall timeout, but bytes keep arriving.
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(600), $"{clock.ElapsedMilliseconds} ms");
        Assert.True(store.IsInstalled);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("connect")]
    [InlineData("reset")]
    public async Task ServerOrConnectionFailure_IsANetworkError(string failure)
    {
        using var workspace = new Workspace();
        TranslationModelFile target = workspace.Files[0];
        byte[] payload = workspace.Payloads[target.FileName];
        workspace.Server.Respond = (name, _) => name != target.FileName
            ? Task.FromResult(workspace.Server.Serve(name))
            : failure switch
            {
                "status" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
                "connect" => Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated connection failure.")),
                _ => Task.FromResult(Ok(new ScriptedContent(payload, payload.Length, prefix: 1024, after: AfterPrefix.Fail))),
            };
        TranslationModelStore store = workspace.CreateStore();

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.Network, error.Error);
        Assert.False(File.Exists(store.PathFor(target)));
        Assert.Empty(Directory.GetFiles(store.ModelDirectory, "*.download"));
    }

    [Fact]
    public async Task FreeSpace_CoversOnlyTheMissingFilesPlusTheMargin()
    {
        using var workspace = new Workspace();
        workspace.Install(workspace.Files[0]);
        long missing = workspace.Files.Skip(1).Sum(file => file.Bytes);
        var queried = new ConcurrentQueue<string>();
        TranslationModelStore tight = workspace.CreateStore(freeSpace: directory =>
        {
            queried.Enqueue(directory);
            return missing + TranslationModelStore.FreeSpaceMargin - 1;
        });

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => tight.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.DiskFull, error.Error);
        Assert.Equal(tight.ModelDirectory, Assert.Single(queried));
        Assert.Empty(workspace.Server.Requests);

        TranslationModelStore enough = workspace.CreateStore(freeSpace: _ => missing + TranslationModelStore.FreeSpaceMargin);
        await enough.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);
        Assert.Equal(workspace.Files.Skip(1).Select(file => file.FileName), workspace.Server.Requests);
        Assert.True(enough.IsInstalled);
    }

    [Fact]
    public async Task WriteFailure_IsADiskError()
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        TranslationModelFile blocked = workspace.Files[0];
        // A folder where the partial file belongs makes the write fail deterministically.
        Directory.CreateDirectory(store.PathFor(blocked) + ".download");

        TranslationModelException error = await Assert.ThrowsAsync<TranslationModelException>(
            () => store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(TranslationModelError.Disk, error.Error);
        Assert.False(File.Exists(store.PathFor(blocked)));
        Assert.False(store.IsInstalled);
    }

    [Fact]
    public async Task SameSizeDamagedFile_IsNotInstalledAndADownloadReplacesOnlyIt()
    {
        using var workspace = new Workspace();
        foreach (TranslationModelFile file in workspace.Files) workspace.Install(file);
        TranslationModelFile damaged = workspace.Files[1];
        workspace.Install(damaged, Flipped(workspace.Payloads[damaged.FileName]));
        TranslationModelStore store = workspace.CreateStore();
        var progress = new ProgressLog();

        Assert.False(store.IsInstalled);
        Assert.Equal(store.TotalBytes, store.InstalledBytes);

        await store.DownloadAsync(progress, CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(damaged.FileName, Assert.Single(workspace.Server.Requests));
        Assert.Equal(workspace.Payloads[damaged.FileName], File.ReadAllBytes(store.PathFor(damaged)));
        Assert.True(store.IsInstalled);
        // Files already present count from the first report on.
        AssertProgress(progress.Reports, store.TotalBytes, firstReceived: store.TotalBytes - damaged.Bytes);
    }

    [Fact]
    public void IsInstalled_ReusesVerificationWhileLengthAndTimestampHold()
    {
        using var workspace = new Workspace();
        foreach (TranslationModelFile file in workspace.Files) workspace.Install(file);
        TranslationModelStore store = workspace.CreateStore();
        Assert.True(store.IsInstalled);

        // Same length and timestamp: the recorded verification is reused without reading the
        // file, which is what keeps repeated checks of a ~250 MB model cheap.
        TranslationModelFile changed = workspace.Files[1];
        string path = store.PathFor(changed);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Flipped(workspace.Payloads[changed.FileName]));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(store.IsInstalled);

        // A new timestamp makes the store read the file again and reject it.
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        Assert.False(store.IsInstalled);
        Assert.False(workspace.CreateStore().IsInstalled);
    }

    [Fact]
    public async Task IsInstalledAsync_HonoursCancellation()
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IsInstalledAsync(cancellation.Token));
    }

    [Fact]
    public void InstalledBytes_CountsCatalogFilesWhateverTheirSize()
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        workspace.Install(workspace.Files[0]);
        workspace.Install(workspace.Files[2], new byte[10]);
        File.WriteAllBytes(Path.Combine(store.ModelDirectory, "other.bin"), new byte[100]);
        File.WriteAllBytes(store.PathFor(workspace.Files[1]) + ".download", new byte[50]);

        Assert.Equal(workspace.Files[0].Bytes + 10, store.InstalledBytes);
        Assert.False(store.IsInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_RemovesModelFilesLeftoversAndTheEmptyFolder(bool foreignFile)
    {
        using var workspace = new Workspace();
        TranslationModelStore store = workspace.CreateStore();
        await store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);
        File.WriteAllBytes(Path.Combine(store.ModelDirectory, "stale.download"), [1, 2, 3]);
        File.WriteAllBytes(store.PathFor(workspace.Files[0]) + ".download", [4, 5, 6]);
        string foreign = Path.Combine(store.ModelDirectory, "notes.txt");
        if (foreignFile) File.WriteAllText(foreign, "keep");
        Assert.True(store.IsInstalled);

        await store.DeleteAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.False(store.IsInstalled);
        Assert.Equal(0L, store.InstalledBytes);
        if (foreignFile)
            Assert.Equal(foreign, Assert.Single(Directory.GetFileSystemEntries(store.ModelDirectory)));
        else
            Assert.False(Directory.Exists(store.ModelDirectory));

        // Removing again is harmless, and the model can be downloaded again afterwards.
        await store.DeleteAsync(CancellationToken.None).WaitAsync(Wait);
        await store.DownloadAsync(null, CancellationToken.None).WaitAsync(Wait);
        Assert.True(store.IsInstalled);
    }

    [Fact]
    public async Task Progress_IsThrottledInsideAFileAndEndsAtTheTotal()
    {
        using var workspace = new Workspace(256 * 1024);
        TranslationModelFile file = workspace.Files[0];
        byte[] payload = workspace.Payloads[file.FileName];
        workspace.Server.Respond = (_, _) => Task.FromResult(Ok(new ScriptedContent(payload, payload.Length, chunk: 256)));
        TranslationModelStore store = workspace.CreateStore();
        var progress = new ProgressLog();
        var clock = Stopwatch.StartNew();

        await store.DownloadAsync(progress, CancellationToken.None).WaitAsync(Wait);
        clock.Stop();

        AssertProgress(progress.Reports, store.TotalBytes, firstReceived: 0);
        // 1,024 reads: the start, at most one report per 100 ms inside the file, and its end.
        Assert.True(progress.Reports.Count <= 3 + clock.ElapsedMilliseconds / 100,
            $"{progress.Reports.Count} reports in {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Catalog_PinsSafeHttpsReleaseAssetsInOrder()
    {
        IReadOnlyList<TranslationModelFile> files = TranslationModelCatalog.Files;
        string[] expected =
        [
            "opus-mt-en-ko-encoder.onnx",
            "opus-mt-en-ko-decoder.onnx",
            "opus-mt-en-ko-decoder-with-past.onnx",
            "opus-mt-en-ko-weights-1.bin",
            "opus-mt-en-ko-weights-2.bin",
            "opus-mt-en-ko-weights-3.bin",
            "opus-mt-en-ko-weights-4.bin",
            "opus-mt-en-ko-source.spm",
            "opus-mt-en-ko-vocab.json",
        ];

        Assert.Equal(expected, files.Select(file => file.FileName));
        Assert.StartsWith("https://", TranslationModelCatalog.DownloadBase, StringComparison.Ordinal);
        Assert.EndsWith("/", TranslationModelCatalog.DownloadBase, StringComparison.Ordinal);
        Assert.Equal(files.Count, files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, file =>
        {
            Assert.Equal(TranslationModelCatalog.DownloadBase + file.FileName, file.Uri);
            Assert.Equal(Uri.UriSchemeHttps, new Uri(file.Uri).Scheme);
            Assert.Equal(file.FileName, Path.GetFileName(file.FileName));
            Assert.DoesNotContain("..", file.FileName, StringComparison.Ordinal);
            Assert.True(file.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, file.FileName);
            Assert.Matches("^[0-9A-F]{64}$", file.Sha256);
            Assert.True(file.Bytes > 0, file.FileName);
        });
        Assert.Equal(files.Sum(file => file.Bytes), TranslationModelCatalog.TotalBytes);
        Assert.Equal((int)Math.Ceiling(TranslationModelCatalog.TotalBytes / 1_000_000d), TranslationModelCatalog.TotalMegabytes);
    }

    [Fact]
    public void Catalog_IsTheDeduplicatedModelAnnouncedInTheDocuments()
    {
        // build/tools/translation-model/README.md: 244,507,815 bytes of graphs and weights plus the
        // two unchanged tokenizer files; README.md and the consent text round this to 247 MB.
        Assert.Equal(246_256_561, TranslationModelCatalog.TotalBytes);
        Assert.Equal(247, TranslationModelCatalog.TotalMegabytes);
        Assert.All(new[]
        {
            TranslationModelCatalog.ModelFiles.Encoder, TranslationModelCatalog.ModelFiles.Decoder,
            TranslationModelCatalog.ModelFiles.DecoderWithPast, TranslationModelCatalog.ModelFiles.SourceVocabulary,
            TranslationModelCatalog.ModelFiles.TargetVocabulary,
        }, name => Assert.Contains(TranslationModelCatalog.Files, file => file.FileName == name));
        // Every weights file stays below the 64 MiB a single request has to carry.
        Assert.All(TranslationModelCatalog.Files, file => Assert.True(file.Bytes <= 64L * 1024 * 1024, file.FileName));
    }

    [Fact]
    public void ProductionStore_UsesTheCatalogInItsVersionedFolder()
    {
        using var workspace = new Workspace();
        var store = new TranslationModelStore(workspace.Paths, NullLogger<TranslationModelStore>.Instance);

        Assert.Equal(Path.Combine(workspace.Paths.TranslationModelsRoot, TranslationModelCatalog.FolderName),
            store.ModelDirectory);
        Assert.Equal(TranslationModelCatalog.Files, store.Files);
        Assert.Equal(TranslationModelCatalog.TotalBytes, store.TotalBytes);
        Assert.False(store.IsInstalled);
        Assert.Equal(0L, store.InstalledBytes);
    }

    [Fact]
    public void AppPaths_KeepTranslationModelsInTheirOwnRootThroughCopies()
    {
        using var workspace = new Workspace();
        AppPaths paths = workspace.Paths;

        Assert.Equal(Path.Combine(paths.DataRoot, "translation-models"), paths.TranslationModelsRoot);
        Assert.NotEqual(paths.OcrModelsRoot, paths.TranslationModelsRoot);
        Assert.Equal(paths.TranslationModelsRoot,
            paths.WithCapturesRoot(Path.Combine(paths.DataRoot, "moved")).TranslationModelsRoot);
        Assert.Equal(paths.TranslationModelsRoot,
            paths.WithQuickSaveRoot(Path.Combine(paths.DataRoot, "quick")).TranslationModelsRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../escape.bin")]
    [InlineData(@"..\escape.bin")]
    [InlineData("nested/model.bin")]
    [InlineData(@"nested\model.bin")]
    [InlineData("C:model.bin")]
    [InlineData("model?.bin")]
    [InlineData("model.bin.download")]
    [InlineData("model.bin.")]
    public void Store_RefusesUnsafeFileNames(string fileName)
    {
        using var workspace = new Workspace();
        var file = new TranslationModelFile(fileName, "https://example.test/models/model.bin", new string('A', 64), 1);

        Assert.Throws<ArgumentException>(() => workspace.CreateStore(files: [file]));
    }

    [Theory]
    [InlineData("http://example.test/models/model.bin", "valid", 1L)]
    [InlineData("file:///C:/models/model.bin", "valid", 1L)]
    [InlineData("models/model.bin", "valid", 1L)]
    [InlineData("https://example.test/models/model.bin", "valid", 0L)]
    [InlineData("https://example.test/models/model.bin", "ABC", 1L)]
    [InlineData("https://example.test/models/model.bin", "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", 1L)]
    public void Store_RefusesPlainAddressesAndUnpinnedFiles(string uri, string sha256, long bytes)
    {
        using var workspace = new Workspace();
        var file = new TranslationModelFile("model.bin", uri, sha256 == "valid" ? new string('A', 64) : sha256, bytes);

        Assert.Throws<ArgumentException>(() => workspace.CreateStore(files: [file]));
    }

    [Fact]
    public void Store_RefusesDuplicateNamesAndAnEmptyList()
    {
        using var workspace = new Workspace();
        TranslationModelFile file = workspace.Files[0];

        Assert.Throws<ArgumentException>(() =>
            workspace.CreateStore(files: [file, file with { FileName = file.FileName.ToUpperInvariant() }]));
        Assert.Throws<ArgumentException>(() => workspace.CreateStore(files: []));
    }

    private static void AssertProgress(IReadOnlyList<TranslationDownloadProgress> reports, long total, long firstReceived)
    {
        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(total, report.TotalBytes));
        Assert.Equal(firstReceived, reports[0].ReceivedBytes);
        for (int i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].ReceivedBytes >= reports[i - 1].ReceivedBytes,
                $"Progress went back from {reports[i - 1].ReceivedBytes} to {reports[i].ReceivedBytes}.");
        }
        Assert.Equal(total, reports[^1].ReceivedBytes);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private static byte[] Flipped(byte[] bytes)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[0] ^= 0xFF;
        return copy;
    }

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + seed * 17);
        return bytes;
    }

    /// <summary>
    /// A model folder under an owned temporary profile, small synthetic files with real SHA-256
    /// pins, and a scripted server for them.
    /// </summary>
    private sealed class Workspace : IDisposable
    {
        private static readonly int[] DefaultSizes = [3_000, 5_000, 4_096];
        private readonly string _root = OwnedTestDirectory.Create("translation-models-");
        private readonly HttpClient _http;

        internal Workspace(params int[] sizes)
        {
            Paths = AppPaths.CreateForRoot(_root);
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var files = new List<TranslationModelFile>();
            int[] chosen = sizes.Length == 0 ? DefaultSizes : sizes;
            for (int i = 0; i < chosen.Length; i++)
            {
                string name = $"model-{i + 1}.bin";
                byte[] bytes = Payload(chosen[i], seed: i + 1);
                payloads[name] = bytes;
                files.Add(new TranslationModelFile(name, "https://example.test/models/" + name,
                    Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length));
            }
            Payloads = payloads;
            Files = files;
            Server = new ModelServer(payloads);
            _http = new HttpClient(Server);
        }

        internal AppPaths Paths { get; }
        internal IReadOnlyList<TranslationModelFile> Files { get; }
        internal IReadOnlyDictionary<string, byte[]> Payloads { get; }
        internal ModelServer Server { get; }

        // The free-space probe is replaced so no test depends on the real drive.
        internal TranslationModelStore CreateStore(TimeSpan? stallTimeout = null, Func<string, long>? freeSpace = null,
            IReadOnlyList<TranslationModelFile>? files = null) =>
            new(Paths, _http, NullLogger.Instance, files ?? Files, stallTimeout ?? TimeSpan.FromSeconds(30),
                freeSpace ?? (_ => long.MaxValue));

        internal void Install(TranslationModelFile file, byte[]? bytes = null)
        {
            string directory = Path.Combine(Paths.TranslationModelsRoot, TranslationModelCatalog.FolderName);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, file.FileName), bytes ?? Payloads[file.FileName]);
        }

        public void Dispose()
        {
            _http.Dispose();
            OwnedTestDirectory.Delete(_root);
        }
    }

    private sealed class ModelServer(IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        internal Func<string, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

        internal string[] Requests => [.. _requests];

        internal HttpResponseMessage Serve(string name) => Ok(new ByteArrayContent(payloads[name]));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string name = request.RequestUri!.Segments[^1];
            _requests.Enqueue(name);
            return Respond is { } respond ? respond(name, cancellationToken) : Task.FromResult(Serve(name));
        }
    }

    private sealed class ProgressLog : IProgress<TranslationDownloadProgress>
    {
        private readonly List<TranslationDownloadProgress> _reports = [];

        internal IReadOnlyList<TranslationDownloadProgress> Reports
        {
            get
            {
                lock (_reports) return [.. _reports];
            }
        }

        public void Report(TranslationDownloadProgress value)
        {
            lock (_reports) _reports.Add(value);
        }
    }

    private enum AfterPrefix
    {
        Block,
        Fail,
    }

    /// <summary>
    /// Serves a body in reads of at most <paramref name="chunk"/> bytes, each after an optional
    /// delay. With a prefix, the body stops there and either waits until the read is cancelled
    /// or fails like a reset connection.
    /// </summary>
    private sealed class ScriptedContent(byte[] body, long? contentLength, int chunk = int.MaxValue,
        int? prefix = null, AfterPrefix after = AfterPrefix.Block, TimeSpan? readDelay = null) : HttpContent
    {
        internal TaskCompletionSource PrefixSent { get; } = Signal();

        protected override bool TryComputeLength(out long length)
        {
            length = contentLength ?? 0;
            return contentLength is not null;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ScriptedStream(body, chunk, prefix, after, readDelay, PrefixSent));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("The store reads the response stream.");
    }

    private sealed class ScriptedStream(byte[] body, int chunk, int? prefix, AfterPrefix after, TimeSpan? readDelay,
        TaskCompletionSource prefixSent) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (readDelay is { } delay) await Task.Delay(delay, cancellationToken);
            int end = prefix ?? body.Length;
            if (prefix is not null && _position >= end)
            {
                prefixSent.TrySetResult();
                if (after == AfterPrefix.Fail) throw new IOException("Simulated connection reset.");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            int count = Math.Min(Math.Min(buffer.Length, chunk), end - _position);
            body.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
