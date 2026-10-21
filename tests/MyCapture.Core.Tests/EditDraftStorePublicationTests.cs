using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// A revision becomes recoverable through one rename of its complete durable marker, decided
/// just before that rename; no lock is held across it, so an editor never waits for the disk.
/// </summary>
public sealed class EditDraftStorePublicationTests
{
    private static string Root() => TestRecycleBin.CreateTempSubdirectory("MyCapture-draft-publication-").FullName;

    [Fact]
    public async Task Revision_IsPublishedByOneRenameOfItsCompleteDurableMarker()
    {
        string root = Root(); Guid id = Guid.NewGuid();
        var published = new List<string>();
        EditDraftStore? store = null;
        store = new EditDraftStore(root, _ => { }, (pending, ready) =>
        {
            Assert.Equal("ready.pending", Path.GetFileName(pending));
            Assert.Equal(Path.GetDirectoryName(pending), Path.GetDirectoryName(ready));
            Assert.True(new FileInfo(pending).Length > 0);
            Assert.False(File.Exists(ready));
            Assert.Empty(store!.ReadAvailable()); // nothing is recoverable before the rename
            File.Move(pending, ready);
            published.Add(Path.GetFileName(ready));
        });
        await store.WriteAsync(id, 3, "image", "document", [], new Dictionary<string, byte[]> { ["base.png"] = [4, 5] }, () => true);
        Assert.Equal(["ready.json"], published);
        var draft = Assert.Single(store.ReadAvailable()).Draft;
        Assert.Equal(3, draft.Revision); Assert.Equal("document", draft.Document);
    }

    [Fact]
    public async Task StaleRevisionAndRevisionRetiredBeforeItsRename_AreNeverPublished()
    {
        string root = Root(); Guid id = Guid.NewGuid(); int publications = 0;
        var store = new EditDraftStore(root, _ => { }, (pending, ready) => { publications++; File.Move(pending, ready); });
        await store.WriteAsync(id, 1, "image", "stale", [], new Dictionary<string, byte[]>(), () => false);
        // The tombstone is checked after the caller's currency check, immediately before the rename.
        await store.WriteAsync(id, 2, "image", "retired", [], new Dictionary<string, byte[]>(), () =>
        {
            File.WriteAllBytes(Path.Combine(root, id.ToString("N"), "retired"), [1]);
            return true;
        });
        Assert.Equal(0, publications);
        Assert.Empty(store.ReadAvailable());
    }
}
