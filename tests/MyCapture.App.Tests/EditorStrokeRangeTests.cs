using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor draws every stroke width the settings accept (1–64 px) instead of silently
/// clamping to a narrower range, and its sliders show their value in an editable number box.
/// </summary>
public sealed class EditorStrokeRangeTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(40, 40)]
    [InlineData(100, 64)]
    [InlineData(0.2, 1)]
    public void SavedDefaultWidthIsDrawnAsSavedWithinTheSettingsRange(double saved, double expected) => StaTestHost.Run(() =>
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        try
        {
            AnnotationEditorPreferences.Read = () => new AnnotationDefaults { StrokeThickness = saved, LastTool = "Rectangle" };
            using EditorWindowHost host = EditorWindowHost.Create();
            AnnotationEditorController controller = Controller(host.Editor);
            Assert.Equal(expected, controller.StrokeThickness);
            Slider slider = Field<Slider>(host.Editor, "_thicknessSlider");
            // One range: the slider, the controller and the settings store share SettingsRanges.
            Assert.Equal(SettingsRanges.StrokeThickness.Min, slider.Minimum);
            Assert.Equal(SettingsRanges.StrokeThickness.Max, slider.Maximum);
            Assert.Equal(expected, slider.Value);

            controller.PointerDown(new PointD(40, 40));
            controller.PointerMove(new PointD(300, 220));
            controller.PointerUp(new PointD(300, 220));
            Assert.Equal(expected, Assert.IsType<RectangleAnnotation>(controller.Selected).StrokeThickness);
        }
        finally
        {
            AnnotationEditorPreferences.Read = read;
        }
    });

    [Fact]
    public void ControllerAndSettingsStoreClampToTheSameRange()
    {
        var controller = new AnnotationEditorController(AnnotationDocument.CreateFor(400, 300), new MyCapture.Core.Undo.UndoStack());
        controller.ApplyStrokeThickness(64);
        Assert.Equal(64, controller.StrokeThickness);
        controller.ApplyStrokeThickness(500);
        Assert.Equal(SettingsRanges.StrokeThickness.Max, controller.StrokeThickness);
        controller.ApplyStrokeThickness(0);
        Assert.Equal(SettingsRanges.StrokeThickness.Min, controller.StrokeThickness);
        controller.ApplyStrokeThickness(double.NaN);
        Assert.Equal(SettingsRanges.StrokeThickness.Min, controller.StrokeThickness);
        controller.ApplyFontSize(400);
        Assert.Equal(SettingsRanges.FontSize.Max, controller.DefaultFontSize);

        string root = OwnedTestDirectory.Create("mc-stroke-range-");
        try
        {
            var paths = MyCapture.Core.Storage.AppPaths.CreateForRoot(root);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(paths.SettingsFile)!);
            System.IO.File.WriteAllText(paths.SettingsFile,
                """{"schemaVersion":4,"annotation":{"strokeThickness":90,"fontSize":2}}""");
            AppSettings loaded = new SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance).Load();
            Assert.Equal(SettingsRanges.StrokeThickness.Max, loaded.Annotation.StrokeThickness);
            Assert.Equal(SettingsRanges.FontSize.Min, loaded.Annotation.FontSize);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Theory]
    [InlineData("12", 1, 64, 1, true, 12, false)]
    [InlineData(" 7 ", 1, 64, 1, true, 7, false)]
    [InlineData("12.4", 1, 64, 1, true, 12, false)]
    [InlineData("12.5", 1, 64, 1, true, 13, false)]
    [InlineData("64", 1, 64, 1, true, 64, false)]
    [InlineData("100", 1, 64, 1, true, 64, true)]
    [InlineData("0", 1, 64, 1, true, 1, true)]
    [InlineData("-5", 6, 64, 1, true, 6, true)]
    [InlineData("18.25", 6, 400, 0, true, 18.3, false)]
    [InlineData("abc", 1, 64, 1, false, 0, false)]
    [InlineData("", 1, 64, 1, false, 0, false)]
    [InlineData("1e400", 1, 64, 1, false, 0, false)]
    [InlineData("NaN", 1, 64, 1, false, 0, false)]
    public void ValueBoxAcceptsNumbersWithinTheRangeOnly(string text, double min, double max, double step,
        bool accepted, double expected, bool clamped)
    {
        using var language = UiText.UseLanguage("en-US");
        Assert.Equal(accepted, SliderValueEntry.TryParse(text, min, max, step, out double value, out bool wasClamped));
        if (!accepted) return;
        Assert.Equal(expected, value, precision: 6);
        Assert.Equal(clamped, wasClamped);
    }

    [Fact]
    public void StrokeWidthBoxShowsTheValueAndAppliesTypedWidthsToTheSelection() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        controller.PointerDown(new PointD(40, 40));
        controller.PointerMove(new PointD(300, 220));
        controller.PointerUp(new PointD(300, 220));
        var shape = Assert.IsType<RectangleAnnotation>(controller.Selected);
        host.Window.UpdateLayout();

        Slider slider = Field<Slider>(host.Editor, "_thicknessSlider");
        TextBox box = StrokeBox(host.Editor);
        Assert.True(box.IsVisible);
        Assert.Equal(SliderValueEntry.BoxWidth, box.ActualWidth, precision: 1);
        Assert.Equal(SliderValueEntry.Format(slider.Value), box.Text);
        Assert.Contains("1–64px", AutomationProperties.GetHelpText(box), StringComparison.Ordinal);
        // The unit and editable value now sit between the two thickness stepper actions.
        TextBlock unit = Assert.Single(((Panel)VisualTreeHelper.GetParent(box)).Children.OfType<TextBlock>());
        Assert.Equal("px", unit.Text);
        Button decrease = Descendants(host.Editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AnnotationThicknessDecrease");
        Button increase = Descendants(host.Editor).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "AnnotationThicknessIncrease");
        Assert.True(decrease.IsVisible && increase.IsVisible);
        Assert.True(decrease.TranslatePoint(default, host.Editor).X + decrease.ActualWidth <= box.TranslatePoint(default, host.Editor).X + 0.5);
        Assert.True(box.TranslatePoint(default, host.Editor).X + box.ActualWidth <= increase.TranslatePoint(default, host.Editor).X + 0.5);
        Assert.DoesNotContain(slider, Descendants(host.Editor));

        // The shared value model still synchronizes the box and selected annotation.
        slider.Value = 9;
        Assert.Equal("9", box.Text);
        Assert.Equal(9, shape.StrokeThickness);

        // Enter applies a typed width and returns the keyboard to the canvas.
        Assert.True(box.Focus());
        box.Text = "64";
        Assert.True(Press(box, Key.Enter, ime: false));
        Assert.Equal(64, shape.StrokeThickness);
        Assert.Equal(64, slider.Value);
        Assert.Same(host.Editor, Keyboard.FocusedElement);

        // A width past the range applies the limit and says so.
        Assert.True(box.Focus());
        box.Text = "120";
        Assert.True(Press(box, Key.Enter, ime: false));
        Assert.Equal(64, shape.StrokeThickness);
        Assert.Equal("64", box.Text);
        Assert.Equal("입력 범위 1–64px · 64px 적용", host.Editor.StatusText);

        // Esc and text that is not a number leave the width alone.
        Assert.True(box.Focus());
        box.Text = "3";
        Assert.True(Press(box, Key.Escape, ime: false));
        Assert.Equal("64", box.Text);
        Assert.Equal(64, shape.StrokeThickness);
        Assert.True(box.Focus());
        box.Text = "두께";
        host.Editor.Focus();
        Assert.Equal("64", box.Text);
        Assert.Equal(64, shape.StrokeThickness);

        // Leaving the box applies what was typed, through the same undoable property change.
        Assert.True(box.Focus());
        box.Text = "20";
        host.Editor.Focus();
        Assert.Equal(20, shape.StrokeThickness);
        host.Editor.ShortcutActions["Undo"]();
        Assert.NotEqual(20, shape.StrokeThickness);
    });

    [Fact]
    public void LettersTypedInTheBoxDoNotSwitchTools() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        TextBox box = StrokeBox(host.Editor);
        Assert.False(InputMethod.GetIsInputMethodEnabled(box));
        Assert.True(box.Focus());
        Assert.False(Press(box, Key.T, ime: false));
        Assert.Equal(EditorTool.Rectangle, controller.Tool);
    });

    [Fact]
    public void ThickArrowEndsInAPointedTip() => StaTestHost.Run(() =>
    {
        // A horizontal 64 px arrow whose tip is at x = 500; its head starts 256 px earlier.
        // The shaft's round cap used to reach 32 px past the tip and blunt it.
        var document = AnnotationDocument.CreateFor(600, 240);
        document.Add(PolylineAnnotation.CreateArrow(new PointD(20, 120), new PointD(500, 120), ColorRgba.FromRgb(255, 0, 0), 64));
        byte[] pixels = Render(document, 600, 240);
        Assert.Equal(255, Alpha(pixels, 600, 490, 120));
        Assert.Equal(0, Alpha(pixels, 600, 504, 120));
        Assert.Equal(0, Alpha(pixels, 600, 520, 120));
        // The shaft still runs from the start to the head at its full width.
        Assert.Equal(255, Alpha(pixels, 600, 100, 120));
        Assert.Equal(255, Alpha(pixels, 600, 100, 120 + 30));
        Assert.Equal(0, Alpha(pixels, 600, 100, 120 + 40));
        // The head is wider than the shaft where they meet.
        Assert.Equal(255, Alpha(pixels, 600, 260, 120 + 60));

        // A thin arrow and a plain line keep their shape.
        var thin = AnnotationDocument.CreateFor(400, 240);
        thin.Add(PolylineAnnotation.CreateArrow(new PointD(20, 120), new PointD(300, 120), ColorRgba.FromRgb(255, 0, 0), 3));
        thin.Add(PolylineAnnotation.CreateLine(new PointD(20, 40), new PointD(300, 40), ColorRgba.FromRgb(255, 0, 0), 64));
        byte[] thinPixels = Render(thin, 400, 240);
        Assert.Equal(255, Alpha(thinPixels, 400, 100, 120));
        Assert.Equal(255, Alpha(thinPixels, 400, 296, 120));
        Assert.Equal(255, Alpha(thinPixels, 400, 320, 40));
    });

    private static TextBox StrokeBox(AnnotationEditorControl editor) =>
        Descendants(editor).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == UiText.Get("Text_70EC2D232B4F"));

    private static byte[] Render(AnnotationDocument document, int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            new AnnotationRenderer(new AnnotationImageStore()).Render(context, document, 1);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static byte Alpha(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4 + 3];
}
