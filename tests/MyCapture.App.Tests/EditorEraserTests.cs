using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The eraser tool: a click removes the one annotation it touches, a drag removes every one it
/// touches as a single undo step, and the capture itself is never erased.
/// </summary>
public sealed class EditorEraserTests : KoreanCaptionTest
{
    private static readonly PointD Far = new(700, 440);

    /// <summary>Every kind of annotation the editor makes, each with a point on its ink.</summary>
    public static TheoryData<string> Kinds() =>
    [
        "pen", "highlighter", "rectangle", "ellipse", "arrow", "line", "text", "step",
        "mosaic", "blur", "image", "watermark",
    ];

    private static (AnnotationItem Item, PointD Touch) Make(string kind) => kind switch
    {
        "pen" => (new PenAnnotation { Points = [new PointD(100, 100), new PointD(160, 100)], StrokeThickness = 3 }, new PointD(130, 100)),
        "highlighter" => (new PenAnnotation { IsHighlighter = true, Points = [new PointD(100, 100), new PointD(160, 100)], StrokeThickness = 14 }, new PointD(130, 104)),
        "rectangle" => (new RectangleAnnotation { Rect = new RectD(100, 80, 80, 50), StrokeThickness = 3 }, new PointD(100, 100)),
        "ellipse" => (new EllipseAnnotation { Rect = new RectD(100, 60, 80, 80), StrokeThickness = 3 }, new PointD(100, 100)),
        "arrow" => (new PolylineAnnotation { Points = [new PointD(100, 100), new PointD(180, 100)], HeadAtEnd = true, StrokeThickness = 3 }, new PointD(140, 100)),
        "line" => (new PolylineAnnotation { Points = [new PointD(100, 100), new PointD(180, 100)], HeadAtEnd = false, StrokeThickness = 3 }, new PointD(140, 101)),
        "text" => (new TextAnnotation { Rect = new RectD(100, 90, 80, 28), Text = "memo", FontSize = 18 }, new PointD(120, 104)),
        "step" => (new TextAnnotation { Rect = new RectD(90, 90, 24, 24), Text = "1", IsStepNumber = true, Background = ColorRgba.Black }, new PointD(102, 102)),
        "mosaic" => (new ImageAnnotation { AssetFileName = "mosaic.png", SourceWidth = 60, SourceHeight = 40, Rect = new RectD(80, 80, 60, 40) }, new PointD(110, 100)),
        "blur" => (new BlurAnnotation { Rect = new RectD(80, 80, 60, 40) }, new PointD(110, 100)),
        "image" => (new ImageAnnotation { AssetFileName = "logo.png", SourceWidth = 60, SourceHeight = 40, Rect = new RectD(80, 80, 60, 40) }, new PointD(110, 100)),
        "watermark" => (new ImageAnnotation
        {
            AssetFileName = "mark.png", SourceWidth = 60, SourceHeight = 40, Rect = new RectD(80, 80, 60, 40),
            Opacity = 0.3, Blend = ImageBlend.IntoPage,
        }, new PointD(110, 100)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AClickErasesTheTouchedAnnotationWholeAndOneUndoBringsItBack(string kind)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var below = new RectangleAnnotation { Rect = new RectD(600, 400, 60, 30) };
        (AnnotationItem item, PointD touch) = Make(kind);
        doc.Add(below);
        doc.Add(item);
        var other = new RectangleAnnotation { Rect = new RectD(Far.X, Far.Y, 40, 30) };
        doc.Add(other);
        int index = doc.IndexOf(item);
        int undoBefore = undo.UndoCount;

        c.Tool = EditorTool.Eraser;
        Assert.Same(item, c.EraserTargetAt(touch));
        Assert.Equal(PointerAction.Erase, c.ResolvePointerIntent(touch).Action);
        Assert.Same(item, c.ResolvePointerIntent(touch).Target);
        c.PointerDown(touch);
        Assert.True(c.IsErasing);
        Assert.DoesNotContain(item, doc.Items);
        c.PointerUp(touch);
        Assert.False(c.IsErasing);

        Assert.Equal(1, c.LastEraseCount);
        Assert.Equal([below, other], doc.Items);
        Assert.Equal(undoBefore + 1, undo.UndoCount);
        Assert.Equal(EditorTool.Eraser, c.Tool);

        Assert.True(c.PerformUndo());
        Assert.Equal(index, doc.IndexOf(item));
        Assert.Equal(3, doc.Items.Count);
        Assert.True(c.PerformRedo());
        Assert.DoesNotContain(item, doc.Items);
    }

    [Fact]
    public void ADragErasesEverythingItTouchesAsOneUndoStepAndLeavesTheRest()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var thin = new PenAnnotation { Points = [new PointD(200, 40), new PointD(200, 300)], StrokeThickness = 1 };
        var box = new RectangleAnnotation { Rect = new RectD(300, 120, 60, 60) };
        var badge = new TextAnnotation { Rect = new RectD(500, 138, 24, 24), Text = "2", IsStepNumber = true, Background = ColorRgba.Black };
        var image = new ImageAnnotation { AssetFileName = "a.png", SourceWidth = 40, SourceHeight = 40, Rect = new RectD(600, 130, 40, 40) };
        var untouched = new RectangleAnnotation { Rect = new RectD(300, 300, 60, 60) };
        foreach (AnnotationItem item in new AnnotationItem[] { thin, box, badge, image, untouched }) doc.Add(item);
        AnnotationItem[] original = [.. doc.Items];
        int undoBefore = undo.UndoCount;

        c.Tool = EditorTool.Eraser;
        c.PointerDown(new PointD(100, 150));
        Assert.Equal(original, doc.Items);
        // One long move: the path between the two points is sampled, so the 1 px stroke is not skipped.
        c.PointerMove(new PointD(450, 150));
        Assert.DoesNotContain(thin, doc.Items);
        Assert.DoesNotContain(box, doc.Items);
        Assert.Equal(undoBefore, undo.UndoCount);
        c.PointerMove(new PointD(700, 150));
        c.PointerUp(new PointD(700, 150));

        Assert.Equal(4, c.LastEraseCount);
        Assert.Equal([untouched], doc.Items);
        Assert.Equal(undoBefore + 1, undo.UndoCount);

        Assert.True(c.PerformUndo());
        Assert.Equal(original, doc.Items);
        Assert.Equal(undoBefore, undo.UndoCount);
        Assert.True(c.PerformRedo());
        Assert.Equal([untouched], doc.Items);
    }

