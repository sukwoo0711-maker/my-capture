using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
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

/// <summary>
/// Review evidence only: renders the real editor offscreen through a fixed sequence of
/// everyday states and records where the captured image lands in each. It runs only when
/// MYCAPTURE_UX_EVIDENCE names an output folder; ordinary test runs return immediately.
/// </summary>
public sealed class EditorUxEvidenceTests : KoreanCaptionTest
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void RenderEditorScenes() => StaTestHost.Run(() =>
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_UX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var rows = new List<Dictionary<string, object>>();
        try
        {
            foreach ((int width, int height) in new[] { (1000, 640), (760, 600) })
                RenderSequence(root, width, height, rows);
            RenderSourceScene(root, rows);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = animations;
        }
        File.WriteAllText(Path.Combine(root, "scenes.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    });

    private static void RenderSequence(string root, int width, int height, List<Dictionary<string, object>> rows)
    {
        BitmapSource bitmap = SpecTable(900, 520);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, null, null);
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        var window = new Window
        {
            Content = editor,
            Resources = resources,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Left = -6000,
            Top = -6000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            var controller = (AnnotationEditorController)Field(editor, "_controller");
            string prefix = $"{width}x{height}";
            void Scene(string name)
            {
                Settle(window);
                rows.Add(Measure(editor, prefix, name));
                Save(window, Path.Combine(root, $"{prefix}-{name}.png"));
            }

            SelectTool(editor, EditorTool.Select);
            Scene("01-select");
            SetAltRevealed(window, true);
            Scene("02-select-alt");
            SetAltRevealed(window, false);
            SelectTool(editor, EditorTool.Pen);
            Scene("03-pen");
            SelectTool(editor, EditorTool.Rectangle);
            Settle(window);
            editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(120, 90)));
            editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(420, 260)), leftPressed: true);
            editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(420, 260)));
            Scene("04-rectangle-drawn");
            controller.DeleteSelected();
            Scene("05-after-delete");
            SelectTool(editor, EditorTool.Text);
            Scene("06-text-tool");
            SelectTool(editor, EditorTool.Pen);
            Scene("07-pen-again");
            SelectTool(editor, EditorTool.Crop);
            Scene("08-crop");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A web capture whose source chip sits in the status line.</summary>
    private static void RenderSourceScene(string root, List<Dictionary<string, object>> rows)
    {
        BitmapSource bitmap = SpecTable(900, 520);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var provenance = MyCapture.Core.Capture.CaptureProvenance.Create(
            url: "https://github.com/acme/orders-api/blob/main/docs/fields.md?token=demo#L12",
            appName: "Microsoft Edge", windowTitle: "fields.md - orders-api");
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, null, null,
            provenance: provenance);
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        var window = new Window
        {
            Content = editor, Resources = resources, Width = 1000, Height = 640, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Left = -6000, Top = -6000, ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            SelectTool(editor, EditorTool.Select);
            Settle(window);
            rows.Add(Measure(editor, "1000x640", "09-source"));
            Save(window, Path.Combine(root, "1000x640-09-source.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static Dictionary<string, object> Measure(AnnotationEditorControl editor, string prefix, string scene)
    {
        var surface = (AnnotationEditorSurface)Field(editor, "_surface");
        Point topLeft = editor.ViewportPointOf(new PointD(0, 0));
        Point bottomRight = editor.ViewportPointOf(new PointD(900, 520));
        return new Dictionary<string, object>
        {
            ["window"] = prefix,
            ["scene"] = scene,
            ["dipPerPixel"] = Math.Round(surface.DipPerPixel, 5),
            ["surfaceWidth"] = Math.Round(surface.ActualWidth, 2),
            ["surfaceHeight"] = Math.Round(surface.ActualHeight, 2),
            ["imageLeft"] = Math.Round(topLeft.X, 2),
            ["imageTop"] = Math.Round(topLeft.Y, 2),
            ["imageWidth"] = Math.Round(bottomRight.X - topLeft.X, 2),
            ["imageHeight"] = Math.Round(bottomRight.Y - topLeft.Y, 2),
        };
    }

    private static void SelectTool(AnnotationEditorControl editor, EditorTool tool) =>
        typeof(AnnotationEditorControl).GetMethod("SelectTool", Any, [typeof(EditorTool)])!.Invoke(editor, [tool]);

    private static void SetAltRevealed(Window window, bool revealed) =>
        typeof(ShortcutPresentation).GetMethod("SetAltRevealed", Any, [typeof(DependencyObject), typeof(bool)])!
            .Invoke(null, [window, revealed]);

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, Any)!.GetValue(owner)!;

    private static void Settle(Window window)
    {
        // Lets any reveal motion reach its end state before the frame is captured.
        DateTime until = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
        window.UpdateLayout();
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        int width = (int)Math.Ceiling(content.ActualWidth * 2);
        int height = (int)Math.Ceiling(content.ActualHeight * 2);
        var target = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)window.Resources["Surface.Base"] ?? Brushes.Black, null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>A small specification table, the kind of reference people pin beside code.</summary>
    private static BitmapSource SpecTable(int width, int height)
    {
        var visual = new DrawingVisual();
        var mono = new Typeface("Consolas");
        var sans = new Typeface("Malgun Gothic");
        string[][] rows =
        [
            ["필드", "형식", "길이", "설명"],
            ["order_id", "CHAR", "12", "주문 번호, 영문 대문자+숫자"],
            ["placed_at", "DATETIME", "-", "UTC 기준 주문 시각"],
            ["amount", "DECIMAL", "12,2", "결제 금액, 원 단위"],
            ["status", "ENUM", "-", "PAID | CANCELED | REFUNDED"],
            ["memo", "VARCHAR", "200", "고객 요청 사항"],
        ];
        double[] columns = [40, 220, 400, 520];
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)), null, new Rect(0, 0, width, height));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)), null, new Rect(0, 0, width, 56));
            context.DrawText(Text("주문 API 응답 필드 (v2.3)", sans, 22, Brushes.White), new Point(40, 14));
            for (int row = 0; row < rows.Length; row++)
            {
                double y = 90 + row * 64;
                if (row == 0)
                    context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF0)), null, new Rect(24, y - 12, width - 48, 48));
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xDD)), 1), new Point(24, y + 40), new Point(width - 24, y + 40));
                for (int column = 0; column < rows[row].Length; column++)
                {
                    Typeface face = column < 3 && row > 0 ? mono : sans;
                    context.DrawText(Text(rows[row][column], face, 18, new SolidColorBrush(Color.FromRgb(0x1D, 0x23, 0x2C))),
                        new Point(columns[column], y));
                }
            }
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, face, size, brush, 1.0);
}
