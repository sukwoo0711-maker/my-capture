using System.Windows.Controls;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorSelectionInspectorTests
{
    [Theory]
    [InlineData(AnnotationStrokeStyle.Solid, false)]
    [InlineData(AnnotationStrokeStyle.Dashed, false)]
    [InlineData(AnnotationStrokeStyle.Dotted, false)]
    [InlineData(AnnotationStrokeStyle.ThickDashed, false)]
    [InlineData(AnnotationStrokeStyle.Solid, true)]
    public void MixedStrokeStyles_ChoosingLastItemsStyleAppliesToGroupAsOneUndoStep(
        AnnotationStrokeStyle style, bool selectNonShapeLast) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        AnnotationStrokeStyle otherStyle = style == AnnotationStrokeStyle.Dashed
            ? AnnotationStrokeStyle.Dotted : AnnotationStrokeStyle.Dashed;
        var first = new RectangleAnnotation
        {
            Rect = new RectD(40, 40, 60, 60), StrokeStyle = otherStyle,
        };
        var second = new EllipseAnnotation
        {
            Rect = new RectD(140, 40, 60, 60), StrokeStyle = otherStyle,
        };
        var last = new RectangleAnnotation
        {
            Rect = new RectD(240, 40, 60, 60), StrokeStyle = style,
        };
        controller.Document.Add(first);
        controller.Document.Add(second);
        controller.Document.Add(last);
        if (selectNonShapeLast)
            controller.Document.Add(new TextAnnotation { Rect = new RectD(340, 40, 100, 40), Text = "Caption" });
        string before = controller.Document.ToJson();
        controller.SelectAll();
        ComboBox combo = Field<ComboBox>(host.Editor, "_strokeStyleComboBox");

        Assert.Equal(-1, combo.SelectedIndex);
        Assert.Null(combo.SelectedItem);
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(0, controller.Undo.UndoCount);

        // Exercise the inspector's SelectionChanged handler, including its synchronization.
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, style));

        Assert.Equal(style, first.StrokeStyle);
        Assert.Equal(style, second.StrokeStyle);
        Assert.Equal(style, last.StrokeStyle);
        Assert.Equal((int)style, combo.SelectedIndex);
        Assert.Equal(1, controller.Undo.UndoCount);
        string after = controller.Document.ToJson();
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(-1, combo.SelectedIndex);
        Assert.True(controller.PerformRedo());
        Assert.Equal(after, controller.Document.ToJson());
        Assert.Equal((int)style, combo.SelectedIndex);
    });

    [Fact]
    public void UniformStrokeStyles_IgnoreIncompatibleSelectionAndKeepToolDefault() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        controller.ApplyStrokeStyle(AnnotationStrokeStyle.Dotted);
        var shape = new RectangleAnnotation { Rect = new RectD(40, 40, 60, 60), StrokeStyle = AnnotationStrokeStyle.Dashed };
        controller.Document.Add(shape);
        controller.Document.Add(new TextAnnotation { Rect = new RectD(140, 40, 100, 40), Text = "Caption" });
        ComboBox combo = Field<ComboBox>(host.Editor, "_strokeStyleComboBox");

        controller.SetSelected(shape);
        Assert.Equal((int)AnnotationStrokeStyle.Dashed, combo.SelectedIndex);
        controller.SelectAll();
        Assert.Equal((int)AnnotationStrokeStyle.Dashed, combo.SelectedIndex);
        controller.SetSelected(null);
        controller.Tool = EditorTool.Rectangle;
        Invoke(host.Editor, "UpdateInspector");
        Assert.Equal((int)AnnotationStrokeStyle.Dotted, combo.SelectedIndex);
        Assert.Equal(0, controller.Undo.UndoCount);
    });
}
