using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// Renders the reported text cases in the real editor window: a one-letter caption
    /// enlarged with the size slider, a long sentence typed near the right edge of the
    /// image, and a caption still being typed.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_EDITOR_TEXT_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_EDITOR_TEXT_PHASE</c> to label them (for example before and after a fix).
    /// </remarks>
    [Fact]
    public Task EditorText_EnlargedAndEdgeCaptionsRenderWholeInEveryEvidenceTheme() =>
        RunIsolated(nameof(EditorText_EnlargedAndEdgeCaptionsRenderWholeInEveryEvidenceTheme), VerifyEditorTextEvidence);

    private static void VerifyEditorTextEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_TEXT_PHASE") is { Length: > 0 } label ? label : "after";
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                BitmapSource flattened = RenderEditorTextScene(theme, phase);
                if (theme == AppTheme.Glass) SaveEditorBitmapEvidence(flattened, $"editor-text-flattened-{phase}.png");
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static BitmapSource RenderEditorTextScene(AppTheme theme, string phase)
    {
        BitmapSource bitmap = EditorEvidenceBackground(720, 300);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1180, Height = 560,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            AnnotationEditorControl editor = window.Editor;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo place = typeof(AnnotationEditorControl).GetMethod("PlaceTextBox", flags)!;
            MethodInfo commit = typeof(AnnotationEditorControl).GetMethod("CommitActiveText", flags)!;
            FieldInfo active = typeof(AnnotationEditorControl).GetField("_activeTextBox", flags)!;
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", flags)!.GetValue(editor)!;
            var fontSize = (Slider)typeof(AnnotationEditorControl).GetField("_fontSizeSlider", flags)!.GetValue(editor)!;

            // The reported case: one Hangul letter, then the size slider dragged up to 60.
            place.Invoke(editor, [new PointD(150, 40)]);
            Drain(window);
            ((TextBox)active.GetValue(editor)!).Text = "ㅅ";
            commit.Invoke(editor, null);
            TextAnnotation enlarged = controller.Document.Items.OfType<TextAnnotation>().Single();
            controller.SetSelected(enlarged);
            Drain(window);
            fontSize.Value = 60;
            Drain(window);
            controller.SetSelected(null);
            fontSize.Value = 28;
            Drain(window);

            // A long sentence started near the right edge of the image.
            place.Invoke(editor, [new PointD(520, 150)]);
            Drain(window);
            ((TextBox)active.GetValue(editor)!).Text = "오른쪽 끝 근처에서 시작한 긴 문장";
            commit.Invoke(editor, null);
            Drain(window);

            // A caption that is still being typed shows the live box at its final position.
            place.Invoke(editor, [new PointD(40, 200)]);
            Drain(window);
            ((TextBox)active.GetValue(editor)!).Text = "입력 중인 글자";
            Drain(window);

            Assert.Equal(3, controller.Document.Items.OfType<TextAnnotation>().Count());
            // Under the real theme (the window's text formatting, themed TextBox template) the live
            // box lays out with ideal metrics and puts its first glyph on the caption origin.
            var live = (TextBox)active.GetValue(editor)!;
            var surface = (AnnotationEditorSurface)typeof(AnnotationEditorControl).GetField("_surface", flags)!.GetValue(editor)!;
            Assert.Equal(MyCapture.App.Themes.ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(window));
            Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(live));
            Point glyph = live.TranslatePoint(live.GetRectFromCharacterIndex(0).TopLeft, surface);
            Point origin = surface.ToSurfacePoint(new PointD(40, 200));
            Assert.True(Math.Abs(glyph.X - origin.X) <= 0.75 && Math.Abs(glyph.Y - origin.Y) <= 0.75,
                $"{theme}: first glyph at {glyph}, caption origin at {origin}.");
            SaveEditorWindowEvidence(window, $"editor-text-{theme}-{phase}.png");
            BitmapSource flattened = editor.RenderCurrentImageAsync().GetAwaiter().GetResult();

            // The committed edge caption selected: its box hugs the wrapped text, with width and
            // corner handles only (its height follows the text).
            commit.Invoke(editor, null);
            controller.SetSelected(controller.Document.Items.OfType<TextAnnotation>().ElementAt(1));
            Drain(window);
            SaveEditorWindowEvidence(window, $"editor-text-selected-{theme}-{phase}.png");
            return flattened;
        }
        finally
        {
            window.Close();
            Drain(window);
        }
    }

    private static BitmapSource EditorEvidenceBackground(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFA)), null, new Rect(0, 0, width, height));
            var bar = new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE6));
            for (int row = 0; row < 6; row++)
            {
                context.DrawRoundedRectangle(bar, null, new Rect(24, 24 + (row * 44), width - 48 - (row * 37 % 160), 18), 4, 4);
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void SaveEditorWindowEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_TEXT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        Drain(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        SaveEditorBitmapEvidence(bitmap, name);
    }

    private static void SaveEditorBitmapEvidence(BitmapSource bitmap, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_EDITOR_TEXT_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
