using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorStableViewportTests : KoreanCaptionTest
{
    public static IEnumerable<object[]> DrawingToolsAndLayouts()
    {
        foreach (double width in new[] { 680d, 1200d })
        foreach ((int imageWidth, int imageHeight) in new[] { (960, 540), (540, 960) })
        foreach (EditorTool tool in Enum.GetValues<EditorTool>().Where(tool => tool is not (EditorTool.Select or EditorTool.Crop)))
            yield return [width, imageWidth, imageHeight, tool.ToString()];
    }

    [Theory]
    [MemberData(nameof(DrawingToolsAndLayouts))]
    public void EveryDrawingToolKeepsViewportAndFitAcrossReleaseSelectionDeletionAndNextDrawing(
        double width, int imageWidth, int imageHeight, string toolName) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        AnnotationEditorController controller = Controller(host.Editor);
        ViewportSnapshot before = Snapshot(host);
        EditorTool tool = Enum.Parse<EditorTool>(toolName);

        AnnotationItem item = Draw(host, tool, before, "first drawing");
        SelectTool(host, EditorTool.Select);
        AssertStable(before, host, "switch to select");
        Click(host, BlankPoint(imageWidth, imageHeight));
        Assert.Null(controller.Selected);
        AssertStable(before, host, "deselect completed drawing");

        Click(host, SelectionPoint(item));
        Assert.Same(item, controller.Selected);
        AssertStable(before, host, "select drawing");
        Click(host, BlankPoint(imageWidth, imageHeight));
        Assert.Null(controller.Selected);
        AssertStable(before, host, "deselect drawing");

        Click(host, SelectionPoint(item));
        Assert.Same(item, controller.Selected);
        Assert.True(host.Editor.HandleShortcut(Key.Delete, ModifierKeys.None));
        Assert.Empty(controller.Document.Items);
        AssertStable(before, host, "delete drawing");
        Draw(host, tool, before, "next drawing");
    });

    [Theory]
    [InlineData(680, 960, 540)]
    [InlineData(1200, 960, 540)]
    [InlineData(680, 540, 960)]
    [InlineData(1200, 540, 960)]
    public void SelectToolKeepsViewportAndFitWhileMovingDeselectingAndDeleting(
        double width, int imageWidth, int imageHeight) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        AnnotationEditorController controller = Controller(host.Editor);
        var item = new RectangleAnnotation { Rect = new RectD(80, 80, 120, 80), Fill = ColorRgba.Black };
        controller.Document.Add(item);
        ViewportSnapshot before = Snapshot(host);
        Point start = WindowPoint(host, new PointD(130, 110));
        Point end = WindowPoint(host, new PointD(160, 130));

        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        Assert.Same(item, controller.Selected);
        AssertStable(before, host, "select pointer down");
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        AssertStable(before, host, "move selected item");
        host.Editor.SimulatePointerUp(ViewportPoint(host, end));
        AssertStable(before, host, "release moved item");
        Assert.Equal(110, item.Rect.X, 8);
        Assert.Equal(100, item.Rect.Y, 8);

        Click(host, BlankPoint(imageWidth, imageHeight));
        Assert.Null(controller.Selected);
        AssertStable(before, host, "deselect moved item");
        Click(host, item.Bounds.Center);
        Assert.Same(item, controller.Selected);
        Assert.True(host.Editor.HandleShortcut(Key.Delete, ModifierKeys.None));
        Assert.Empty(controller.Document.Items);
        AssertStable(before, host, "delete moved item");
        Draw(host, EditorTool.Rectangle, before, "draw after selection");
    });

    [Theory]
    [InlineData(680, 960, 540)]
    [InlineData(1200, 960, 540)]
    [InlineData(680, 540, 960)]
    [InlineData(1200, 540, 960)]
    public void CropEntryValidationGestureAndCancellationKeepViewportAndFit(
        double width, int imageWidth, int imageHeight) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        ViewportSnapshot before = Snapshot(host);
        SelectTool(host, EditorTool.Crop);
        AssertStable(before, host, "enter crop");
        TextBox cropWidth = Field<TextBox>(host.Editor, "_cropWidth");
        TextBox cropHeight = Field<TextBox>(host.Editor, "_cropHeight");
        cropWidth.Text = "invalid";
        cropHeight.Text = "1";
        host.Editor.ApplyPendingCrop();
        Assert.Equal(EditorTool.Crop, Controller(host.Editor).Tool);
        Assert.Equal(UiText.Get("Crop.InvalidSize"), host.Editor.StatusText);
        var errors = Field<Dictionary<TextBox, (Border Border, TextBlock Message)>>(host.Editor, "_cropInputErrors");
        Assert.Equal(Visibility.Visible, errors[cropWidth].Message.Visibility);
        Assert.Equal(Visibility.Visible, errors[cropHeight].Message.Visibility);
        AssertStable(before, host, "show both crop validation errors");

        cropWidth.Text = "240";
        cropHeight.Text = "180";
        Invoke(host.Editor, "ChangeCropSize", cropWidth);
        Invoke(host.Editor, "ChangeCropSize", cropHeight);
        Assert.Equal(Visibility.Collapsed, errors[cropWidth].Message.Visibility);
        Assert.Equal(Visibility.Collapsed, errors[cropHeight].Message.Visibility);
        AssertStable(before, host, "correct crop validation errors");
        PointD center = Field<CropSelection>(host.Editor, "_crop").Region.Center;
        Point start = WindowPoint(host, center);
        Point end = WindowPoint(host, new PointD(center.X + 20, center.Y + 20));
        host.Editor.SimulatePointerDown(ViewportPoint(host, start));
        AssertStable(before, host, "crop pointer down");
        host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
        AssertStable(before, host, "crop pointer move");
        host.Editor.SimulatePointerUp(ViewportPoint(host, end));
        AssertStable(before, host, "crop pointer release");

        host.Editor.CancelPendingCrop();
        Assert.Equal(EditorTool.Select, Controller(host.Editor).Tool);
        AssertStable(before, host, "cancel crop");
        SelectTool(host, EditorTool.Crop);
        AssertStable(before, host, "reenter crop");
        Assert.True(host.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        AssertStable(before, host, "leave crop with Escape");
    });

    [Theory]
    [InlineData(680, 960, 540)]
    [InlineData(1200, 960, 540)]
    [InlineData(680, 540, 960)]
    [InlineData(1200, 540, 960)]
    public void RealWindowResizeRefitsTheImage(double width, int imageWidth, int imageHeight) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        // Windows clamps even an offscreen window to its maximum tracking size, which follows
        // the display: a small CI screen cannot hold the 1440 x 820 DIP target. Ask for that
        // target, keep the size this machine really gives, and start below it by the growth.
        host.Window.Width = width + 240;
        host.Window.Height = 640 + 180;
        host.Window.UpdateLayout();
        double grownWidth = host.Window.ActualWidth, grownHeight = host.Window.ActualHeight;
        double growWidth = Math.Min(240, Math.Floor((grownWidth - host.Window.MinWidth) / 2));
        double growHeight = Math.Min(180, Math.Floor((grownHeight - host.Window.MinHeight) / 2));
        Assert.True(growWidth >= 1 && growHeight >= 1,
            $"This screen leaves no room to grow the editor: largest {grownWidth}x{grownHeight} DIP, minimum {host.Window.MinWidth}x{host.Window.MinHeight} DIP.");
        host.Window.Width = grownWidth - growWidth;
        host.Window.Height = grownHeight - growHeight;
        ViewportSnapshot before = Snapshot(host);
        double beforeWidth = host.Window.ActualWidth, beforeHeight = host.Window.ActualHeight;
        // Change both axes so both portrait and landscape captures have a new limiting axis size.
        host.Window.Width = grownWidth;
        host.Window.Height = grownHeight;
        ViewportSnapshot resized = Snapshot(host);

        Assert.True(host.Window.ActualWidth > beforeWidth && host.Window.ActualHeight > beforeHeight,
            $"Window growth precondition failed: {beforeWidth}x{beforeHeight} -> {host.Window.ActualWidth}x{host.Window.ActualHeight} DIP.");
        Assert.True(resized.ViewportWidth > before.ViewportWidth);
        Assert.True(resized.ViewportHeight > before.ViewportHeight);
        Assert.True(resized.DipPerPixel > before.DipPerPixel);
        Assert.NotEqual(before.FrameBounds, resized.FrameBounds);
        AssertFittedToSurface(host);
    });

    [Theory]
    [InlineData(680, 960, 540)]
    [InlineData(1200, 960, 540)]
    [InlineData(680, 540, 960)]
    [InlineData(1200, 540, 960)]
    public void ApplyingCropRefitsTheImageAndUndoRestoresItsFit(double width, int imageWidth, int imageHeight) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        ViewportSnapshot before = Snapshot(host);
        SelectTool(host, EditorTool.Crop);
        TextBox cropWidth = Field<TextBox>(host.Editor, "_cropWidth");
        TextBox cropHeight = Field<TextBox>(host.Editor, "_cropHeight");
        cropWidth.Text = "240";
        cropHeight.Text = "180";
        host.Editor.ApplyPendingCrop();
        ViewportSnapshot cropped = Snapshot(host);

        Assert.Equal(EditorTool.Select, Controller(host.Editor).Tool);
        Assert.Equal(240, host.Editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(180, host.Editor.DisplayedBitmap.PixelHeight);
        Assert.Equal(before.ViewportWidth, cropped.ViewportWidth, 8);
        Assert.Equal(before.ViewportHeight, cropped.ViewportHeight, 8);
        Assert.True(cropped.DipPerPixel > before.DipPerPixel);
        Assert.NotEqual(before.FrameBounds, cropped.FrameBounds);
        AssertFittedToSurface(host);
        Assert.True(Controller(host.Editor).PerformUndo());
        AssertStable(before, host, "undo applied crop");
    });

    [Theory]
    [InlineData(680, 960, 540)]
    [InlineData(1200, 960, 540)]
    [InlineData(680, 540, 960)]
    [InlineData(1200, 540, 960)]
    public void RotationRefitsTheImageAndUndoRestoresItsFit(double width, int imageWidth, int imageHeight) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width, imageWidth, imageHeight);
        ViewportSnapshot before = Snapshot(host);
        Assert.True(host.Editor.HandleShortcut(Key.W, ModifierKeys.None));
        ViewportSnapshot rotated = Snapshot(host);

        Assert.Equal(imageHeight, host.Editor.DisplayedBitmap.PixelWidth);
        Assert.Equal(imageWidth, host.Editor.DisplayedBitmap.PixelHeight);
        Assert.Equal(before.ViewportWidth, rotated.ViewportWidth, 8);
        Assert.Equal(before.ViewportHeight, rotated.ViewportHeight, 8);
        Assert.NotEqual(before.DipPerPixel, rotated.DipPerPixel);
        Assert.NotEqual(before.FrameBounds, rotated.FrameBounds);
        AssertFittedToSurface(host);
        Assert.True(Controller(host.Editor).PerformUndo());
        AssertStable(before, host, "undo rotation");
    });

    private static EditorWindowHost CreateHost(double width, int imageWidth, int imageHeight)
    {
        EditorWindowHost host = EditorWindowHost.Create(Solid(imageWidth, imageHeight, 0x30));
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        host.Window.Width = width;
        host.Window.Height = 640;
        SelectTool(host, EditorTool.Select);
        Controller(host.Editor).SetSelected(null);
        host.Window.UpdateLayout();
        // Show queues the real entrance at render priority. Let it begin, then establish
        // the geometry baseline after it settles so a later asynchronous edit cannot be
        // the first dispatcher pump that starts the window's own translation.
        EditorMosaicAsyncTests.WaitFor(host.Editor.Dispatcher.InvokeAsync(
            static () => { }, DispatcherPriority.ApplicationIdle).Task);
        if (!EntranceSettled())
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timer = new DispatcherTimer(DispatcherPriority.Render, host.Editor.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            timer.Tick += (_, _) =>
            {
                if (EntranceSettled()) settled.TrySetResult();
            };
            timer.Start();
            try { EditorMosaicAsyncTests.WaitFor(settled.Task); }
            finally { timer.Stop(); }
        }
        host.Window.UpdateLayout();
        return host;

        bool EntranceSettled() => host.Editor.RenderTransform.Value.IsIdentity
            && !host.Editor.RenderTransform.HasAnimatedProperties;
    }

    private static AnnotationItem Draw(EditorWindowHost host, EditorTool tool, ViewportSnapshot before, string phase)
    {
        SelectTool(host, tool);
        AssertStable(before, host, $"{phase}: choose {tool}");
        AnnotationEditorController controller = Controller(host.Editor);
        int itemCount = controller.Document.Items.Count;
        if (tool == EditorTool.Image)
        {
            // Exercise the real insertion path after a picker result, without opening a file dialog.
            var asset = Field<AnnotationImageStore>(host.Editor, "_imageStore").RegisterInMemory(Solid(24, 16, 0x60));
            controller.AddImageAnnotation(asset.AssetFileName, 24, 16, new RectD(80, 80, 120, 80));
            AssertStable(before, host, $"{phase}: insert image");
        }
        else
        {
            Point start = WindowPoint(host, new PointD(80, 80));
            Point end = WindowPoint(host, new PointD(200, 160));
            host.Editor.SimulatePointerDown(ViewportPoint(host, start));
            AssertStable(before, host, $"{phase}: pointer down");
            if (tool is not (EditorTool.Text or EditorTool.StepNumber))
            {
                host.Editor.SimulatePointerMove(ViewportPoint(host, end), leftPressed: true);
                AssertStable(before, host, $"{phase}: pointer move");
            }
            host.Editor.SimulatePointerUp(ViewportPoint(host, tool is EditorTool.Text or EditorTool.StepNumber ? start : end));
            AssertStable(before, host, $"{phase}: pointer release");
            if (tool == EditorTool.Mosaic)
            {
                EditorMosaicAsyncTests.WaitFor(host.Editor.MosaicOperationsSettledAsync());
                AssertStable(before, host, $"{phase}: mosaic completion");
            }
            if (tool == EditorTool.Text)
            {
                TextBox input = Field<TextBox>(host.Editor, "_activeTextBox");
                input.Text = "Stable caption";
                AssertStable(before, host, $"{phase}: live text");
                Invoke(host.Editor, "FinishActiveText", false);
                AssertStable(before, host, $"{phase}: commit text");
            }
        }
        Assert.Equal(itemCount + 1, controller.Document.Items.Count);
        return controller.Document.Items.Last();
    }

    private static void SelectTool(EditorWindowHost host, EditorTool tool)
    {
        Invoke(host.Editor, "SelectTool", tool);
        host.Window.UpdateLayout();
    }

    private static PointD SelectionPoint(AnnotationItem item) => item switch
    {
        RectangleAnnotation or EllipseAnnotation => new PointD(item.Bounds.Right, item.Bounds.Center.Y),
        _ => item.Bounds.Center,
    };

    private static PointD BlankPoint(int width, int height) => new(width - 40, height - 40);

    private static void Click(EditorWindowHost host, PointD point)
    {
        Point windowPoint = WindowPoint(host, point);
        host.Editor.SimulatePointerDown(ViewportPoint(host, windowPoint));
        host.Window.UpdateLayout();
        host.Editor.SimulatePointerUp(ViewportPoint(host, windowPoint));
        host.Window.UpdateLayout();
    }

    private static Point WindowPoint(EditorWindowHost host, PointD imagePoint) =>
        Field<FrameworkElement>(host.Editor, "_viewport").TranslatePoint(host.Editor.ViewportPointOf(imagePoint), host.Window);

    private static Point ViewportPoint(EditorWindowHost host, Point point) =>
        host.Window.TranslatePoint(point, Field<FrameworkElement>(host.Editor, "_viewport"));

    private static ViewportSnapshot Snapshot(EditorWindowHost host)
    {
        host.Window.UpdateLayout();
        var viewport = Field<FrameworkElement>(host.Editor, "_viewport");
        var inspector = Field<FrameworkElement>(host.Editor, "_inspectorPanel");
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        Assert.True(viewport.ActualWidth > 0 && viewport.ActualHeight > 0);
        Assert.True(double.IsFinite(inspector.Height) && inspector.Height > 0);
        Rect frameBounds = surface.TransformToAncestor(host.Window).TransformBounds(surface.FrameRectDip);
        return new ViewportSnapshot(viewport.ActualWidth, viewport.ActualHeight, inspector.Height, surface.DipPerPixel, frameBounds);
    }

    private static void AssertStable(ViewportSnapshot before, EditorWindowHost host, string phase)
    {
        ViewportSnapshot after = Snapshot(host);
        AssertClose(before.ViewportWidth, after.ViewportWidth, phase + " viewport width");
        AssertClose(before.ViewportHeight, after.ViewportHeight, phase + " viewport height");
        AssertClose(before.InspectorHeight, after.InspectorHeight, phase + " inspector budget");
        AssertClose(before.DipPerPixel, after.DipPerPixel, phase + " image scale");
        AssertClose(before.FrameBounds.X, after.FrameBounds.X, phase + " image left");
        AssertClose(before.FrameBounds.Y, after.FrameBounds.Y, phase + " image top");
        AssertClose(before.FrameBounds.Width, after.FrameBounds.Width, phase + " image width");
        AssertClose(before.FrameBounds.Height, after.FrameBounds.Height, phase + " image height");
    }

    private static void AssertClose(double expected, double actual, string context) =>
        Assert.True(Math.Abs(expected - actual) < 0.0000001, $"{context} changed from {expected:R} to {actual:R}.");

    private static void AssertFittedToSurface(EditorWindowHost host)
    {
        var surface = Field<AnnotationEditorSurface>(host.Editor, "_surface");
        double inset = AnnotationEditorSurface.CanvasInset;
        double expectedScale = Math.Min(1 / VisualTreeHelper.GetDpi(surface).PixelsPerDip,
            Math.Min((surface.ActualWidth - 2 * inset) / surface.Frame.PixelWidth,
                (surface.ActualHeight - 2 * inset) / surface.Frame.PixelHeight));
        Assert.Equal(expectedScale, surface.DipPerPixel, 8);
        Rect bounds = surface.FrameRectDip;
        Assert.Equal(surface.Frame.PixelWidth * expectedScale, bounds.Width, 8);
        Assert.Equal(surface.Frame.PixelHeight * expectedScale, bounds.Height, 8);
        Assert.True(bounds.Left >= inset - 0.0000001 && bounds.Top >= inset - 0.0000001);
        Assert.True(bounds.Right <= surface.ActualWidth - inset + 0.0000001 && bounds.Bottom <= surface.ActualHeight - inset + 0.0000001);
        Assert.True(Math.Abs(expectedScale - 1 / VisualTreeHelper.GetDpi(surface).PixelsPerDip) < 0.0000001
            || Math.Abs(bounds.Width - (surface.ActualWidth - 2 * inset)) < 0.0000001
            || Math.Abs(bounds.Height - (surface.ActualHeight - 2 * inset)) < 0.0000001);
    }

    private readonly record struct ViewportSnapshot(double ViewportWidth, double ViewportHeight,
        double InspectorHeight, double DipPerPixel, Rect FrameBounds);
}
