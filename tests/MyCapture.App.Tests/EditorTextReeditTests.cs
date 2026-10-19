using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorTextReeditTests
{
    private static AnnotationEditorController Controller(out TextAnnotation text)
    {
        var document = AnnotationDocument.CreateFor(640, 480);
        text = new TextAnnotation
        {
            Text = "Original caption",
            Rect = new RectD(40, 60, 200, 80),
            FontSize = 24,
            Bold = true,
            Italic = true,
            Background = new ColorRgba(180, 12, 34, 56),
            RotationDegrees = 15,
        };
        document.Add(text);
        return new AnnotationEditorController(document, new UndoStack());
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void PointerReopensSameAnnotationWithItsStyle(bool textTool, int clicks)
    {
        var controller = Controller(out var text);
        string before = controller.Document.ToJson();
        controller.Tool = textTool ? EditorTool.Text : EditorTool.Select;
        Assert.Same(text, controller.BeginTextAtPointer(text.Rect.Center, clicks, shift: false));
        Assert.Same(text, controller.EditingText);
        Assert.Same(text, controller.Selected);
        Assert.False(controller.IsCreatingText);
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(0, controller.Undo.UndoCount);
    }

    [Fact]
    public void SelectedTextReopensAndCancelLeavesContentLayoutAndHistoryUntouched()
    {
        var controller = Controller(out var text);
        controller.SetSelected(text);
        string before = controller.Document.ToJson();
        Assert.Same(text, controller.BeginSelectedTextEdit());
        controller.CancelTextEdit(text);
        Assert.Null(controller.EditingText);
        Assert.Same(text, controller.Selected);
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal(0, controller.Undo.UndoCount);
    }

    [Fact]
    public void CommitContentAndLayoutAreOneStepAndUndoRedoRestoreBoth()
    {
        var controller = Controller(out var text);
        controller.TextFitter = t => new RectD(t.Rect.X, t.Rect.Y, t.Rect.Width, t.Text.Length * 3);
        RectD before = text.Rect;
        Assert.True(controller.BeginTextEdit(text));
        controller.CommitTextEdit(text, "Changed");
        Assert.Null(controller.EditingText);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.Equal("Changed", text.Text);
        Assert.Equal(new RectD(40, 60, 200, 21), text.Rect);
        Assert.True(controller.PerformUndo());
        Assert.Equal("Original caption", text.Text);
        Assert.Equal(before, text.Rect);
        Assert.True(controller.PerformRedo());
        Assert.Equal("Changed", text.Text);
        Assert.Equal(new RectD(40, 60, 200, 21), text.Rect);
        Assert.True(text.Bold);
        Assert.Equal(15, text.RotationDegrees);
    }

    [Fact]
    public void SeparateEditsNeverMergeAndUnchangedCommitAddsNothing()
    {
        var controller = Controller(out var text);
        foreach (string content in new[] { "First", "Second", "Second" })
        {
            Assert.True(controller.BeginTextEdit(text));
            controller.CommitTextEdit(text, content);
        }
        Assert.Equal(2, controller.Undo.UndoCount);
        controller.PerformUndo();
        Assert.Equal("First", text.Text);
        controller.PerformUndo();
        Assert.Equal("Original caption", text.Text);
    }

    [Fact]
    public void EmptyExistingEditRemovesOnlyThatItemAndIsUndoable()
    {
        var controller = Controller(out var text);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(300, 200));
        controller.PointerUp(new PointD(400, 300));
        AnnotationItem other = controller.Document.Items[1];
        Assert.True(controller.BeginTextEdit(text));
        controller.CommitTextEdit(text, string.Empty);
        Assert.Same(other, Assert.Single(controller.Document.Items));
        Assert.True(controller.PerformUndo());
        Assert.Same(text, controller.Document.Items[0]);
        Assert.Equal("Original caption", text.Text);
        Assert.True(controller.PerformRedo());
        Assert.Same(other, Assert.Single(controller.Document.Items));
    }

    [Fact]
    public void ShiftTextClickCreatesNewBoxAboveExistingAndCancelRemovesOnlyNewBox()
    {
        var controller = Controller(out var text);
        controller.Tool = EditorTool.Text;
        var added = Assert.IsType<TextAnnotation>(controller.BeginTextAtPointer(text.Rect.Center, 1, shift: true));
        Assert.NotSame(text, added);
        Assert.True(controller.IsCreatingText);
        Assert.Same(added, controller.Document.Items[1]);
        controller.CancelTextEdit(added);
        Assert.Same(text, Assert.Single(controller.Document.Items));
        Assert.Equal(0, controller.Undo.UndoCount);
    }

    [Fact]
    public void SingleSelectClickAndStepNumbersDoNotOpenTextEntry()
    {
        var controller = Controller(out var text);
        controller.Tool = EditorTool.Select;
        Assert.Null(controller.BeginTextAtPointer(text.Rect.Center, 1, shift: false));
        text.IsStepNumber = true;
        controller.SetSelected(text);
        Assert.Null(controller.BeginSelectedTextEdit());
        Assert.Null(controller.BeginTextAtPointer(text.Rect.Center, 2, shift: false));
        Assert.False(controller.BeginTextEdit(new TextAnnotation()));
    }

    [Fact]
    public void BoldRefitsTextAndBackgroundPreservesSavedColorOnUndo()
    {
        var controller = Controller(out var text);
        controller.SetSelected(text);
        controller.TextFitter = t => new RectD(t.Rect.X, t.Rect.Y, t.Rect.Width, t.Bold ? 80 : 40);
        RectD before = text.Rect;
        ColorRgba plate = text.Background;
        controller.ApplyTextBold(false);
        controller.ApplyTextBackground(false);
        Assert.False(text.Bold);
        Assert.Equal(40, text.Rect.Height);
        Assert.Equal(0, text.Background.A);
        Assert.Equal(2, controller.Undo.UndoCount);
        controller.PerformUndo();
        Assert.Equal(plate, text.Background);
        controller.PerformUndo();
        Assert.True(text.Bold);
        Assert.Equal(before, text.Rect);
        controller.PerformRedo();
        controller.PerformRedo();
        Assert.False(text.Bold);
        Assert.Equal(0, text.Background.A);
    }

    [Fact]
    public void BackgroundCanBeEnabledAndUndoneWithoutChangingOtherStyle()
    {
        var controller = Controller(out var text);
        text.Background = ColorRgba.Transparent;
        controller.SetSelected(text);
        controller.ApplyTextBackground(true);
        Assert.Equal(ColorRgba.Black, text.Background);
        controller.PerformUndo();
        Assert.Equal(ColorRgba.Transparent, text.Background);
        controller.PerformRedo();
        Assert.Equal(ColorRgba.Black, text.Background);
        Assert.True(text.Bold);
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.F2)]
    public void ReopenShortcutsAreUnmodifiedAndKeepFinishShortcut(Key key)
    {
        Assert.Equal("EditText", ShortcutCatalog.Match(ShortcutScope.Annotation, key, ModifierKeys.None));
        Assert.Equal("Done", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.Enter, ModifierKeys.Control));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SurfaceRoutesDoubleClickTextClickAndShiftClick(bool textTool, bool shift) => StaTestHost.Run(() =>
    {
        var source = Controller(out var original);
        var bitmap = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 480 * 4], 640 * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 640, 480), null, 0);
        var editor = new AnnotationEditorControl(frame, frame.ScreenBounds, bitmap, source.Document, null);
        editor.Measure(new Size(1000, 700));
        editor.Arrange(new Rect(0, 0, 1000, 700));
        var controller = Field<AnnotationEditorController>(editor, "_controller");
        controller.Tool = textTool ? EditorTool.Text : EditorTool.Select;
        editor.SimulatePointerDown(editor.ViewportPointOf(original.Rect.Center), textTool ? 1 : 2, shift);
        var box = Field<TextBox>(editor, "_activeTextBox");
        Assert.Equal(shift ? string.Empty : original.Text, box.Text);
        Assert.Equal(shift ? 2 : 1, controller.Document.Items.Count);
        Assert.Equal(shift, controller.IsCreatingText);
        if (!shift) Assert.Same(original, controller.EditingText);
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Same(original, Assert.Single(controller.Document.Items));

        controller.SetSelected(original);
        CheckBox bold = Field<CheckBox>(editor, "_textBold");
        bold.IsChecked = false;
        bold.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(original.Bold);
        CheckBox plate = Field<CheckBox>(editor, "_textBackground");
        plate.IsChecked = false;
        plate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(0, original.Background.A);
        controller.PerformUndo();
        Assert.Equal(180, original.Background.A);
        controller.PerformUndo();
        Assert.True(original.Bold);
    });

    [Fact]
    public void LibraryLayersReopenPrefilledAndEscRestoresOriginalWithoutHistory() => StaTestHost.Run(() =>
    {
        var source = Controller(out var original);
        original.Text = "Caption";
        original.Rect = new RectD(80, 100, 240, 180);
        // Retain this isolated fixture under the test output directory; never use the real library.
        string root = Path.Combine(AppContext.BaseDirectory, "text-reedit-fixtures", Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord { RelativeDirectory = "capture", Width = 640, Height = 480 };
        string directory = queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), source.Document.ToJson());
        var bitmap = BitmapSource.Create(640, 480, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 480 * 4], 640 * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, CaptureFileNames.Original))) encoder.Save(stream);

        var loader = new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance);
        GalleryReeditContext context = Assert.IsType<GalleryReeditContext>(loader.TryLoad(record, out var failure));
        Assert.Equal(GalleryReeditLoader.LoadFailure.None, failure);
        var restored = Assert.IsType<TextAnnotation>(Assert.Single(context.Document.Items));
        Assert.Equal(original.Id, restored.Id);
        using var host = EditorKeysTests.EditorWindowHost.Create(context.OriginalBitmap, context.Document);
        var editor = host.Editor;
        var surface = Field<AnnotationEditorSurface>(editor, "_surface");
        Assert.Equal(original.Rect, restored.Rect);
        Assert.True(TextAnnotationLayout.Measure(restored).Height < restored.Rect.Height / 2);
        var controller = Field<AnnotationEditorController>(editor, "_controller");
        controller.SetSelected(restored);
        Assert.True(editor.HandleShortcut(Key.F2, ModifierKeys.None));
        host.Window.UpdateLayout();
        // Selection changes the contextual bar's height. Compare both positions after the
        // same arrange pass, while keeping immediate selection -> F2 as the input sequence.
        Rect stored = surface.ToSurfaceRect(restored.Rect);
        Point expectedGlyph = new RotateTransform(restored.RotationDegrees,
            stored.Left + stored.Width / 2, stored.Top + stored.Height / 2).Transform(stored.TopLeft);
        var box = Field<TextBox>(editor, "_activeTextBox");
        Point glyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, surface);
        Assert.True((glyph - expectedGlyph).Length <= 0.75,
            $"First glyph at {glyph}, committed rotated caption at {expectedGlyph}.");
        Assert.Equal(restored.Rect, Assert.IsType<TextAnnotation>(Assert.Single(editor.CreateCurrentImageSnapshot().Items)).Rect);
        Assert.Equal(original.Text, box.Text);
        Assert.Equal(box.Text.Length, box.CaretIndex);
        Assert.Equal(FontWeights.Bold, box.FontWeight);
        Assert.Equal(FontStyles.Italic, box.FontStyle);
        Assert.Equal(original.FontFamily, box.FontFamily.Source);
        Assert.Equal(original.FontSize * surface.DipPerPixel, box.FontSize);
        Assert.Equal(original.RotationDegrees, Assert.IsType<RotateTransform>(box.RenderTransform).Angle);
        Assert.Equal(UiText.Get("EditorText_Editing"), editor.StatusText);
        EditorKeysTests.Invoke(editor, "CommitActiveText");
        Assert.Equal(original.Rect, restored.Rect);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.True(editor.HandleShortcut(Key.F2, ModifierKeys.None));
        box = Field<TextBox>(editor, "_activeTextBox");
        box.Text = "Uncommitted replacement";
        host.Window.UpdateLayout();
        Rect fitted = surface.ToSurfaceRect(controller.FitText(restored, box.Text,
            restored.FontSize, restored.AutoSize, restored.Rect).Rect);
        Point editedExpected = new RotateTransform(restored.RotationDegrees,
            fitted.Left + fitted.Width / 2, fitted.Top + fitted.Height / 2).Transform(fitted.TopLeft);
        Point editedGlyph = box.TranslatePoint(box.GetRectFromCharacterIndex(0).TopLeft, surface);
        Assert.True((editedGlyph - editedExpected).Length <= 0.75);
        Assert.True(editor.HasUnsavedChanges);
        Assert.True(editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Equal(original.Text, restored.Text);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Null(controller.EditingText);

        Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        box = Field<TextBox>(editor, "_activeTextBox");
        box.Text = "Updated from library";
        Assert.False(editor.HandleShortcut(Key.Enter, ModifierKeys.None)); // TextBox inserts a line break.
        // A canvas click uses the same commit path as focus leaving the in-place box.
        editor.SimulatePointerDown(new Point(500, 400));
        Assert.Equal("Updated from library", restored.Text);
        Assert.Equal(1, controller.Undo.UndoCount);
        controller.PerformUndo();
        Assert.Equal(original.Text, restored.Text);
        controller.PerformRedo();
        Assert.Equal("Updated from library", restored.Text);
        var saved = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(context.Document.ToJson()));
        Assert.Equal("Updated from library", Assert.IsType<TextAnnotation>(Assert.Single(saved.Items)).Text);
    });

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
