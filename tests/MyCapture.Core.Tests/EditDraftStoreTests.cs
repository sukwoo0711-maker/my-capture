using MyCapture.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

public sealed class EditDraftStoreTests
{
    private readonly ITestOutputHelper _output;
    public EditDraftStoreTests(ITestOutputHelper output) => _output = output;
    private static string Root() => TestRecycleBin.CreateTempSubdirectory("MyCapture-drafts-").FullName;
    [Fact]
    public async Task DurableRevision_RecoversDocumentAndOriginalBytes()
    {
        string root = Root(); var store = new EditDraftStore(root, _ => { }); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "document", [], new Dictionary<string, byte[]> { ["original.png"] = [1, 2, 3] }, () => true);
        var draft = Assert.Single(store.ReadAvailable());
        Assert.Equal(id, draft.Draft.SessionId); Assert.Equal("document", draft.Draft.Document);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(store.ResolveFile(draft.Draft, "original.png")));
    }
    [Fact]
    public async Task StaleWriter_CannotReplaceLatestDurableRevision()
    {
        var store = new EditDraftStore(Root(), _ => { }); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 2, "image", "latest", [], new Dictionary<string, byte[]>(), () => true);
        await store.WriteAsync(id, 1, "image", "stale", [], new Dictionary<string, byte[]>(), () => false);
        Assert.Equal("latest", Assert.Single(store.ReadAvailable()).Draft.Document);
    }
    [Fact]
    public async Task IncompleteAndCorruptedRevisions_DoNotRecover()
    {
        var store = new EditDraftStore(Root(), _ => { }); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "partial", [], new Dictionary<string, byte[]>(), () => false);
        Assert.Empty(store.ReadAvailable());
        await store.WriteAsync(id, 2, "image", "complete", [], new Dictionary<string, byte[]> { ["base.png"] = [5] }, () => true);
        var entry = Assert.Single(store.ReadAvailable());
        File.WriteAllBytes(store.ResolveFile(entry.Draft, "base.png"), [9]);
        Assert.Empty(store.ReadAvailable());
    }
    [Fact]
    public async Task Retire_DeletesTheSessionFromDiskByDefault()
    {
        string root = Root(); var store = new EditDraftStore(root); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "edit", [], new Dictionary<string, byte[]> { ["base.png"] = [1, 2] }, () => true);
        Assert.Single(store.ReadAvailable());
        await store.RetireAsync(id);
        // Drafts are app-owned: the retired session leaves the disk, it is not moved anywhere.
        Assert.False(Directory.Exists(Path.Combine(root, id.ToString("N"))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.Empty(store.ReadAvailable());
    }
    [Fact]
    public async Task Retire_InterruptedDeletionKeepsTheTombstoneSoNothingReappears()
    {
        string root = Root(); var store = new EditDraftStore(root); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "edit", [], new Dictionary<string, byte[]> { ["base.png"] = [3] }, () => true);
        string blob = store.ResolveFile(Assert.Single(store.ReadAvailable()).Draft, "base.png");
        using (new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => store.RetireAsync(id));
        Assert.True(store.IsRetired(id));
        Assert.Empty(store.ReadAvailable());
        await store.RetireAsync(id); // a later retry finishes the deletion
        Assert.False(Directory.Exists(Path.Combine(root, id.ToString("N"))));
    }
    [Fact]
    public async Task Retire_TombstonePreventsRecoveryEvenWhenDeletionFails()
    {
        var store = new EditDraftStore(Root(), _ => throw new IOException("Deletion unavailable")); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "edit", [], new Dictionary<string, byte[]>(), () => true);
        string directory = Assert.Single(store.ReadAvailable()).Directory;
        await Assert.ThrowsAsync<IOException>(() => store.RetireAsync(id));
        Assert.True(Directory.Exists(directory)); Assert.Empty(store.ReadAvailable());
        await store.WriteAsync(id, 2, "image", "late", [], new Dictionary<string, byte[]>(), () => true);
        Assert.Empty(store.ReadAvailable());
    }
    [Fact]
    public async Task UnsafeAssetPath_IsRejectedAndNoExternalFileIsCreated()
    {
        string root = Root(); var store = new EditDraftStore(root, _ => { });
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Guid.NewGuid(), 1, "image", "edit", [],
            new Dictionary<string, byte[]> { ["../escape"] = [1] }, () => true));
        Assert.False(File.Exists(Path.Combine(root, "escape")));
    }
    [Fact]
    public async Task VideoDraft_ReferencesTheLibraryOriginalByHashWithoutCopyingIt()
    {
        string root = Root(); string source = Path.Combine(root, "library-original.mp4");
        byte[] video = new byte[256 * 1024]; new Random(3).NextBytes(video); File.WriteAllBytes(source, video);
        string drafts = Path.Combine(root, "drafts"); var store = new EditDraftStore(drafts); Guid id = Guid.NewGuid();
        string hash = EditDraftStore.HashFile(source);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(video)), hash);
        await store.WriteAsync(id, 1, "video", "document", new Dictionary<string, string> { ["source"] = source, ["sourceHash"] = hash },
            new Dictionary<string, byte[]>(), () => true);
        var draft = Assert.Single(store.ReadAvailable()).Draft;
        Assert.Equal(source, draft.Metadata["source"]);
        long draftBytes = Directory.EnumerateFiles(drafts, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        Assert.True(draftBytes < video.Length / 16, $"Draft holds {draftBytes} bytes; the video must not be copied.");
        Assert.Equal(video, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task AnnotationOnlyRevisions_ReuseImmutableBlobsWithoutRepeatedDeletes()
    {
        string root = Root(); int deletes = 0; var store = new EditDraftStore(root, _ => deletes++); Guid id = Guid.NewGuid();
        byte[] pixels = new byte[1024]; pixels[16] = 42;
        var files = new Dictionary<string, byte[]> { ["original.png"] = pixels, ["base.png"] = pixels, ["asset.png"] = [7] };
        await store.WriteAsync(id, 1, "image", "first", [], files, () => true);
        var first = Assert.Single(store.ReadAvailable()); string original = store.ResolveFile(first.Draft, "original.png");
        DateTime written = File.GetLastWriteTimeUtc(original);
        for (int revision = 2; revision <= 20; revision++)
            await store.WriteAsync(id, revision, "image", "document " + revision, [], files, () => true);
        var final = Assert.Single(store.ReadAvailable()); Assert.Equal(20, final.Draft.Revision);
        Assert.Equal(original, store.ResolveFile(final.Draft, "original.png"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(original));
        string[] blobs = Directory.GetFiles(Path.Combine(root, id.ToString("N"), "blobs"));
        Assert.Equal(2, blobs.Length); Assert.Equal(1025, blobs.Sum(path => new FileInfo(path).Length));
        Assert.Equal(0, deletes);
        _output.WriteLine($"Revisions=20; BlobFiles={blobs.Length}; BlobBytes={blobs.Sum(path => new FileInfo(path).Length)}; DeletesBeforeRetire={deletes}; OriginalLastWriteUtcUnchanged={written == File.GetLastWriteTimeUtc(original)}");
        await store.RetireAsync(id); Assert.Equal(1, deletes); Assert.Empty(store.ReadAvailable());
    }

    [Fact]
    public async Task ChangedPixelContent_CreatesANewBlobAndPreservesTheOriginal()
    {
        var store = new EditDraftStore(Root(), _ => { }); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "first", [], new Dictionary<string, byte[]> { ["base.png"] = [1] }, () => true);
        var first = Assert.Single(store.ReadAvailable()); string path = store.ResolveFile(first.Draft, "base.png");
        await store.WriteAsync(id, 2, "image", "second", [], new Dictionary<string, byte[]> { ["base.png"] = [2] }, () => true);
        var second = Assert.Single(store.ReadAvailable()); Assert.NotEqual(path, store.ResolveFile(second.Draft, "base.png"));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path)); Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(store.ResolveFile(second.Draft, "base.png")));
    }
}
