using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Draft autosave work that must never hold the editor's dispatcher: publishing a revision on a
/// slow disk, and serializing the snapshot document.
/// </summary>
public sealed class EditDraftAutosaveDispatcherTests
{
    /// <summary>How long the simulated slow disk takes to publish one revision.</summary>
    private static readonly TimeSpan SlowDisk = TimeSpan.FromSeconds(2);

    /// <summary>Far below <see cref="SlowDisk"/>; a dispatcher call that waited for the disk exceeds it.</summary>
    private const double PromptMs = 250;

    [Fact]
    public void EditDuringASlowPublication_DoesNotWaitForTheDisk_AndTheNewerRevisionIsRecovered() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = TestRecycleBin.CreateTempSubdirectory("mc-draft-slow-publish-").FullName;
        using var publishing = new ManualResetEventSlim();
        using var disk = new ManualResetEventSlim();
        var store = new EditDraftStore(root, _ => { }, SlowFirstPublication(publishing, disk));
        string document = "first";
        var session = new EditDraftSession(store, Dispatcher.CurrentDispatcher,
            () => Task.FromResult<DraftSnapshot?>(Snapshot(document)), _ => { });
        session.Changed();
        Task first = session.FlushAsync();
        PumpUntil(() => publishing.IsSet);

        document = "second";
        var watch = Stopwatch.StartNew();
        session.Changed(); // the user keeps drawing while revision 1 is being published
        double editMs = watch.Elapsed.TotalMilliseconds;
        disk.Set();
        PumpUntil(() => first.IsCompleted);
        Assert.True(editMs < PromptMs, $"An edit waited {editMs:0} ms for a draft publication on a slow disk.");

        Task second = session.FlushAsync();
        PumpUntil(() => second.IsCompleted);
        var draft = Assert.Single(store.ReadAvailable()).Draft;
        Assert.Equal(2, draft.Revision);
        Assert.Equal("second", draft.Document);
    });

    [Fact]
    public void RetireDuringASlowPublication_DoesNotWaitForTheDisk_AndNothingIsRecoverable() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = TestRecycleBin.CreateTempSubdirectory("mc-draft-slow-retire-").FullName;
        using var publishing = new ManualResetEventSlim();
        using var disk = new ManualResetEventSlim();
        var store = new EditDraftStore(root, publishReadyMarker: SlowFirstPublication(publishing, disk));
        var session = new EditDraftSession(store, Dispatcher.CurrentDispatcher,
            () => Task.FromResult<DraftSnapshot?>(Snapshot("edit")), _ => { });
        session.Changed();
        Task write = session.FlushAsync();
        PumpUntil(() => publishing.IsSet);

        var watch = Stopwatch.StartNew();
        Task<bool> retire = session.RetireAsync(); // Save or Don't save while revision 1 is being published
        double retireMs = watch.Elapsed.TotalMilliseconds;
        Assert.False(retire.IsCompleted, "Retirement must wait for the writer, asynchronously.");
        disk.Set();
        PumpUntil(() => retire.IsCompleted && write.IsCompleted);
        Assert.True(retireMs < PromptMs, $"Retiring waited {retireMs:0} ms on the dispatcher for a draft publication on a slow disk.");
        Assert.True(retire.Result);

        // The revision that finished publishing during retirement is gone with its session, and
        // later edits on the retired session never bring a draft back.
        Assert.Empty(store.ReadAvailable());
        Assert.False(Directory.Exists(Path.Combine(root, session.Id.ToString("N"))));
        session.Changed();
        Task late = session.FlushAsync();
        PumpUntil(() => late.IsCompleted);
        Assert.Empty(store.ReadAvailable());
        Assert.False(Directory.Exists(Path.Combine(root, session.Id.ToString("N"))));
    });

    [Fact]
    public void DraftDocumentAndMetadata_AreSerializedFromTheStateWhenTheSnapshotWasTaken() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var bitmap = Solid(64, 48, 0x30);
        var region = new RectD(0, 0, 64, 48);
        var document = new AnnotationDocument { CanvasWidth = 64, CanvasHeight = 48 };
        foreach (string asset in new[] { "first-asset.png", "second-asset.png" })
            document.Items.Add(new ImageAnnotation { AssetFileName = asset, SourceWidth = 8, SourceHeight = 8, Rect = new RectD(30, 4, 8, 8) });
        var assets = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>
        {
            ["first-asset.png"] = Solid(8, 8, 0x80), ["second-asset.png"] = Solid(8, 8, 0xC0),
        };
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap,
            initialDocument: document, initialAssets: assets, sourceWindowTitle: "Owned \"draft\" title");
        Draw(window, new PointD(2, 2), new PointD(20, 20));
        Assert.True(window.Editor.HandleShortcut(Key.W, ModifierKeys.None));
        window.Editor.ApplyCrop(new RectD(4, 6, 30, 40));
        var pendingCrop = new RectD(1, 2, 10, 12);
        window.Editor.RestorePendingDraftCrop(JsonSerializer.Serialize(pendingCrop, JsonDefaults.Compact));
        AnnotationEditorController controller = Controller(window.Editor);
        // What CaptureDraftAsync produced when it serialized on the dispatcher.
        string expectedDocument = window.Editor.CreateCurrentImageSnapshot().ToJson();
        var expectedMetadata = new Dictionary<string, string>
        {
            ["title"] = "Owned \"draft\" title",
            ["turns"] = controller.RotationTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["origin"] = JsonSerializer.Serialize(controller.CropOrigin, JsonDefaults.Compact),
            ["crop"] = JsonSerializer.Serialize(window.Editor.PendingCrop!.Region, JsonDefaults.Compact),
            ["asset:first-asset.png"] = "asset-0000.png",
            ["asset:second-asset.png"] = "asset-0001.png",
        };

        Task<DraftSnapshot?> capture = window.Editor.CaptureDraftAsync();
        Draw(window, new PointD(3, 3), new PointD(9, 9)); // the live document changes while the encoder serializes
        DraftSnapshot snapshot = Await(capture)!;

        Assert.Equal(expectedDocument, snapshot.Document);
        Assert.Equal(3, AnnotationDocument.TryFromJson(snapshot.Document)!.Items.Count);
        Assert.Equal(expectedMetadata.ToList(), snapshot.Metadata.ToList()); // same entries, same order
        Assert.Equal(["asset-0000.png", "asset-0001.png", "base.png", "original.png"], snapshot.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(4, controller.Document.Items.Count);
        window.Close();
    });

    private static Action<string, string> SlowFirstPublication(ManualResetEventSlim publishing, ManualResetEventSlim disk)
    {
        int publications = 0;
        return (pending, ready) =>
        {
            if (Interlocked.Increment(ref publications) == 1)
            {
                publishing.Set();
                disk.Wait(SlowDisk); // released by the test, or after the slow disk's time
            }
            File.Move(pending, ready);
        };
    }

    private static DraftSnapshot Snapshot(string document) =>
        new("image", document, new Dictionary<string, string>(), new Dictionary<string, byte[]> { ["base.png"] = [1, 2, 3] });

    private static void Draw(AnnotationEditorWindow window, PointD from, PointD to)
    {
        var controller = Controller(window.Editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(from);
        controller.PointerMove(to);
        controller.PointerUp(to);
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Bounded dispatcher operation did not finish.");
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
