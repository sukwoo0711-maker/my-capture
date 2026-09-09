using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AnnotationAssetReachabilityTests
{
    [Fact]
    public async Task UndoRedoRetainsImage_NewBranchReleasesAbandonedPixelsAndSource() => await StaThreadTask.RunAsync(() =>
    {
        string root = OwnedTestDirectory.Create("MyCapture-assets-");
        try
        {
            string path = Path.Combine(root, "insert.png");
            ImageCodec.SavePng(Image(), path);
            var store = new AnnotationImageStore();
            var loaded = store.LoadFromFile(path)!.Value;
            var document = AnnotationDocument.CreateFor(100, 100);
            var history = new UndoStack();
            history.Changed += (_, _) => store.PruneToReachable(document, history);
            var item = new ImageAnnotation { AssetFileName = loaded.AssetFileName };
            history.Execute(new AddAnnotationCommand(document, item));
            Assert.NotNull(store.Get(item.AssetFileName));
            Assert.True(history.Undo());
            Assert.NotNull(store.Get(item.AssetFileName));
            Assert.True(history.Redo());
            Assert.NotNull(store.Get(item.AssetFileName));
            Assert.True(history.Undo());
            history.Execute(new AddAnnotationCommand(document, new RectangleAnnotation()));
            Assert.Null(store.Get(item.AssetFileName));
            Assert.Empty(store.SourcesFor([item.AssetFileName]));
            Assert.False(history.CanRedo);
            return true;
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedImage_IsRetainedUntilHistoryClearsOrEvicts(bool clear) => await StaThreadTask.RunAsync(() =>
    {
        var store = new AnnotationImageStore();
        store.Seed(new Dictionary<string, BitmapSource> { ["image.png"] = Image() });
        var document = AnnotationDocument.CreateFor(100, 100);
        var item = new ImageAnnotation { AssetFileName = "image.png" };
        document.Add(item);
        var history = new UndoStack(depthLimit: 1);
        history.Changed += (_, _) => store.PruneToReachable(document, history);
        history.Execute(new RemoveAnnotationCommand(document, item));
        Assert.NotNull(store.Get("image.png"));
        if (clear) history.Clear();
        else history.Execute(new AddAnnotationCommand(document, new RectangleAnnotation()));
        Assert.Null(store.Get("image.png"));
        return true;
    });

    [Fact]
    public async Task LiveAssetsSurviveClear_AndUnusedSeededAssetsArePruned() => await StaThreadTask.RunAsync(() =>
    {
        var store = new AnnotationImageStore();
        store.Seed(new Dictionary<string, BitmapSource> { ["live.png"] = Image(), ["unused.png"] = Image() });
        var document = AnnotationDocument.CreateFor(100, 100);
        document.Add(new ImageAnnotation { AssetFileName = "live.png" });
        var history = new UndoStack();
        store.PruneToReachable(document, history);
        Assert.NotNull(store.Get("live.png"));
        Assert.Null(store.Get("unused.png"));
        return true;
    });

    [Fact]
    public async Task ActiveNestedBatchAndComposite_RetainRestorableAssets() => await StaThreadTask.RunAsync(() =>
    {
        var store = new AnnotationImageStore();
        store.Seed(new Dictionary<string, BitmapSource> { ["image.png"] = Image() });
        var document = AnnotationDocument.CreateFor(100, 100);
        var history = new UndoStack();
        var item = new ImageAnnotation { AssetFileName = "image.png" };
        document.Add(item);
        using (history.BeginBatch("outer"))
        {
            using (history.BeginBatch("inner")) history.Execute(new RemoveAnnotationCommand(document, item));
            history.Execute(new AddAnnotationCommand(document, new RectangleAnnotation()));
            store.PruneToReachable(document, history);
            Assert.NotNull(store.Get("image.png"));
        }
        Assert.Contains("image.png", history.ReferencedImageAssets);
        Assert.True(history.Undo());
        Assert.Contains(item, document.Items);
        store.PruneToReachable(document, history);
        Assert.NotNull(store.Get("image.png"));
        Assert.True(history.Redo());
        store.PruneToReachable(document, history);
        Assert.NotNull(store.Get("image.png"));
        return true;
    });

    [Fact]
    public void ImagePropertyHistory_ReportsBeforeAndAfterAcrossMergedChanges()
    {
        var item = new ImageAnnotation { AssetFileName = "first.png" };
        var history = new UndoStack();
        history.Execute(new PropertyChangeCommand<ImageAnnotation, string>(item, "asset",
            (image, name) => image.AssetFileName = name, "first.png", "second.png"));
        history.Execute(new PropertyChangeCommand<ImageAnnotation, string>(item, "asset",
            (image, name) => image.AssetFileName = name, "second.png", "third.png"));
        Assert.Contains("first.png", history.ReferencedImageAssets);
        Assert.Contains("third.png", history.ReferencedImageAssets);
        Assert.DoesNotContain("second.png", history.ReferencedImageAssets);
        Assert.True(history.Undo());
        Assert.Equal("first.png", item.AssetFileName);
        Assert.Contains("third.png", history.ReferencedImageAssets);
    }

    [Fact]
    public void EveryImageCommand_ReportsReferencedAsset()
    {
        var document = AnnotationDocument.CreateFor(100, 100);
        var image = new ImageAnnotation { AssetFileName = "image.png" };
        document.Add(image);
        IUndoableCommand[] commands =
        [
            new AddAnnotationCommand(document, image), new RemoveAnnotationCommand(document, image),
            new TransformAnnotationCommand(image, new RectD(0, 0, 1, 1), new RectD(1, 1, 1, 1)),
            new ReorderAnnotationCommand(document, image, 0), new ReplacePointsCommand(image, [], []),
            new AlreadyAddedCommand(document, image),
        ];
        foreach (IUndoableCommand command in commands) Assert.Contains("image.png", command.ReferencedImageAssets);
        Assert.Contains("image.png", new CompositeCommand("all", commands).ReferencedImageAssets);
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, 8);
        image.Freeze();
        return image;
    }
}
