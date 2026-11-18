using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LocalControlLanguageAppearanceTests
{
    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void CompositeInputEdgeKeepsItsThicknessAtEveryHeightAndForBothInputOrigins(AppTheme theme) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, false);
        InstallDistinctPaint(resources);
        foreach (int height in new[] { 32, 64, 128 })
        {
            var field = new InputFieldBorder { Resources = resources, BorderThickness = new Thickness(1) };
            byte[] rest = Render(field, 128, height);
            Assert.Equal(Colors.Blue, Pixel(rest, 128, 64, height - 1));
            Assert.NotEqual(Colors.Blue, Pixel(rest, 128, 64, height - 2));
            field.IsInputFocused = true;
            KeyboardFocusCue.NotifyPointerInput(field);
            byte[] pointerFocus = Render(field, 128, height);
            Assert.False(KeyboardFocusCue.GetIsVisible(field));
            Assert.Equal(Colors.Lime, Pixel(pointerFocus, 128, 64, height - 1));
            Assert.Equal(Colors.Lime, Pixel(pointerFocus, 128, 64, height - 2));
            Assert.NotEqual(Colors.Lime, Pixel(pointerFocus, 128, 64, height - 3));
            KeyboardFocusCue.NotifyKeyboardInput(field);
            Assert.True(KeyboardFocusCue.GetIsVisible(field));
            byte[] keyboardFocus = Render(field, 128, height);
            Assert.Equal(Colors.Lime, Pixel(keyboardFocus, 128, 64, height - 1));
            Assert.Equal(Colors.Lime, Pixel(keyboardFocus, 128, 64, height - 2));
            Color keyboardInk = Assert.IsType<SolidColorBrush>(resources["Border.Focus"]).Color;
            Assert.Equal(keyboardInk, Pixel(keyboardFocus, 128, 64, 0));
            Assert.Equal(keyboardInk, Pixel(keyboardFocus, 128, 0, height / 2));
            Assert.NotEqual(Pixel(pointerFocus, 128, 64, 0), Pixel(keyboardFocus, 128, 64, 0));
            field.IsInvalid = true;
            byte[] invalid = Render(field, 128, height);
            Assert.Equal(Colors.Magenta, Pixel(invalid, 128, 64, height - 1));
            Assert.Equal(Colors.Magenta, Pixel(invalid, 128, 64, height - 2));
            field.IsInputFocused = false;
            Assert.Equal(Colors.Magenta, Pixel(Render(field, 128, height), 128, 64, height - 1));
            field.IsInvalid = false;
            field.IsReadOnly = true;
            byte[] readOnly = Render(field, 128, height);
            Assert.Equal(0, Pixel(readOnly, 128, 64, height / 2).A);
            Assert.NotEqual(Colors.Blue, Pixel(readOnly, 128, 64, height - 1));
        }
        KeyboardFocusCue.NotifyPointerInput();
    });

    [Theory]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    [InlineData(AppTheme.GlassLight, true)]
    public void ContrastCompositeInputMatchesTheOriginalBorderPixelsAndMeasure(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, systemContrast);
        foreach (int height in new[] { 32, 64, 128 })
        foreach (bool focused in new[] { false, true })
        foreach (bool invalid in new[] { false, true })
        {
            Brush edge = (Brush)resources[invalid ? "State.Danger" : focused ? "Border.Focus" : "Border.Control"];
            var baseline = new Border();
            var actual = new InputFieldBorder { IsInputFocused = focused, IsInputHovered = true, IsInvalid = invalid, IsReadOnly = true };
            foreach (Border border in new Border[] { baseline, actual })
            {
                border.Resources = resources;
                border.Background = (Brush)resources["Surface.Sunken"];
                border.BorderBrush = edge;
                border.BorderThickness = new Thickness(1);
                border.CornerRadius = new CornerRadius(6);
                border.Padding = new Thickness(3);
                border.Child = new Border { Width = 20, Height = 12, Background = Brushes.Orange };
            }
            Assert.False(ControlLanguage.GetEnabled(actual));
            byte[] expectedPixels = Render(baseline, 128, height);
            Assert.Equal(expectedPixels, Render(actual, 128, height));
            Assert.Equal(baseline.DesiredSize, actual.DesiredSize);
            Assert.Equal(baseline.RenderSize, actual.RenderSize);
            Assert.Equal(baseline.Child.RenderSize, actual.Child.RenderSize);
            Assert.Equal(baseline.BorderThickness, actual.BorderThickness);
            Assert.Equal(baseline.Padding, actual.Padding);
            Assert.Equal(baseline.CornerRadius, actual.CornerRadius);
            Assert.Same(baseline.Background, actual.Background);
            Assert.Same(baseline.BorderBrush, actual.BorderBrush);
        }
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void RoleOneLabelsChangeWeightLiveWithoutChangingTheirOtherTextProperties(AppTheme theme, bool contrast) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = Resources(theme, contrast);
        var text = ControlLabelTypography.Apply(new TextBlock { Text = "레이블", FontSize = 17, LineHeight = 24, Resources = resources });
        var run = ControlLabelTypography.Apply(new Run("값") { FontSize = 15 });
        var paragraph = new TextBlock { Resources = resources };
        paragraph.Inlines.Add(run);
        AssertWeight(theme == AppTheme.HighContrast || contrast ? FontWeights.SemiBold : FontWeights.Normal);
        ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: false);
        AssertWeight(FontWeights.Normal);
        ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: true);
        AssertWeight(FontWeights.SemiBold);
        Assert.Equal(17, text.FontSize);
        Assert.Equal(24, text.LineHeight);
        Assert.Equal(15, run.FontSize);
        Assert.Equal("레이블", text.Text);
        Assert.Equal("값", run.Text);
        void AssertWeight(FontWeight expected)
        {
            Assert.Equal(expected, text.FontWeight);
            Assert.Equal(expected, run.FontWeight);
        }
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void NumericCompositeRetainsItsSizeAndErrorBoundary(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        var box = new TextBox { Text = "12", Width = 54 };
        Border frame = NumberStepperField.CreateValueField(box, "px");
        frame.Resources = Resources(theme, systemContrast);
        var input = Assert.IsAssignableFrom<InputFieldBorder>(frame);
        Render(frame, 128, 32);
        Assert.Equal(32, frame.ActualHeight);
        Assert.Equal(32, box.Height);
        Assert.Equal(new Thickness(1), frame.BorderThickness);
        Assert.Equal(new Thickness(0), box.BorderThickness);
        Assert.True(box.Focusable);
        Assert.True(box.IsTabStop);
        Assert.Null(box.FocusVisualStyle);
        Assert.Equal(!systemContrast && theme != AppTheme.HighContrast, ControlLanguage.GetEnabled(input));
        NumberStepperField.SetValueFieldError(frame, true);
        Assert.True(input.IsInvalid);
        Assert.Same(frame.Resources["State.Danger"], frame.BorderBrush);
        NumberStepperField.SetValueFieldError(frame, false);
        Assert.False(input.IsInvalid);
    });

    [Theory]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void HotkeyRecorderUsesTheInputFaceAndPreservesItsHitAreaAndInvalidState(AppTheme theme, bool systemContrast) => StaTestHost.Run(() =>
    {
        var recorder = new HotkeyRecorder { Resources = Resources(theme, systemContrast) };
        var row = Assert.IsType<WrapPanel>(recorder.Children[0]);
        var button = Assert.IsType<Button>(row.Children[0]);
        Render(recorder, 400, 80);
        Assert.Same(recorder.Resources["Button.InputField"], button.Style);
        Assert.Equal(210, button.MinWidth);
        Assert.Equal(38, button.MinHeight);
        Assert.True(button.ActualHeight >= 38);
        Assert.True(button.IsTabStop);
        Assert.True(button.Focusable);
        bool normal = theme != AppTheme.HighContrast && !systemContrast;
        if (normal) Assert.IsType<InputFieldBorder>(button.Template.FindName("Chrome", button));
        else
        {
            Assert.IsType<Border>(button.Template.FindName("Chrome", button));
            Assert.IsType<Border>(button.Template.FindName("FocusRing", button));
        }
        Assert.True(recorder.HandleRecordedKey(Key.A, ModifierKeys.None));
        Render(recorder, 400, 80);
        Assert.True(InputFieldBorder.GetIsInvalid(button));
        Assert.Same(recorder.Resources["State.Danger"], button.BorderBrush);
        if (normal) Assert.True(((InputFieldBorder)button.Template.FindName("Chrome", button)).IsInvalid);
        Assert.True(recorder.HandleRecordedKey(Key.Escape, ModifierKeys.None));
        Render(recorder, 400, 80);
        Assert.False(InputFieldBorder.GetIsInvalid(button));
    });

    [Fact]
    public void SettingsAndLibrarySearchKeepTheirOuterInputChromeAndContrastFallback()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace themes = "clr-namespace:MyCapture.App.Themes";
        XElement settings = Load("SettingsFixture.Window.xaml");
        XElement settingsField = settings.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "SettingsSearchField");
        Assert.Equal(themes + "InputFieldBorder", settingsField.Name);
        Assert.Contains(settingsField.Descendants(presentation + "Trigger"), e => (string?)e.Attribute("Property") == "IsKeyboardFocusWithin");
        XElement gallery = Load("GalleryFixture.Window.xaml");
        XElement galleryField = gallery.Descendants().Single(e => (string?)e.Attribute(xaml + "Name") == "SearchInputChrome");
        Assert.Equal(themes + "InputFieldBorder", galleryField.Name);
        Assert.Equal("{Binding IsKeyboardFocusWithin, ElementName=SearchField}", (string?)galleryField.Attribute("IsInputFocused"));
        Assert.Equal("{Binding IsMouseOver, ElementName=SearchField}", (string?)galleryField.Attribute("IsInputHovered"));
        Assert.Equal("False", (string?)galleryField.Attribute("IsHitTestVisible"));
        XElement contrastSwitch = Assert.Single(galleryField.Descendants(presentation + "Trigger"));
        Assert.Equal("themes:ControlLanguage.Enabled", (string?)contrastSwitch.Attribute("Property"));
        Assert.Equal("False", (string?)contrastSwitch.Attribute("Value"));
        Assert.Contains(contrastSwitch.Elements(presentation + "Setter"), e => (string?)e.Attribute("Property") == "Visibility" && (string?)e.Attribute("Value") == "Hidden");
        Assert.Contains(gallery.Descendants(presentation + "Border"), e => (string?)e.Attribute(xaml + "Name") == "SearchEdge");
    }

    [Fact]
    public void SecretInputUsesTheSameBottomEdgeAndRetainsItsContrastTemplate()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace themes = "clr-namespace:MyCapture.App.Themes";
        XElement style = Load("SettingsFixture.Window.xaml").Descendants(p + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == "Settings.PasswordBox");
        XElement modern = Assert.Single(style.Element(p + "Style.Triggers")!.Elements(p + "Trigger"));
        Assert.Equal("themes:ControlLanguage.Enabled", (string?)modern.Attribute("Property"));
        Assert.Equal("True", (string?)modern.Attribute("Value"));
        XElement chrome = Assert.Single(modern.Descendants(themes + "InputFieldBorder"));
        Assert.Equal("{TemplateBinding BorderThickness}", (string?)chrome.Attribute("BorderThickness"));
        Assert.Equal("{Binding IsKeyboardFocused, RelativeSource={RelativeSource TemplatedParent}}", (string?)chrome.Attribute("IsInputFocused"));
        Assert.Single(chrome.Descendants(p + "ScrollViewer"));
        XElement legacy = style.Elements(p + "Setter").Single(e => (string?)e.Attribute("Property") == "Template");
        Assert.Contains(legacy.Descendants(p + "Border"), e => (string?)e.Attribute(x + "Name") == "FocusRing");
        Assert.DoesNotContain(legacy.Descendants(), e => e.Name == themes + "InputFieldBorder");
    }

    private static XElement Load(string name)
    {
        using Stream stream = typeof(LocalControlLanguageAppearanceTests).Assembly.GetManifestResourceStream(name)!;
        return XDocument.Load(stream).Root!;
    }

    private static ResourceDictionary Resources(AppTheme theme, bool contrast)
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: contrast);
        return resources;
    }

    private static void InstallDistinctPaint(ResourceDictionary resources)
    {
        resources["Fill.Input"] = Brushes.DarkGray;
        resources["Fill.InputHover"] = Brushes.Gray;
        resources["Fill.InputActive"] = Brushes.LightGray;
        resources["Border.Subtle"] = Brushes.Red;
        resources["Stroke.InputEdge"] = Brushes.Blue;
        resources["Text.Primary"] = Brushes.Lime;
        resources["State.Danger"] = Brushes.Magenta;
    }

    private static byte[] Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static Color Pixel(byte[] pixels, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }
}
