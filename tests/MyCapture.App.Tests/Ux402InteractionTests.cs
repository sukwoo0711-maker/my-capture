using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ux402InteractionTests
{
    [Theory]
    [InlineData(AfterCaptureAction.Editor, true)]
    [InlineData(AfterCaptureAction.QuickAccess, false)]
    [InlineData(AfterCaptureAction.ClipboardOnly, false)]
    public void CaptureModesPersistOnceAndOnlyEditorModeOpensAnEditor(AfterCaptureAction action, bool opensEditor) => StaTestHost.Run(() =>
    {
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        using var coordinator = new CaptureOverlayCoordinator(engine, new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance);
        int saves = 0, closed = 0;
        coordinator.SelectionPersistRequested = _ => { saves++; return Task.CompletedTask; };
        coordinator.OpenEditorAfterCapture = () => AfterCaptureBehavior.Resolve(action, true) == AfterCaptureAction.Editor;
        coordinator.OverlayClosed += (_, _) => closed++;
        var image = Image(); var region = new RectD(0, 0, 640, 360);
        Assert.True(coordinator.StartWithSelection(new FrozenFrame(image, region, null, 0), region));
        Assert.True(coordinator.LastTransitionForTest.IsCompletedSuccessfully);
        Assert.Equal(1, saves); Assert.Equal(opensEditor, coordinator.CurrentEditor is not null);
        Assert.Equal(opensEditor, coordinator.IsActive);
        Assert.Equal(opensEditor ? 0 : 1, closed);
        Assert.Equal(AfterCaptureAction.Editor, AfterCaptureBehavior.Resolve(action, false));
        coordinator.Cancel();
    });

    [Fact]
    public void CropCanBeAdjustedCancelledAppliedUndoneAndRotated() => StaTestHost.Run(() =>
    {
        var editor = Editor();
        Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
        Assert.Equal(new RectD(0, 0, 640, 360), editor.PendingCrop!.Region);
        editor.PendingCrop.SetWidth(200); editor.PendingCrop.SetHeight(100);
        double left = editor.PendingCrop.Region.Left;
        Assert.True(editor.HandleShortcut(Key.Right, ModifierKeys.None));
        Assert.Equal(left + 1, editor.PendingCrop.Region.Left);
        Assert.True(editor.HandleShortcut(Key.Down, ModifierKeys.Shift));
        Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Null(editor.PendingCrop); Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
        editor.HandleShortcut(Key.X, ModifierKeys.None); editor.PendingCrop!.SetWidth(200); editor.PendingCrop.SetHeight(100);
        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        Assert.Null(editor.PendingCrop); Assert.Equal(200, editor.CreateCurrentImageSnapshot().CanvasWidth);
        Assert.Equal(100, editor.CreateCurrentImageSnapshot().CanvasHeight);
        editor.HandleShortcut(Key.Z, ModifierKeys.Control);
        Assert.Equal(640, editor.CreateCurrentImageSnapshot().CanvasWidth);
        editor.HandleShortcut(Key.Y, ModifierKeys.Control);
        Assert.Equal(200, editor.CreateCurrentImageSnapshot().CanvasWidth);
        editor.HandleShortcut(Key.W, ModifierKeys.None);
        Assert.Equal(100, editor.CreateCurrentImageSnapshot().CanvasWidth);
        editor.HandleShortcut(Key.X, ModifierKeys.None);
        Assert.Equal(100, editor.PendingCrop!.Bounds.Width);
        editor.HandleShortcut(Key.R, ModifierKeys.None);
        Assert.Null(editor.PendingCrop);
        Assert.False(editor.HandleShortcut(Key.R, ModifierKeys.Control));
    });

    [Theory]
    [InlineData(1000, 620)] [InlineData(680, 460)]
    public void AllToolsFitWithoutScrollingAndHintsFollowPreference(int width, int height) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var image = Image(); var region = new RectD(0, 0, 640, 360);
        var window = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image) { Width = width, Height = height, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var tools = Descendants(window).OfType<ToggleButton>().Where(button => AutomationProperties.GetAcceleratorKey(button).Length > 0).ToArray();
            // Every tool but Crop, plus the stamp command beside Image.
            Assert.Equal(Enum.GetValues<EditorTool>().Length, tools.Length);
            Assert.Single(tools, button => AutomationProperties.GetAcceleratorKey(button)
                == ShortcutCatalog.Get(ShortcutScope.Annotation, "Stamp").Hint);
            foreach (var button in tools)
            {
                Rect bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight && bounds.Width >= 30 && bounds.Height >= 30);
                Assert.Contains(AutomationProperties.GetAcceleratorKey(button), button.ToolTip!.ToString());
            }
            var read = ShortcutPresentation.ReadEnabled;
            try
            {
                ShortcutPresentation.ReadEnabled = () => false; ShortcutPresentation.Refresh();
                Assert.All(tools, button => ShortcutMotionTestScope.AssertState(
                    Assert.Single(Descendants(button).OfType<TextBlock>()), false));
            }
            finally { ShortcutPresentation.ReadEnabled = read; ShortcutPresentation.Refresh(); }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EveryCatalogEntryHasARealHandlerAndUnmodifiedKeysDoNotAcceptControl() => StaTestHost.Run(() =>
    {
        var image = Image(); var editor = Editor();
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        var ocr = new OcrResultWindow();
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyCapture-ux402-commands-" + Guid.NewGuid().ToString("N"))), NullLoggerFactory.Instance);
        try
        {
            var handlers = new Dictionary<ShortcutScope, IReadOnlyDictionary<string, Action>>
            { [ShortcutScope.Annotation] = editor.ShortcutActions, [ShortcutScope.Crop] = editor.CropShortcutActions,
                [ShortcutScope.QuickAccess] = card.ShortcutActions, [ShortcutScope.Ocr] = ocr.ShortcutActions, [ShortcutScope.Video] = video.ShortcutActions };
            Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(entry => (entry.Scope, entry.Key, entry.Modifiers)).Distinct().Count());
            foreach (var entry in ShortcutCatalog.All)
            {
                Assert.True(handlers[entry.Scope].ContainsKey(entry.Command), entry.ToString());
                Assert.Equal(entry.Command, ShortcutCatalog.Match(entry.Scope, entry.Key, entry.Modifiers));
            }
            Assert.Equal("Copy", ShortcutCatalog.Match(ShortcutScope.QuickAccess, Key.C, ModifierKeys.Control));
            Assert.Equal("Save", ShortcutCatalog.Match(ShortcutScope.QuickAccess, Key.S, ModifierKeys.Control));
            Assert.Null(ShortcutCatalog.Match(ShortcutScope.QuickAccess, Key.C, ModifierKeys.Control | ModifierKeys.Shift));
            // Ctrl+A selects all annotations; bare A still selects the Arrow tool.
            Assert.Equal("SelectAll", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.A, ModifierKeys.Control));
            Assert.Equal("Arrow", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.A, ModifierKeys.None));
            Assert.Null(ShortcutCatalog.Match(ShortcutScope.Annotation, Key.R, ModifierKeys.Control));
            Assert.Null(ShortcutCatalog.Match(ShortcutScope.Annotation, Key.A, ModifierKeys.Control | ModifierKeys.Shift));
        }
        finally { card.Close(); ocr.CloseForExit(); video.Close(); }
    });

    [Fact]
    public void ReusedOcrWindowTracksReplacedThemeBrushes() => StaTestHost.Run(() =>
    {
        var window = new OcrResultWindow();
        try
        {
            foreach (Color color in new[] { Colors.Black, Colors.White })
            {
                var surface = new SolidColorBrush(color);
                var text = new SolidColorBrush(color == Colors.Black ? Colors.White : Colors.Black);
                window.Resources["Surface.Base"] = surface;
                window.Resources["Surface.Sunken"] = surface;
                window.Resources["Text.Primary"] = text;
                window.Resources["Text.Secondary"] = text;
                var root = Assert.IsType<Grid>(window.Content);
                Assert.Same(surface, window.Background);
                Assert.Same(surface, root.Background);
                Assert.All(root.Children.OfType<Border>(), border => Assert.Same(surface, border.Background));
                var heading = Assert.IsType<StackPanel>(Assert.IsType<Border>(root.Children[0]).Child);
                Assert.All(heading.Children.OfType<TextBlock>(), block => Assert.Same(text, block.Foreground));
                Assert.Same(surface, Descendants(root).OfType<TextBox>().Single().Background);
            }
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void FailedVideoRenderCleanupRemovesOnlyItsPrivateStage() => StaTestHost.Run(() =>
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        var paths = AppPaths.CreateForRoot(root);
        string directory = ValidatedFilePaths.CreateDirectory(paths.GetCaptureDirectory(Guid.NewGuid(), DateTimeOffset.UtcNow));
        string source = ValidatedFilePaths.FileWithinRoot(root, System.IO.Path.Combine(directory, "source.mp4"), ".mp4");
        string stageName = $".render-{Guid.NewGuid():N}.mp4";
        string stage = ValidatedFilePaths.FileWithinRoot(root, System.IO.Path.Combine(directory, stageName), ".mp4");
        string outside = ValidatedFilePaths.FileWithinRoot(root, stageName, ".mp4");
        string sibling = ValidatedFilePaths.FileWithinRoot(root, System.IO.Path.Combine(paths.CapturesRoot, stageName), ".mp4");
        string invalidName = ValidatedFilePaths.FileWithinRoot(root, System.IO.Path.Combine(directory, ".render-invalid.mp4"), ".mp4");
        foreach (string path in new[] { source, stage, outside, sibling, invalidName }) System.IO.File.WriteAllText(path, "synthetic fixture");
        var window = new VideoEditorWindow(new RecordingResult(source, 3000, 15, 45, 640, 360), paths, NullLoggerFactory.Instance)
            { RenderStagingPathFactory = () => stage };
        var cleanup = typeof(VideoEditorWindow).GetMethod("DeletePrivateRenderStage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            foreach (string path in new[] { source, outside, sibling, invalidName })
            {
                cleanup.Invoke(window, [path]);
                Assert.True(System.IO.File.Exists(path), path);
            }
            cleanup.Invoke(window, [stage]);
            Assert.False(System.IO.File.Exists(stage));
            Assert.True(System.IO.File.Exists(source));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)]
    public void CardStaysNearRegionAndInsideNegativeCoordinateMonitor(double scale)
    {
        var work = new RectD(-1920, 0, 1920, 1080);
        var capture = new RectD(-1800, 100, 400, 200);
        Rect position = QuickAccessWindow.Position(work, scale, 280, 110, capture);
        Assert.Equal(capture.Right / scale + 8, position.Left);
        Assert.Equal(capture.Bottom / scale + 8, position.Top);
        foreach (RectD region in new[] { work, new RectD(-200, 990, 200, 90), new RectD(-1920, 0, 50, 50) })
        {
            Rect bounds = QuickAccessWindow.Position(work, scale, 280, 110, region);
            Assert.True(bounds.Left >= work.Left / scale && bounds.Top >= work.Top / scale && bounds.Right <= work.Right / scale && bounds.Bottom <= work.Bottom / scale);
        }
    }

    private static AnnotationEditorControl Editor()
    {
        var image = Image(); var region = new RectD(0, 0, 640, 360);
        return new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image);
    }
    private static BitmapSource Image()
    {
        var bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 360 * 4], 640 * 4);
        bitmap.Freeze(); return bitmap;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
