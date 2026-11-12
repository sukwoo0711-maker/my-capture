using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Writes PNGs of the copy notice and the copy menu in the dark theme when MYCAPTURE_PIN_COPY_EVIDENCE names a folder.</summary>
public sealed class PinCopyClarityEvidenceTests
{
    private const string Source = "#define MAX_LEN 256\r\nint value = MAX_LEN;\r\n";

    [Fact]
    public void CopyNoticeAndMenuSnapshots_WhenRequested()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_COPY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string output = DiagnosticOutputPaths.Create(requested);
        StaTestHost.Run(() =>
        {
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var culture = UiText.UseLanguage(language);
                string tag = language[..2];
                foreach (string kind in new[] { "text", "image" })
                {
                    var bitmap = BitmapSource.Create(360, 140, 96, 96, PixelFormats.Bgr32, null,
                        Enumerable.Repeat((byte)0x30, 360 * 140 * 4).ToArray(), 360 * 4);
                    bitmap.Freeze();
                    PinContent content = kind == "text" ? PinContent.FromText(bitmap, Source, false) : PinContent.FromImage(bitmap);
                    var pin = new PinWindow(content, new PinViewState(360, 140, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
                    {
                        ShowActivated = false,
                        ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
                    };
                    try
                    {
                        var resources = new ResourceDictionary();
                        resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml"),
                        });
                        foreach (string dictionary in new[] { "Symbols", "Controls" })
                            resources.MergedDictionaries.Add(new ResourceDictionary
                            {
                                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml"),
                            });
                        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false, accent: AppAccent.Red);
                        pin.Resources = resources;
                        pin.Show();
                        pin.ReportCopyResult(true);
                        Save(pin.FeedbackSurfaceForTest, DiagnosticOutputPaths.Child(output, $"notice-{kind}-{tag}.png"));

                        ContextMenu menu = pin.ContextMenu!;
                        menu.Resources = resources;
                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
                        menu.PlacementTarget = pin;
                        menu.IsOpen = true;
                        menu.UpdateLayout();
                        Save(menu, DiagnosticOutputPaths.Child(output, $"menu-{kind}-{tag}.png"));
                        menu.IsOpen = false;
                    }
                    finally { pin.Close(); }
                }
            }
        });
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int width = Math.Max(1, (int)Math.Ceiling(Math.Max(element.ActualWidth, element.DesiredSize.Width)));
        int height = Math.Max(1, (int)Math.Ceiling(Math.Max(element.ActualHeight, element.DesiredSize.Height)));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x18)), Padding = new Thickness(8) };
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(backdrop.Background, null, new Rect(0, 0, width + 16, height + 16));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null, new Rect(8, 8, width, height));
        }
        var target = new RenderTargetBitmap(width + 16, height + 16, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
