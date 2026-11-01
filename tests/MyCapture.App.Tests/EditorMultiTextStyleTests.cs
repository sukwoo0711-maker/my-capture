using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorMultiTextStyleTests
{
    [Theory]
    [InlineData("size")]
    [InlineData("bold")]
    [InlineData("background")]
    [InlineData("family")]
    public void SelectedCaptionsChangeTogetherAndUndoInOneStep(string property)
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        var controller = new AnnotationEditorController(document, undo) { TextFitter = Measure };
        var first = Caption("First", 30, 18);
        var second = Caption("Second", 200, 24);
        var unselected = Caption("Unselected", 400, 30);
        var shape = new RectangleAnnotation { Rect = new RectD(650, 50, 80, 60) };
        var badge = Caption("1", 740, 18);
        badge.IsStepNumber = true;
        document.Add(first);
        document.Add(second);
        document.Add(shape);
        document.Add(badge);
        controller.SelectAll();
        document.Add(unselected);
        string before = document.ToJson();

        Apply(controller, property);

        Assert.Equal(1, undo.UndoCount);
        Assert.Equal(4, controller.SelectedItems.Count);
        Assert.All(new[] { first, second }, text => AssertApplied(text, property));
        Assert.Equal(30, unselected.FontSize);
        Assert.False(unselected.Bold);
        Assert.Equal(ColorRgba.Transparent, unselected.Background);
        Assert.Equal(AnnotationDefaults.DefaultFontFamily, unselected.FontFamily);
        Assert.Equal(18, badge.FontSize);
        Assert.False(badge.Bold);
        Assert.Equal(ColorRgba.Transparent, badge.Background);
        Assert.Equal(AnnotationDefaults.DefaultFontFamily, badge.FontFamily);
        string after = document.ToJson();

        Assert.True(controller.PerformUndo());
        Assert.Equal(before, document.ToJson());
        Assert.False(controller.PerformUndo());
        Assert.True(controller.PerformRedo());
        Assert.Equal(after, document.ToJson());
        Assert.Equal(4, controller.SelectedItems.Count);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("bold")]
    [InlineData("background")]
    [InlineData("family")]
    public void AnAlreadyMatchingLastCaptionDoesNotPreventUpdatingEarlierCaptions(string property)
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var controller = new AnnotationEditorController(document, new UndoStack()) { TextFitter = Measure };
        var first = Caption("First", 30, 18);
        var last = Caption("Last", 200, 36);
        last.Bold = true;
        last.Background = ColorRgba.Black;
        last.FontFamily = "Consolas";
        last.Rect = Measure(last);
        document.Add(first);
        document.Add(last);
        controller.SelectAll();
        string before = document.ToJson();

        Apply(controller, property);

        AssertApplied(first, property);
        AssertApplied(last, property);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, document.ToJson());
    }

    [Fact]
    public void BackgroundToggleRestoresEachCaptionsOwnColourAndAlpha()
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var controller = new AnnotationEditorController(document, new UndoStack(mergeWindow: TimeSpan.Zero));
        var first = Caption("First", 30, 18);
        var second = Caption("Second", 200, 24);
        first.Background = new ColorRgba(128, 10, 20, 30);
        second.Background = new ColorRgba(255, 40, 50, 60);
        document.Add(first);
        document.Add(second);
        controller.SelectAll();
        string before = document.ToJson();

        controller.ApplyTextBackground(false);
        Assert.Equal(new ColorRgba(0, 10, 20, 30), first.Background);
        Assert.Equal(new ColorRgba(0, 40, 50, 60), second.Background);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, document.ToJson());
        Assert.True(controller.PerformRedo());
        controller.ApplyTextBackground(true);
        Assert.Equal(new ColorRgba(255, 10, 20, 30), first.Background);
        Assert.Equal(new ColorRgba(255, 40, 50, 60), second.Background);
    }

    private static TextAnnotation Caption(string value, double x, double size) => new()
    {
        Text = value,
        Rect = new RectD(x, 50, 100, 40),
        FontSize = size,
        FontFamily = AnnotationDefaults.DefaultFontFamily,
        AutoSize = true,
    };

    private static RectD Measure(TextAnnotation text) => new(text.Rect.X, text.Rect.Y,
        text.Text.Length * text.FontSize * (text.Bold ? 0.7 : 0.5)
            * (text.FontFamily == "Consolas" ? 1.2 : 1), text.FontSize * 1.4);

    private static void Apply(AnnotationEditorController controller, string property)
    {
        switch (property)
        {
            case "size": controller.ApplyFontSize(36); break;
            case "bold": controller.ApplyTextBold(true); break;
            case "background": controller.ApplyTextBackground(true); break;
            case "family": controller.ApplyTextFontFamily("Consolas"); break;
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }
    }

    private static void AssertApplied(TextAnnotation text, string property)
    {
        switch (property)
        {
            case "size": Assert.Equal(36, text.FontSize); break;
            case "bold": Assert.True(text.Bold); break;
            case "background": Assert.Equal(ColorRgba.Black, text.Background); break;
            case "family": Assert.Equal("Consolas", text.FontFamily); break;
            default: throw new ArgumentOutOfRangeException(nameof(property));
        }
        if (property != "background")
        {
            RectD measured = Measure(text);
            Assert.Equal(measured.X, text.Rect.X, 8);
            Assert.Equal(measured.Y, text.Rect.Y, 8);
            Assert.Equal(measured.Width, text.Rect.Width, 8);
            Assert.Equal(measured.Height, text.Rect.Height, 8);
        }
    }
}
