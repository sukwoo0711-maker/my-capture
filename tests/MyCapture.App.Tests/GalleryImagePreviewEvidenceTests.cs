using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryImagePreviewEvidenceTests
{
    [Fact]
    public Task RendersPreviewAtActualSizeAndAfterZoomWithoutShowingWindow() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_PREVIEW_EVIDENCE"))
            ? Task.CompletedTask
            : SettingsThemePreviewTests.RunIsolated(nameof(RendersPreviewAtActualSizeAndAfterZoomWithoutShowingWindow), () =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            using var language = UiText.UseLanguage("ko-KR");
            Window preview = GalleryWindow.CreateImagePreviewWindow(FixtureImage(), "미리보기 검토", "미리보기 검토");
            try
            {
                var content = (FrameworkElement)preview.Content;
                preview.Content = null;
                var frame = new Border
                {
                    Width = 640, Height = 420, Child = content,
                    Background = (Brush)app.FindResource("Surface.Base"),
                };
                Layout(frame);
                string phase = Environment.GetEnvironmentVariable("MYCAPTURE_PREVIEW_PHASE") ?? "after";
                Assert.Equal("after", phase); // Never overwrite the captured pre-zoom implementation.
                var zoomPreview = Assert.IsType<GalleryImagePreviewWindow>(preview);
                zoomPreview.UpdateViewport(new Size(content.ActualWidth, content.ActualHeight), 1.5);
                Assert.Equal(100, zoomPreview.ViewportModel.Percentage, 6);
                Assert.EndsWith("100%", preview.Title);
                Save(frame, $"r2-{phase}-preview-100.png");
                Assert.True(zoomPreview.HandleWheel(720, ModifierKeys.Control,
                    new Point(content.ActualWidth / 2, content.ActualHeight / 2)));
                Assert.InRange(zoomPreview.ViewportModel.Percentage, 298, 299);
                Layout(frame);
                Save(frame, $"r2-{phase}-preview-zoomed.png");
                Assert.False(preview.IsVisible);
            }
            finally { preview.Close(); app.Shutdown(); }
        }, typeof(GalleryImagePreviewEvidenceTests));

    internal static BitmapSource FixtureImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 247, 252)), null, new Rect(0, 0, 400, 260));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(43, 73, 110)), null, new Rect(0, 0, 400, 44));
            drawing.DrawText(Text("CAPTURE REVIEW", 18, Brushes.White), new Point(20, 10));
            drawing.DrawText(Text("Image preview", 24, Brushes.Black), new Point(20, 62));
            drawing.DrawText(Text("Read small text without opening the editor.", 13, Brushes.DimGray), new Point(20, 104));
            for (int row = 0; row < 4; row++)
            {
                drawing.DrawRectangle(row % 2 == 0 ? Brushes.White : Brushes.LightSteelBlue, null, new Rect(20, 138 + row * 23, 360, 22));
                drawing.DrawText(Text($"Item {row + 1}                         {(row + 1) * 125:N0} px", 12, Brushes.Black), new Point(28, 140 + row * 23));
            }
            drawing.DrawRectangle(null, new Pen(Brushes.SteelBlue, 1), new Rect(0.5, 0.5, 399, 259));
        }
        var bitmap = new RenderTargetBitmap(400, 260, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Text(string text, double size, Brush foreground) => new(text,
        System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, foreground, 1);

    private static void Layout(FrameworkElement frame)
    {
        frame.Measure(new Size(frame.Width, frame.Height));
        frame.Arrange(new Rect(0, 0, frame.Width, frame.Height));
        frame.UpdateLayout();
    }

    private static void Save(FrameworkElement frame, string name)
    {
        string root = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable("MYCAPTURE_PREVIEW_EVIDENCE")!);
        var bitmap = new RenderTargetBitmap(960, 630, 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(root, name));
        encoder.Save(stream);
    }
}