    [Fact]
    public void OverlappingAnnotationsUnderOneClickAreAllErased()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var a = new RectangleAnnotation { Rect = new RectD(100, 100, 50, 50) };
        var b = new EllipseAnnotation { Rect = new RectD(100, 100, 50, 50) };
        doc.Add(a);
        doc.Add(b);
        c.Tool = EditorTool.Eraser;
        c.PointerDown(new PointD(100, 125));
        c.PointerUp(new PointD(100, 125));
        Assert.Empty(doc.Items);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal([a, b], doc.Items);
    }

    [Fact]
    public void OnAnEmptyAreaNothingChangesAndNoUndoStepIsAdded()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var rect = new RectangleAnnotation { Rect = new RectD(100, 100, 50, 50) };
        doc.Add(rect);
        c.Tool = EditorTool.Eraser;
        var empty = new PointD(400, 300);
        Assert.Null(c.EraserTargetAt(empty));
        Assert.Null(c.ResolvePointerIntent(empty).Target);
        c.PointerDown(empty);
        c.PointerMove(new PointD(420, 320));
        c.PointerUp(new PointD(420, 320));
        Assert.Equal(0, c.LastEraseCount);
        Assert.Equal([rect], doc.Items);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void TheReachIsAScreenDistanceSoItShrinksInImagePixelsWhenZoomedIn()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var line = new PenAnnotation { Points = [new PointD(100, 100), new PointD(200, 100)], StrokeThickness = 2 };
        doc.Add(line);
        c.Tool = EditorTool.Eraser;
        var near = new PointD(150, 107);
        Assert.Equal(AnnotationEditorController.EraserRadiusDip, c.EraserReach, 6);
        Assert.Same(line, c.EraserTargetAt(near));

        c.DipPerPixel = 4;
        Assert.Equal(2, c.EraserReach, 6);
        Assert.Null(c.EraserTargetAt(near));
        Assert.Same(line, c.EraserTargetAt(new PointD(150, 101.5)));
    }

    [Fact]
    public void CancellingADragPutsBackWhatItErasedWithoutAnUndoStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var a = new RectangleAnnotation { Rect = new RectD(100, 100, 50, 50) };
        var b = new RectangleAnnotation { Rect = new RectD(300, 100, 50, 50) };
        var c2 = new RectangleAnnotation { Rect = new RectD(500, 100, 50, 50) };
        doc.Add(a);
        doc.Add(b);
        doc.Add(c2);
        c.Tool = EditorTool.Eraser;
        c.PointerDown(new PointD(100, 120));
        c.PointerMove(new PointD(300, 120));
        Assert.Equal([c2], doc.Items);
        c.CancelGesture();
        Assert.False(c.IsErasing);
        Assert.Equal([a, b, c2], doc.Items);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void ASelectedAnnotationThatIsErasedLeavesTheSelection()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var a = new RectangleAnnotation { Rect = new RectD(100, 100, 50, 50) };
        var b = new RectangleAnnotation { Rect = new RectD(300, 100, 50, 50) };
        doc.Add(a);
        doc.Add(b);
        c.SelectAll();
        c.Tool = EditorTool.Eraser;
        c.PointerDown(new PointD(100, 120));
        c.PointerUp(new PointD(100, 120));
        Assert.DoesNotContain(a, c.SelectedItems);
    }

    [Fact]
    public void AMosaicStillBeingComputedIsNotOffered()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var pending = new PendingMosaicAnnotation();
        doc.Add(pending);
        c.Tool = EditorTool.Eraser;
        Assert.Null(c.EraserTargetAt(new PointD(0, 0)));
        c.PointerDown(new PointD(0, 0));
        c.PointerUp(new PointD(0, 0));
        Assert.Equal([pending], doc.Items);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void ShiftEIsTheEraserKeyBesideTheEllipseAndHasOneHelpRow()
    {
        Assert.Equal("Ellipse", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.E, ModifierKeys.None));
        Assert.Equal("Eraser", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.E, ModifierKeys.Shift));
        ShortcutDefinition eraser = ShortcutCatalog.Get(ShortcutScope.Annotation, "Eraser");
        Assert.Equal("Shift+E", eraser.Hint);
        Assert.Equal(1, ShortcutCatalog.ForScope(ShortcutScope.Annotation).Count(definition =>
            definition.Key == Key.E && definition.Modifiers == ModifierKeys.Shift));

        ShortcutHelpEntry[] rows = ShortcutCatalog.Help(ShortcutScope.Annotation).ToArray();
        ShortcutHelpEntry row = Assert.Single(rows, entry => entry.Action == "Tools.Eraser");
        int highlighter = Array.FindIndex(rows, entry => entry.Action == "Tools.Highlighter");
        Assert.Equal(highlighter + 1, Array.IndexOf(rows, row));

        Assert.Equal("지우개", UiText.Get("Tools.Eraser"));
        Assert.Equal("닿은 주석을 통째로 지웁니다. 캡처 원본은 지우지 않습니다.", UiText.Get("Tools.EraserHelp"));
        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Eraser", UiText.Get("Tools.Eraser"));
            Assert.Equal("Removes each annotation it touches. The capture itself is not erased.", UiText.Get("Tools.EraserHelp"));
        }
    }

    [Fact]
    public void ShiftEPicksTheEraserAlsoInHangulModeAndTheRailButtonFollowsTheEraser() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        Assert.Equal(Key.E, ShortcutInput.Resolve(Key.ImeProcessed, Key.E, textInputFocused: false));
        Assert.True(editor.HandleShortcut(Key.E, ModifierKeys.Shift));
        Assert.Equal(EditorTool.Eraser, controller.Tool);
        Assert.Equal(EditorCursorKind.Eraser, EditorCursorPolicy.Resolve(controller.Tool, default));

        ToggleButton button = editor.ToolButton(EditorTool.Eraser);
        Assert.True(button.IsChecked);
        Assert.Contains("지우개", AutomationProperties.GetName(button));
        Assert.Equal(UiText.Get("Tools.EraserHelp"), AutomationProperties.GetHelpText(button));
        Assert.Contains("Shift+E", Assert.IsType<string>(button.ToolTip));

        Assert.True(editor.HandleShortcut(Key.E, ModifierKeys.None));
        Assert.Equal(EditorTool.Ellipse, controller.Tool);
        Assert.False(button.IsChecked);
    });

    [Fact]
    public void TheEraserCornerKeyFitsBesideTheGlyph() => StaTestHost.Run(() =>
    {
        var glyph = new System.Windows.Controls.Border { Width = 20, Height = 20 };
        var button = new ToggleButton { Content = glyph, Width = 40, Height = 38, Padding = new Thickness(2) };
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Eraser", corner: true);
        ShortcutPresentation.SetAltRevealed(button, true);
        var grid = Assert.IsType<System.Windows.Controls.Grid>(button.Content);
        System.Windows.Controls.TextBlock letter = grid.Children.OfType<System.Windows.Controls.TextBlock>().Single();
        grid.Measure(new Size(32, 32));
        grid.Arrange(new Rect(0, 0, 32, 32));
        Rect glyphBounds = glyph.TransformToAncestor(grid).TransformBounds(new Rect(glyph.RenderSize));
        Rect letterBounds = letter.TransformToAncestor(grid).TransformBounds(new Rect(letter.RenderSize));
        Assert.False(glyphBounds.IntersectsWith(letterBounds), $"key {letter.Text} at {letterBounds} covers glyph {glyphBounds}");
        // The 32-DIP face is centred in the 40-DIP rail button: the wider chord may use the 4 DIP on
        // either side but no more, so it never reaches the neighbouring button.
        Assert.Equal("Shift+E", letter.Text);
        Assert.InRange(letterBounds.Left, -4, 32);
        Assert.InRange(letterBounds.Right, 0, 36);
    });

    [Fact]
    public void HoverOutlinesTheTargetAndAClickErasesItWithAStatusLine() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        var rect = new RectangleAnnotation { Rect = new RectD(200, 120, 120, 80), StrokeThickness = 4 };
        controller.Document.Add(rect);
        Assert.True(editor.HandleShortcut(Key.E, ModifierKeys.Shift));

        Point onEdge = editor.ViewportPointOf(new PointD(200, 160));
        editor.SimulatePointerMove(onEdge, false);
        Assert.Equal(EditorCursorKind.Eraser, editor.CanvasCursorKind);
        Assert.Same(rect, surface.EraserTarget);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(500, 300)), false);
        Assert.Null(surface.EraserTarget);

        editor.SimulatePointerMove(onEdge, false);
        double scale = surface.DipPerPixel;
        Point origin = surface.ToSurfacePoint(new PointD(0, 0));
        editor.SimulatePointerDown(onEdge);
        editor.SimulatePointerUp(onEdge);
        Assert.Empty(controller.Document.Items);
        Assert.Null(surface.EraserTarget);
        Assert.Equal(UiText.Get("Text_5D27F0D47A0B"), editor.StatusText);
        Assert.Equal(EditorTool.Eraser, controller.Tool);
        // Erasing keeps the view where it was.
        Assert.Equal(scale, surface.DipPerPixel, 6);
        Assert.Equal(origin, surface.ToSurfacePoint(new PointD(0, 0)));

        Assert.True(controller.PerformUndo());
        Assert.Equal([rect], controller.Document.Items);
    });

    [Fact]
    public void TheEraserIsNotRememberedAsTheToolToOpenWith() => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        try
        {
            AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = "Eraser", StrokeThickness = 3 };
            using EditorWindowHost host = EditorWindowHost.Create();
            Assert.Equal(EditorTool.Select, Controller(host.Editor).Tool);
        }
        finally
        {
            AnnotationEditorPreferences.Read = read;
        }
    });

    private static AnnotationEditorController NewController(out AnnotationDocument doc, out UndoStack undo)
    {
        doc = AnnotationDocument.CreateFor(900, 520);
        undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        return new AnnotationEditorController(doc, undo) { DipPerPixel = 1, HitTolerance = 6 };
    }
}
