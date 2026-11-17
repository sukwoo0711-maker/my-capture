using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The shape property bar shows the fill as opacity (100 = solid) while the model keeps its
/// transparency (100 = hollow, the default). Only the label, the number and the slider direction
/// are inverted; the stored value, the defaults and undo are untouched.
/// </summary>
public sealed class EditorFillOpacityTests
{
    [Theory]
    [InlineData(100, 0)]
    [InlineData(75, 25)]
    [InlineData(50, 50)]
    [InlineData(0, 100)]
    public void DisplayedOpacityIsTheComplementOfTheStoredTransparencyInBothDirections(double transparency, double opacity)
    {
        Assert.Equal(opacity, AnnotationEditorControl.FillOpacityOf(transparency));
        Assert.Equal(transparency, AnnotationEditorControl.FillTransparencyOf(opacity));
        Assert.Equal(transparency, AnnotationEditorControl.FillTransparencyOf(AnnotationEditorControl.FillOpacityOf(transparency)));
    }

    [Fact]
    public void SliderShowsOpacityAndWritesTheStoredTransparencyWhileNewShapesStayHollow() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        Slider fill = Field<Slider>(host.Editor, "_fillOpacitySlider");

        // The default is a hollow shape: transparency 100 in the model, opacity 0 on the bar.
        Assert.Equal(100, controller.FillTransparency);
        Assert.Equal(0, fill.Value);
        Assert.Equal("채움 불투명도", UiText.Get("EditorProperty_FillTransparency"));

        // Dragging right makes the fill more solid: the model's transparency goes down.
        fill.Value = 70;
        Assert.Equal(30, controller.FillTransparency);
        fill.Value = 100;
        Assert.Equal(0, controller.FillTransparency);

        // A selected shape is shown by its own fill, without changing the model or the undo stack.
        var rectangle = new RectangleAnnotation
        {
            Rect = new RectD(8, 8, 40, 30),
            Stroke = ColorRgba.FromRgb(0, 0, 255),
            StrokeThickness = 2,
            Fill = new ColorRgba(64, 0, 0, 255),
        };
        controller.Document.Add(rectangle);
        string before = controller.Document.ToJson();
        double stored = rectangle.FillTransparency;
        controller.SetSelected(rectangle);
        Assert.Equal(100 - stored, fill.Value);
        Assert.False(controller.CanUndo);
        Assert.Equal(before, controller.Document.ToJson());
    });
}
