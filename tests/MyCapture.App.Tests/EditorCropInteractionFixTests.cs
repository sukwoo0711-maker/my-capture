using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Serialization;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorCropInteractionFixTests(ITestOutputHelper output)
{
    [Fact]
    public void DragInsideUntouchedFullCropCreatesANewRange() => StaTestHost.Run(() =>
    {
        byte[] sourcePixels = new byte[640 * 360 * 4];
        for (int y = 0; y < 360; y++)
        for (int x = 0; x < 640; x++)
        {
            int offset = (y * 640 + x) * 4;
            sourcePixels[offset] = (byte)(x & 255);
            sourcePixels[offset + 1] = (byte)(y & 255);
            sourcePixels[offset + 2] = (byte)((x * 17 + y * 31) & 255);
            sourcePixels[offset + 3] = 255;
        }
        BitmapSource source = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, sourcePixels, 640 * 4);
        source.Freeze();
        using EditorWindowHost host = EditorWindowHost.Create(source);
        var editor = host.Editor;
        editor.HandleShortcut(Key.X, ModifierKeys.None);
        host.Window.UpdateLayout();
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(100, 80)));
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(350, 240)), leftPressed: true);
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(350, 240)));
        RectD actual = editor.PendingCrop!.Region;
        RectD pixels = actual.ToPixelBounds();
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        output.WriteLine($"Crop scale={surface.DipPerPixel:R}; " +
            $"actual X={actual.X:R} Y={actual.Y:R} W={actual.Width:R} H={actual.Height:R}; " +
            $"pixel bounds X={pixels.X:R} Y={pixels.Y:R} W={pixels.Width:R} H={pixels.Height:R}.");
        Assert.Equal(new RectD(100, 80, 250, 160), editor.PendingCrop!.Region);
        var original = new FormatConvertedBitmap(editor.DisplayedBitmap, PixelFormats.Bgra32, null, 0);
        byte[] expected = new byte[250 * 160 * 4];
        original.CopyPixels(new Int32Rect(100, 80, 250, 160), expected, 250 * 4, 0);
        editor.ApplyPendingCrop();
        Assert.Equal((250, 160), (editor.DisplayedBitmap.PixelWidth, editor.DisplayedBitmap.PixelHeight));
        byte[] cropped = new byte[expected.Length];
        new FormatConvertedBitmap(editor.DisplayedBitmap, PixelFormats.Bgra32, null, 0).CopyPixels(cropped, 250 * 4, 0);
        Assert.Equal(expected, cropped);
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
