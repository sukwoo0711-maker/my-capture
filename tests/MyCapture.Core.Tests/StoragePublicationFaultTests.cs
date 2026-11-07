using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Retains synthetic storage evidence; no fixture cleanup or user-profile access.</summary>
public sealed partial class StoragePublicationFaultTests
{
    private const string OldTitle = "committed 이전 日本 😀";
    private const string NewTitle = "replacement 새 자료 e\u0301";
    private const string OldFolder = "보관 previous";
    private const string NewFolder = "보관 replacement";
    private const string UnicodeText = "한글 日本語 العربية e\u0301 😀\r\n\t\"quoted\" \\ path \u0000 end";
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task InjectedWriteErrorAtEachPublicationBoundary_PreservesCommittedIndexAndAllowsRetry(
        int boundary, bool accessDenied)
    {
        Fixture fixture = await Fixture.CreateAsync($"injected-{boundary}-{accessDenied}");
        fixture.SetReplacement();
        string[] targets = fixture.PublicationPaths;
        var reached = new List<string>();
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            reached.Add(path);
            if (path == targets[boundary])
            {
                if (accessDenied) throw new UnauthorizedAccessException("Synthetic publication-boundary access denial.");
                throw new IOException("Synthetic publication-boundary disk full.", unchecked((int)0x80070070));
            }
        };

