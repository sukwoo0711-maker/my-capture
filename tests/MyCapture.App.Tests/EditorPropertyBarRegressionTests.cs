using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorPropertyBarRegressionTests : KoreanCaptionTest
{
    [Theory]
    [InlineData(1000, 1, false)]
    [InlineData(680, 1, false)]
    [InlineData(1000, 2, false)]
    [InlineData(680, 2, false)]
    [InlineData(1000, 1, true)]
    [InlineData(680, 1, true)]
    [InlineData(1000, 2, true)]
    [InlineData(680, 2, true)]
    public void StationaryWindowClickSelectsWithoutMovingOrAddingUndo(double width, double scale, bool sendMove) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Width = width * scale;
        host.Window.Height = 640 * scale;
        host.Editor.LayoutTransform = new ScaleTransform(scale, scale);
        AnnotationEditorController controller = Controller(host.Editor);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 100, 160, 100), Fill = ColorRgba.Black };
        controller.Document.Add(shape);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(null);
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        double inspectorHeight = StableInspectorHeight(host.Editor);
        string before = controller.Document.ToJson();
        int undoBefore = controller.Undo.UndoCount;
        Point windowPoint = WindowPoint(host, new PointD(170, 150));

        host.Editor.SimulatePointerDown(ViewportPoint(host, windowPoint));
        Assert.Same(shape, controller.Selected);
        host.Window.UpdateLayout();
        // Recompute the viewport point from the SAME window coordinate after reflow.
        // Reusing a viewport/canvas coordinate would hide this regression.
        if (sendMove)
        {
            host.Editor.SimulatePointerMove(ViewportPoint(host, windowPoint), leftPressed: true);
            host.Window.UpdateLayout();
        }
        host.Editor.SimulatePointerUp(ViewportPoint(host, windowPoint));
        host.Window.UpdateLayout();

        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(undoBefore, controller.Undo.UndoCount);
        Assert.Same(shape, controller.Selected);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
    });

    [Theory]
    [InlineData(1000)]
    [InlineData(680)]
    public void MarqueeKeepsWindowCoordinatesWhenPropertiesDisappearAndReappear(double width) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Width = width;
        AnnotationEditorController controller = Controller(host.Editor);
        var first = new RectangleAnnotation { Rect = new RectD(100, 100, 80, 60), Fill = ColorRgba.Black };
        var second = new RectangleAnnotation { Rect = new RectD(240, 190, 10, 10), Fill = ColorRgba.Black };
        controller.Document.Add(first);
        controller.Document.Add(second);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(first);
        host.Window.UpdateLayout();
        string before = controller.Document.ToJson();
        int undoBefore = controller.Undo.UndoCount;
        Point start = WindowPoint(host, new PointD(80, 80));
        Point end = WindowPoint(host, new PointD(261, 211));

        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        Assert.Empty(controller.SelectedItems);
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        AssertMarqueeBounds(controller);
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        host.Window.UpdateLayout();
        AssertMarqueeBounds(controller);
        host.Editor.SimulatePointerUp(ViewportPoint(host, end));

        Assert.Equal(new AnnotationItem[] { first, second }, controller.SelectedItems);
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(undoBefore, controller.Undo.UndoCount);
    });

    [Fact]
    public void ActualWindowDragStillMovesExactlyOnceAndKeepsStablePropertyHeight() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Width = 680;
        AnnotationEditorController controller = Controller(host.Editor);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 100, 160, 100), Fill = ColorRgba.Black };
        controller.Document.Add(shape);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(null);
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        double inspectorHeight = StableInspectorHeight(host.Editor);
        Point start = WindowPoint(host, new PointD(170, 150));
        Point end = WindowPoint(host, new PointD(210, 170));
        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerUp(ViewportPoint(host, end));
        host.Window.UpdateLayout();

        Assert.Equal(140, shape.Rect.X, precision: 8);
        Assert.Equal(120, shape.Rect.Y, precision: 8);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
        Assert.True(Field<FrameworkElement>(host.Editor, "_colorSection").IsVisible);
        Assert.True(controller.PerformUndo());
        Assert.Equal(100, shape.Rect.X, precision: 8);
        Assert.Equal(100, shape.Rect.Y, precision: 8);
        Assert.True(controller.PerformRedo());
        Assert.Equal(140, shape.Rect.X, precision: 8);
        Assert.Equal(120, shape.Rect.Y, precision: 8);
    });

    [Theory]
    [InlineData("escape")]
    [InlineData("rightClick")]
    [InlineData("toolChange")]
    [InlineData("captureLoss")]
    public void CancelledGestureKeepsStablePropertyHeight(string cancellation) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 100, 160, 100), Fill = ColorRgba.Black };
        controller.Document.Add(shape);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(null);
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        double inspectorHeight = StableInspectorHeight(host.Editor);
        string before = controller.Document.ToJson();
        Point start = WindowPoint(host, new PointD(170, 150));
        Point end = WindowPoint(host, new PointD(210, 170));
        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
        switch (cancellation)
        {
            case "escape":
                Assert.True(host.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
                break;
            case "rightClick":
                Invoke(host.Editor, "OnSurfaceRightDown", host.Editor.PointerInputElement,
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                        { RoutedEvent = UIElement.MouseRightButtonDownEvent });
                break;
            case "toolChange":
                Assert.True(host.Editor.HandleShortcut(Key.R, ModifierKeys.None));
                break;
            case "captureLoss":
                host.Editor.PointerInputElement.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent });
                break;
        }
        host.Window.UpdateLayout();
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(0, controller.Undo.UndoCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextEditWhilePointerIsHeldKeepsStablePropertyHeight(bool moveBeforeEditing) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Width = 680;
        AnnotationEditorController controller = Controller(host.Editor);
        var text = new TextAnnotation { Text = "Edit me", Rect = new RectD(100, 100, 200, 80), FontSize = 24 };
        controller.Document.Add(text);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(null);
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        double inspectorHeight = StableInspectorHeight(host.Editor);
        Point point = WindowPoint(host, new PointD(170, 140));
        Point end = WindowPoint(host, new PointD(210, 160));
        host.Editor.SimulatePointerDown(ViewportPoint(host, point));
        Assert.Same(text, controller.Selected);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
        if (moveBeforeEditing)
        {
            host.Window.UpdateLayout();
            host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        }
        Assert.True(host.Editor.HandleShortcut(Key.F2, ModifierKeys.None));
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerUp(ViewportPoint(host, point));

        var input = Field<System.Windows.Controls.TextBox>(host.Editor, "_activeTextBox");
        Assert.NotNull(input);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
        Assert.Equal(text.FontSize * Field<AnnotationEditorSurface>(host.Editor, "_surface").DipPerPixel, input.FontSize);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Equal(new RectD(100, 100, 200, 80), text.Rect);
        Invoke(host.Editor, "FinishActiveText", true);
        Assert.Equal(new RectD(100, 100, 200, 80), text.Rect);
        Assert.Equal(0, controller.Undo.UndoCount);
    });

    [Fact]
    public void SaveDuringDragRestoresLayoutAndHandsOffOnlyCommittedGeometry() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 100, 160, 100), Fill = ColorRgba.Black };
        controller.Document.Add(shape);
        controller.Tool = EditorTool.Select;
        controller.SetSelected(null);
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        double inspectorHeight = StableInspectorHeight(host.Editor);
        string before = controller.Document.ToJson();
        Point start = WindowPoint(host, new PointD(170, 150));
        Point end = WindowPoint(host, new PointD(210, 170));
        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        string? handedOff = null;
        // Replace persistence with an in-memory sink; no disk/clipboard/interactive save.
        host.Editor.CommitRequested = result =>
        {
            handedOff = result.Document.ToJson();
            return Task.FromResult(false);
        };
        host.Editor.SaveAndClose();
        host.Window.UpdateLayout();
        Assert.Equal(before, handedOff);
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Equal(inspectorHeight, StableInspectorHeight(host.Editor));
    });

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(65, 65)]
    [InlineData(128, 128)]
    [InlineData(1, 2)]
    [InlineData(129, 128)]
    public void MosaicPixelBlocksMatchStrengthAndSettingsBounds(int requestedSize, int expectedSize) => StaTestHost.Run(() =>
    {
        const int size = 256;
        byte[] source = new byte[size * size * 4];
        byte[] expected = new byte[source.Length];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            source[offset] = (byte)x;
            source[offset + 1] = (byte)y;
            source[offset + 2] = source[offset + 3] = 255;
            int blockX = x / expectedSize * expectedSize;
            int blockY = y / expectedSize * expectedSize;
            // A linear ramp's exact integer mean is its first value plus half its span.
            expected[offset] = (byte)(blockX + (Math.Min(expectedSize, size - blockX) - 1) / 2);
            expected[offset + 1] = (byte)(blockY + (Math.Min(expectedSize, size - blockY) - 1) / 2);
            expected[offset + 2] = expected[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, source, size * 4);
        bitmap.Freeze();
        using EditorWindowHost host = EditorWindowHost.Create(bitmap);
        host.Editor.ApplyMosaic(new RectD(0, 0, size, size), requestedSize);
        EditorMosaicAsyncTests.WaitFor(host.Editor.MosaicOperationsSettledAsync());

        ImageAnnotation patch = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
        BitmapSource actualBitmap = Field<AnnotationImageStore>(host.Editor, "_imageStore").Get(patch.AssetFileName)!;
        byte[] actual = new byte[source.Length];
        actualBitmap.CopyPixels(actual, size * 4, 0);
        Assert.Equal(new RectD(0, 0, size, size), patch.Rect);
        Assert.Equal(expected, actual);
        Assert.Equal(1, Controller(host.Editor).Undo.UndoCount);
    });

    private static double StableInspectorHeight(AnnotationEditorControl editor)
    {
        double height = Field<FrameworkElement>(editor, "_inspectorPanel").Height;
        Assert.True(double.IsFinite(height) && height > 0, $"Inspector height must retain a positive stable budget; actual: {height}.");
        return height;
    }

    private static Point WindowPoint(EditorWindowHost host, PointD imagePoint) =>
        Field<FrameworkElement>(host.Editor, "_viewport").TranslatePoint(host.Editor.ViewportPointOf(imagePoint), host.Window);

    private static Point ViewportPoint(EditorWindowHost host, Point windowPoint) =>
        host.Window.TranslatePoint(windowPoint, Field<FrameworkElement>(host.Editor, "_viewport"));

    private static void AssertMarqueeBounds(AnnotationEditorController controller)
    {
        RectD marquee = Assert.IsType<RectD>(controller.MarqueeBounds);
        Assert.Equal(80, marquee.X, precision: 8);
        Assert.Equal(80, marquee.Y, precision: 8);
        Assert.Equal(181, marquee.Width, precision: 8);
        Assert.Equal(131, marquee.Height, precision: 8);
    }
}
