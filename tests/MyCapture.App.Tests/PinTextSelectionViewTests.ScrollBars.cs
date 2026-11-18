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
                Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Scroll.Lane"]).Color,
                    Assert.IsType<SolidColorBrush>(bar.Background).Color);
                Thumb thumb = Descendants<Thumb>(bar).Single();
                ScrollThumbChrome grip = Assert.IsType<ScrollThumbChrome>(thumb.Template.FindName("Grip", thumb));
                Assert.Equal(Expected("Fill.ScrollThumb"), Assert.IsType<SolidColorBrush>(grip.RestBrush).Color);
                Assert.Equal(Expected("Fill.ScrollThumbHover"), Assert.IsType<SolidColorBrush>(grip.HoverBrush).Color);
                Assert.Equal(Expected("Fill.ScrollThumbPressed"), Assert.IsType<SolidColorBrush>(grip.PressedBrush).Color);
                Assert.Equal(bar.Orientation, grip.Orientation);
                Assert.Equal(3, grip.GripWidth);
                Assert.Equal(Expected("Text.Muted"), Assert.IsType<SolidColorBrush>(thumb.FindResource("Text.Muted")).Color);
                Assert.Equal(Expected("Text.Primary"), Assert.IsType<SolidColorBrush>(thumb.FindResource("Text.Primary")).Color);
            }
            var outside = new ScrollBar { Height = 100 };
            host.Children.Add(outside);
            outside.ApplyTemplate();
            Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Scroll.Lane"]).Color, Assert.IsType<SolidColorBrush>(outside.Background).Color);
            Thumb outsideThumb = Descendants<Thumb>(outside).Single();
            outsideThumb.ApplyTemplate();
            var outsideGrip = Assert.IsType<ScrollThumbChrome>(outsideThumb.Template.FindName("Grip", outsideThumb));
            Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Fill.ScrollThumb"]).Color,
                Assert.IsType<SolidColorBrush>(outsideGrip.RestBrush).Color);
            host.Children.Remove(outside);
            var menu = new ContextMenu();
            view.ContextMenu = menu;
            // Resource lookup at the view retains the surrounding palette for the menu.
            Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Text.Primary"]).Color,
                Assert.IsType<SolidColorBrush>(view.FindResource("Text.Primary")).Color);
            var image = RenderTableView(host, 400, 240);
            SaveTableEvidence(image, $"scrollbars-{theme}-{contrast}-{darkCode}");
            foreach (ScrollBar bar in bars) AssertPinScrollGrip(host, view, bar, image);
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
                    Color expected = Assert.IsType<SolidColorBrush>(resources["Scroll.Lane"]).Color;
                    Assert.True(expected == Assert.IsType<SolidColorBrush>(bar.Background).Color, $"{step}: bar {bar.Orientation} {bar.Background}, expected {expected}");
                    Thumb thumb = Descendants<Thumb>(bar).Single();
                    var grip = Assert.IsType<ScrollThumbChrome>(thumb.Template.FindName("Grip", thumb));
                    Color expectedGrip = palette is { } light ? PaletteColor(light, "Fill.ScrollThumb")
                        : Assert.IsType<SolidColorBrush>(resources["Fill.ScrollThumb"]).Color;
                    Assert.Equal(expectedGrip, Assert.IsType<SolidColorBrush>(grip.RestBrush).Color);
                    Assert.Equal(expectedGrip, grip.GripColor);
                    AssertPinScrollGrip(host, view, bar, RenderTableView(host, 400, 240));
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

    private static void AssertPinScrollGrip(FrameworkElement host, PinTextSelectionView view, ScrollBar bar, System.Windows.Media.Imaging.BitmapSource image)
    {
        Thumb thumb = Descendants<Thumb>(bar).Single();
        var grip = Assert.IsType<ScrollThumbChrome>(thumb.Template.FindName("Grip", thumb));
        bool vertical = bar.Orientation == Orientation.Vertical;
        Point painted = grip.TranslatePoint(vertical ? new Point(grip.ActualWidth - 5.5, grip.ActualHeight / 2)
            : new Point(grip.ActualWidth / 2, grip.ActualHeight - 5.5), host);
        Point clear = grip.TranslatePoint(vertical ? new Point(grip.ActualWidth - 1, grip.ActualHeight / 2)
            : new Point(grip.ActualWidth / 2, grip.ActualHeight - 1), host);
        Color lane = Pixel(image, (int)clear.X, (int)clear.Y);
        Color paintedGrip = Pixel(image, (int)painted.X, (int)painted.Y);
        static ThemeColor Theme(Color color) => ThemeColor.Argb(color.A, color.R, color.G, color.B);
        ThemeColor expectedLane = ThemeContrast.Composite(Theme(Assert.IsType<SolidColorBrush>(bar.Background).Color),
            Theme(Assert.IsType<SolidColorBrush>(view.Background).Color));
        Assert.Equal(expectedLane, Theme(lane));
        Assert.True(ThemeContrast.Ratio(Theme(paintedGrip), Theme(lane)) >= 1.5,
            $"The {bar.Orientation} grip must remain visible on the pin's own background: {paintedGrip} on {lane}.");
    }
}
