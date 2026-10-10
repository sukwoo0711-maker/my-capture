using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string OnboardEvidence = "MYCAPTURE_ONBOARD_EVIDENCE";

    /// <summary>
    /// Renders the first-run welcome, the tray menu, the capture and recording selectors, the
    /// recording bar and a hovered pin with the real theme resources in glass and high contrast.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_ONBOARD_EVIDENCE</c> to a directory to keep the PNGs (named "-after").
    /// Without it the scenes are still built and laid out, so a broken template fails here.
    /// </remarks>
    [Fact]
    public Task Onboard_FirstRunTrayAndCaptureScenesRenderInEvidenceThemes() =>
        RunIsolated(nameof(Onboard_FirstRunTrayAndCaptureScenesRenderInEvidenceThemes), RenderOnboardEvidence);

    private static void RenderOnboardEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable(OnboardEvidence);
        string? root = string.IsNullOrWhiteSpace(requested) ? null : Directory.CreateDirectory(requested).FullName;
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                string suffix = AppThemeNames.ToSetting(theme) + "-after.png";
                void Save(BitmapSource image, string name)
                {
                    Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
                    if (root is not null) SaveBitmap(image, Path.Combine(root, name + "-" + suffix));
                }

                var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                };
                welcome.Show();
                Drain(welcome);
                Save(RenderWindow(welcome), "welcome");
                welcome.Close();

                using var presenter = new ThemedShellPresenter(() => false);
                IReadOnlyList<ThemedShellPresenter.MenuEntry> entries = TrayMenuLayout.Build(new TrayMenuCommands(
                    () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                    () => { }, () => { }, () => { }, () => { }), new HotkeySettings(), canRestoreClosedPin: true, scrollingActive: false);
                ContextMenu menu = presenter.CreateMenu(entries);
                Save(RenderMenu(menu, openBranch: 2), "tray-menu");

                RenderCaptureScenes(theme, Save);
                RenderRecordingBarScenes(Save);
                RenderPinHoverScene(Save);
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }
    }

    /// <summary>The screenshot selector (first open, window hover, ⋯ menu) and the recording selector mid-drag.</summary>
    private static void RenderCaptureScenes(AppTheme theme, Action<BitmapSource, string> save)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        foreach (string state in new[] { "first-open", "window-hover" })
        {
            CaptureOverlayView view = CaptureCueEvidenceTests.EvidenceView(frame, theme, state);
            try { save(CaptureCueEvidenceTests.Render(view), "capture-" + state); }
            finally { view.ReleaseResources(); }
        }

        CaptureOverlayView more = CaptureCueEvidenceTests.EvidenceView(frame, theme, "window-hover");
        try
        {
            more.CanRepeatLastRegion = () => true;
            more.OffersOtherActions = true;
            RenderTargetBitmap selector = CaptureCueEvidenceTests.Render(more);
            Button button = more.ModeBarForTest!.MoreButton;
            Point anchor = button.TranslatePoint(new Point(0, button.ActualHeight + 4), more);
            BitmapSource menu = RenderMenuCard(more.ModeBarForTest.CreateMoreMenu());
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(selector, new Rect(0, 0, selector.PixelWidth, selector.PixelHeight));
                dc.DrawImage(menu, new Rect(anchor.X, anchor.Y, menu.PixelWidth, menu.PixelHeight));
            }
            var composed = new RenderTargetBitmap(selector.PixelWidth, selector.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            composed.Render(visual);
            save(composed, "capture-more-menu");
        }
        finally { more.ReleaseResources(); }

        PointD pointer = new(320, 210);
        var record = new CaptureOverlayView(frame, showMagnifier: false)
        {
            ReadCursor = () => pointer,
            ReadMonitors = () => CaptureCueEvidenceTests.Monitors,
            RecordingSelection = true,
        };
        CaptureCueEvidenceTests.UsePalette(record, CaptureGuidePalette.Create(theme, customColor: null, systemHighContrast: false));
        record.RecordingBrush = RecordingSelectionCue.Create(theme, systemHighContrast: false);
        try
        {
            record.InitializePointer();
            save(CaptureCueEvidenceTests.Render(record), "record-select-ready");
            WindowDetectOverlayTests.Invoke(record, "OnMouseLeftButtonDown", WindowDetectOverlayTests.Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(900, 560);
            WindowDetectOverlayTests.Invoke(record, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            save(CaptureCueEvidenceTests.Render(record), "record-select");
        }
        finally { record.ReleaseResources(); }
    }

    /// <summary>The recording bar before, during and paused, laid out without showing its window.</summary>
    private static void RenderRecordingBarScenes(Action<BitmapSource, string> save)
    {
        RecordingControlWindow Bar() => new(new RectD(200, 200, 1280, 720), new RecordingSettings(),
            () => throw new InvalidOperationException("Evidence only: no recorder starts."), () => "unused.mp4",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        foreach ((string name, bool? paused) in new[] { ("record-bar-ready", (bool?)null), ("record-bar-recording", false), ("record-bar-paused", true) })
        {
            RecordingControlWindow window = Bar();
            try
            {
                if (paused is { } isPaused) window.PreviewRecordingForTest(isPaused, TimeSpan.FromSeconds(12));
                var strip = (FrameworkElement)typeof(RecordingControlWindow)
                    .GetField("_controlStrip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(window)!;
                save(RenderPlaced(strip), name);
            }
            finally { window.Close(); }
        }
    }

    /// <summary>A hovered pin with its bar in the corner, both kept off the desktop.</summary>
    private static void RenderPinHoverScene(Action<BitmapSource, string> save)
    {
        BitmapSource image = PinEvidenceImage();
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(image.PixelWidth, image.PixelHeight, 1, 1, 0.1),
            -10000, -10000, () => new PinSettings())
        {
            ShowActivated = false,
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
        try
        {
            pin.Show();
            pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            PinHoverBar bar = pin.HoverBarForTest!;
            Drain(pin);
            bar.UpdateLayout();
            var pinContent = (FrameworkElement)pin.Content;
            var barContent = (FrameworkElement)bar.Content;
            RectD placed = PinHoverBar.Place(new RectD(0, 0, pin.ActualWidth, pin.ActualHeight), bar.ActualWidth, bar.ActualHeight,
                new RectD(-1000, -1000, 4000, 4000), 1);
            const double margin = 24;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x7A), Color.FromRgb(0x7A, 0x9C, 0xB8), 35), null,
                    new Rect(0, 0, pin.ActualWidth + (margin * 2), pin.ActualHeight + (margin * 2)));
                dc.DrawRectangle(new VisualBrush(pinContent) { Stretch = Stretch.None }, null,
                    new Rect(margin, margin, pinContent.ActualWidth, pinContent.ActualHeight));
                dc.DrawRectangle(new VisualBrush(barContent) { Stretch = Stretch.None }, null,
                    new Rect(margin + placed.Left, margin + placed.Top, barContent.ActualWidth, barContent.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(pin.ActualWidth + (margin * 2)),
                (int)Math.Ceiling(pin.ActualHeight + (margin * 2)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            save(bitmap, "pin-hover");
        }
        finally { pin.Close(); }
    }

    private static BitmapSource PinEvidenceImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)), null, new Rect(0, 0, 360, 220));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE4, 0xE8, 0xEE)), null, new Rect(0, 0, 360, 30));
            for (int line = 0; line < 5; line++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x66, 0x2B, 0x31, 0x3B)), null,
                    new Rect(20, 50 + line * 30, 300 - (line % 2) * 80, 10), 4, 4);
        }
        var bitmap = new RenderTargetBitmap(360, 220, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A context menu's items on its own card, without a popup.</summary>
    private static BitmapSource RenderMenuCard(ContextMenu menu)
    {
        object[] items = menu.Items.Cast<object>().ToArray();
        menu.Items.Clear();
        FrameworkElement card = MenuCard(items);
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(card);
        return bitmap;
    }

    /// <summary>
    /// A menu drawn as it opens, without opening a popup on the desktop: its items move into the
    /// same item template on a menu-styled card, and one branch's children appear beside it.
    /// </summary>
    internal static BitmapSource RenderMenu(ContextMenu menu, int openBranch = -1)
    {
        object[] items = menu.Items.Cast<object>().ToArray();
        menu.Items.Clear();
        FrameworkElement top = MenuCard(items);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(top);
        if (openBranch >= 0 && items.OfType<MenuItem>().ElementAtOrDefault(openBranch) is { } branch)
        {
            object[] children = branch.Items.Cast<object>().ToArray();
            branch.Items.Clear();
            branch.Background = Application.Current.TryFindResource("Surface.Pressed") as Brush;
            FrameworkElement side = MenuCard(children);
            int index = Array.IndexOf(items, branch);
            side.Margin = new Thickness(4, 6 + (index * 36), 0, 0);
            side.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(side);
        }
        return RenderOnBackdrop(row, 560);
    }

    private static FrameworkElement MenuCard(IEnumerable<object> items)
    {
        var host = new Menu { Background = Brushes.Transparent };
        host.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)));
        foreach (object item in items) host.Items.Add(item);
        var border = new Border
        {
            Child = host, BorderThickness = new Thickness(1), Padding = new Thickness(5), MinWidth = 240,
            CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        border.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        return border;
    }

    internal static BitmapSource RenderWindow(Window window)
    {
        // Glass windows are translucent over a system backdrop RenderTargetBitmap cannot see; an
        // opaque base keeps the evidence readable the way the desktop shows it.
        Color baseColor = (Application.Current.TryFindResource("Surface.Base") as SolidColorBrush)?.Color ?? Colors.Black;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(baseColor.R, baseColor.G, baseColor.B)), null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None }, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>A detached element laid out at its desired size on a neutral desktop-like backdrop.</summary>
    internal static BitmapSource RenderOnBackdrop(FrameworkElement element, double width)
    {
        var host = new Border
        {
            Background = new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x7A), Color.FromRgb(0x7A, 0x9C, 0xB8), 35),
            Padding = new Thickness(24),
            Child = element,
        };
        host.Measure(new Size(width + 48, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;
        return bitmap;
    }

    /// <summary>An element inside an unshown window: lays the window content out and draws the element alone.</summary>
    internal static BitmapSource RenderPlaced(FrameworkElement element, double margin = 24)
    {
        Window owner = Window.GetWindow(element)!;
        var content = (FrameworkElement)owner.Content;
        double width = double.IsNaN(content.Width) ? owner.Width : content.Width;
        double height = double.IsNaN(content.Height) ? owner.Height : content.Height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        double elementWidth = element.ActualWidth, elementHeight = element.ActualHeight;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x7A), Color.FromRgb(0x7A, 0x9C, 0xB8), 35), null,
                new Rect(0, 0, elementWidth + (margin * 2), elementHeight + (margin * 2)));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null, new Rect(margin, margin, elementWidth, elementHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(elementWidth + (margin * 2)), (int)Math.Ceiling(elementHeight + (margin * 2)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    internal static void SaveBitmap(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
