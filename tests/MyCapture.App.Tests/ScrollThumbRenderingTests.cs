using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ScrollThumbRenderingTests
{
    public static IEnumerable<object[]> Palettes()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            yield return [theme, false, 0, false, 1d, false];
        yield return [AppTheme.Workspace, true, 0, false, 1d, false];
        yield return [AppTheme.System, true, 0, false, 1d, false];
        foreach (int contrast in new[] { 1, 2 })
            yield return [AppTheme.Workspace, true, contrast, false, 1d, false];
        foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
        {
            yield return [theme, false, 0, false, 1d, true];
            yield return [theme, false, 0, false, 1.5d, true];
            yield return [theme, false, 0, true, 1d, false];
            yield return [theme, false, 0, true, 1.5d, true];
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void OverflowingViewerPaintsVisibleRestHoverAndPressedGrips(
        AppTheme theme, bool lightWorkspace, int contrast, bool horizontal, double scale, bool animations) => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        Func<SystemColorRole, Color>? previousColors = ThemeService.SystemColorsOverrideForTest;
        Func<bool>? previousLight = ThemeService.SystemLightThemeReaderForTest;
        IReadOnlyDictionary<SystemColorRole, Color>? system = contrast switch
        {
            1 => SystemHighContrastThemeTests.NightSky,
            2 => SystemHighContrastThemeTests.Desert,
            _ => null,
        };
        FluidMotion.AnimationsEnabledOverrideForTest = animations;
        ThemeService.SystemHighContrastOverrideForTest = system is not null;
        ThemeService.SystemColorsOverrideForTest = system is null ? null : role => system[role];
        ThemeService.SystemLightThemeReaderForTest = () => lightWorkspace;
        Window? window = null;
        try
        {
            ResourceDictionary resources = theme == AppTheme.Workspace
                ? WorkspaceTheme.Create(lightWorkspace ? WorkspaceRole.Gallery : WorkspaceRole.VideoEditor)
                : SystemHighContrastThemeTests.LoadTheme();
            if (theme == AppTheme.Workspace)
                WorkspaceTheme.Apply(resources, lightWorkspace ? WorkspaceRole.Gallery : WorkspaceRole.VideoEditor,
                    theme, systemHighContrast: system is not null);
            else
                ThemeService.ApplyResources(theme, resources, systemHighContrast: system is not null);

            var content = new Border { Width = horizontal ? 1200 : 240, Height = horizontal ? 120 : 1200 };
            var viewer = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            };
            var host = new Border { Width = 320, Height = 200, Child = viewer,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                UseLayoutRounding = true, SnapsToDevicePixels = true };
            host.SetResourceReference(Border.BackgroundProperty, "Surface.Canvas");
            window = new Window { Content = host, Resources = resources, Width = 360, Height = 260, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            ModernWindowChrome.SetEnabled(window, false);
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            Settle(host, animations);
            Assert.True(horizontal ? viewer.ScrollableWidth > 0 : viewer.ScrollableHeight > 0);
            ScrollBar bar = Descendants<ScrollBar>(viewer).Single(item =>
                item.Orientation == (horizontal ? Orientation.Horizontal : Orientation.Vertical));
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.Same(resources[typeof(ScrollBar)], bar.Style);
            Track track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
            Thumb thumb = track.Thumb;
            string caseName = $"{theme}-{lightWorkspace}-{contrast}-{horizontal}-{scale}-{animations}";
            string? evidence = EvidenceDirectory(caseName);
            RenderTargetBitmap rest = Render(host, scale);
            Save(evidence, "rest.png", rest);
            Assert.Equal(new Vector(), VisualTreeHelper.GetOffset(host));
            Assert.True(thumb.ActualWidth > 0 && thumb.ActualHeight > 0,
                $"Thumb desired {thumb.DesiredSize}, actual {thumb.RenderSize}; track {track.RenderSize}.");
            PixelBand restBand = Band(rest, host, thumb, horizontal, scale);
            string chromeDetails = thumb.Template.FindName("Grip", thumb) is ScrollThumbChrome chrome
                ? $" Chrome desired {chrome.DesiredSize}, actual {chrome.RenderSize}, color {chrome.GripColor}, rest {chrome.RestBrush}, hover {chrome.HoverBrush}, pressed {chrome.PressedBrush}, width {chrome.GripWidth}, visible {chrome.IsVisible}, opacity {chrome.Opacity}."
                : string.Empty;
            AssertCommittedState(thumb, resources, "Fill.ScrollThumb", 3);
            AssertVisible(restBand, "rest" + chromeDetails);
            AssertSystemRole(restBand, system, SystemColorRole.Highlight);

            SetReadOnlyState(bar, typeof(UIElement), "IsMouseOverPropertyKey", true);
            SetReadOnlyState(thumb, typeof(UIElement), "IsMouseOverPropertyKey", true);
            Settle(host, animations);
            RenderTargetBitmap hover = Render(host, scale);
            Save(evidence, "hover.png", hover);
            PixelBand hoverBand = Band(hover, host, thumb, horizontal, scale);
            AssertCommittedState(thumb, resources, "Fill.ScrollThumbHover", 6);
            AssertVisible(hoverBand, "hover");
            Assert.True(hoverBand.VisiblePixels > restBand.VisiblePixels,
                $"Hover grip must grow: {restBand.VisiblePixels} -> {hoverBand.VisiblePixels} pixels.");
            AssertSystemRole(hoverBand, system, SystemColorRole.WindowText);

            SetReadOnlyState(bar, typeof(UIElement), "IsMouseOverPropertyKey", false);
            SetReadOnlyState(thumb, typeof(UIElement), "IsMouseOverPropertyKey", false);
            SetReadOnlyState(thumb, typeof(Thumb), "IsDraggingPropertyKey", true);
            Settle(host, animations);
            RenderTargetBitmap pressed = Render(host, scale);
            Save(evidence, "pressed.png", pressed);
            PixelBand pressedBand = Band(pressed, host, thumb, horizontal, scale);
            AssertCommittedState(thumb, resources, "Fill.ScrollThumbPressed", 6);
            AssertVisible(pressedBand, "pressed");
            Assert.True(pressedBand.VisiblePixels > restBand.VisiblePixels);
            AssertSystemRole(pressedBand, system, SystemColorRole.WindowText);

            SetReadOnlyState(thumb, typeof(Thumb), "IsDraggingPropertyKey", false);
            Settle(host, animations);
            PixelBand released = Band(Render(host, scale), host, thumb, horizontal, scale);
            AssertCommittedState(thumb, resources, "Fill.ScrollThumb", 3);
            Assert.Equal(restBand.VisiblePixels, released.VisiblePixels);
            Assert.Equal(restBand.Grip, released.Grip);
        }
        finally
        {
            window?.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
            ThemeService.SystemColorsOverrideForTest = previousColors;
            ThemeService.SystemLightThemeReaderForTest = previousLight;
        }
    });

    private readonly record struct PixelBand(Color Lane, Color Grip, int VisiblePixels, double Contrast);

    private static void AssertCommittedState(Thumb thumb, ResourceDictionary resources, string brushKey, double width)
    {
        var chrome = Assert.IsType<ScrollThumbChrome>(thumb.Template.FindName("Grip", thumb));
        Color color = Assert.IsType<SolidColorBrush>(resources[brushKey]).Color;
        Assert.Equal(width, chrome.GetAnimationBaseValue(ScrollThumbChrome.GripWidthProperty));
        Assert.Equal(color, chrome.GetAnimationBaseValue(ScrollThumbChrome.GripColorProperty));
        Assert.Equal(width, chrome.GripWidth);
        Assert.Equal(color, chrome.GripColor);
    }

    private static PixelBand Band(BitmapSource image, FrameworkElement host, FrameworkElement thumb, bool horizontal, double scale)
    {
        Point origin = thumb.TranslatePoint(new Point(), host);
        int width = image.PixelWidth;
        byte[] pixels = new byte[width * image.PixelHeight * 4];
        image.CopyPixels(pixels, width * 4, 0);
        Color Pixel(double x, double y)
        {
            int offset = ((int)Math.Floor(y * scale) * width + (int)Math.Floor(x * scale)) * 4;
            return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        }
        double center = horizontal ? origin.X + thumb.ActualWidth / 2 : origin.Y + thumb.ActualHeight / 2;
        double cross = horizontal ? origin.Y : origin.X;
        Color lane = horizontal ? Pixel(center, cross + 1) : Pixel(cross + 1, center);
        var colors = new List<Color>();
        int count = (int)Math.Floor((horizontal ? thumb.ActualHeight : thumb.ActualWidth) * scale);
        for (int index = 0; index < count; index++)
        {
            double coordinate = cross + index / scale;
            colors.Add(horizontal ? Pixel(center, coordinate) : Pixel(coordinate, center));
        }
        double Contrast(Color color) => ThemeContrast.Ratio(ToThemeColor(color), ToThemeColor(lane));
        Color grip = colors.MaxBy(Contrast);
        return new(lane, grip, colors.Count(color => Contrast(color) >= 1.5), Contrast(grip));
    }

    private static void AssertVisible(PixelBand band, string state) =>
        Assert.True(band.VisiblePixels >= 2 && band.Contrast >= 1.5,
            $"{state}: {band.VisiblePixels} visible pixels, contrast {band.Contrast:F4}:1, lane {band.Lane}, grip {band.Grip}.");

    private static void AssertSystemRole(PixelBand band, IReadOnlyDictionary<SystemColorRole, Color>? system, SystemColorRole role)
    {
        if (system is not null) Assert.Equal(system[role], band.Grip);
    }

    private static ThemeColor ToThemeColor(Color color) => ThemeColor.Argb(color.A, color.R, color.G, color.B);

    private static void SetReadOnlyState(DependencyObject owner, Type type, string name, bool value)
    {
        var key = Assert.IsType<DependencyPropertyKey>(type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        owner.SetValue(key, value);
    }

    private static void Settle(FrameworkElement host, bool animations)
    {
        host.UpdateLayout();
        if (animations)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        host.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        host.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement host, double scale)
    {
        var image = new RenderTargetBitmap((int)(host.ActualWidth * scale), (int)(host.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(host);
        return image;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string? EvidenceDirectory(string caseName)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_THEME_CONTROL_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return null;
        string parent = DiagnosticOutputPaths.Create(requested);
        return TestDiagnosticArtifacts.NewDirectory(DiagnosticOutputPaths.Child(parent, $"scroll-{caseName}-{Guid.NewGuid():N}"));
    }

    private static void Save(string? directory, string name, BitmapSource image)
    {
        if (directory is not null) TestDiagnosticArtifacts.SavePng(directory, name, image);
    }
}
