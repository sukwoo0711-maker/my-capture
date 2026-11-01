using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPointerStepEvidenceTests : KoreanCaptionTest
{
    [Fact]
    public void RenderStepBarAndArrowSweep() => StaTestHost.Run(() =>
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_POINTER_PHASE");
        if (phase is not ("before" or "after")) return;
        bool? previousAnimations = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        string root = Path.Combine(FindRoot(), "work", "editor-pointer-step");
        Directory.CreateDirectory(root);
        BitmapSource bitmap = Source();
        var region = new RectD(0, 0, 900, 520);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, null, null);
        var resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        var window = new Window
        {
            Content = editor, Resources = resources, Width = 1100, Height = 700,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            Settle(window);
            var controller = Field<AnnotationEditorController>(editor, "_controller");
            Select(editor, EditorTool.StepNumber);
            editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(450, 260)));
            editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(450, 260)));
            Settle(window);
            Save(editor, Path.Combine(root, $"{phase}-step-properties.png"));
            controller.DeleteSelected();
            Select(editor, EditorTool.Select);
            var arrow = PolylineAnnotation.CreateArrow(new PointD(225, 130), new PointD(630, 364), ColorRgba.FromRgb(239, 68, 68), 5);
            controller.Document.Add(arrow);
            controller.SetSelected(arrow);
            Settle(window);
            Save(editor, Path.Combine(root, $"{phase}-arrow-start.png"));
            editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(630, 364)));
            editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(225, 364)), true);
            editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(180, 400)), true);
            editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(180, 400)));
            Settle(window);
            Save(editor, Path.Combine(root, $"{phase}-arrow-sweep.png"));
            controller.PerformUndo();
            Settle(window);
            Save(editor, Path.Combine(root, $"{phase}-arrow-undo.png"));
        }
        finally { window.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previousAnimations; }
    });

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Project root missing.");
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static void Select(AnnotationEditorControl editor, EditorTool tool) =>
        typeof(AnnotationEditorControl).GetMethod("SelectTool", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [tool]);

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Source()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 900, 520));
            var pen = new Pen(Brushes.LightGray, 1);
            for (int x = 0; x <= 900; x += 50) dc.DrawLine(pen, new Point(x, 0), new Point(x, 520));
            for (int y = 0; y <= 520; y += 50) dc.DrawLine(pen, new Point(0, y), new Point(900, y));
        }
        var source = new RenderTargetBitmap(900, 520, 96, 96, PixelFormats.Pbgra32);
        source.Render(visual);
        source.Freeze();
        return source;
    }
}
