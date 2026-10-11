using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders the shared controls in every palette, in simulated Windows contrast themes, and in
/// their hover, pressed, focus and checked states, which the UX review self-test cannot show.
/// Set MYCAPTURE_THEME_CONTROL_EVIDENCE to a folder to keep the PNGs.
/// </summary>
public sealed class ThemeControlEvidenceTests
{
    // Desert: a light contrast theme, to pair with the dark Night sky palette.
    private static readonly IReadOnlyDictionary<SystemColorRole, Color> Desert = new Dictionary<SystemColorRole, Color>
    {
        [SystemColorRole.Window] = Color.FromRgb(0xFF, 0xFA, 0xEF),
        [SystemColorRole.WindowText] = Color.FromRgb(0x3D, 0x3D, 0x3D),
        [SystemColorRole.ButtonFace] = Color.FromRgb(0xFF, 0xFA, 0xEF),
        [SystemColorRole.ButtonText] = Color.FromRgb(0x20, 0x20, 0x20),
        [SystemColorRole.Highlight] = Color.FromRgb(0x90, 0x39, 0x09),
        [SystemColorRole.HighlightText] = Color.FromRgb(0xFF, 0xF5, 0xE3),
        [SystemColorRole.GrayText] = Color.FromRgb(0x67, 0x67, 0x67),
        [SystemColorRole.HotTrack] = Color.FromRgb(0x1C, 0x5E, 0x75),
    };

