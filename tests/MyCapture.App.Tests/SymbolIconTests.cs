using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SymbolIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void NarrowGlyphKeepsAuthoredPositionAndScalesItsStroke(double size) => StaTestHost.Run(() =>
    {
        Geometry geometry = Geometry.Parse("M10,4 L10,16");
        var icon = new SymbolIcon { Data = geometry, Size = size, Stroke = Brushes.White };
        var referencePath = new System.Windows.Shapes.Path
        {
            Data = geometry, Stroke = Brushes.White, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var canvas = new Canvas { Width = 20, Height = 20 };
        canvas.Children.Add(referencePath);
        var reference = new Viewbox { Width = size, Height = size, Child = canvas };
        Assert.Equal(Pixels(Render(reference, size, size)), Pixels(Render(icon, size, size)));
        Assert.Equal(size / 20 * 1.6, icon.RenderedStrokeThickness, 8);
        Assert.Equal(new Size(size, size), icon.DesiredSize);
    });

    [Fact]
    public void ForegroundAndResourceReplacementStayLiveWithoutMutatingSharedBrush() => StaTestHost.Run(() =>
    {
        var original = new SolidColorBrush(Colors.Red);
        var replacement = new SolidColorBrush(Colors.Blue);
        var root = new Border();
        root.Resources["Text.Primary"] = original;
        root.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        var icon = SymbolIcon.Create(Geometry.Parse("M4,10 L16,10"));
        root.Child = icon;
        byte[] before = Pixels(Render(root, 20, 20));
        Assert.Same(original, icon.Foreground);
        root.Resources["Text.Primary"] = replacement;
        byte[] after = Pixels(Render(root, 20, 20));
        Assert.Same(replacement, icon.Foreground);
        Assert.NotEqual(before, after);
        Assert.Equal(Colors.Red, original.Color);
        Assert.False(original.IsFrozen);
    });

    [Fact]
    public void LateGeometryResourceAndFilledGlyphRenderOnTheSameGrid() => StaTestHost.Run(() =>
    {
        var icon = SymbolIcon.FromResource("Icon.Test", 16);
        var root = new Border { Child = icon };
        Assert.All(Pixels(Render(root, 16, 16)), value => Assert.Equal(0, value));
        root.Resources["Icon.Test"] = new RectangleGeometry(new Rect(5, 5, 10, 10));
        Assert.Contains(Pixels(Render(root, 16, 16)), value => value != 0);
        var filled = SymbolIcon.CreateFilled(icon.Data, 16);
        filled.Foreground = Brushes.Lime;
        Assert.Equal(0, filled.StrokeThickness);
        Assert.Same(Brushes.Lime, filled.Fill);
        Assert.Contains(Pixels(Render(filled, 16, 16)), value => value != 0);
    });

    [Fact]
    public void AllSharedGlyphsRenderAndKeepInsideTheirGridAtMenuAndCommandSizes() => StaTestHost.Run(() =>
    {
        var symbols = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Symbols.xaml"),
        };
        string[] keys = symbols.Keys.Cast<string>().OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Assert.True(keys.Length >= 45);
        foreach (string key in keys)
        {
            Geometry geometry = Assert.IsAssignableFrom<Geometry>(symbols[key]);
            Assert.True(new Rect(0, 0, 20, 20).Contains(geometry.Bounds), key);
            foreach (double size in new[] { SymbolIcon.InlineSize, SymbolIcon.CommandSize })
            {
                SymbolIcon icon = key.StartsWith("MediaExport_", StringComparison.Ordinal)
                    ? SymbolIcon.CreateFilled(geometry, size) : SymbolIcon.Create(geometry, size);
                icon.Foreground = Brushes.White;
                Assert.Contains(Pixels(Render(icon, size, size)), value => value != 0);
            }
        }

        string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_SYMBOL_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence)) return;
        Directory.CreateDirectory(evidence);
        ResourceDictionary baseline = symbols;
        string? baselineFile = Environment.GetEnvironmentVariable("MYCAPTURE_SYMBOL_BASELINE");
        if (!string.IsNullOrWhiteSpace(baselineFile))
            baseline = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(File.ReadAllText(baselineFile));
        foreach (double dpi in new[] { 96.0, 144.0 })
        foreach (bool contrast in new[] { false, true })
        {
            string suffix = $"{(contrast ? "contrast" : "glass")}-{dpi:0}dpi";
            Save(Atlas(baseline, keys, legacy: true, contrast, dpi), System.IO.Path.Combine(evidence, $"glyphs-before-{suffix}.png"));
            Save(Atlas(symbols, keys, legacy: false, contrast, dpi), System.IO.Path.Combine(evidence, $"glyphs-after-{suffix}.png"));
        }
    });

    private static BitmapSource Atlas(ResourceDictionary symbols, string[] keys, bool legacy, bool contrast, double dpi)
    {
        Brush foreground = contrast ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xCC, 0xD8, 0xE6));
        Brush background = contrast ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0x15, 0x1D, 0x2A));
        var canvas = new Canvas { Width = 960, Height = Math.Ceiling(keys.Length / 4.0) * 64 + 40, Background = background };
        canvas.Children.Add(new TextBlock { Text = $"{(legacy ? "AS-IS Path" : "TO-BE SymbolIcon")} | 16 / 20 DIP | {dpi:0} DPI", Foreground = foreground, FontSize = 14 });
        for (int i = 0; i < keys.Length; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal };
            cell.Children.Add(new TextBlock { Text = keys[i], Foreground = foreground, FontSize = 11, Width = 160, VerticalAlignment = VerticalAlignment.Center });
            foreach (double size in new[] { 16.0, 20.0 })
            {
                Geometry geometry = (Geometry)symbols[keys[i]];
                bool filled = keys[i].StartsWith("MediaExport_", StringComparison.Ordinal);
                FrameworkElement glyph;
                if (legacy)
                    glyph = new System.Windows.Shapes.Path { Data = geometry, Width = size, Height = size, Stretch = filled ? Stretch.None : Stretch.Uniform,
                        Stroke = filled ? null : foreground, Fill = filled ? foreground : null, StrokeThickness = 1.6,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
                else
                {
                    SymbolIcon icon = filled ? SymbolIcon.CreateFilled(geometry, size) : SymbolIcon.Create(geometry, size);
                    icon.Foreground = foreground;
                    glyph = icon;
                }
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                cell.Children.Add(new Border { Width = 32, Height = 32, Child = glyph, BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0.5) });
            }
            Canvas.SetLeft(cell, i % 4 * 240);
            Canvas.SetTop(cell, 36 + i / 4 * 64);
            canvas.Children.Add(cell);
        }
        return Render(canvas, canvas.Width, canvas.Height, dpi);
    }

    private static BitmapSource Render(FrameworkElement element, double width, double height, double dpi = 96)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96), (int)Math.Ceiling(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using Stream stream = File.Create(path);
        encoder.Save(stream);
    }
}
