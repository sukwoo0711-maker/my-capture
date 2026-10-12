using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class EditDraftStoreTests
{
    private static string Root() => TestRecycleBin.CreateTempSubdirectory("MyCapture-drafts-").FullName;
    [Fact]
    public async Task DurableRevision_RecoversDocumentAndOriginalBytes()
    {
        string root = Root(); var store = new EditDraftStore(root, _ => { }); Guid id = Guid.NewGuid();
        await store.WriteAsync(id, 1, "image", "document", [], new Dictionary<string, byte[]> { ["original.png"] = [1, 2, 3] }, () => true);
        var draft = Assert.Single(store.ReadAvailable());
        Assert.Equal(id, draft.Draft.SessionId); Assert.Equal("document", draft.Draft.Document);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(draft.Directory, "original.png")));
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
        File.WriteAllBytes(Path.Combine(entry.Directory, "base.png"), [9]);
        Assert.Empty(store.ReadAvailable());
    }
    [Fact]
    public async Task Retire_TombstonePreventsRecoveryEvenWhenTrashFails()
    {
        var store = new EditDraftStore(Root(), _ => throw new IOException("Recycle unavailable")); Guid id = Guid.NewGuid();
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
    public async Task VideoSource_IsCopiedOnceAndOriginalRemainsUnchanged()
    {
        string root = Root(); string source = Path.Combine(root, "fixture.mp4"); File.WriteAllBytes(source, [1, 2, 3, 4]);
        var store = new EditDraftStore(Path.Combine(root, "drafts"), _ => { }); Guid id = Guid.NewGuid();
        string copy = await store.EnsureVideoSourceAsync(id, source);
        Assert.Equal(copy, await store.EnsureVideoSourceAsync(id, source));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy)); Assert.True(File.Exists(source));
    }
}
