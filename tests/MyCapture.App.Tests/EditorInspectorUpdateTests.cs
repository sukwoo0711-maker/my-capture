using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorInspectorUpdateTests : KoreanCaptionTest
{
    [Fact]
    public void ApplyingFullImageCropNotifiesRecoveryAndRemovesPendingCropMetadata() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost();
        var notifications = new List<Task<DraftSnapshot?>>();
        host.Editor.DraftChanged += (_, _) => notifications.Add(host.Editor.CaptureDraftAsync());
        SelectTool(host, EditorTool.Crop);
        DraftSnapshot pending = OnlySnapshot(notifications);
        Assert.Equal(new RectD(0, 0, 96, 64), CropRegion(pending));
        notifications.Clear();

        host.Editor.ApplyPendingCrop();

        Assert.Equal(0, Controller(host.Editor).Undo.UndoCount);
        Assert.Equal(EditorTool.Select, Controller(host.Editor).Tool);
        DraftSnapshot applied = OnlySnapshot(notifications);
        Assert.False(applied.Metadata.ContainsKey("crop"));
        Assert.Equal(pending.Document, applied.Document);
        Assert.Equal(pending.Files["base.png"], applied.Files["base.png"]);
        Assert.Equal(pending.Files["original.png"], applied.Files["original.png"]);
    });

    [Fact]
    public void OnlyChangesToRecoverableCropStateRaiseDraftNotifications() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost();
        int notifications = 0;
        host.Editor.DraftChanged += (_, _) => notifications++;
        string originalDocument = Controller(host.Editor).Document.ToJson();
        foreach (EditorTool tool in Enum.GetValues<EditorTool>().Where(tool => tool != EditorTool.Crop))
        {
            // Selecting Image directly only changes tools; no image picker is opened.
            SelectTool(host, tool);
            Assert.Equal(0, notifications);
        }
        Assert.Equal(originalDocument, Controller(host.Editor).Document.ToJson());

        SelectTool(host, EditorTool.Crop);
        Assert.Equal(1, notifications);
        notifications = 0;
        SelectTool(host, EditorTool.Crop);
        host.Editor.CropShortcutActions["Right"](); // Full-image bounds cannot move.
        Assert.Equal(0, notifications);

        TextBox width = Field<TextBox>(host.Editor, "_cropWidth");
        width.Text = "invalid";
        host.Editor.ApplyPendingCrop();
        Assert.Equal(0, notifications); // Validation is not recoverable geometry.
        width.Text = "48";
        Invoke(host.Editor, "ChangeCropSize", width);
        Assert.Equal(1, notifications);
        notifications = 0;
        Invoke(host.Editor, "ChangeCropSize", width); // Already-committed value.
        Assert.Equal(0, notifications);
        host.Editor.CropShortcutActions["Right"]();
        Assert.Equal(1, notifications);
        notifications = 0;

        host.Editor.CancelPendingCrop();
        Assert.Equal(1, notifications);
        notifications = 0;
        SelectTool(host, EditorTool.Rectangle);
        SelectTool(host, EditorTool.Text);
        Assert.Equal(0, notifications);
        Assert.Equal(originalDocument, Controller(host.Editor).Document.ToJson());
        Assert.Equal(0, Controller(host.Editor).Undo.UndoCount);
    });

    [Fact]
    public void ApplyingChangedCropPublishesOneAlreadySynchronizedDraft() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost();
        SelectTool(host, EditorTool.Crop);
        TextBox width = Field<TextBox>(host.Editor, "_cropWidth");
        TextBox height = Field<TextBox>(host.Editor, "_cropHeight");
        width.Text = "48";
        height.Text = "32";
        Invoke(host.Editor, "ChangeCropSize", width);
        Invoke(host.Editor, "ChangeCropSize", height);
        var notifications = new List<Task<DraftSnapshot?>>();
        host.Editor.DraftChanged += (_, _) => notifications.Add(host.Editor.CaptureDraftAsync());

        host.Editor.ApplyPendingCrop();

        Assert.Equal(1, Controller(host.Editor).Undo.UndoCount);
        DraftSnapshot snapshot = OnlySnapshot(notifications);
        Assert.False(snapshot.Metadata.ContainsKey("crop"));
        AssertSnapshotDimensions(snapshot, 48, 32);
        Assert.Equal(Controller(host.Editor).Document.ToJson(), snapshot.Document);
        Assert.Equal(Controller(host.Editor).CropOrigin,
            JsonSerializer.Deserialize<PointD>(snapshot.Metadata["origin"], JsonDefaults.Compact));
    });

    [Theory]
    [InlineData(Key.Q, 3)]
    [InlineData(Key.W, 1)]
    public void RotationWhileCroppingPublishesOneDraftWithRotatedPixelsAndPendingRegion(Key key, int turns) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost();
        SelectTool(host, EditorTool.Crop);
        var notifications = new List<Task<DraftSnapshot?>>();
        host.Editor.DraftChanged += (_, _) => notifications.Add(host.Editor.CaptureDraftAsync());

        Assert.True(host.Editor.HandleShortcut(key, ModifierKeys.None));

        DraftSnapshot snapshot = OnlySnapshot(notifications);
        AssertSnapshotDimensions(snapshot, 64, 96);
        Assert.Equal(new RectD(0, 0, 64, 96), CropRegion(snapshot));
        Assert.Equal(turns.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Metadata["turns"]);
        Assert.Equal(Controller(host.Editor).Document.ToJson(), snapshot.Document);
        Assert.Equal(1, Controller(host.Editor).Undo.UndoCount);
        Assert.Equal(EditorTool.Crop, Controller(host.Editor).Tool);
    });

    private static EditorWindowHost CreateHost()
    {
        EditorWindowHost host = EditorWindowHost.Create(Solid(96, 64, 0x30));
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        SelectTool(host, EditorTool.Select);
        return host;
    }

    private static void SelectTool(EditorWindowHost host, EditorTool tool) => Invoke(host.Editor, "SelectTool", tool);

    private static DraftSnapshot OnlySnapshot(List<Task<DraftSnapshot?>> notifications)
    {
        // Drain every encoder task before the assertion, including an unexpected duplicate.
        DraftSnapshot?[] snapshots = notifications.Select(Await).ToArray();
        return Assert.IsType<DraftSnapshot>(Assert.Single(snapshots));
    }

    private static RectD CropRegion(DraftSnapshot snapshot)
    {
        Assert.True(snapshot.Metadata.ContainsKey("crop"));
        return JsonSerializer.Deserialize<RectD>(snapshot.Metadata["crop"], JsonDefaults.Compact);
    }

    private static void AssertSnapshotDimensions(DraftSnapshot snapshot, int width, int height)
    {
        AnnotationDocument document = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(snapshot.Document));
        Assert.Equal(width, document.CanvasWidth);
        Assert.Equal(height, document.CanvasHeight);
        using var stream = new MemoryStream(snapshot.Files["base.png"], writable: false);
        BitmapFrame bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
    }

    private static T Await<T>(Task<T> task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Draft encoding did not finish within ten seconds.");
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            tick.Tick += (_, _) => { tick.Stop(); frame.Continue = false; };
            tick.Start();
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
}