    [Fact]
    public void SharedControlsRenderInEveryPaletteAndContrastTheme() => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? previousColors = ThemeService.SystemColorsOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var sheets = new List<BitmapSource>();
        try
        {
            var cases = new List<(string Name, AppTheme Theme, IReadOnlyDictionary<SystemColorRole, Color>? System)>();
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight, AppTheme.Midnight, AppTheme.Daylight, AppTheme.Workspace, AppTheme.HighContrast })
                cases.Add((AppThemeNames.ToSetting(theme), theme, null));
            cases.Add(("system-contrast-night-sky", AppTheme.Glass, SystemHighContrastThemeTests.NightSky));
            cases.Add(("system-contrast-desert", AppTheme.Daylight, Desert));

            foreach ((string name, AppTheme theme, IReadOnlyDictionary<SystemColorRole, Color>? system) in cases)
            {
                ThemeService.SystemHighContrastOverrideForTest = system is not null;
                ThemeService.SystemColorsOverrideForTest = system is null ? null : role => system[role];
                ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
                ThemeService.ApplyResources(theme, resources, systemHighContrast: system is not null);
                (BitmapSource image, Border secondaryChrome, Border card) = Render(resources, name);
                sheets.Add(image);
                Save(image, $"controls-{name}.png");

                Color outline = ((SolidColorBrush)secondaryChrome.BorderBrush).Color;
                Color decorative = ((SolidColorBrush)card.BorderBrush).Color;
                if (system is not null)
                {
                    Assert.Equal(system[SystemColorRole.ButtonText], outline);
                    Assert.Equal(system[SystemColorRole.WindowText], decorative);
                }
                else if (theme != AppTheme.HighContrast)
                {
                    Assert.NotEqual(outline, decorative);
                }
            }

            Save(Stack(sheets), "controls-all.png");
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.SystemColorsOverrideForTest = previousColors;
        }
    });

    private static (BitmapSource Image, Border SecondaryChrome, Border Card) Render(ResourceDictionary resources, string name)
    {
        T Styled<T>(T element, object key) where T : FrameworkElement
        {
            element.Style = (Style)resources[key];
            element.Margin = new Thickness(0, 0, 8, 8);
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }

        WrapPanel Row(params UIElement[] children)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement child in children) row.Children.Add(child);
            return row;
        }

        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        Button primary = Styled(new Button { Content = "저장" }, "Button.Primary");
        Button secondary = Styled(new Button { Content = "취소" }, typeof(Button));
        Button ghost = Styled(new Button { Content = "도구" }, "Button.Ghost");
        Button hover = Styled(new Button { Content = "호버" }, typeof(Button));
        Button pressed = Styled(new Button { Content = "누름" }, typeof(Button));
        Button focused = Styled(new Button { Content = "포커스" }, typeof(Button));
        Button ghostHover = Styled(new Button { Content = "도구 호버" }, "Button.Ghost");
        Button disabled = Styled(new Button { Content = "사용 안 함", IsEnabled = false }, typeof(Button));

        CheckBox check = Styled(new CheckBox { Content = "선택 안 함" }, typeof(CheckBox));
        CheckBox checkOn = Styled(new CheckBox { Content = "선택", IsChecked = true }, typeof(CheckBox));
        CheckBox checkHover = Styled(new CheckBox { Content = "호버" }, typeof(CheckBox));
        RadioButton radio = Styled(new RadioButton { Content = "하나", GroupName = name + "a" }, typeof(RadioButton));
        RadioButton radioOn = Styled(new RadioButton { Content = "둘", IsChecked = true, GroupName = name + "b" }, typeof(RadioButton));

        ToggleButton toggle = Styled(new ToggleButton { Content = "펜" }, typeof(ToggleButton));
        ToggleButton toggleOn = Styled(new ToggleButton { Content = "사각형", IsChecked = true }, typeof(ToggleButton));
        ToggleButton toggleHover = Styled(new ToggleButton { Content = "호버" }, typeof(ToggleButton));

        TextBox text = Styled(new TextBox { Text = "파일 이름", Width = 150 }, typeof(TextBox));
        TextBox textHover = Styled(new TextBox { Text = "호버", Width = 110 }, typeof(TextBox));
        TextBox textFocus = Styled(new TextBox { Text = "포커스", Width = 110 }, typeof(TextBox));
        ComboBox combo = Styled(new ComboBox { Width = 150, ItemsSource = new[] { "실선", "점선" }, SelectedIndex = 0 }, typeof(ComboBox));

        Slider slider = Styled(new Slider { Minimum = 1, Maximum = 24, Value = 8, Width = 180 }, typeof(Slider));
        Slider sliderHover = Styled(new Slider { Minimum = 0, Maximum = 100, Value = 70, Width = 180 }, typeof(Slider));
        Slider sliderOff = Styled(new Slider { Minimum = 0, Maximum = 100, Value = 40, Width = 120, IsEnabled = false }, typeof(Slider));

        var card = new Border { Style = (Style)resources["Card"], Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 0) };
        var cardText = new TextBlock { Text = "패널 경계와 구분선은 장식선입니다.", TextWrapping = TextWrapping.Wrap };
        cardText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var separator = new Separator { Style = (Style)resources[typeof(Separator)] };
        var cardBody = new StackPanel();
        cardBody.Children.Add(cardText);
        cardBody.Children.Add(separator);
        cardBody.Children.Add(Row(Styled(new TextBox { Text = "입력", Width = 140 }, typeof(TextBox)),
            Styled(new Button { Content = "보조" }, typeof(Button))));
        card.Child = cardBody;

        var panel = new StackPanel { Margin = new Thickness(16), Width = 600 };
        panel.Children.Add(title);
        panel.Children.Add(Row(primary, secondary, ghost, hover, pressed, focused, ghostHover, disabled));
        panel.Children.Add(Row(check, checkOn, checkHover, radio, radioOn));
        panel.Children.Add(Row(toggle, toggleOn, toggleHover));
        panel.Children.Add(Row(text, textHover, textFocus, combo));
        panel.Children.Add(Row(slider, sliderHover, sliderOff));
        panel.Children.Add(card);

        var raised = new Border { Child = panel, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(12) };
        raised.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        raised.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var page = new Border { Child = raised };
        page.SetResourceReference(Border.BackgroundProperty, "Surface.Base");
        // WPF clears the client to black under translucent glass surfaces.
        var root = new Border { Child = page, Background = Brushes.Black, Resources = resources };
        root.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");

        var window = new Window
        {
            Content = root, SizeToContent = SizeToContent.WidthAndHeight, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            ControlStateMotionTests.Drain(window);

            // States a pointer or keyboard would produce, driven through the same template parts.
            FluidMotion.SetFadeTo(Part(hover, "HoverLayer"), 1);
            FluidMotion.SetFadeTo(Part(ghostHover, "HoverLayer"), 1);
            FluidMotion.SetFadeTo(Part(pressed, "HoverLayer"), 1);
            FluidMotion.SetFadeTo(Part(pressed, "PressedLayer"), 1);
            Part(focused, "FocusRing").Opacity = 1;
            ((Border)Part(focused, "Chrome")).SetResourceReference(Border.BorderBrushProperty, "Border.Focus");
            FluidMotion.SetFadeTo(Part(checkHover, "HoverLayer"), 1);
            FluidMotion.SetFadeTo(Part(toggleHover, "HoverLayer"), 1);
            FluidMotion.SetFadeTo(Part(textHover, "HoverLayer"), 1);
            Part(textFocus, "FocusRing").Opacity = 1;
            ((Border)Part(textFocus, "Chrome")).SetResourceReference(Border.BorderBrushProperty, "Border.Focus");
            Track track = (Track)Part(sliderHover, "PART_Track");
            FluidMotion.SetScaleTo((UIElement)track.Thumb.Template.FindName("Centre", track.Thumb), 1.2);
            Part(sliderHover, "FocusRing").Opacity = 1;
            ControlStateMotionTests.Drain(window);

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5), (int)Math.Ceiling(root.ActualHeight * 1.5),
                144, 144, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            return (bitmap, (Border)Part(secondary, "Chrome"), card);
        }
        finally
        {
            window.Close();
        }
    }

    private static UIElement Part(Control control, string name) => (UIElement)control.Template.FindName(name, control);

    private static BitmapSource Stack(IReadOnlyList<BitmapSource> images)
    {
        int columns = 2;
        int width = images.Max(image => image.PixelWidth);
        int height = images.Max(image => image.PixelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            for (int i = 0; i < images.Count; i++)
                dc.DrawImage(images[i], new Rect(i % columns * width, i / columns * height, images[i].PixelWidth, images[i].PixelHeight));
        }

        int rows = (images.Count + columns - 1) / columns;
        var sheet = new RenderTargetBitmap(width * columns, height * rows, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        return sheet;
    }

    private static void Save(BitmapSource image, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_THEME_CONTROL_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
