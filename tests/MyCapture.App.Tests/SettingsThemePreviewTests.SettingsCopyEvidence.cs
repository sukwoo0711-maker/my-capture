using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task SettingsCopy_InitialShowFitsCurrentMonitor() =>
        RunIsolated(nameof(SettingsCopy_InitialShowFitsCurrentMonitor), () =>
        {
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
            var window = new SettingsWindow(() => new AppSettings(),
                _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
            {
                MinWidth = monitor.WorkArea.Width * 2, MinHeight = monitor.WorkArea.Height * 2,
                Width = monitor.WorkArea.Width * 2, Height = monitor.WorkArea.Height * 2,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                window.Show(); Drain(window);
                double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
                Assert.True(window.ActualWidth * scale <= monitor.WorkArea.Width + 1);
                Assert.True(window.ActualHeight * scale <= monitor.WorkArea.Height + 1);
                Assert.True(window.MinWidth <= monitor.WorkArea.Width / monitor.ScaleFactor);
                Assert.True(window.MinHeight <= monitor.WorkArea.Height / monitor.ScaleFactor);
            }
            finally { window.CloseForExit(); FluidMotion.AnimationsEnabledOverrideForTest = null; application.Shutdown(); }
        });

    [Fact]
    public Task SettingsCopy_ShortWorkAreaKeepsLastSettingsAndWelcomeChoicesReachable() =>
        RunIsolated(nameof(SettingsCopy_ShortWorkAreaKeepsLastSettingsAndWelcomeChoicesReachable), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                var settings = new SettingsWindow(() => new AppSettings(),
                    _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                var fit = MonitorWindowPlacement.Fit(new RectD(0, 0, 1920, 1032), 2,
                    new Size(settings.Width, settings.Height), new Size(settings.MinWidth, settings.MinHeight), 0);
                settings.MinWidth = fit.Minimum.Width; settings.MinHeight = fit.Minimum.Height;
                settings.Width = fit.Size.Width; settings.Height = fit.Size.Height;
                try
                {
                    settings.Show(); Drain(settings);
                    var tabs = (TabControl)settings.FindName("CategoryTabs");
                    tabs.SelectedIndex = tabs.Items.Count - 1;
                    Drain(settings);
                    var last = (Button)settings.FindName("DefaultsButton");
                    ScrollViewer scroll = ParentScroll(last);
                    scroll.ScrollToEnd(); Drain(settings);
                    Rect lastBounds = last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize));
                    Assert.InRange(lastBounds.Top, 0, scroll.ActualHeight);
                    Assert.True(lastBounds.Bottom <= scroll.ActualHeight + 1);
                    var close = (Button)settings.FindName("CancelButton");
                    Rect closeBounds = close.TransformToAncestor(settings).TransformBounds(new Rect(close.RenderSize));
                    Assert.InRange(closeBounds.Bottom, 1, settings.ActualHeight);
                    Assert.True(scroll.ScrollableHeight > 0);
                    SaveSettingsCopyImage(settings, "settings-small-about");
                }
                finally { settings.CloseForExit(); }

                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(locale);
                    var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                        ShowActivated = false, ShowInTaskbar = false,
                    };
                    try
                    {
                        welcome.Show();
                        welcome.LimitToWorkArea(new MonitorInfo("small", new RectD(0, 0, 1920, 768), new RectD(0, 0, 1920, 720), 192, true));
                        Drain(welcome);
                        Assert.InRange(welcome.ActualHeight, 1, 360);
                        var scroll = Assert.IsType<ScrollViewer>(welcome.Content);
                        scroll.ScrollToEnd(); Drain(welcome);
                        Rect bottom = welcome.AccurateOcrHelp.TransformToAncestor(scroll).TransformBounds(new Rect(welcome.AccurateOcrHelp.RenderSize));
                        Assert.InRange(bottom.Bottom, 1, scroll.ActualHeight + 1);
                        Assert.True(scroll.ScrollableHeight > 0);
                        SaveSettingsCopyImage(welcome, "welcome-small-" + locale);
                    }
                    finally { welcome.Close(); }
                }
            }
            finally { FluidMotion.AnimationsEnabledOverrideForTest = null; application.Shutdown(); }

            static ScrollViewer ParentScroll(DependencyObject target)
            {
                for (DependencyObject? node = VisualTreeHelper.GetParent(target); node is not null; node = VisualTreeHelper.GetParent(node))
                    if (node is ScrollViewer scroll) return scroll;
                throw new InvalidOperationException("Setting must remain inside its scrolling page.");
            }
        });

    [Fact]
    public Task SettingsCopy_VideoBadgesStayApartAtMinimumCardWidths() =>
        RunIsolated(nameof(SettingsCopy_VideoBadgesStayApartAtMinimumCardWidths), () =>
        {
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-settings-copy-video-");
            try
            {
                foreach ((string key, double width) in new[] { ("Gallery.TileTemplate", 208d), ("Gallery.CompactTileTemplate", 134d) })
                {
                    var record = new CaptureRecord { MediaKind = CaptureMediaKind.Video, DurationMs = 3_665_000,
                        SourcePageUrl = "https://github.com/example/my-capture", Width = 1920, Height = 1080 };
                    var tile = new GalleryItemViewModel(record, _ => "missing-thumbnail.jpg", 320);
                    var item = new ListBoxItem { Content = tile, DataContext = tile,
                        ContentTemplate = (DataTemplate)fixture.Window.FindResource(key), Width = width,
                        Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    var window = new Window { Content = item, Width = 350, Height = 300, ShowActivated = false,
                        ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                    try
                    {
                        window.Show(); Drain(window);
                        Button source = Descendants(item).OfType<Button>().Single(button => button.Name == "SourceBadge");
                        Border video = Descendants(item).OfType<Border>().Single(border => border.Name == "VideoBadge");
                        foreach (bool selected in new[] { false, true, false })
                        {
                            tile.IsSelected = selected; Drain(window);
                            Assert.Equal(selected ? (double)application.FindResource("CommandButton.Height.Compact") : 22, source.ActualHeight);
                            Assert.Equal(selected ? 26 : 22, source.MinHeight);
                            Assert.Equal(selected ? 200 : 130, source.MaxWidth);
                            Assert.Equal(new Thickness(selected ? 1 : 0), source.BorderThickness);
                            Assert.Same(application.FindResource(selected ? "Text.Primary" : "Text.Secondary"), source.Foreground);
                            Rect left = source.TransformToAncestor(item).TransformBounds(new Rect(source.RenderSize));
                            Rect right = video.TransformToAncestor(item).TransformBounds(new Rect(video.RenderSize));
                            Assert.True(left.Right <= right.Left, $"{key}: {left} overlaps {right}");
                            Assert.True(source.ActualWidth > 0 && video.ActualWidth > 0);
                            Assert.InRange(right.Right, 1, item.ActualWidth);
                        }
                    }
                    finally { window.Close(); }
                }
            }
            finally { fixture.Window.CloseForExit(); FluidMotion.AnimationsEnabledOverrideForTest = null; application.Shutdown(); }
        });

    [Fact]
    public Task SettingsCopy_ChangedSurfacesRender() =>
        RunIsolated(nameof(SettingsCopy_ChangedSurfacesRender), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.Apply(AppTheme.Glass);
            try
            {
                var settings = new SettingsWindow(() => new AppSettings(),
                    _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance);
                Prepare(settings);
                try
                {
                    settings.Show();
                    Drain(settings);
                    SaveSettingsCopyImage(settings, "settings");
                }
                finally { settings.CloseForExit(); }

                var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor);
                Prepare(welcome);
                try { welcome.Show(); Drain(welcome); SaveSettingsCopyImage(welcome, "welcome"); }
                finally { welcome.Close(); }

                LibraryUxFixture fixture = CreateLibraryUxFixture("mc-settings-copy-evidence-");
                try
                {
                    CaptureRecord record = fixture.Records[0];
                    record.SourcePageUrl = "https://github.com/example/my-capture";
                    var board = new StackPanel { Margin = new Thickness(16), Orientation = Orientation.Horizontal };
                    foreach ((string key, double width) in new[] { ("Gallery.TileTemplate", 208d), ("Gallery.CompactTileTemplate", 134d) })
                    {
                        var tile = new GalleryItemViewModel(record,
                            _ => fixture.Queue.GetFilePath(record, CaptureFileNames.Thumbnail), 320);
                        var item = new ListBoxItem
                        {
                            Content = tile, DataContext = tile, ContentTemplate = (DataTemplate)fixture.Window.FindResource(key),
                            Width = width, Padding = new Thickness(0), Margin = new Thickness(8),
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        };
                        board.Children.Add(item);
                    }
                    var window = new Window { Content = board, Width = 430, Height = 340 };
                    window.SetResourceReference(Window.BackgroundProperty, "Surface.Base");
                    Prepare(window);
                    try { window.Show(); Drain(window); SaveSettingsCopyImage(window, "source-badges"); }
                    finally { window.Close(); }
                }
                finally { fixture.Window.CloseForExit(); }
            }
            finally { FluidMotion.AnimationsEnabledOverrideForTest = null; application.Shutdown(); }

            static void Prepare(Window window)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000; window.Top = -10000;
                window.ShowActivated = false; window.ShowInTaskbar = false;
                FluidMotion.SetWindowEntrance(window, false);
            }
        });

    private static void SaveSettingsCopyImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_COPY_EVIDENCE") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_COPY_PHASE") ?? "after";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(RenderEditorWindow(window)));
        using var file = File.Create(Path.Combine(directory, $"{name}-{phase}.png"));
        encoder.Save(file);
    }
}
