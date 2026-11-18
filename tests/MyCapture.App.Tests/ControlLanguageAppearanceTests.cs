using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ControlLanguageAppearanceTests
{
    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void CheckAndRadioUseFilledMarksAndPreserveContrastGeometry(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, systemContrast);
        bool legacy = systemContrast || theme == AppTheme.HighContrast;
        var check = new CheckBox { Content = "Sample", Style = (Style)resources[typeof(CheckBox)] };
        var radio = new RadioButton { Content = "Sample", Style = (Style)resources[typeof(RadioButton)] };
        var stack = new StackPanel(); stack.Children.Add(check); stack.Children.Add(radio);
        var host = Host(resources, stack); Layout(host);
        var box = Part<Border>(check, "Box");
        Assert.Equal(new CornerRadius(legacy ? 5 : 4), box.CornerRadius);
        Assert.Equal(new Thickness(1), box.BorderThickness);
        Assert.Equal(ColorOf(resources, legacy ? "Border.Control" : "Stroke.CheckEdge"), ColorOf(box.BorderBrush));
        Assert.Equal(legacy ? ColorOf(resources, "Surface.Sunken") : Colors.Transparent, ColorOf(box.Background));
        var pressTarget = Part<Grid>(check, "PART_PressTarget");
        Assert.Equal(new Size(18, 18), pressTarget.RenderSize);
        check.IsChecked = true; radio.IsChecked = true; Layout(host);
        var selected = Part<Border>(check, "CheckedLayer");
        Assert.Equal(new Thickness(legacy ? 2 : 0), selected.BorderThickness);
        Assert.Equal(ColorOf(resources, legacy ? "Surface.Selected" : "Text.Primary"), ColorOf(selected.Background));
        Color baseColor = ColorOf(resources, "Surface.Base");
        Color opaqueBase = Color.FromRgb(baseColor.R, baseColor.G, baseColor.B);
        var mark = Part<ShapePath>(check, "Check");
        Assert.Equal(legacy ? ColorOf(resources, "Text.Primary") : opaqueBase, ColorOf(mark.Stroke));
        Assert.Equal(new Thickness(legacy ? 0 : 2), mark.Margin);
        Assert.Equal(legacy ? ColorOf(resources, "Surface.Raised") : opaqueBase, ColorOf(Part<Ellipse>(radio, "Check").Fill));
        check.IsThreeState = true; check.IsChecked = null; Layout(host);
        Assert.Equal(new Thickness(legacy ? 2 : 0), selected.BorderThickness);
        Assert.Equal(1d, selected.Opacity);
        Assert.Equal(Rect.Parse("4,7,6,0"), mark.Data.Bounds);
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void BaselineMeasuredSizesRemainAndTheScrollTrackKeepsItsLane(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, systemContrast);
        bool legacy = systemContrast || theme == AppTheme.HighContrast;
        var input = new TextBox { Text = "Sample", FontSize = 14, Width = 160 };
        var combo = new ComboBox { FontSize = 14, Width = 160 };
        combo.Items.Add("Sample"); combo.SelectedIndex = 0;
        var check = new CheckBox { Content = "Sample", FontSize = 14 };
        var bar = new ScrollBar { Width = 16, Height = 120, Maximum = 100, ViewportSize = 10, Value = 20,
            HorizontalAlignment = HorizontalAlignment.Left };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (Control control in new Control[] { input, combo, check, bar }) stack.Children.Add(control);
        var host = Host(resources, stack); Layout(host);
        // Captured from the unmodified build's control-measurements-glass.json and high-contrast twin.
        Assert.Equal(new Size(160, 36), input.DesiredSize);
        Assert.Equal(new Size(160, 32), combo.DesiredSize);
        Assert.Equal(new Size(75, 32), check.DesiredSize);
        // WPF's system minimum makes the outer control 17, while its declared lane stays 16.
        Assert.Equal(new Size(17, 120), bar.DesiredSize);
        Assert.Equal(new Size(17, 120), bar.RenderSize);
        Assert.Equal(16d, bar.Width);
        var track = Part<Track>(bar, "PART_Track");
        Assert.Equal(Orientation.Vertical, track.Orientation);
        Assert.Same(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
        Assert.Same(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);
        Assert.Equal(16d, Part<Border>(bar, "Lane").ActualWidth);
        var grip = Part<ScrollThumbChrome>(track.Thumb, "Grip");
        Assert.Equal(3d, grip.GripWidth);
        Assert.Equal(ColorOf(resources, "Fill.ScrollThumb"), grip.GripColor);
        Assert.True(track.Thumb.ActualHeight >= 24);
        if (legacy)
        {
            Assert.Equal(ColorOf(resources, "Surface.Base"), ColorOf(bar.Background));
            Assert.NotNull(input.Template.FindName("FocusRing", input));
        }
        else
        {
            Assert.Equal(Colors.Transparent, ColorOf(bar.Background));
            Assert.Equal(0d, Part<Border>(input, "FocusRing").Opacity);
            var edge = Part<InputEdge>(input, "BottomEdge");
            Assert.Equal(1d, edge.Thickness);
            Assert.Equal(ColorOf(resources, "Stroke.InputEdge"), ColorOf(edge.Brush!));
            Assert.Same(resources["Button.Fill"], combo.Background);
            Assert.Same(resources["Button.BorderBrush"], combo.BorderBrush);
            Assert.Equal(new CornerRadius(6), Part<Border>(combo, "ComboChrome").CornerRadius);
            input.IsReadOnly = true; Layout(host);
            Assert.Equal(Visibility.Collapsed, edge.Visibility);
            Assert.Equal(Colors.Transparent, ColorOf(Part<Border>(input, "Chrome").Background));
        }
    });

    [Fact]
    public void PointerFocusedInputHasTwoDipEdgeWithoutKeyboardRing() => StaTestHost.Run(() =>
    {
        var input = new TextBox { Text = "Sample", Width = 160 };
        var window = new Window { Resources = Resources(AppTheme.Glass, false), Content = input,
            Width = 240, Height = 120, Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        ModernWindowChrome.SetEnabled(window, false);
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            KeyboardFocusCue.NotifyPointerInput(window);
            Assert.Same(input, Keyboard.Focus(input));
            window.UpdateLayout();
            Assert.True(input.IsKeyboardFocused);
            Assert.False(KeyboardFocusCue.GetIsVisible(input));
            Assert.Equal(0d, Part<Border>(input, "FocusRing").Opacity);
            Assert.Equal(2d, Part<InputEdge>(input, "BottomEdge").Thickness);
            Assert.Equal(ColorOf(window.Resources, "Text.Primary"), ColorOf(Part<InputEdge>(input, "BottomEdge").Brush!));
            Border ring = Part<Border>(input, "FocusRing");
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            KeyboardFocusCue.NotifyKeyboardInput(window);
            window.UpdateLayout();
            Assert.True(KeyboardFocusCue.GetIsVisible(input));
            Assert.Equal(1d, ring.Opacity);
            Assert.Equal(2d, Part<InputEdge>(input, "BottomEdge").Thickness);
            KeyboardFocusCue.NotifyPointerInput(window);
            window.UpdateLayout();
            Assert.Equal(0d, ring.Opacity);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(32, 1)]
    [InlineData(96, 1)]
    [InlineData(32, 2)]
    [InlineData(96, 2)]
    public void BottomStrokeHasConstantPixelThicknessAndRoundedEnds(int height, int thickness) => StaTestHost.Run(() =>
    {
        var drawing = new DrawingVisual();
        using (DrawingContext dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 40, height));
            var edge = new InputEdgeDrawing();
            edge.Draw(dc, new Size(40, height), thickness, Brushes.White, FoundationMetrics.ControlRadius);
        }
        var image = new RenderTargetBitmap(40, height, 96, 96, PixelFormats.Pbgra32); image.Render(drawing);
        var pixels = new byte[40 * height * 4]; image.CopyPixels(pixels, 40 * 4, 0);
        for (int y = 0; y < height; y++)
            Assert.Equal(y >= height - thickness ? (byte)255 : (byte)0, pixels[(y * 40 + 20) * 4]);
        Assert.Equal((byte)0, pixels[((height - 1) * 40) * 4]);
    });

    private static ResourceDictionary Resources(AppTheme theme, bool contrast)
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: contrast);
        return resources;
    }
    private static Border Host(ResourceDictionary resources, UIElement child)
    {
        var host = new Border { Width = 440, Height = 470, Resources = resources, Child = child,
            UseLayoutRounding = true, SnapsToDevicePixels = true };
        host.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        host.SetResourceReference(TextElement.FontSizeProperty, "FontSize.Body");
        ThemeFonts.ApplyText(host);
        return host;
    }
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
    }
    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));
    private static Color ColorOf(ResourceDictionary resources, string key) => ColorOf((Brush)resources[key]);
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}
