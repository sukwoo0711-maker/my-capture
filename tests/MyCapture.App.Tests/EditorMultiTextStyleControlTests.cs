using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorMultiTextStyleControlTests
{
    [Theory]
    [InlineData("size")]
    [InlineData("bold")]
    [InlineData("background")]
    [InlineData("family")]
    public void MixedSelectionWithShapeLastExposesAndAppliesCaptionControls(string property) => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? previousRead = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? previousWrite = AnnotationEditorPreferences.Write;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults();
        AnnotationEditorPreferences.Write = _ => { };
        try
        {
            var document = AnnotationDocument.CreateFor(900, 520);
            var first = new TextAnnotation { Text = "First", Rect = new RectD(40, 40, 180, 50), FontSize = 18 };
            var second = new TextAnnotation { Text = "Second", Rect = new RectD(250, 100, 180, 50), FontSize = 24 };
            var shape = new RectangleAnnotation { Rect = new RectD(600, 100, 100, 80) };
            document.Add(first);
            document.Add(second);
            document.Add(shape);
            using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 520, 0x30), document);
            host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);
            controller.SelectAll();
            editor.UpdateLayout();

            Assert.Same(shape, controller.Selected);
            Assert.True(Field<FrameworkElement>(editor, "_fontSizeSection").IsVisible);
            Assert.True(Field<FrameworkElement>(editor, "_fontFamilySection").IsVisible);
            Assert.True(Field<FrameworkElement>(editor, "_textStyleSection").IsVisible);
            Assert.Equal(24, Field<Slider>(editor, "_fontSizeSlider").Value);
            string before = document.ToJson();
            string? chosenFamily = null;
            switch (property)
            {
                case "size":
                    Field<Slider>(editor, "_fontSizeSlider").Value = 36;
                    break;
                case "bold":
                case "background":
                    ToggleButton toggle = Field<ToggleButton>(editor, property == "bold" ? "_textBold" : "_textBackground");
                    toggle.IsChecked = true;
                    toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    break;
                case "family":
                    ComboBox fonts = Field<ComboBox>(editor, "_textFontFamily");
                    FontChoice choice = fonts.Items.OfType<FontChoice>()
                        .First(font => !font.IsMore && font.Value != first.FontFamily);
                    chosenFamily = choice.Value;
                    fonts.SelectedItem = choice;
                    break;
            }

            Assert.Equal(1, controller.Undo.UndoCount);
            Assert.All(new[] { first, second }, text =>
            {
                switch (property)
                {
                    case "size": Assert.Equal(36, text.FontSize); break;
                    case "bold": Assert.True(text.Bold); break;
                    case "background": Assert.Equal(ColorRgba.Black, text.Background); break;
                    case "family": Assert.Equal(chosenFamily, text.FontFamily); break;
                }
            });
            Assert.Equal(new RectD(600, 100, 100, 80), shape.Rect);
            Assert.Equal(3, controller.SelectedItems.Count);
            string after = document.ToJson();
            Assert.True(controller.PerformUndo());
            Assert.Equal(before, document.ToJson());
            Assert.False(controller.PerformUndo());
            Assert.True(controller.PerformRedo());
            Assert.Equal(after, document.ToJson());
        }
        finally
        {
            AnnotationEditorPreferences.Read = previousRead;
            AnnotationEditorPreferences.Write = previousWrite;
        }
    });
}
