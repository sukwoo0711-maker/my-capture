using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Real WPF windows, isolated settings and synthetic pixels. Also compiled against
/// the preserved 4.0.1 binaries by the comparison harness, without replacing their UI.</summary>
internal static class Ux402SelfTest
{
    internal const string CommandLineSwitch = "--selftest-ux402";
    internal static int Run()
    {
        string output = Directory.CreateTempSubdirectory("MyCapture-ux402-").FullName;
        Console.WriteLine("UX402_OUTPUT=" + output);
        var report = new StringBuilder();
        bool before = typeof(AnnotationEditorControl).GetProperty("PendingCrop", BindingFlags.Instance | BindingFlags.NonPublic) is null;
        report.AppendLine($"Assembly: {typeof(AnnotationEditorWindow).Assembly.Location}; version={typeof(AnnotationEditorWindow).Assembly.GetName().Version}; baseline={before}");
        report.AppendLine("Real native WPF windows rendered at 96/144/192 raster density. OS monitor DPI is unchanged. Synthetic image/video and isolated settings only.");
        var windows = new List<(string Name, Window Window)>();
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        string oldLanguage = UiText.Culture.Name;
        var oldTheme = ThemeService.IsAppliedForTest;
        int count = 0, failures = 0;
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            BitmapSource sample = Sample();
            var region = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
            var paths = AppPaths.CreateForRoot(Path.Combine(output, "fixtures"));
            paths.EnsureCreated();
            RecordingResult recording = Video(Path.Combine(paths.DataRoot, "sample.mp4"), sample);
            foreach (string language in new[] { "ko", "en" })
            using (UiText.UseLanguage(language))
            {
                UiText.Configure(language);
                ThemeService.Apply(AppTheme.Midnight);
                AnnotationEditorWindow Editor(double width, double height) => new(new FrozenFrame(sample, region, null, 0), region, sample) { Width = width, Height = height };
                windows.Add(("editor", Editor(1000, 620)));
                windows.Add(("editor-small", Editor(680, 460)));
                foreach (var (name, width, height) in new[] { ("crop", 1000, 620), ("crop-small", 680, 460) })
                {
                    var editor = Editor(width, height);
                    editor.Editor.HandleShortcut(Key.X, ModifierKeys.None);
                    // Both versions enter their actual production crop-drag state. In 4.0.1
                    // this state deliberately has no preview; the fixture does not draw one.
                    object? crop = typeof(AnnotationEditorControl).GetProperty("PendingCrop", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor.Editor);
                    if (crop is not null)
                    {
                        crop.GetType().GetMethod("Begin")!.Invoke(crop, [new PointD(640, 360), 8.0, 4.0, 4.0]);
                        crop.GetType().GetMethod("Drag")!.Invoke(crop, [new PointD(510, 286), false]);
                        typeof(AnnotationEditorControl).GetMethod("RefreshCrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor.Editor, null);
                    }
                    else
                    {
                        typeof(AnnotationEditorControl).GetField("_cropDragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor.Editor, true);
                        typeof(AnnotationEditorControl).GetField("_cropStart", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor.Editor, new PointD(0, 0));
                    }
                    windows.Add((name, editor));
                }
                windows.Add(("card", new QuickAccessWindow(sample, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                    () => Task.FromResult(true), () => { }, () => Task.FromResult("fixture.png")) { IsKept = true }));
                windows.Add(("video", new VideoEditorWindow(recording, paths, NullLoggerFactory.Instance)));
                windows.Add(("video-small", new VideoEditorWindow(recording, paths, NullLoggerFactory.Instance) { Width = 760, Height = 555 }));
                var settings = new AppSettings();
                windows.Add(("settings", new SettingsWindow(() => settings, _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)));
                var ocr = new OcrResultWindow();
                ocr.ShowResult(OcrResult.Success("Synthetic OCR text\n선택한 텍스트", "ko", [], TimeSpan.Zero), "Capture");
                ocr.Hide(); windows.Add(("ocr", ocr));
                foreach (var (name, window) in windows)
                {
                    window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = 30; window.Top = 30; window.Show(); Pump(name.StartsWith("video", StringComparison.Ordinal) ? 1800 : 100);
                    foreach (AppTheme theme in Enum.GetValues<AppTheme>())
                    {
                        ThemeService.Apply(theme); Pump(50); window.UpdateLayout();
                        string label = $"{name}-{language}-{AppThemeNames.ToSetting(theme)}";
                        foreach (int dpi in new[] { 96, 144, 192 }) { Render(window, Path.Combine(output, label + $"-{dpi}.png"), dpi); count++; }
                        var inventory = new List<object>();
                        foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>().Where(element => element.IsVisible))
                        {
                            if (element is not (TextBlock or ButtonBase or ComboBox or TextBox)) continue;
                            Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                            inventory.Add(new { Type = element.GetType().Name, Text = (element as TextBlock)?.Text,
                                Name = AutomationProperties.GetName(element), Gesture = AutomationProperties.GetAcceleratorKey(element),
                                bounds.X, bounds.Y, bounds.Width, bounds.Height, Outside = bounds.Left < 0 || bounds.Right > window.ActualWidth + 1 || bounds.Bottom > window.ActualHeight + 1 });
                        }
                        File.WriteAllText(Path.Combine(output, label + ".json"), JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
                        if (!before && window is AnnotationEditorWindow annotation)
                        {
                            ToggleButton[] tools = Descendants(annotation.Editor).OfType<ToggleButton>()
                                .Where(tool => !string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(tool))).ToArray();
                            bool fits = tools.Length == 11 && tools.All(tool =>
                            {
                                Rect bounds = tool.TransformToAncestor(window).TransformBounds(new Rect(tool.RenderSize));
                                return tool.IsVisible && bounds.Width >= 30 && bounds.Height >= 30 && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight;
                            });
                            report.AppendLine($"{label}: all 11 tools visible={fits}");
                            if (!fits) failures++;
                        }
                    }
                    window.Hide();
                }
                CloseWindows(windows); windows.Clear();
            }
            report.AppendLine($"Images: {count}; layout failures: {failures}");
        }
        catch (Exception error) { failures++; report.AppendLine(error.ToString()); }
        finally
        {
            CloseWindows(windows);
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            UiText.Configure(oldLanguage);
            if (oldTheme is AppTheme restore) ThemeService.Apply(restore);
        }
        report.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        File.WriteAllText(Path.Combine(output, "ux402-report.txt"), report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    private static void CloseWindows(List<(string Name, Window Window)> windows)
    {
        foreach (var (_, window) in windows)
        {
            if (window is SettingsWindow settings) settings.CloseForExit();
            else if (window is OcrResultWindow ocr) ocr.CloseForExit();
            else window.Close();
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static BitmapSource Sample()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 29, 46)), null, new Rect(0, 0, 640, 360));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(29, 47, 68)), null, new Rect(28, 28, 584, 304), 12, 12);
            dc.DrawText(new FormattedText("MyCapture · Capture review", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.White, 1), new Point(54, 58));
            dc.DrawText(new FormattedText("public void Capture()\n{\n    Save();\n}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 23, Brushes.LightSkyBlue, 1), new Point(54, 120));
            dc.DrawRoundedRectangle(Brushes.CornflowerBlue, null, new Rect(448, 273, 128, 34), 6, 6);
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static RecordingResult Video(string path, BitmapSource bitmap)
    {
        var pixels = new byte[640 * 360 * 4]; bitmap.CopyPixels(pixels, 640 * 4, 0);
        using var encoder = new MediaFoundationVideoEncoder(new VideoEncoderOptions(path, 640, 360, 15, 1_000_000), NullLogger<MediaFoundationVideoEncoder>.Instance);
        for (int frame = 0; frame < 45; frame++) encoder.WriteFrame(new EncoderFrame(pixels, 640, 360, 640 * 4, frame * (1000.0 / 15)));
        encoder.Complete(); return new RecordingResult(path, 3000, 15, 45, 640, 360);
    }
    private static void Render(Window window, string path, int dpi)
    {
        double scale = dpi / 96.0;
        var target = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(window.TryFindResource("Surface.Base") as Brush ?? Brushes.Black, null, new Rect(window.RenderSize));
        target.Render(backdrop); target.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
