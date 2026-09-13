using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EnglishLayoutTests
{
    [Fact]
    public void ExportButtonUsesRealFilledThemeGeometry() => StaTestHost.Run(() =>
    {
        var window = new Window();
        LoadWindowTheme(window);
        var button = MediaExportVisuals.Button(window, "Export", "MediaExport_ArrowExport", true);
        window.Content = button;
        button.Measure(new Size(300, 80));
        button.Arrange(new Rect(0, 0, 300, 80));
        var path = Assert.Single(Descendants(button).OfType<System.Windows.Shapes.Path>());
        Assert.NotNull(path.Data);
        Assert.NotNull(path.Fill);
        Assert.Null(path.Stroke);
        Assert.Equal(20, path.Width);
        Assert.Same(window.FindResource("Button.Primary"), button.Style);
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(780, 560)]
    [InlineData(920, 680)]
    public void VideoEditorEnglishActionsAndStatusFitNativeWindow(int width, int height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        string root = OwnedTestDirectory.Create("mc-en-layout-");
        var recording = new RecordingResult(Path.Combine(root, "layout-only.mp4"), 2000, 30, 60, 320, 240);
        var document = VideoEditDocument.CreateFor(320, 240, 2000);
        document.TextOverlays.Add(new TimedTextOverlay { Text = "Readable source timing", StartMs = 300, EndMs = 2000 });
        var window = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document);
        try
        {
            LoadWindowTheme(window);
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
            Border preview = Descendants(layout).OfType<Border>().Single(child => child.Child is Grid panel && panel.Children.OfType<Grid>().Any(grid => grid.Name == "VideoPreviewViewport"));
            Border status = layout.Children.OfType<Border>().Single(b => Grid.GetRow(b) == 3);
            Assert.True(preview.ActualHeight >= 112);
            Grid viewport = Descendants(preview).OfType<Grid>().Single(grid => grid.Name == "VideoPreviewViewport");
            Assert.True(viewport.ActualHeight >= 112, "The actual video viewport must retain usable height");
            Assert.InRange(viewport.TranslatePoint(new Point(0, viewport.ActualHeight), preview).Y, 0, preview.ActualHeight + 0.5);
            ScrollViewer timeline = Assert.Single(layout.Children.OfType<ScrollViewer>());
            Assert.True(timeline.ActualHeight <= layout.RowDefinitions[2].ActualHeight + 0.5,
                "Timeline contents must scroll within the space allocated by the window");
            Assert.InRange(timeline.ViewportHeight, 0, timeline.ActualHeight + 0.5);
            foreach (string key in new[] { "Video.TrimIn", "Video.TrimOut" })
            {
                TextBox input = Assert.Single(Descendants(layout).OfType<TextBox>(),
                    candidate => AutomationProperties.GetName(candidate) == UiText.Get(key));
                Assert.True(input.IsVisible, key);
                input.Text = "00:00:02.000";
                input.ApplyTemplate();
                window.UpdateLayout();
                var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
                Assert.True(host.ViewportWidth > 0, key);
                Assert.True(host.ExtentWidth <= host.ViewportWidth + 0.5, "Precise In/Out timecode requires horizontal scrolling");
            }
            ListBox layers = Assert.Single(Descendants(layout).OfType<ListBox>());
            var firstLayer = Assert.IsType<ListBoxItem>(layers.Items[0]);
            Point layerBottom = firstLayer.TranslatePoint(new Point(0, firstLayer.ActualHeight), layers);
            Assert.InRange(layerBottom.Y, 0, layers.ActualHeight + 0.5);
            foreach (TextBlock text in Descendants(firstLayer).OfType<TextBlock>())
                Assert.InRange(text.TranslatePoint(new Point(0, text.ActualHeight), layers).Y, 0, layers.ActualHeight + 0.5);
            Border layerPanel = Assert.Single(Descendants(layout).OfType<Border>(), border => border.Name == "VideoLayersPanel");
            foreach (Button action in Descendants(layerPanel).OfType<Button>())
            {
                Point bottom = action.TranslatePoint(new Point(action.ActualWidth, action.ActualHeight), layerPanel);
                Assert.InRange(bottom.X, 0, layerPanel.ActualWidth + 0.5);
                Assert.InRange(bottom.Y, 0, layerPanel.ActualHeight - layerPanel.Padding.Bottom + 0.5);
            }
            var client = (FrameworkElement)VisualTreeHelper.GetParent(layout);
            var statusText = Assert.IsType<TextBlock>(status.Child);
            Assert.True(statusText.ActualHeight >= statusText.DesiredSize.Height - 0.5);
            Assert.InRange(statusText.TranslatePoint(new Point(0, statusText.ActualHeight), client).Y, 0, client.ActualHeight + 0.5);
            for (DependencyObject? ancestor = VisualTreeHelper.GetParent(statusText); ancestor is FrameworkElement bounds; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                Point top = statusText.TranslatePoint(new Point(), bounds);
                Point bottom = statusText.TranslatePoint(new Point(statusText.ActualWidth, statusText.ActualHeight), bounds);
                Assert.InRange(top.Y, -0.5, bounds.ActualHeight + 0.5);
                Assert.InRange(bottom.Y, 0, bounds.ActualHeight + 0.5);
            }
            var connector = Assert.Single(Descendants(layout).OfType<TimelineRenderSurface>(), surface => !surface.IsHitTestVisible);
            Assert.True(connector.ActualHeight >= 18, "Connector hint must reserve a complete text line");
            Button[] buttons = Descendants(layout).OfType<Button>().ToArray();
            foreach (string caption in new[] { "Text", "Rectangle", "Circle", "Image", "Save edits", "Export · MP4 / GIF" })
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
            foreach (string key in new[]
            {
                "Text_AEB8B9AA4E6F", "Text_9163C86EDB22", // selected layer edit/delete
                "Text_DC710AA11970", "Text_E166B75FADF4", "Text_9A9C87658130", "Text_E5CAF7CFC916", "Text_DFCA22AB9F61", // transport
                "Text_B39342508541", "Text_B753165F6585", "Text_48D137437347", "Text_C5176D8C3041", "Text_6CA53DEDFF4A", // frames and zoom
            })
            {
                Button action = Assert.Single(buttons, button => AutomationProperties.GetName(button) == UiText.Get(key));
                Assert.NotNull(action.ToolTip);
                Assert.True(action.IsVisible && action.ActualWidth >= 36 && action.ActualHeight >= 36);
                Point bottom = action.TranslatePoint(new Point(action.ActualWidth, action.ActualHeight), layout);
                Assert.InRange(bottom.X, 0, layout.ActualWidth + 0.5);
                Assert.InRange(bottom.Y, 0, layout.ActualHeight + 0.5);
            }
            var bitmap = new RenderTargetBitmap((int)layout.ActualWidth, (int)layout.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(layout);
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_LOCALIZATION_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                evidence = DiagnosticOutputPaths.Create(evidence);
                using FileStream stream = File.Create(DiagnosticOutputPaths.Child(evidence, $"video-english-{width}.png"));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
        }
        finally
        {
            try { window.Close(); }
            finally { OwnedTestDirectory.Delete(root); }
        }
    });

    private static void LoadWindowTheme(Window window)
    {
        // Compiled Controls BAML prefetches StaticResource values at Source load time.
        // App.xaml supplies them through Application.Resources; attaching dictionaries
        // to a standalone Window afterwards cannot repair an already captured UnsetValue.
        // Parse the exact embedded product markup together, in the production order,
        // so these multi-STA tests retain real templates without a global Application.
        XElement? combined = null;
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(EnglishLayoutTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            combined ??= new XElement(dictionary.Name);
            foreach (XAttribute declaration in dictionary.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                combined.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined!.ToString(SaveOptions.DisableFormatting)));
    }

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
