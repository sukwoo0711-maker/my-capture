using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders the editor states the UX review self-test cannot reach, in the real themes: the
/// inspector's slider value boxes, the size-reduction dialog while it recalculates, and the
/// video editor's trim bar and layer panel at narrow and wide sizes.
/// </summary>
/// <remarks>
/// Set <c>MYCAPTURE_EDITORS_QA6_EVIDENCE</c> to a directory to keep the PNGs, and
/// <c>MYCAPTURE_EDITORS_QA6_PHASE</c> to label them. Without it the scenes still run and
/// assert their layout.
/// </remarks>
public sealed partial class SettingsThemePreviewTests
{
    private const string EditorsQa6Evidence = "MYCAPTURE_EDITORS_QA6_EVIDENCE";

    [Fact]
    public Task EditorsQa6_InspectorValueBoxesFitTheInspectorInEveryEvidenceTheme() =>
        RunIsolated(nameof(EditorsQa6_InspectorValueBoxesFitTheInspectorInEveryEvidenceTheme), VerifyInspectorValueBoxes);

    private static Application CreateQa6Application()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        return application;
    }

    private static string Qa6Phase =>
        Environment.GetEnvironmentVariable("MYCAPTURE_EDITORS_QA6_PHASE") is { Length: > 0 } label ? label : "after";

    private static void VerifyInspectorValueBoxes()
    {
        Application application = CreateQa6Application();
        try
        {
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var culture = UiText.UseLanguage(language);
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight })
                {
                    ThemeService.Apply(theme);
                    foreach ((int width, int height) in new[] { (1180, 640), (860, 600) })
                        RenderInspectorScene(theme, language, width, height);
                }
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void RenderInspectorScene(AppTheme theme, string language, int width, int height)
    {
        BitmapSource bitmap = Qa6Background(720, 300);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = width, Height = height,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            AnnotationEditorControl editor = window.Editor;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var inspector = (FrameworkElement)typeof(AnnotationEditorControl).GetField("_inspectorPanel", flags)!.GetValue(editor)!;
            var controller = (AnnotationEditorController)typeof(AnnotationEditorControl).GetField("_controller", flags)!.GetValue(editor)!;
            MethodInfo selectTool = typeof(AnnotationEditorControl).GetMethod("SelectTool", flags)!;
            // The widest value the box must show.
            controller.ApplyStrokeThickness(64);
            foreach (EditorTool tool in new[] { EditorTool.Rectangle, EditorTool.Text, EditorTool.Blur })
            {
                selectTool.Invoke(editor, [tool]);
                Drain(window);
                foreach (TextBox box in EditorKeysTests.Descendants(inspector).OfType<TextBox>()
                    .Where(box => box.IsVisible && box.Width == SliderValueEntry.BoxWidth))
                {
                    Rect bounds = box.TransformToAncestor(inspector).TransformBounds(new Rect(box.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= inspector.ActualWidth + 0.5,
                        $"{tool} {AutomationProperties.GetName(box)} box {bounds} in {inspector.ActualWidth} at {width}");
                    var host = (ScrollViewer?)box.Template.FindName("PART_ContentHost", box);
                    Assert.True(host is null || host.ExtentWidth <= host.ViewportWidth + 0.5, $"{tool} value '{box.Text}' does not fit");
                }

                BitmapSource full = RenderEditorWindow(window);
                SaveQa6Evidence(Crop(full, window, inspector, 0), $"inspector-{tool}-{theme}-{language}-{width}-{Qa6Phase}.png");
                // The whole window once per size: the command bar and tool rail carry the key captions.
                if (tool == EditorTool.Rectangle) SaveQa6Evidence(full, $"annotation-{theme}-{language}-{width}-{Qa6Phase}.png");
            }
        }
        finally
        {
            window.Close();
            Drain(window);
        }
    }

    private static BitmapSource Qa6Background(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = ((y * width) + x) * 4;
            pixels[offset] = (byte)(40 + (x * 120 / width));
            pixels[offset + 1] = (byte)(50 + (y * 100 / height));
            pixels[offset + 2] = 70;
            pixels[offset + 3] = 255;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveQa6Evidence(BitmapSource bitmap, string name)
    {
        string? requested = Environment.GetEnvironmentVariable(EditorsQa6Evidence);
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
