using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Renders the real editor with deterministic, synthetic document content and local resources.</summary>
public sealed class EditorInspectorVisualTests
{
    [Fact]
    public void SelectedRectangleInspectorRendersAtOneThousandBySixForty() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application? applicationBefore = Application.Current;
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        Func<bool>? sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
        Window? window = null;
        try
        {
            AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = "Rectangle", StrokeThickness = 3 };
            AnnotationEditorPreferences.Write = _ => { };
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
            FluidMotion.AnimationsEnabledOverrideForTest = false;

            BitmapSource bitmap = SampleBitmap();
            var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var document = AnnotationDocument.CreateFor(bitmap.PixelWidth, bitmap.PixelHeight);
            var rectangle = new RectangleAnnotation
            {
                Rect = new RectD(150, 90, 500, 270), StrokeThickness = 3,
                Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            };
            document.Add(rectangle);
            document.Add(new TextAnnotation
            {
                Text = "편집할 영역", Rect = new RectD(294, 192, 220, 50),
                FontFamily = "Malgun Gothic", FontSize = 28, AutoSize = true,
                Foreground = ColorRgba.FromRgb(0x11, 0x18, 0x27),
                Background = ColorRgba.Transparent,
            });
            var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, document, null);
            var resources = new ResourceDictionary();
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml", UriKind.Absolute),
                });
            ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
            window = new Window
            {
                Content = editor, Resources = resources, Width = 1000, Height = 640,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(1, 1));
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl)
                .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            controller.Tool = EditorTool.Rectangle;
            controller.SetSelected(rectangle);
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));

            TextBox value = Descendants(editor).OfType<TextBox>()
                .Single(box => AutomationProperties.GetAutomationId(box) == "AnnotationThicknessValue");
            Assert.True(value.IsVisible);
            Assert.True(value.ActualWidth >= 24 && value.ActualHeight >= 24);
            Assert.Equal("3", value.Text);
            Assert.Equal(1000, editor.ActualWidth);
            Assert.Equal(640, editor.ActualHeight);
            Assert.Same(rectangle, controller.Selected);
            Assert.Equal(2, controller.Document.Items.Count);

            var rendered = new RenderTargetBitmap(1000, 640, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(editor);
            Assert.Equal(1000, rendered.PixelWidth);
            Assert.Equal(640, rendered.PixelHeight);
            var pixels = new byte[1000 * 640 * 4];
            rendered.CopyPixels(pixels, 1000 * 4, 0);
            Assert.Contains(pixels, component => component != 0);
            Assert.True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 1000 * 640 / 2);

            string? capturePath = Environment.GetEnvironmentVariable("MYCAPTURE_INSPECTOR_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Assert.True(Path.IsPathFullyQualified(capturePath), "The optional capture path must be absolute.");
                Assert.Equal(".png", Path.GetExtension(capturePath), ignoreCase: true);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rendered));
                using FileStream output = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                encoder.Save(output);
                Assert.True(output.Length > 0);
            }
            Assert.Same(applicationBefore, Application.Current);
        }
        finally
        {
            window?.Close();
            AnnotationEditorPreferences.Read = read;
            AnnotationEditorPreferences.Write = write;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = sourceTitle;
            FluidMotion.AnimationsEnabledOverrideForTest = animations;
        }
    });

    private static BitmapSource SampleBitmap()
    {
        const int width = 800, height = 450;
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0xEA;
            pixels[offset + 3] = 0xFF;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
