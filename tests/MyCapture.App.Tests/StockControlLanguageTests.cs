using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StockControlLanguageTests
{
    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void ProgressKeepsNativeValueSizingAndContrastPixels(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, systemContrast);
        var baseline = new ProgressBar { Style = new Style(typeof(ProgressBar)), Width = 160, Height = 4, Value = 25 };
        var current = new ProgressBar { Width = 160, Height = 4, Value = 25 };
        var baselineHost = Host(resources, baseline); Layout(baselineHost);
        var host = Host(resources, current); Layout(host);
        Assert.Equal(baseline.DesiredSize, current.DesiredSize);
        Assert.Equal(baseline.RenderSize, current.RenderSize);
        if (systemContrast || theme == AppTheme.HighContrast)
        {
            Assert.Same(baseline.Template, current.Template);
            Assert.Equal(Pixels(baseline), Pixels(current));
            return;
        }
        var track = Part<Border>(current, "PART_Track");
        var indicator = Part<ProgressIndicator>(current, "PART_Indicator");
        Assert.Equal(ColorOf(resources, "Border.Subtle"), ColorOf(track.Background));
        Assert.Equal(ColorOf(resources, "Text.Primary"), ColorOf(indicator.Background));
        Assert.Equal(40d, indicator.ActualWidth);
        current.Value = 60; Layout(host);
        Assert.Equal(96d, indicator.ActualWidth);
        current.IsIndeterminate = true; Layout(host);
        Assert.Equal(160d, indicator.ActualWidth);
        Assert.Equal(60d, current.Value);
        Assert.Equal(new CornerRadius(99), indicator.CornerRadius);
        Assert.False(indicator.IsPulsing); // An unloaded fixture cannot start a continuous clock.
        current.IsIndeterminate = false;
        ControlLanguage.SetPreserveProgressPalette(current, true);
        current.SetResourceReference(Control.BackgroundProperty, "Surface.Hover");
        current.SetResourceReference(Control.ForegroundProperty, "Border.Strong");
        Layout(host);
        Assert.Equal(ColorOf(resources, "Surface.Hover"), ColorOf(track.Background));
        Assert.Equal(ColorOf(resources, "Border.Strong"), ColorOf(indicator.Background));
        Assert.Equal(60d, current.Value);
        Assert.Equal(100d, current.Maximum);
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void TooltipKeepsMeasuredSizeAndContrastTemplate(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, systemContrast);
        var oldStyle = new Style(typeof(ToolTip));
        // The old style has these exact setters and no template trigger.
        foreach (Setter setter in ((Style)resources[typeof(ToolTip)]).Setters)
            if (setter.Property != ControlLanguage.EnabledProperty)
                oldStyle.Setters.Add(new Setter(setter.Property, setter.Value));
        var baseline = new ToolTip { Content = "Sample", Resources = resources, Style = oldStyle };
        var current = new ToolTip { Content = "Sample", Resources = resources, Style = (Style)resources[typeof(ToolTip)] };
        // A ToolTip is a popup root and WPF forbids adding it to a visual/logical parent.
        LayoutPopup(baseline); LayoutPopup(current);
        Assert.Equal(baseline.DesiredSize, current.DesiredSize);
        Assert.Equal(baseline.RenderSize, current.RenderSize);
        if (systemContrast || theme == AppTheme.HighContrast)
        {
            Assert.Same(baseline.Template, current.Template);
            Assert.Equal(Pixels(baseline), Pixels(current));
            return;
        }
        var chrome = Part<Border>(current, "ToolTipChrome");
        Assert.Equal(new CornerRadius(8), chrome.CornerRadius);
        Assert.Equal(resources["Radius.Floating"], chrome.CornerRadius);
        Assert.Equal(new Thickness(1), chrome.BorderThickness);
        Assert.Equal(ColorOf(resources, "Border.Subtle"), ColorOf(chrome.BorderBrush));
        Assert.Equal(ColorOf(resources, "Surface.Floating"), ColorOf(chrome.Background));
        var shadow = Assert.IsType<DropShadowEffect>(chrome.Effect);
        var token = Assert.IsType<DropShadowEffect>(resources["Shadow.Card"]);
        Assert.Equal(token.BlurRadius, shadow.BlurRadius);
        Assert.Equal(token.ShadowDepth, shadow.ShadowDepth);
        Assert.Equal(token.Opacity, shadow.Opacity);
    });

    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void VerticalProgressKeepsItsWidthAndFillsUpward(AppTheme theme) => StaTestHost.Run(() =>
    {
        var progress = new ProgressBar { Width = 4, Height = 160, Value = 25, Orientation = Orientation.Vertical };
        var host = Host(Resources(theme, false), progress); Layout(host);
        var indicator = Part<ProgressIndicator>(progress, "PART_Indicator");
        Assert.Equal(new Size(4, 160), progress.RenderSize);
        Rect bounds = indicator.TransformToAncestor(progress).TransformBounds(new Rect(indicator.RenderSize));
        Assert.Equal(new Rect(0, 120, 4, 40), bounds);
        progress.Value = 60; Layout(host);
        bounds = indicator.TransformToAncestor(progress).TransformBounds(new Rect(indicator.RenderSize));
        Assert.Equal(new Rect(0, 64, 4, 96), bounds);
        Assert.Equal(60d, progress.Value);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalExpanderKeepsStockHeaderAndContentMeasurements(bool expanded) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(AppTheme.Glass, false);
        Expander Create(bool current) => new Expander
        {
            Header = new TextBlock { Text = "Details", FontSize = 14 },
            Content = new Border { Width = 200, Height = 80 },
            IsExpanded = expanded,
            Style = current ? (Style)resources["Expander.OptionalStyle"] : new Style(typeof(Expander)),
        };
        var baseline = Create(false); var current = Create(true);
        var oldHost = Host(resources, baseline); Layout(oldHost);
        var newHost = Host(resources, current); Layout(newHost);
        Assert.Equal(baseline.DesiredSize, current.DesiredSize);
        Assert.Equal(baseline.RenderSize, current.RenderSize);
    });

    [Fact]
    public void IndeterminatePaintStopsForReducedMotionAndUnloadWithoutChangingValue() => StaTestHost.Run(() =>
    {
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        var progress = new ProgressBar { Width = 160, Height = 4, Value = 37, IsIndeterminate = true };
        var window = new Window { Resources = Resources(AppTheme.Glass, false), Content = progress,
            Width = 200, Height = 100, Left = 0, Top = 0, ShowInTaskbar = false };
        ModernWindowChrome.SetEnabled(window, false);
        FluidMotion.SetWindowEntrance(window, false);
        ProgressIndicator? indicator = null;
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            window.Show(); window.UpdateLayout();
            indicator = Part<ProgressIndicator>(progress, "PART_Indicator");
            Assert.True(indicator.IsPulsing);
            Assert.Equal(37d, progress.Value);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            indicator.RefreshMotion();
            Assert.False(indicator.IsPulsing);
            Assert.Equal(1d, indicator.Opacity);
            Assert.Equal(37d, progress.Value);
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            indicator.RefreshMotion();
            Assert.True(indicator.IsPulsing);
            progress.IsIndeterminate = false;
            Assert.False(indicator.IsPulsing);
            Assert.Equal(37d, progress.Value);
            progress.IsIndeterminate = true;
            Assert.True(indicator.IsPulsing);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
        }
        Assert.NotNull(indicator);
        Assert.False(indicator.IsPulsing);
    });

    private static ResourceDictionary Resources(AppTheme theme, bool contrast)
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: contrast);
        return resources;
    }

    private static Border Host(ResourceDictionary resources, params Control[] controls)
    {
        var stack = new StackPanel();
        foreach (Control control in controls) stack.Children.Add(control);
        return new Border { Width = 200, Height = 100, Resources = resources, Child = stack };
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
    }

    private static void LayoutPopup(ToolTip tooltip)
    {
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tooltip.Arrange(new Rect(new Point(), tooltip.DesiredSize)); tooltip.UpdateLayout();
    }

    private static byte[] Pixels(FrameworkElement element)
    {
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Capture the control in local coordinates rather than its position in the fixture.
        var drawing = new DrawingVisual();
        using (DrawingContext dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        image.Render(drawing);
        var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static T Part<T>(Control control, string name) where T : DependencyObject =>
        Assert.IsType<T>(control.Template.FindName(name, control));
    private static Color ColorOf(ResourceDictionary resources, string key) => ColorOf((Brush)resources[key]);
    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}
