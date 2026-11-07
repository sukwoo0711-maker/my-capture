using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTextSelectionViewTests
{
    [Theory]
    [InlineData(AppTheme.Midnight, AppTheme.Daylight, false, false)]
    [InlineData(AppTheme.Workspace, AppTheme.Daylight, false, false)]
    [InlineData(AppTheme.Glass, AppTheme.GlassLight, false, false)]
    [InlineData(AppTheme.Daylight, AppTheme.Daylight, false, false)]
    [InlineData(AppTheme.GlassLight, AppTheme.GlassLight, false, false)]
    [InlineData(AppTheme.HighContrast, AppTheme.HighContrast, false, false)]
    [InlineData(AppTheme.Midnight, AppTheme.Midnight, false, true)]
    [InlineData(AppTheme.Midnight, AppTheme.Midnight, true, false)]
    public void ScrollBarPaletteIsScopedToLightPins(AppTheme theme, AppTheme expected, bool contrast, bool darkCode) => StaTestHost.Run(() =>
    {
        AppTheme previous = ThemeService.Current;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        try
        {
            ThemeService.SystemHighContrastOverrideForTest = contrast;
            ThemeService.Apply(theme);
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(theme, resources, systemHighContrast: contrast);
            string text = string.Join("\r\n", Enumerable.Range(0, 30).Select(row => string.Join('\t', Enumerable.Repeat($"Row {row} extended column", 15))));
            var code = new PinCodeDocument(string.Join("\r\n", Enumerable.Repeat(new string('w', 200), 50)), [], "#1E1E1E", "#DCDCDC");
            PinContent content = darkCode ? PinContent.FromCode(ClipboardCodeRenderer.Render(code), code.DisplayText, code)
                : PinContent.FromText(ClipboardTextRenderer.Render(text, true), text, true);
            var view = new PinTextSelectionView(content);
            var host = new Grid { Resources = resources };
            host.Children.Add(view);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            LayoutTable(host, new Size(400, 240));
            ScrollBar[] bars = Descendants<ScrollBar>(view).Where(bar => bar.Visibility == Visibility.Visible).ToArray();
            Assert.Equal(2, bars.Length);
            bool overridden = expected != theme;
            Assert.Equal(overridden, view.Resources.Contains(typeof(ScrollBar)));
            foreach (string key in new[] { "Surface.Base", "Border.Strong", "Text.Muted", "Text.Primary" })
                Assert.False(view.Resources.Contains(key));
            Color Expected(string key) => contrast ? Assert.IsType<SolidColorBrush>(resources[key]).Color : PaletteColor(expected, key);
            foreach (ScrollBar bar in bars)
            {
                Assert.Equal(Expected("Surface.Base"), Assert.IsType<SolidColorBrush>(bar.Background).Color);
                Thumb thumb = Descendants<Thumb>(bar).Single();
                Border grip = Assert.IsType<Border>(thumb.Template.FindName("Grip", thumb));
                Assert.Equal(Expected("Border.Strong"), Assert.IsType<SolidColorBrush>(grip.Background).Color);
                Assert.Equal(Expected("Text.Muted"), Assert.IsType<SolidColorBrush>(thumb.FindResource("Text.Muted")).Color);
                Assert.Equal(Expected("Text.Primary"), Assert.IsType<SolidColorBrush>(thumb.FindResource("Text.Primary")).Color);
            }
            var outside = new ScrollBar { Height = 100 };
            host.Children.Add(outside);
            outside.ApplyTemplate();
            Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Surface.Base"]).Color, Assert.IsType<SolidColorBrush>(outside.Background).Color);
            host.Children.Remove(outside);
            var menu = new ContextMenu();
            view.ContextMenu = menu;
            // Resource lookup at the view retains the surrounding palette for the menu.
            Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Text.Primary"]).Color,
                Assert.IsType<SolidColorBrush>(view.FindResource("Text.Primary")).Color);
            var image = RenderTableView(host, 400, 240);
            SaveTableEvidence(image, $"scrollbars-{theme}-{contrast}-{darkCode}");
            if (!darkCode)
                Assert.Equal(Assert.IsType<SolidColorBrush>(view.Background).Color, Pixel(image, 392, 232));
        }
        finally
        {
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.Apply(previous);
        }
    });

    /// <summary>
    /// While the view is open the app theme and a Windows contrast theme change: the light scroll
    /// bar palette is added, removed and changed to match each, through the real refresh paths.
    /// </summary>
    [Fact]
    public void ScrollBarPaletteFollowsThemeAndContrastChangesWhileOpen() => StaTestHost.Run(() =>
    {
        AppTheme previous = ThemeService.Current;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        try
        {
            ThemeService.SystemHighContrastOverrideForTest = false;
            ThemeService.Apply(AppTheme.Midnight);
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
            string text = string.Join("\r\n", Enumerable.Range(0, 30).Select(row => string.Join('\t', Enumerable.Repeat($"Row {row} extended column", 15))));
            var view = new PinTextSelectionView(PinContent.FromText(ClipboardTextRenderer.Render(text, true), text, true));
            var host = new Grid { Resources = resources };
            host.Children.Add(view);
            LayoutTable(host, new Size(400, 240));
            AssertPalette(AppTheme.Daylight, "Midnight");

            Theme(AppTheme.Daylight);
            AssertPalette(null, "Daylight");
            Theme(AppTheme.Glass);
            AssertPalette(AppTheme.GlassLight, "Glass");
            Theme(AppTheme.Midnight);
            AssertPalette(AppTheme.Daylight, "Midnight again");

            Contrast(true);
            AssertPalette(null, "Windows contrast theme on");
            Contrast(false);
            AssertPalette(AppTheme.Daylight, "Windows contrast theme off");

            void Theme(AppTheme theme)
            {
                ThemeService.Apply(theme);
                ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
                host.UpdateLayout();
            }

            void Contrast(bool on)
            {
                ThemeService.SystemHighContrastOverrideForTest = on;
                DispatcherOperation refresh = ThemeService.ScheduleSystemThemeRefresh(view.Dispatcher, resources, () => on);
                view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.Equal(DispatcherOperationStatus.Completed, refresh.Status);
                host.UpdateLayout();
            }

            void AssertPalette(AppTheme? palette, string step)
            {
                Assert.True((palette is not null) == view.Resources.Contains(typeof(ScrollBar)), $"{step}: replacement present {view.Resources.Contains(typeof(ScrollBar))}");
                ScrollBar[] bars = Descendants<ScrollBar>(view).Where(bar => bar.Visibility == Visibility.Visible).ToArray();
                Assert.Equal(2, bars.Length);
                foreach (ScrollBar bar in bars)
                {
                    Color expected = palette is { } light ? PaletteColor(light, "Surface.Base")
                        : Assert.IsType<SolidColorBrush>(resources["Surface.Base"]).Color;
                    Assert.True(expected == Assert.IsType<SolidColorBrush>(bar.Background).Color, $"{step}: bar {bar.Orientation} {bar.Background}, expected {expected}");
                }
            }
        }
        finally
        {
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.Apply(previous);
        }
    });

    private static Color PaletteColor(AppTheme theme, string key)
    {
        ThemeColor color = ThemeCatalog.ColorsFor(theme)[key];
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }
}