        Exception? error = await Record.ExceptionAsync(() => fixture.PublishAsync());
        Assert.NotNull(error);
        Assert.Equal(accessDenied ? unchecked((int)0x80070005) : unchecked((int)0x80070070), error.HResult);
        Assert.Equal(targets.Take(boundary + 1), reached);
        fixture.AssertDiskBoundary(boundary);
        fixture.AssertReloaded(replacement: false);
        await AssertAllSlotsReturned(fixture.Queue);
        fixture.Queue.BeforePublicationWriteForTest = null;
        await fixture.PublishAsync();
        fixture.AssertReloaded(replacement: true);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", fault = accessDenied ? "injected-access-denied" : "injected-disk-full",
            boundary, error.HResult, scope = "before AtomicFile; not physical disk exhaustion or ACL denial" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualIndexReplacementFailure_PreservesCommittedIndexAndAllowsRetry(bool readOnly)
    {
        Fixture fixture = await Fixture.CreateAsync(readOnly ? "readonly-index" : "shared-index");
        fixture.SetReplacement();
        string index = fixture.Paths.IndexFile;
        byte[] committed = File.ReadAllBytes(index);
        FileAttributes attributes = File.GetAttributes(index);
        FileStream? held = null;
        Exception? error;
        try
        {
            if (readOnly) File.SetAttributes(index, attributes | FileAttributes.ReadOnly);
            else held = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read);
            error = await Record.ExceptionAsync(() => fixture.PublishAsync());
        }
        finally
        {
            held?.Dispose();
            if (readOnly) File.SetAttributes(index, attributes);
        }
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Equal(readOnly ? unchecked((int)0x80070005) : unchecked((int)0x80070020), error.HResult);
        Assert.Equal(committed, File.ReadAllBytes(index));
        fixture.AssertDiskBoundary(2);
        fixture.AssertReloaded(replacement: false);
        await AssertAllSlotsReturned(fixture.Queue);
        await fixture.PublishAsync();
        fixture.AssertReloaded(replacement: true);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", fault = readOnly ? "Windows read-only destination" : "Windows rename sharing violation",
            error.HResult, indexUnchangedOnFailure = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptIndex_UsesValidBackupOrDurableSidecars(bool corruptBackup)
    {
        Fixture fixture = await Fixture.CreateAsync(corruptBackup ? "corrupt-both" : "corrupt-primary");
        fixture.SetReplacement();
        await fixture.PublishAsync();
        File.WriteAllText(fixture.Paths.IndexFile, "{synthetic truncated JSON", Encoding.UTF8);
        if (corruptBackup)
            File.WriteAllText(fixture.Paths.IndexFile + AtomicFile.BackupSuffix, "{also invalid", Encoding.UTF8);

        // A valid schema-2 backup wins over newer completed metadata. With no valid index,
        // Load rebuilds from durable metadata and recovers folder state from its sidecar.
        fixture.AssertReloaded(replacement: corruptBackup);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", corruptBackup,
            expectedRecovery = corruptBackup ? "metadata + folder sidecar" : "previous valid index backup" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnicodeAndLongPaths_PublishExactlyTheExistingStringSerializerBytes(bool longPath)
    {
        Fixture fixture = await Fixture.CreateAsync(longPath ? "unicode-long" : "unicode", longPath);
        fixture.SetReplacement();
        fixture.Record.OcrText = UnicodeText;
        fixture.Record.Tags = "태그,日本語,😀";
        fixture.Record.ImportedFileName = "문서 😀.png";
        await fixture.Queue.SaveRecordMetaOrThrowAsync(fixture.Record).WaitAsync(WaitBudget);
        await fixture.Queue.SaveAsync().WaitAsync(WaitBudget);
        byte[][] canonical = fixture.PublicationPaths.Select(File.ReadAllBytes).ToArray();
        await fixture.PublishAsync();
        for (int i = 0; i < canonical.Length; i++)
        {
            Assert.Equal(canonical[i], File.ReadAllBytes(fixture.PublicationPaths[i]));
            Assert.Equal(canonical[i], File.ReadAllBytes(fixture.PublicationPaths[i] + AtomicFile.BackupSuffix));
            Assert.False(canonical[i].AsSpan().StartsWith(Encoding.UTF8.Preamble));
        }
        CaptureQueue restored = fixture.Reload();
        CaptureRecord actual = Assert.Single(restored.Records);
        Assert.Equal(UnicodeText, actual.OcrText);
        Assert.Equal(fixture.Record.Title, actual.Title);
        Assert.Equal(fixture.Record.Tags, actual.Tags);
        Assert.Equal(fixture.Record.ImportedFileName, actual.ImportedFileName);
        Assert.Equal(fixture.Record.FolderId, actual.FolderId);
        Assert.Equal(NewFolder, Assert.Single(restored.Folders).Name);
        if (longPath) Assert.True(fixture.Paths.IndexFile.Length > 260);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", longPath, indexPathCharacters = fixture.Paths.IndexFile.Length,
            canonicalSource = "unchanged SaveRecordMetaOrThrowAsync + SaveAsync string serialization", exactUtf8Bytes = true });
    }

    [Fact]
    public Task QueuedSnapshotsAndSynchronousSave_PreserveFifoAndMutableFieldIsolation() => QueueOwnerThread.RunAsync(async () =>
    {
        Fixture fixture = await Fixture.CreateAsync("fifo-snapshots");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var observed = new List<(string Path, string Content)>();
        fixture.Queue.BeforePublicationWriteForTest = (path, content) =>
        {
            entered.Set();
            if (!release.Wait(WaitBudget)) throw new TimeoutException("FIFO fixture was not released.");
            observed.Add((path, content));
        };
        fixture.Record.Title = "first";
        fixture.Record.OcrText = "first OCR";
        fixture.Queue.Folders[0].Name = "first folder";
        Task first = fixture.PublishAsync();
        Task second = Task.CompletedTask;
        Task third = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(WaitBudget));
            fixture.Record.Title = "second";
            fixture.Record.OcrText = "second OCR";
            fixture.Queue.Folders[0].Name = "second folder";
            second = fixture.PublishAsync();
            fixture.Record.Title = "third";
            fixture.Queue.Folders[0].Name = "third folder";
            third = fixture.Queue.SaveAsync();
            fixture.Record.Title = "unpublished mutation";
            fixture.Record.OcrText = "unpublished OCR";
            fixture.Queue.Folders[0].Name = "unpublished folder";
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second, third).WaitAsync(WaitBudget);
        Assert.Equal(8, observed.Count);
        Assert.Equal(fixture.PublicationPaths.Concat(fixture.PublicationPaths)
            .Concat(fixture.PublicationPaths.Skip(1)), observed.Select(item => item.Path));
        Assert.Equal("first", ReadMetadata(observed[0].Content).Title);
        Assert.Equal("first", ReadIndex(observed[2].Content).Records.Single().Title);
        Assert.Equal("first folder", ReadIndex(observed[2].Content).FolderState!.Folders.Single().Name);
        Assert.Equal("second", ReadMetadata(observed[3].Content).Title);
        Assert.Equal("second OCR", ReadMetadata(observed[3].Content).OcrText);
        Assert.Equal("second", ReadIndex(observed[5].Content).Records.Single().Title);
        Assert.Equal("third", ReadIndex(observed[7].Content).Records.Single().Title);
        Assert.Equal("third folder", ReadIndex(observed[7].Content).FolderState!.Folders.Single().Name);
        Assert.Equal("third", Assert.Single(fixture.Reload().Records).Title);
        Assert.Equal("second", ReadMetadata(File.ReadAllText(fixture.MetadataPath)).Title);
        await AssertAllSlotsReturned(fixture.Queue);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", immutableSnapshots = 3, fileWrites = observed.Count });
    });

    private static CaptureRecord ReadMetadata(string json) => JsonSerializer.Deserialize<CaptureRecord>(json, JsonDefaults.Readable)!;
    private static CaptureIndexFile ReadIndex(string json) => JsonSerializer.Deserialize<CaptureIndexFile>(json, JsonDefaults.Compact)!;

    private static async Task AssertAllSlotsReturned(CaptureQueue queue)
    {
        var slots = new List<CaptureWriteReservation>();
        try
        {
            for (int i = 0; i < 8; i++)
                slots.Add(await queue.ReservePublicationAsync().WaitAsync(WaitBudget));
            using var cancel = new CancellationTokenSource();
            Task<CaptureWriteReservation> ninth = queue.ReservePublicationAsync(cancel.Token);
            Assert.False(ninth.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ninth);
        }
        finally { foreach (CaptureWriteReservation slot in slots) slot.Dispose(); }
    }

    private sealed class Fixture
    {
        private readonly Dictionary<string, byte[]> _payloads = [];
        private Fixture(string root, bool longPath = false)
        {
            Root = root;
            string data = Path.Combine(root, "자료 日本 😀");
            if (longPath)
                for (int i = 0; i < 4; i++) data = Path.Combine(data, $"segment-{i}-" + new string('l', 60));
            Paths = AppPaths.CreateForRoot(data);
            Queue = Reload();
        }

        internal string Root { get; }
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CaptureRecord Record => Assert.Single(Queue.Records);
        internal string MetadataPath => Queue.GetFilePath(Record, CaptureFileNames.Meta);
        internal string FolderPath => Path.Combine(Paths.DataRoot, "library-folders.json");
        internal string[] PublicationPaths => [MetadataPath, FolderPath, Paths.IndexFile];

        internal static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Storage fixture requires the repository test output.");
        }

        private static string OwnedRoot() => Path.Combine(RepositoryRoot(), "artifacts", "storage-fault-tests");

        internal static async Task<Fixture> CreateAsync(string scenario, bool longPath = false)
        {
            string root = Path.Combine(OwnedRoot(), scenario + "-" + Guid.NewGuid().ToString("N"));
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root, longPath);
            CaptureFolder folder = await fixture.Queue.CreateFolderAsync(OldFolder,
                createdAt: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
            var record = new CaptureRecord
            {
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                CreatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
                Width = 3, Height = 2, DpiScale = 1, Title = OldTitle, OcrText = "committed OCR", TotalBytes = 16,
            };
            fixture.Queue.Add(record);
            fixture._payloads.Add(fixture.Queue.GetFilePath(record, CaptureFileNames.Original), [0, 1, 2, 127, 128, 254, 255]);
            fixture._payloads.Add(fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered), [10, 20, 30, 40]);
            fixture._payloads.Add(fixture.Queue.GetFilePath(record, CaptureFileNames.Layers), Encoding.UTF8.GetBytes("{\"synthetic\":\"layer 한글\"}"));
            foreach ((string path, byte[] bytes) in fixture._payloads)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            await fixture.Queue.MoveCapturesAsync([record.Id], folder.Id);
            await fixture.Queue.SetNewCaptureFolderAsync(folder.Id, true);
            await fixture.PublishAsync();
            fixture.WriteReceipt(new { status = "PREPARED", synthetic = true }, "prepared.json");
            return fixture;
        }

        /// <summary>The crash worker's fixture; its environment-supplied root is validated before any file access.</summary>
        internal static Fixture OpenChild(string root) => new(ChildRoot(OwnedRoot(), root));

        /// <summary>
        /// An existing direct child of <paramref name="ownedRoot"/> without links that holds the parent's
        /// prepared receipt. Escapes, device and network paths fail before they are read; nothing is created.
        /// </summary>
        internal static string ChildRoot(string ownedRoot, string requested)
        {
            string allowed = ValidatedFilePaths.DirectoryRoot(ownedRoot);
            string root = ValidatedFilePaths.DirectoryWithinRoot(allowed, requested);
            if (!string.Equals(Path.GetDirectoryName(root), allowed, StringComparison.Ordinal) || !Directory.Exists(root))
                throw new InvalidOperationException("Child fixture must be an existing direct child of the owned storage-fault directory.");
            if (!File.Exists(ValidatedFilePaths.FileWithinRoot(root, "prepared.json", ".json")))
                throw new InvalidOperationException("Child fixture must be a prepared owned storage-fault directory.");
            return root;
        }

        internal CaptureQueue Reload()
        {
            var queue = new CaptureQueue(Paths, new QueueSettings { MaxItems = 100, ImageRetentionHours = CaptureRetention.NeverHours },
                NullLogger<CaptureQueue>.Instance);
            queue.Load();
            return queue;
        }

        internal void SetReplacement()
        {
            Record.Title = NewTitle;
            Record.OcrText = UnicodeText;
            Queue.Folders[0].Name = NewFolder;
        }

        internal async Task PublishAsync()
        {
            using CaptureWriteReservation reservation = await Queue.ReservePublicationAsync().WaitAsync(WaitBudget);
            await Queue.PublishRecordAsync(Record, reservation).WaitAsync(WaitBudget);
        }

        // Boundary 0/1/2 is before metadata/folder/index; 3 is after awaited completion.
        internal void AssertDiskBoundary(int boundary)
        {
            Assert.Equal(boundary == 3 ? NewTitle : OldTitle, ReadIndex(File.ReadAllText(Paths.IndexFile)).Records.Single().Title);
            Assert.Equal(boundary == 0 ? OldTitle : NewTitle, ReadMetadata(File.ReadAllText(MetadataPath)).Title);
            var folders = JsonSerializer.Deserialize<CaptureFolderState>(File.ReadAllText(FolderPath), JsonDefaults.Compact)!;
            Assert.Equal(boundary < 2 ? OldFolder : NewFolder, Assert.Single(folders.Folders).Name);
        }

        internal void AssertReloaded(bool replacement)
        {
            CaptureQueue loaded = Reload();
            CaptureRecord record = Assert.Single(loaded.Records);
            Assert.Equal(Record.Id, record.Id);
            Assert.Equal(replacement ? NewTitle : OldTitle, record.Title);
            Assert.Equal(replacement ? UnicodeText : "committed OCR", record.OcrText);
            CaptureFolder folder = Assert.Single(loaded.Folders);
            Assert.Equal(replacement ? NewFolder : OldFolder, folder.Name);
            Assert.Equal(folder.Id, record.FolderId);
            Assert.Equal(1, record.FolderSequence);
            Assert.Equal(folder.Id, loaded.DefaultCaptureFolderId);
            Assert.True(loaded.UseCurrentFolderForNewCaptures);
        }

        internal void AssertPayloadsUnchanged()
        {
            Assert.NotEmpty(_payloads);
            foreach ((string path, byte[] bytes) in _payloads) Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        internal void WriteReceipt(object result, string name = "result.json")
        {
            var hashes = PublicationPaths.Concat(_payloads.Keys).Where(File.Exists).ToDictionary(
                path => Path.GetRelativePath(Root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.WriteAllText(Path.Combine(Root, name), JsonSerializer.Serialize(new
            {
                result, runtime = Environment.Version.ToString(), coreSha256 = AssemblyHash(typeof(CaptureQueue).Assembly.Location),
                testSha256 = AssemblyHash(typeof(StoragePublicationFaultTests).Assembly.Location), hashes,
            }, JsonDefaults.Readable));
        }

        private static string AssemblyHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
