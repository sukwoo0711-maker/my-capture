using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorCropInteractionFixTests
{
    [Fact]
    public void DragInsideUntouchedFullCropCreatesANewRange() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        editor.HandleShortcut(Key.X, ModifierKeys.None);
        host.Window.UpdateLayout();
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(100, 80)));
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(350, 240)), leftPressed: true);
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(350, 240)));
        Assert.Equal(new RectD(100, 80, 250, 160), editor.PendingCrop!.Region);
    });

    [Fact]
    public void ShapeUndoAndRedoKeepCropButCanvasRotationResetsIt() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(20, 20));
        controller.PointerMove(new PointD(120, 80));
        controller.PointerUp(new PointD(120, 80));
        editor.HandleShortcut(Key.X, ModifierKeys.None);
        var range = new RectD(60, 50, 200, 150);
        editor.PendingCrop!.SetRegion(range);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Empty(controller.Document.Items);
        Assert.Equal(range, editor.PendingCrop.Region);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        Assert.Single(controller.Document.Items);
        Assert.Equal(range, editor.PendingCrop.Region);
        editor.HandleShortcut(Key.W, ModifierKeys.None);
        Assert.Equal(new RectD(0, 0, 360, 640), editor.PendingCrop.Region);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Typed101PixelsProducesExactly101Pixels(bool width) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        editor.HandleShortcut(Key.X, ModifierKeys.None);
        var field = Field<TextBox>(editor, width ? "_cropWidth" : "_cropHeight");
        field.Text = "101";
        Invoke(editor, "ChangeCropSize", field);
        Assert.Equal(Math.Floor(editor.PendingCrop!.Region.X), editor.PendingCrop.Region.X);
        Assert.Equal(Math.Floor(editor.PendingCrop.Region.Y), editor.PendingCrop.Region.Y);
        editor.ApplyPendingCrop();
        Assert.Equal(101, width ? editor.DisplayedBitmap.PixelWidth : editor.DisplayedBitmap.PixelHeight);
    });

    [Fact]
    public void RecoveredCropRestoresModeAndSurvivesAnnotationUndo() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        var range = new RectD(17, 23, 101, 83);
        var controller = Controller(editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(20, 20));
        controller.PointerMove(new PointD(80, 70));
        controller.PointerUp(new PointD(80, 70));
        editor.RestorePendingDraftCrop(JsonSerializer.Serialize(range, JsonDefaults.Compact));
        Assert.Equal(EditorTool.Crop, Controller(editor).Tool);
        Assert.True(editor.IsCropCommandBarVisible);
        Assert.Equal(range, editor.PendingCrop!.Region);
        Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
        Assert.Equal(range, editor.PendingCrop.Region);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Empty(controller.Document.Items);
        Assert.Equal(range, editor.PendingCrop.Region);
        editor.ApplyPendingCrop();
        Assert.Equal(101, editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(83, editor.DisplayedBitmap.PixelHeight);
    });
}
