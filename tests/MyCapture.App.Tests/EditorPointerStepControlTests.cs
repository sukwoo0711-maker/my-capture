using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorPointerStepControlTests : KoreanCaptionTest
{
    [Fact]
    public void PressWithoutHoverSetsResizeCursorAndKeepsItUntilRelease() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.Select);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 80, 180, 100), Fill = ColorRgba.White };
        controller.Document.Add(shape);
        controller.SetSelected(shape);
        editor.SimulatePointerMove(editor.ViewportPointOf(shape.Rect.Center), false);
        Assert.Equal(EditorCursorKind.Move, editor.CanvasCursorKind);
        var right = new PointD(280, 130);
        editor.SimulatePointerDown(editor.ViewportPointOf(right));
        Assert.Equal(EditorCursorKind.ResizeWestEast, editor.CanvasCursorKind);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(320, 230)), true);
        Assert.Equal(EditorCursorKind.ResizeWestEast, editor.CanvasCursorKind);
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(320, 230)));
        Assert.Equal(220, shape.Rect.Width, 6);
        Assert.Equal(100, shape.Rect.Height, 6);
    });

    [Fact]
    public void ToolChangeReevaluatesTheLastPointerAndBrushLeavesNoDocumentItem() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.Pen);
        var point = editor.ViewportPointOf(new PointD(250, 150));
        editor.SimulatePointerMove(point, false);
        Assert.Equal(EditorCursorKind.Pen, editor.CanvasCursorKind);
        Assert.NotNull(Field<AnnotationEditorSurface>(editor, "_surface").BrushOutlineDip);
        Assert.Empty(controller.Document.Items);
        Select(editor, EditorTool.Select);
        Assert.Equal(EditorCursorKind.Arrow, editor.CanvasCursorKind);
        Assert.Null(Field<AnnotationEditorSurface>(editor, "_surface").BrushOutlineDip);
    });

    [Fact]
    public void SmallSelectedStepMovesAndAnotherImagePositionAdds() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        var step = controller.AddStepNumber(new PointD(200, 150));
        var before = step.Rect;
        editor.SimulatePointerMove(editor.ViewportPointOf(before.Center), false);
        Assert.Equal(EditorCursorKind.Move, editor.CanvasCursorKind);
        editor.SimulatePointerDown(editor.ViewportPointOf(before.Center));
        editor.SimulatePointerMove(editor.ViewportPointOf(before.Center.Offset(30, 20)), true);
        editor.SimulatePointerUp(editor.ViewportPointOf(before.Center.Offset(30, 20)));
        Assert.Single(controller.Document.Items);
        Assert.Equal(before.Width, step.Rect.Width);
        Assert.Equal(before.X + 30, step.Rect.X, 6);
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(400, 200)));
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(400, 200)));
        Assert.Equal(2, controller.Document.Items.Count);
    });

    [Fact]
    public void StepInputsCommitValidValuesRejectInvalidAndRenumberInOneUndo() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        SetInput(editor, "_stepNextInput", "20");
        var first = controller.AddStepNumber(new PointD(160, 100));
        Assert.Equal("20", first.Text);
        SetInput(editor, "_stepNumberInput", "9");
        Assert.Equal("9", first.Text);
        SetInput(editor, "_stepNumberInput", "1000");
        Assert.Equal("9", first.Text);
        Assert.Contains("1–999", editor.StatusText);
        SetInput(editor, "_stepNumberInput", "9");
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(Field<TextBox>(editor, "_stepNumberInput")));
        SetInput(editor, "_stepSizeInput", "64");
        Assert.Equal(64, first.Rect.Width);
        Assert.Equal(64, first.Rect.Height);
        var second = controller.AddStepNumber(new PointD(340, 200));
        Assert.Equal("21", second.Text);
        int undoCount = controller.Undo.UndoCount;
        Field<Button>(editor, "_stepRenumberButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("1", first.Text);
        Assert.Equal("2", second.Text);
        Assert.Equal(undoCount + 1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal("9", first.Text);
        Assert.Equal("21", second.Text);
        Select(editor, EditorTool.Select);
        Assert.Equal(Visibility.Visible, Field<FrameworkElement>(editor, "_stepSection").Visibility);
        Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(editor, "_fontSizeSection").Visibility);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(999)]
    public void MinimumStepSizeFitsDigitsUsingTheActualExportFormatter(int number) => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        Assert.True(controller.TrySetNextStepNumber(number));
        Assert.True(controller.ApplyStepSize(24));
        var badge = controller.AddStepNumber(new PointD(100, 100));
        Rect ink = TextAnnotationLayout.Format(badge, 1).BuildGeometry(default).Bounds;
        double outline = badge.HasOutline ? TextAnnotationLayout.OutlineMargin(badge) : 0;
        Assert.True(ink.Width + outline <= 20.01, $"Ink {ink.Width} + outline {outline} exceeds 20 px.");
        Assert.True(ink.Height + outline <= 20.01);
        Assert.True(badge.FontSize >= 10);
    });

    [Fact]
    public void ChangingSelectionDiscardsTheOtherBadgesUncommittedSize() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        var first = controller.AddStepNumber(new PointD(100, 100));
        var second = controller.AddStepNumber(new PointD(220, 100));
        controller.SetSelected(first);
        var input = Field<TextBox>(editor, "_stepSizeInput");
        input.Focus();
        input.Text = "90";
        controller.SetSelected(second);
        Assert.Equal("36", input.Text);
        Assert.True(Press(input, Key.Enter, ime: false));
        Assert.Equal(36, second.Rect.Width);
        Assert.Equal(36, first.Rect.Width);
    });

    [Fact]
    public void GroupSelectionDoesNotExposeOneBadgesSizeInput() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        var controller = Controller(editor);
        Select(editor, EditorTool.StepNumber);
        controller.AddStepNumber(new PointD(100, 100));
        controller.AddStepNumber(new PointD(220, 100));
        Select(editor, EditorTool.Select);
        controller.SelectAll();
        Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(editor, "_stepSection").Visibility);
    });

    [Fact]
    public void HighlighterFieldShowsTheEffectiveMinimumWidth() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var editor = host.Editor;
        Controller(editor).StrokeThickness = 3;
        Select(editor, EditorTool.Highlighter);
        Assert.Equal(12, Field<Slider>(editor, "_thicknessSlider").Value);
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(100, 100)), false);
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        Assert.Equal(12 * surface.DipPerPixel, surface.BrushOutlineDip!.Value.Width, 6);
    });

    private static void SetInput(AnnotationEditorControl editor, string field, string value)
    {
        var input = Field<TextBox>(editor, field);
        input.Focus();
        input.Text = value;
        Assert.True(Press(input, Key.Enter, ime: false));
    }

    private static void Select(AnnotationEditorControl editor, EditorTool tool) => Invoke(editor, "SelectTool", tool);
}
