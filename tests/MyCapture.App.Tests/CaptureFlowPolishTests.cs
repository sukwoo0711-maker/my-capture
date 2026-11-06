using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// What the capture-flow look check of 2026-11-06 found: the countdown's shadow cut off by its own
/// window, a start-point chip with half the size read-out's padding, square magnifier image corners
/// against the panel's round ones, a tray branch row without the other rows' hover layer and focus
/// ring, and an action notification whose Close wrapped under the card after a scale change.
/// </summary>
/// <remarks>Set <c>MYCAPTURE_AES_CAPTURE_FLOW_FIX</c> to an absolute folder to keep the rendered frames.</remarks>
public sealed partial class CaptureFlowPolishTests
{
    private const string EvidenceGate = "MYCAPTURE_AES_CAPTURE_FLOW_FIX";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly double[] Scales = [1.0, 1.25, 1.5];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CountdownKeepsAShadowBandOnlyOnALayeredWindow(bool layered) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var mode = new ExclusionMode(layered);
        CountdownWindow window = OffscreenCountdown();
        try
        {
            window.Show();
            ControlStateMotionTests.Drain(window);
            Border panel = Assert.IsType<Border>(window.Content);
            double band = layered ? CountdownWindow.ShadowBand : 0;
            Assert.Equal(new Thickness(band), panel.Margin);
            if (!layered) return;
            // The band goes round the panel, so the window is still centred on it.
            Assert.Equal(panel.ActualWidth + (2 * band), window.ActualWidth, 3);
            Assert.Equal(panel.ActualHeight + (2 * band), window.ActualHeight, 3);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public Task CountdownShadowEndsInsideItsWindow() => SettingsThemePreviewTests.RunIsolated(
        nameof(CountdownShadowEndsInsideItsWindow), VerifyCountdownShadow, typeof(CaptureFlowPolishTests));

    private static void VerifyCountdownShadow() => WithTheme(() =>
    {
        using var mode = new ExclusionMode(true);
        foreach (double scale in Scales)
        {
            CountdownWindow window = OffscreenCountdown();
            try
            {
                TestDpiContext.Initialize96DpiWindow(window);
                window.Show();
                ControlStateMotionTests.Drain(window);
                AtScale(window, scale);
                Border panel = Assert.IsType<Border>(window.Content);
                Assert.IsType<DropShadowEffect>(panel.Effect);
                RenderTargetBitmap image = Render(window, scale);
                Save(image, $"countdown-{Percent(scale)}.png");
                var pixels = new Pixels(image);
                int edge = pixels.EdgeAlpha();
                Assert.True(edge <= 1, $"At {Percent(scale)}% the shadow reaches the window's edge with alpha {edge}.");
                // The shadow is drawn into the band: 8 DIPs under the middle of the panel.
                int below = pixels.Alpha(pixels.Width / 2, (int)((CountdownWindow.ShadowBand + panel.ActualHeight + 8) * scale));
                Assert.True(below > 8, $"At {Percent(scale)}% the band under the panel has alpha {below}.");
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void StartPointChipPadsItsLabelAndKeepsOffTheEdgesLikeTheSizeReadout() => StaTestHost.Run(() =>
    {
        var label = new Size(40, 16);
        var screen = new Size(1280, 720);
        // The read-out's 9 x 5 DIPs round the label, from the chip's old corner.
        Assert.Equal(new Rect(311, 260, 58, 26), CaptureOverlayView.AnchorChipBounds(new Point(300, 250), label, screen));
        // A drag started in the bottom-right corner keeps it 8 DIPs inside the screen, as the read-out is.
        Assert.Equal(new Rect(1214, 686, 58, 26), CaptureOverlayView.AnchorChipBounds(new Point(1279, 719), label, screen));
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void DragDrawsTheStartPointChipWithTheSizeReadoutsCorners(double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame desktop = CaptureCueEvidenceTests.Desktop();
        foreach ((PointD start, PointD end) in new[] { (new PointD(300, 250), new PointD(520, 430)), (new PointD(1279, 719), new PointD(1000, 500)) })
        {
            PointD pointer = start;
            var view = new CaptureOverlayView(desktop, showMagnifier: false)
            {
                ReadCursor = () => pointer,
                PlaceCursor = _ => false,
                SimulatedPointerInside = true,
            };
            try
            {
                CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Create(AppTheme.Daylight, customColor: null, systemHighContrast: false));
                view.InitializePointer();
                view.SimulatePointerDown();
                pointer = end;
                view.SimulatePointerMove();
                view.FlushFeedbackForTest();
                // The first frame settles the scale; the chips are drawn again at it.
                _ = CaptureCueEvidenceTests.Render(view, scale);
                view.FlushFeedbackForTest();
                Save(CaptureCueEvidenceTests.Render(view, scale), $"selector-drag-{start.X:0}-{Percent(scale)}.png");
                Rect chip = view.AnchorChipBoundsForTest ?? throw new InvalidOperationException("The drag drew no start-point chip.");
                Rect readout = view.DeferredGuideReadout ?? throw new InvalidOperationException("The drag drew no size read-out.");
                RectangleGeometry drawn = Assert.Single(Rounded(Visual(view, "_anchorVisual")));
                RectangleGeometry size = Assert.Single(Rounded(Visual(view, "_selectionVisual")), geometry => geometry.Rect == readout);
                Assert.Equal(chip, drawn.Rect);
                Assert.Equal(size.RadiusX, drawn.RadiusX);
                Assert.True(chip.Left >= 8 && chip.Top >= 8 && chip.Right <= view.ActualWidth - 8 + 1e-6
                    && chip.Bottom <= view.ActualHeight - 8 + 1e-6, $"Chip {chip} on a {view.ActualWidth} x {view.ActualHeight} screen.");
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void MagnifierImageTurnsItsTopCornersWithThePanel(double scale) => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(SolidFrame(Colors.Magenta), showSelectionGrid: false)
        {
            ReadCursor = () => new PointD(320, 180),
            SimulatedPointerInside = true,
        };
        try
        {
            view.InitializePointer();
            view.FlushFeedbackForTest();
            _ = RenderView(view, scale);
            view.FlushFeedbackForTest();
            RenderTargetBitmap frame = RenderView(view, scale);
            Save(frame, $"magnifier-{Percent(scale)}.png");
            var pixels = new Pixels(frame);
            Rect image = view.MagnifierImageBoundsForTest ?? throw new InvalidOperationException("No magnifier was drawn.");
            int left = (int)Math.Round(image.Left * scale), top = (int)Math.Round(image.Top * scale);
            int right = (int)Math.Round(image.Right * scale) - 1, foot = (int)Math.Round(image.Bottom * scale) - 1;
            int inset = (int)Math.Round(3 * scale);
            string what = $"At {Percent(scale)}% the image at {image}";
            Assert.False(IsMagenta(pixels.At(left, top)), $"{what} still has a square top-left corner.");
            Assert.False(IsMagenta(pixels.At(right, top)), $"{what} still has a square top-right corner.");
            Assert.True(IsMagenta(pixels.At(left + inset, top + inset)), $"{what} rounds its corner further than the panel's.");
            Assert.True(IsMagenta(pixels.At(left, foot)) && IsMagenta(pixels.At(right, foot)), $"{what} lost the square foot it shares with the footer.");
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 1.25)]
    [InlineData(1.0, 1.5)]
    [InlineData(1.0, 1.75)]
    [InlineData(1.0, 2.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.5, 1.5)]
    [InlineData(1.5, 1.0)]
    public void ModeBarKeepsEveryCommandOnOneRow(double monitorScale, double overlayScale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en");
        var view = new CaptureOverlayView(CaptureCueEvidenceTests.Desktop(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(900, 520),
            DetectWindows = true,
            ReadMonitors = () => [new MonitorInfo(@"\\.\DISPLAY1", new RectD(0, 0, 1280, 720), new RectD(0, 0, 1280, 672),
                (uint)Math.Round(96 * monitorScale), IsPrimary: true)],
            WindowCandidates = [CaptureCueEvidenceTests.Browser, CaptureCueEvidenceTests.Notes,
                CaptureCueEvidenceTests.Taskbar, CaptureCueEvidenceTests.Shell],
            CopyColorText = _ => Task.FromResult(true),
            TextCaptureAvailable = true,
            ShowModeBar = true,
            OffersOtherActions = true,
        };
        try
        {
            CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Create(AppTheme.Daylight, customColor: null, systemHighContrast: false));
            view.InitializePointer();
            Save(CaptureCueEvidenceTests.Render(view, overlayScale), $"modebar-monitor{Percent(monitorScale)}-overlay{Percent(overlayScale)}.png");
            CaptureModeBar bar = view.ModeBarForTest!;
            var row = (Panel)bar.Child;
            FrameworkElement[] shown = row.Children.OfType<FrameworkElement>().Where(item => item.Visibility == Visibility.Visible).ToArray();
            // Dividers are shorter than the buttons and centred on the row.
            double[] middles = shown.Select(item => item.TranslatePoint(new Point(0, item.ActualHeight / 2), row).Y).ToArray();
            double used = shown.Sum(item => item.DesiredSize.Width);
            string what = $"Monitor {Percent(monitorScale)}%, overlay {Percent(overlayScale)}%: middles "
                + $"{string.Join(", ", middles.Select(middle => middle.ToString("0.##")))}; row {row.ActualWidth:R} DIPs for "
                + $"{used:R} ({string.Join(" ", shown.Select(item => item.DesiredSize.Width.ToString("0.###")))}), "
                + $"dpi {VisualTreeHelper.GetDpi(bar).DpiScaleX}, guide scale {view.GuideScaleForTest:R}.";
            Assert.True(middles.All(middle => Math.Abs(middle - middles[0]) < 0.01), what);
            // The row ends with Close, without a gap after it that the start of the row does not have.
            Assert.True(row.ActualWidth - used < (1 / VisualTreeHelper.GetDpi(bar).DpiScaleX) - 0.0001, what);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void TrayBranchRowShowsTheHoverAndFocusOfEveryOtherRow() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var presenter = new ThemedShellPresenter(() => false);
        try
        {
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Daylight, resources, systemHighContrast: false);
            ContextMenu menu = presenter.CreateMenu(TrayMenuLayout.Build(NoCommands(), NewInstallDefaults.Create().Hotkeys,
                new TrayMenuState(ClipboardHasContent: false)), resources);
            MenuItem[] rows = menu.Items.OfType<MenuItem>().ToArray();
            MenuItem plain = rows[0];
            MenuItem branch = rows.First(row => row.HasItems);
            LayOut(menu);
            var focusWithin = (DependencyPropertyKey)typeof(UIElement)
                .GetField("IsKeyboardFocusWithinPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            MethodInfo highlight = typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.GetSetMethod(nonPublic: true)!;
            foreach (MenuItem row in new[] { plain, branch })
            {
                highlight.Invoke(row, [true]);
                row.SetValue(focusWithin, true);
            }
            LayOut(menu);
            RenderTargetBitmap image = Render(menu, 1, desiredSize: true);
            Save(image, "tray-rows-highlighted-focused.png");
            foreach (MenuItem row in new[] { plain, branch })
                foreach (string part in new[] { "HoverLayer", "FocusRing" })
                    Assert.True(row.Template.FindName(part, row) is UIElement { Opacity: 1 },
                        $"{row.Header}: its {part} does not show while highlighted with keyboard focus.");
            // On the ring and just inside it, the branch row's pixels are the plain row's.
            var pixels = new Pixels(image);
            foreach (double x in new[] { 1.0, 5.0 })
                Assert.Equal(PixelIn(pixels, menu, plain, x), PixelIn(pixels, menu, branch, x));
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [Fact]
    public Task ActionNotificationKeepsItsButtonsOnOneRowAfterAScaleChange() => SettingsThemePreviewTests.RunIsolated(
        nameof(ActionNotificationKeepsItsButtonsOnOneRowAfterAScaleChange), VerifyActionNotification, typeof(CaptureFlowPolishTests));

    private static void VerifyActionNotification() => WithTheme(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        foreach (string language in new[] { "en", "ko-KR" })
        {
            using var scope = UiText.UseLanguage(language);
            foreach (double scale in new[] { 1.25, 1.5, 1.75 })
            {
                string what = $"{language} at {Percent(scale)}%";
                // Laid out at the scale from the start, as on a single monitor.
                Window fresh = ActionNotification(presenter);
                var surface = (FrameworkElement)fresh.Content;
                fresh.Content = null;
                var host = new Border { Child = surface };
                VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
                host.Measure(new Size(fresh.Width, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                AssertOneRow(surface, what + " from the start");
                fresh.Close();

                // Laid out at 100% and then moved to a monitor at the scale, and back.
                Window window = ActionNotification(presenter);
                try
                {
                    FluidMotion.SetWindowEntrance(window, false);
                    TestDpiContext.Initialize96DpiWindow(window);
                    window.Show();
                    ControlStateMotionTests.Drain(window);
                    AtScale(window, scale);
                    Save(Render(window, scale), $"notify-action-{language[..2]}-{Percent(scale)}.png");
                    AssertOneRow(window, what + " after 100%");
                    AtScale(window, 1);
                    AssertOneRow(window, $"{language} back at 100% from {Percent(scale)}%");
                }
                finally { window.Close(); }
            }
        }
    });

    private static Window ActionNotification(ThemedShellPresenter presenter) => presenter.CreateActionNotification(
        UiText.Get("QuickAccess.Saved"), "MyCapture_20261106_131000.png", UiText.Get("QuickAccess.OpenFolder"), () => { });

    private static void AssertOneRow(FrameworkElement root, string what)
    {
        WrapPanel row = Descendants(root).OfType<WrapPanel>().Single();
        Button[] buttons = row.Children.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        Point action = buttons[0].TranslatePoint(default, row), close = buttons[1].TranslatePoint(default, row);
        Assert.True(Math.Abs(action.Y - close.Y) < 0.01 && close.X > action.X,
            $"{what}: Close at {close} and the action at {action}, in a row {row.ActualWidth:R} DIPs wide for buttons "
            + $"{buttons[0].DesiredSize.Width:R} and {buttons[1].DesiredSize.Width:R} wide.");
    }

    /// <summary>Runs <paramref name="body"/> under the app's theme dictionaries, as the app does, at the thread's 96 DPI.</summary>
    private static void WithTheme(Action body)
    {
        using var nativeDpi = TestDpiContext.At96Dpi();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? highContrast = ThemeService.SystemHighContrastOverrideForTest;
        AppTheme previous = ThemeService.Current;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.SystemHighContrastOverrideForTest = false;
        try
        {
            ThemeService.Apply(AppTheme.Daylight);
            body();
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            ThemeService.Apply(previous);
            ThemeService.SystemHighContrastOverrideForTest = highContrast;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            app.Shutdown();
        }
    }

    private static CountdownWindow OffscreenCountdown()
    {
        // Off screen and never activated: other sessions may be using this desktop.
        var window = new CountdownWindow(3)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        // The countdown must not run on while the test looks at it.
        window.Loaded += (_, _) => ((DispatcherTimer)typeof(CountdownWindow).GetField("_timer", PrivateInstance)!.GetValue(window)!).Stop();
        return window;
    }

    private static void AtScale(Window window, double scale)
    {
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        window.InvalidateMeasure();
        window.UpdateLayout();
        Assert.Equal(scale, VisualTreeHelper.GetDpi(window).DpiScaleX);
        Assert.True(window.Left < -1000 && window.Top < -1000, "The window left its place off screen.");
    }

    private static void LayOut(FrameworkElement element)
    {
        element.Measure(new Size(440, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static TrayMenuCommands NoCommands()
    {
        Action none = () => { };
        return new TrayMenuCommands(none, none, none, none, none, none, none, none, none, none, none, none, none,
            PinClipboard: none, HideOrShowAllPins: none, ClearClickThrough: none, ContactSupport: none,
            InstallUpdate: none, FirstUseGuide: none);
    }

    private static Color PixelIn(Pixels pixels, Visual root, FrameworkElement row, double x)
    {
        Point at = row.TransformToAncestor(root).Transform(new Point(x, row.ActualHeight / 2));
        if (root is FrameworkElement element)
            at.Offset(element.Margin.Left, element.Margin.Top);
        return pixels.At((int)at.X, (int)at.Y);
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double scale, bool desiredSize = false)
    {
        Size size = desiredSize ? element.DesiredSize : element.RenderSize;
        var image = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale),
            (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(element);
        return image;
    }

    private static RenderTargetBitmap RenderView(CaptureOverlayView view, double scale)
    {
        if (Math.Abs(view.GuidePixelSizeX - (1 / scale)) > 0.000001)
            typeof(CaptureOverlayView).GetMethod("OnDpiChanged", PrivateInstance)!.Invoke(view,
                [new DpiScale(1 / view.GuidePixelSizeX, 1 / view.GuidePixelSizeY), new DpiScale(scale, scale)]);
        // Offscreen roots report 96 DPI; keep the simulated scale's DIP sizes unrounded.
        view.UseLayoutRounding = false;
        view.InvalidateVisual();
        view.Measure(new Size(640 / scale, 360 / scale));
        view.Arrange(new Rect(0, 0, 640 / scale, 360 / scale));
        view.UpdateLayout();
        var image = new RenderTargetBitmap(640, 360, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(view);
        return image;
    }

    private static FrozenFrame SolidFrame(Color color)
    {
        byte[] pixels = new byte[640 * 360 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 640, 360), null, 0);
    }

    private static bool IsMagenta(Color color) => color.R > 200 && color.G < 80 && color.B > 200;

    private static DrawingVisual Visual(CaptureOverlayView view, string field) =>
        (DrawingVisual)typeof(CaptureOverlayView).GetField(field, PrivateInstance)!.GetValue(view)!;

    private static IEnumerable<RectangleGeometry> Rounded(DrawingVisual visual) =>
        Leaves(visual.Drawing).OfType<GeometryDrawing>().Select(drawing => drawing.Geometry)
            .OfType<RectangleGeometry>().Where(geometry => geometry.RadiusX > 0);

    private static IEnumerable<Drawing> Leaves(Drawing? drawing) => drawing switch
    {
        DrawingGroup group => group.Children.SelectMany(Leaves),
        null => [],
        _ => [drawing],
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0, count = VisualTreeHelper.GetChildrenCount(root); index < count; index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(root, index)))
                yield return child;
    }

    private static string Percent(double scale) => (scale * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static void Save(BitmapSource image, string name)
    {
        string? folder = Environment.GetEnvironmentVariable(EvidenceGate);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Assert.True(Path.IsPathFullyQualified(folder), "Evidence output must be absolute.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(folder), name));
        encoder.Save(output);
    }

    private sealed class Pixels
    {
        private readonly byte[] _bgra;

        internal Pixels(BitmapSource image)
        {
            Width = image.PixelWidth;
            Height = image.PixelHeight;
            _bgra = new byte[Width * Height * 4];
            image.CopyPixels(_bgra, Width * 4, 0);
        }

        internal int Width { get; }

        internal int Height { get; }

        internal int Alpha(int x, int y) => _bgra[(((y * Width) + x) * 4) + 3];

        internal Color At(int x, int y)
        {
            int i = ((y * Width) + x) * 4;
            return Color.FromArgb(_bgra[i + 3], _bgra[i + 2], _bgra[i + 1], _bgra[i]);
        }

        /// <summary>The highest alpha on the image's outermost ring of pixels.</summary>
        internal int EdgeAlpha()
        {
            int highest = 0;
            for (int x = 0; x < Width; x++) highest = Math.Max(highest, Math.Max(Alpha(x, 0), Alpha(x, Height - 1)));
            for (int y = 0; y < Height; y++) highest = Math.Max(highest, Math.Max(Alpha(0, y), Alpha(Width - 1, y)));
            return highest;
        }
    }
}
