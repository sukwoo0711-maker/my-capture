using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FoundationInputValidationTests
{
    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void KeyboardFocusedInvalidTextKeepsItsErrorEdge(AppTheme theme) => StaTestHost.Run(() =>
    {
        var resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        var input = new TextBox { Width = 160, Height = 36 };
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text)) { Source = new TextBox { Text = "12" } });
        var window = new Window { Resources = resources, Content = input, Width = 240, Height = 120,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0, ShowInTaskbar = false };
        ModernWindowChrome.SetEnabled(window, false);
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            KeyboardFocusCue.NotifyKeyboardInput(window);
            Assert.Same(input, Keyboard.Focus(input));
            BindingExpression binding = input.GetBindingExpression(TextBox.TextProperty)!;
            Validation.MarkInvalid(binding, new ValidationError(new ExceptionValidationRule(), binding, "Invalid value", null));
            window.UpdateLayout();
            var ring = Assert.IsType<Border>(input.Template.FindName("FocusRing", input));
            Assert.Equal(1d, ring.Opacity);
            Assert.Equal(new Thickness(2), ring.BorderThickness);
            var edge = Assert.IsType<InputEdge>(input.Template.FindName("BottomEdge", input));
            Assert.True(Validation.GetHasError(input));
            Assert.Same(resources["State.Danger"], edge.Brush);
            Assert.Equal(2d, edge.Thickness);
            // Render the template root in its own coordinates, without the window's layout offset.
            AssertBottomErrorPixel(Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetChild(input, 0)), resources);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.Glass, true)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.GlassLight, true)]
    public void CompositeErrorEdgeSurvivesTheKeyboardCueAndReadOnlyState(AppTheme theme, bool readOnly) => StaTestHost.Run(() =>
    {
        var resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        var input = new InputFieldBorder { Resources = resources, Width = 160, Height = 36,
            IsInputFocused = true, IsInvalid = true, IsReadOnly = readOnly };
        KeyboardFocusCue.NotifyKeyboardInput(input);
        input.Measure(new Size(160, 36));
        input.Arrange(new Rect(0, 0, 160, 36));
        input.UpdateLayout();
        Assert.True(KeyboardFocusCue.GetIsVisible(input));
        AssertBottomErrorPixel(input, resources);
    });

    private static void AssertBottomErrorPixel(FrameworkElement element, ResourceDictionary resources)
    {
        Assert.Equal(new Size(160, 36), element.RenderSize);
        var bitmap = new RenderTargetBitmap(160, 36, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(80, 35, 1, 1), pixel, 4, 0);
        Color error = Assert.IsType<SolidColorBrush>(resources["State.Danger"]).Color;
        Assert.Equal(new byte[] { error.B, error.G, error.R, error.A }, pixel);
    }
}
