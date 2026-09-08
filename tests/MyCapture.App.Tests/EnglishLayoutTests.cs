using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EnglishLayoutTests
{
    [Theory]
    [InlineData(760, 555)]
    [InlineData(920, 680)]
    public void VideoEditorEnglishActionsAndStatusFitNativeWindow(int width, int height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string root = Path.Combine(Path.GetTempPath(), "mc-en-layout-" + Guid.NewGuid().ToString("N"));
        var recording = new RecordingResult(Path.Combine(root, "layout-only.mp4"), 2000, 30, 60, 320, 240);
        var window = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        try
        {
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            }
            window.FontSize = (double)window.FindResource("FontSize.Body");
            window.Width = width;
            window.Height = height;
            window.Left = -10000;
            window.Top = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();
            window.UpdateLayout();
            Grid layout = Assert.IsType<Grid>(window.Content);
            Assert.DoesNotMatch("[가-힣]", window.Title);
            Border preview = layout.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 0);
            Border status = layout.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 3);
            Assert.True(preview.ActualHeight >= 112);
            var client = (FrameworkElement)VisualTreeHelper.GetParent(layout);
            var statusText = Assert.IsType<TextBlock>(status.Child);
            Assert.True(statusText.ActualHeight >= statusText.DesiredSize.Height - 0.5);
            Assert.InRange(statusText.TranslatePoint(new Point(0, statusText.ActualHeight), client).Y, 0, client.ActualHeight + 0.5);
            var connector = Assert.Single(Descendants(layout).OfType<TimelineRenderSurface>(), surface => !surface.IsHitTestVisible);
            Assert.True(connector.ActualHeight >= 18, "Connector hint must reserve a complete text line");
            Button[] buttons = Descendants(layout).OfType<Button>().ToArray();
            foreach (string caption in new[] { "Text", "Rectangle", "Circle", "Image", "Edit", "Delete", "GIF options", "GIF" })
            {
                Button button = Assert.Single(buttons, b => Equals(b.Content, caption) || Descendants(b).OfType<TextBlock>().Any(t => t.Text == caption));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), caption);
                Assert.NotNull(button.ToolTip);
                Point top = button.TranslatePoint(new Point(0, 0), layout);
                Point bottom = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), layout);
                Assert.True(button.IsVisible && top.X >= 0 && bottom.X <= layout.ActualWidth + 0.5 && bottom.Y <= layout.ActualHeight + 0.5, caption);
                foreach (TextBlock text in Descendants(button).OfType<TextBlock>())
                    Assert.True(text.ActualWidth + text.Margin.Left + text.Margin.Right + 0.5 >= text.DesiredSize.Width, $"Clipped {caption}");
            }
            var bitmap = new RenderTargetBitmap((int)layout.ActualWidth, (int)layout.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(layout);
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_LOCALIZATION_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                using FileStream stream = File.Create(Path.Combine(evidence, $"video-english-{width}.png"));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
