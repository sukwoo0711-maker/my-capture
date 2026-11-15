using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The real editor window at its smallest size and on a 1024x768 screen, in Korean and English:
/// every tool on the rail, the eraser included, is inside the rail, inside the window and is the
/// element a click at its centre reaches. The rail never scrolls; it only tightens its spacing.
/// </summary>
public sealed class EditorToolRailFitTests(ITestOutputHelper output) : KoreanCaptionTest
{
    public static TheoryData<string, int, int> Sizes()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            // 0 x 0 stands for the window's own minimum size.
            data.Add(language, 0, 0);
            data.Add(language, 1044, 788);
            data.Add(language, 1024, 768);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryRailToolIncludingTheEraserIsVisibleAndReachable(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using IDisposable culture = UiText.UseLanguage(language);
        using EditorWindowHost host = EditorWindowHost.Create(Solid(1600, 900, 0x50), beforeShow: window =>
        {
            window.Width = width > 0 ? width : AnnotationEditorWindow.MinimumSize.Width;
            window.Height = height > 0 ? height : AnnotationEditorWindow.MinimumSize.Height;
        });
        AnnotationEditorWindow window = host.Window;
        AnnotationEditorControl editor = host.Editor;
        window.UpdateLayout();
        double expected = height > 0 ? height : Math.Max(AnnotationEditorWindow.MinimumSize.Height, window.MinHeight);
        Assert.Equal(expected, window.ActualHeight, 1);

        Border rail = Descendants(editor).OfType<Border>().Single(border => border.Name == "AnnotationToolRail");
        var groups = (FrameworkElement)rail.Child;
        Dictionary<EditorTool, ToggleButton> tools = Field<Dictionary<EditorTool, ToggleButton>>(editor, "_toolButtons");
        Assert.Contains(EditorTool.Eraser, tools.Keys);
        List<ButtonBase> buttons = Descendants(rail).OfType<ButtonBase>().Where(button => button.IsVisible).ToList();
        Assert.Equal(tools.Count + 1, buttons.Count); // every tool plus the stamp command
        Rect railBounds = Bounds(rail, editor);
        Rect editorBounds = new(editor.RenderSize);
        Assert.True(editorBounds.Contains(railBounds), $"rail {railBounds} outside editor {editorBounds}");
        // Nothing of the tool groups hangs below the rail: it is never clipped.
        Assert.True(groups.ActualHeight <= rail.ActualHeight - rail.Padding.Top - rail.Padding.Bottom + 0.5,
            $"groups {groups.ActualHeight} taller than rail {rail.ActualHeight}");

        foreach (ButtonBase button in buttons)
        {
            string name = System.Windows.Automation.AutomationProperties.GetName(button);
            Rect bounds = Bounds(button, editor);
            Assert.True(button.IsEnabled, name);
            Assert.True(button.ActualWidth >= 36 && button.ActualHeight >= 36, $"{name} {button.RenderSize}");
            Assert.True(railBounds.Contains(bounds), $"{name} {bounds} outside rail {railBounds}");
            Point centre = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Assert.Same(button, Owner(editor.InputHitTest(centre) as DependencyObject));
        }

        // The eraser responds to a click of its rail button.
        ToggleButton eraser = tools[EditorTool.Eraser];
        eraser.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, eraser));
        Assert.Equal(EditorTool.Eraser, Controller(editor).Tool);

        double spare = rail.ActualHeight - rail.Padding.Top - rail.Padding.Bottom - groups.ActualHeight;
        bool compact = eraser.Margin.Top == 0;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{language} {window.ActualWidth}x{window.ActualHeight}: rail {rail.ActualHeight:0.#}, tools {groups.ActualHeight:0.#}, spare {spare:0.#}, compact {compact}"));

        if (width == 0 && Environment.GetEnvironmentVariable("MYCAPTURE_EDTOOLS_OUT") is { Length: > 0 } folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (!Path.IsPathFullyQualified(folder)) throw new IOException("Evidence requires an absolute output directory.");
            string target = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(folder), "after"));
            string tag = language[..2];
            File.AppendAllText(DiagnosticOutputPaths.Child(target, "rail-fit.csv"), string.Create(CultureInfo.InvariantCulture,
                $"{tag},{window.ActualWidth},{window.ActualHeight},with-eraser,{rail.ActualHeight:0.##},{groups.ActualHeight:0.##},{spare:0.##},{compact}\n"));
            Save(rail, DiagnosticOutputPaths.Child(target, $"rail-minimum-{tag}-with-eraser.png"));
            // The rail as it was before the eraser: the freehand group back to one row.
            eraser.Visibility = Visibility.Collapsed;
            window.UpdateLayout();
            double spareBefore = rail.ActualHeight - rail.Padding.Top - rail.Padding.Bottom - groups.ActualHeight;
            File.AppendAllText(DiagnosticOutputPaths.Child(target, "rail-fit.csv"), string.Create(CultureInfo.InvariantCulture,
                $"{tag},{window.ActualWidth},{window.ActualHeight},without-eraser,{rail.ActualHeight:0.##},{groups.ActualHeight:0.##},{spareBefore:0.##},{tools[EditorTool.Pen].Margin.Top == 0}\n"));
            Save(rail, DiagnosticOutputPaths.Child(target, $"rail-minimum-{tag}-without-eraser.png"));
            eraser.Visibility = Visibility.Visible;
        }
    });

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static ButtonBase? Owner(DependencyObject? hit)
    {
        for (DependencyObject? node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase button) return button;
        }

        return null;
    }

    private static void Save(FrameworkElement element, string path)
    {
        double dpi = VisualTreeHelper.GetDpi(element).PixelsPerInchX;
        double scale = dpi / 96;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
