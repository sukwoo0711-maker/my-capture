using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// How often the editor draws its whole surface (capture, every annotation including blur layers,
/// adorners) while the pointer moves: hovering must not redraw the document at all; only a move
/// that changes the document (a stroke growing, an annotation erased) may.
/// </summary>
public sealed class EditorPointerRenderCostTests(ITestOutputHelper output) : KoreanCaptionTest
{
    private const int Moves = 24;

    private static readonly EditorTool[] Tools =
        [EditorTool.Pen, EditorTool.Highlighter, EditorTool.StepNumber, EditorTool.Eraser, EditorTool.Rectangle, EditorTool.Select];

    private sealed record Row(EditorTool Tool, string Gesture, int Passes, int PreviewPasses, double MillisecondsPerMove);

    [Fact]
    public void HoveringRedrawsNoDocumentAndOnlyDocumentChangesDoWhileDragging() => StaTestHost.Run(() =>
    {
        var rows = new List<Row>();
        foreach (EditorTool tool in Tools)
        {
            rows.Add(Measure(tool, pressed: false));
            rows.Add(Measure(tool, pressed: true));
        }

        var csv = new StringBuilder("tool,gesture,moves,surface_renders,renders_per_move,preview_layer_draws,ms_per_move\n");
        foreach (Row row in rows)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.Tool},{row.Gesture},{Moves},{row.Passes},{row.Passes / (double)Moves:0.##},{row.PreviewPasses},{row.MillisecondsPerMove:0.###}\n");
        }

        output.WriteLine(csv.ToString());
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_EDTOOLS_PHASE");
        string? folder = Environment.GetEnvironmentVariable("MYCAPTURE_EDTOOLS_OUT");
        if (phase is "before" or "after" && !string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(Path.Combine(folder, phase));
            File.WriteAllText(Path.Combine(folder, phase, "render-cost.csv"), csv.ToString());
        }

        foreach (Row row in rows.Where(row => row.Gesture == "hover"))
            Assert.True(row.Passes == 0, $"{row.Tool}: {row.Passes} whole-surface renders for {Moves} hover moves.");

        // The footprint and badge outline still follow every hover move, in their own layer.
        foreach (EditorTool tool in new[] { EditorTool.Pen, EditorTool.Highlighter, EditorTool.StepNumber })
            Assert.Equal(Moves, rows.Single(row => row.Tool == tool && row.Gesture == "hover").PreviewPasses);

        // The eraser path crosses a few annotations: its frame moves only when the target changes.
        Assert.InRange(rows.Single(row => row.Tool == EditorTool.Eraser && row.Gesture == "hover").PreviewPasses, 1, Moves / 2);

        // A stroke that grows on every move is a document change on every move, and no more.
        foreach (EditorTool tool in new[] { EditorTool.Pen, EditorTool.Highlighter })
            Assert.InRange(rows.Single(row => row.Tool == tool && row.Gesture == "drag").Passes, 1, Moves);
    });

    private static Row Measure(EditorTool tool, bool pressed)
    {
        using EditorWindowHost host = EditorWindowHost.Create(Solid(1600, 900, 0x80));
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        // A document worth drawing: two blur layers, shapes, a caption and a stroke.
        controller.Document.Add(new BlurAnnotation { Rect = new RectD(100, 100, 600, 400) });
        controller.Document.Add(new BlurAnnotation { Rect = new RectD(800, 300, 500, 400) });
        controller.Document.Add(new RectangleAnnotation { Rect = new RectD(300, 420, 200, 120), StrokeThickness = 4 });
        controller.Document.Add(new EllipseAnnotation { Rect = new RectD(900, 380, 160, 160), StrokeThickness = 4 });
        controller.Document.Add(new TextAnnotation { Rect = new RectD(1200, 440, 200, 40), Text = "memo", FontSize = 24 });
        controller.Document.Add(new PenAnnotation { Points = [new PointD(150, 600), new PointD(1450, 620)], StrokeThickness = 3 });
        Invoke(editor, "SelectTool", tool);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        host.Window.UpdateLayout();

        Point At(int index) => editor.ViewportPointOf(new PointD(120 + (index * 55), 470 + (index % 3)));
        editor.SimulatePointerMove(At(0), false);
        if (pressed) editor.SimulatePointerDown(At(0));
        host.Window.UpdateLayout();

        int before = surface.RenderPasses;
        int previewBefore = surface.PreviewPasses;
        var clock = Stopwatch.StartNew();
        for (int index = 1; index <= Moves; index++)
        {
            editor.SimulatePointerMove(At(index), pressed);
            host.Window.UpdateLayout();
        }

        clock.Stop();
        int passes = surface.RenderPasses - before;
        // A whole-surface render also redraws the preview layer; count only the layer's own draws.
        int previewPasses = surface.PreviewPasses - previewBefore - passes;
        if (pressed) editor.SimulatePointerUp(At(Moves));
        return new Row(tool, pressed ? "drag" : "hover", passes, previewPasses, clock.Elapsed.TotalMilliseconds / Moves);
    }
}
