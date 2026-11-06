using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class CaptureFlowPolishTests
{
    private static readonly AppTheme[] ShadowThemes = [AppTheme.Daylight, AppTheme.Midnight];
    private static readonly string[] RecordingShadowStates = ["ready", "recording", "paused", "saving", "saved"];

    // C4 found a cut-off shadow barely visible where its outermost pixel is at most this alpha.
    private const int BarelyVisibleShadowEdge = 8;

    [Theory]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.HighContrast)]
    public void TrayMenusUseASubtleOneDipBorderAndTwelveDipBand(AppTheme theme) => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        ContextMenu menu = ShadowTray(presenter, theme);
        LayOut(menu);
        string what = $"{theme}, tray default, 100%";
        Assert.True(menu.BorderThickness == new Thickness(1), $"{what}: border is {menu.BorderThickness}, expected 1 DIP.");
        Assert.True(menu.Margin == new Thickness(12), $"{what}: popup band is {menu.Margin}, expected 12 DIP.");
        var expected = menu.FindResource("Border.Subtle") as SolidColorBrush;
        var actual = menu.BorderBrush as SolidColorBrush;
        Assert.True(expected is not null && actual is not null && actual.Color == expected.Color,
            $"{what}: border color is {actual?.Color}, Border.Subtle is {expected?.Color}.");
    });

    [Fact]
    public Task TrayMenuShadowEndsInsideItsPopupBand() => SettingsThemePreviewTests.RunIsolated(
        nameof(TrayMenuShadowEndsInsideItsPopupBand), () => WithTheme(() =>
        {
            using var presenter = new ThemedShellPresenter(() => false);
            foreach (AppTheme theme in ShadowThemes)
                foreach (double scale in Scales)
                {
                    ContextMenu menu = ShadowTray(presenter, theme);
                    LayoutShadowMenu(menu, scale);
                    RenderTargetBitmap image = Render(menu, scale, desiredSize: true);
                    Save(image, $"tray-shadow-{theme}-{Percent(scale)}.png");
                    var pixels = new Pixels(image);
                    int edge = pixels.EdgeAlpha();
                    int below = pixels.Alpha((int)((menu.Margin.Left + menu.ActualWidth / 2) * scale),
                        (int)((menu.Margin.Top + menu.ActualHeight + 4) * scale));
                    string what = $"{theme}, tray default, {Percent(scale)}%";
                    ShadowMeasurement($"tray-shadow-{theme}-{Percent(scale)}.txt", $"{what}: edge alpha={edge}; 4 DIP below alpha={below}.");
                    Assert.True(edge <= 1, $"{what}: outermost popup ring alpha is {edge}, expected <= 1.");
                    Assert.True(below > 0, $"{what}: 4 DIP below the menu has alpha {below}; the band contains no shadow.");
                }
        }), typeof(CaptureFlowPolishTests));

    [Fact]
    public Task HighContrastTrayMenuHasATransparentBandAndOpaqueOnePixelEdge() => SettingsThemePreviewTests.RunIsolated(
        nameof(HighContrastTrayMenuHasATransparentBandAndOpaqueOnePixelEdge), () => WithTheme(() =>
        {
            using var presenter = new ThemedShellPresenter(() => false);
            foreach (double scale in Scales)
            {
                ContextMenu menu = ShadowTray(presenter, AppTheme.HighContrast);
                LayoutShadowMenu(menu, scale);
                RenderTargetBitmap image = Render(menu, scale, desiredSize: true);
                Save(image, $"tray-shadow-HighContrast-{Percent(scale)}.png");
                var pixels = new Pixels(image);
                int left = (int)Math.Round(menu.Margin.Left * scale), top = (int)Math.Round(menu.Margin.Top * scale);
                int right = (int)Math.Round((menu.Margin.Left + menu.ActualWidth) * scale) - 1;
                int bottom = (int)Math.Round((menu.Margin.Top + menu.ActualHeight) * scale) - 1;
                int band = 0;
                for (int y = 0; y < pixels.Height; y++)
                    for (int x = 0; x < pixels.Width; x++)
                        if (x < left || x > right || y < top || y > bottom) band = Math.Max(band, pixels.Alpha(x, y));
                // The menu keeps its rounded corners, which leave their outermost pixels clear; the
                // straight runs between them are the outline and must be the opaque border colour.
                double radius = ((CornerRadius)menu.FindResource("Radius.Md")).TopLeft;
                int inset = (int)Math.Ceiling((radius + 1) * scale);
                Color outline = ((SolidColorBrush)menu.BorderBrush).Color;
                var run = new List<(int X, int Y)>();
                for (int x = left + inset; x <= right - inset; x++) { run.Add((x, top)); run.Add((x, bottom)); }
                for (int y = top + inset; y <= bottom - inset; y++) { run.Add((left, y)); run.Add((right, y)); }
                int runMinimum = run.Min(point => pixels.Alpha(point.X, point.Y));
                int offColour = run.Count(point => !Near(pixels.At(point.X, point.Y), outline));
                string what = $"HighContrast, tray default, {Percent(scale)}%";
                ShadowMeasurement($"tray-shadow-HighContrast-{Percent(scale)}.txt",
                    $"{what}: margin-ring maximum alpha={band}; straight outline runs={run.Count} pixels, minimum alpha={runMinimum}, not {outline}={offColour}.");
                Assert.True(band == 0, $"{what}: 12-DIP margin has alpha {band}, expected fully transparent.");
                Assert.True(run.Count > 0 && runMinimum == 255, $"{what}: the straight runs of the 1-pixel outline ({run.Count} pixels) have minimum alpha {runMinimum}, expected 255.");
                Assert.True(offColour == 0, $"{what}: {offColour} of {run.Count} outline pixels are not the border colour {outline}.");
            }
        }), typeof(CaptureFlowPolishTests));

    [Fact]
    public Task TrayMenuShadowBandPreservesRealPopupPlacement() => SettingsThemePreviewTests.RunIsolated(
        nameof(TrayMenuShadowBandPreservesRealPopupPlacement), () => WithTheme(() =>
        {
            using var mode = new ExclusionMode(true);
            Rect work = SystemParameters.WorkArea;
            var anchor = new Border { Width = 40, Height = 24, Background = Brushes.Transparent };
            var host = new Canvas();
            Canvas.SetLeft(anchor, 20);
            Canvas.SetTop(anchor, 12);
            host.Children.Add(anchor);
            var owner = new Window
            {
                Content = host, Width = 80, Height = 48, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = work.Left + (work.Width - 80) / 2, Top = work.Top + (work.Height - 48) / 2,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                TestDpiContext.Initialize96DpiWindow(owner);
                owner.Show();
                NativeExclusion.Drain();
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight, AppTheme.HighContrast })
                {
                    foreach (bool above in new[] { false, true })
                    {
                        owner.Top = above ? work.Bottom - 20 - 36 : work.Top + (work.Height - owner.Height) / 2;
                        NativeExclusion.Drain();
                        owner.UpdateLayout();
                        Point expected = anchor.PointToScreen(new Point(0, above ? 0 : anchor.ActualHeight));
                        Point styled = OpenAndMeasure(12, above, theme);
                        Point zero = OpenAndMeasure(0, above, theme);
                        string what = $"{theme}, real popup {(above ? "flipped above" : "below")}, primary monitor";
                        ScreenPointNear(styled, zero, what + ": styled versus zero-margin menu");
                        ScreenPointNear(styled, expected, what + ": styled menu versus anchor");
                        ScreenPointNear(zero, expected, what + ": zero-margin menu versus anchor");
                        ShadowMeasurement($"tray-placement-{theme}-{(above ? "above" : "below")}.txt",
                            $"{what}: styled={styled}; zero-margin={zero}; anchor={expected} (physical pixels).");
                    }
                }
            }
            finally { owner.Close(); }

            Point OpenAndMeasure(double band, bool above, AppTheme theme)
            {
                ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
                ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
                var menu = new ContextMenu { Resources = resources, Placement = PlacementMode.Bottom, PlacementTarget = anchor };
                menu.Items.Add(new MenuItem { Header = "Capture" });
                menu.Items.Add(new MenuItem { Header = "Library" });
                if (band == 0) menu.Margin = new Thickness(0);
                try
                {
                    menu.IsOpen = true;
                    NativeExclusion.Drain();
                    menu.UpdateLayout();
                    string what = $"{theme}, {(above ? "above" : "below")}, {band}-DIP popup band";
                    Assert.True(menu.IsOpen && PresentationSource.FromVisual(menu) is not null, $"{what}: real popup did not open.");
                    Assert.True(menu.Margin == new Thickness(band), $"{what}: actual margin is {menu.Margin}.");
                    return menu.PointToScreen(new Point(0, above ? menu.ActualHeight : 0));
                }
                finally { menu.IsOpen = false; NativeExclusion.Drain(); }
            }
        }), typeof(CaptureFlowPolishTests));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void RecordingPlannerClipsOnlyThePaletteShadowBand(double scale)
    {
        RectD work = ScaleRect(new RectD(0, 0, 1920, 1040), scale);
        foreach ((string state, RectD regionDip) in new[]
        {
            ("middle", new RectD(640, 248, 640, 360)),
            ("palette at bottom", new RectD(640, 616, 640, 360)),
            ("palette flipped above", new RectD(640, 672, 640, 360)),
            ("palette at left", new RectD(8, 248, 80, 360)),
        })
        {
            RectD region = ScaleRect(regionDip, scale);
            // An 8-DIP frame makes the touching-edge inputs integral physical pixels at every
            // scale, so ToPixelBounds cannot turn a touching palette into a flipped one.
            RecordingControlLayout old = RecordingControlLayoutPlanner.Plan(region, work, scale, 8, 56, 480, shadowBandDip: 0);
            RecordingControlLayout banded = RecordingControlLayoutPlanner.Plan(region, work, scale, 8, 56, 480, shadowBandDip: RecordingControlWindow.ShadowBand);
            string what = $"recording planner {state}, {Percent(scale)}%";
            Assert.True(old.FrameBounds == banded.FrameBounds, $"{what}: frame changed from {old.FrameBounds} to {banded.FrameBounds}.");
            Assert.True(old.PaletteBounds == banded.PaletteBounds, $"{what}: palette changed from {old.PaletteBounds} to {banded.PaletteBounds}.");
            Assert.True(old.PaletteOverlapsRegion == banded.PaletteOverlapsRegion, $"{what}: palette overlap changed.");
            ContainsRect(banded.WindowBounds, banded.PaletteBounds, what + ": palette");
            RectD shadow = banded.PaletteBounds.Inflate(RecordingControlWindow.ShadowBand * scale);
            RectD clipped = IntersectRect(shadow, work);
            ContainsRect(banded.WindowBounds, clipped, what + ": allowed shadow band");
            if (state == "middle") ContainsRect(banded.WindowBounds, shadow, what + ": full shadow band");
            if (state == "palette at bottom")
            {
                CloseDip(banded.PaletteBounds.Bottom, work.Bottom, what + ": palette touches work-area bottom");
                CloseDip(banded.WindowBounds.Bottom, work.Bottom, what + ": bottom shadow is clipped");
            }
            if (state == "palette flipped above")
            {
                Assert.True(banded.PaletteBounds.Bottom <= banded.FrameBounds.Top, $"{what}: palette did not flip above the frame.");
                Assert.True(banded.WindowBounds.Bottom <= work.Bottom, $"{what}: window exceeds the work area's bottom.");
            }
            if (state == "palette at left")
            {
                CloseDip(banded.PaletteBounds.Left, work.Left, what + ": palette touches work-area left");
                CloseDip(banded.WindowBounds.Left, work.Left, what + ": left shadow is clipped");
            }
        }
    }

    [Fact]
    public Task RecordingWindowsKeepTheStripShadowInsideTheAllowedBand() => SettingsThemePreviewTests.RunIsolated(
        nameof(RecordingWindowsKeepTheStripShadowInsideTheAllowedBand), () => WithTheme(() =>
        {
            foreach (AppTheme theme in ShadowThemes)
            {
                ThemeService.Apply(theme);
                foreach (bool layered in new[] { true, false })
                {
                    using var mode = new ExclusionMode(layered);
                    foreach (string state in RecordingShadowStates)
                    {
                        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
                        RectD work = monitor.WorkArea;
                        double nativeScale = monitor.ScaleFactor;
                        var region = new RectD(work.Left + work.Width / 3, work.Top + work.Height / 4,
                            Math.Min(640, work.Width / 3), Math.Min(360, work.Height / 3));
                        RecordingControlWindow window = ShadowRecording(region, state);
                        try
                        {
                            double plannedLeft = window.Left, plannedTop = window.Top;
                            ShowShadowFixture(window);
                            Border palette = ShadowField<Border>(window, "_controlStrip");
                            Border frame = ShadowField<Border>(window, "_regionFrame");
                            foreach (double scale in Scales)
                            {
                                AtScale(window, scale);
                                string what = $"{theme}, recording {state}, {(layered ? "layered" : "opaque")}, {Percent(scale)}%";
                                Rect paletteRect = ElementRect(palette, window);
                                Rect frameRect = ElementRect(frame, window);
                                if (!layered)
                                {
                                    Rect union = Rect.Union(DeclaredCanvasRect(frame), DeclaredCanvasRect(palette));
                                    CloseDip(union.Left, 0, what + ": old union left");
                                    CloseDip(union.Top, 0, what + ": old union top");
                                    CloseDip(union.Right, window.Width, what + ": old union right");
                                    CloseDip(union.Bottom, window.Height, what + ": old union bottom");
                                    continue;
                                }
                                // Native layout happened at the monitor DPI before the HWND was moved offscreen.
                                // The raster scale below is independent; the palette's DIP shadow band is unchanged.
                                double leftRoom = plannedLeft + Canvas.GetLeft(palette) - work.Left / nativeScale;
                                double topRoom = plannedTop + Canvas.GetTop(palette) - work.Top / nativeScale;
                                double rightRoom = work.Right / nativeScale - plannedLeft - Canvas.GetLeft(palette) - palette.Width;
                                double bottomRoom = work.Bottom / nativeScale - plannedTop - Canvas.GetTop(palette) - palette.Height;
                                double band = RecordingControlWindow.ShadowBand;
                                Assert.True(Canvas.GetLeft(palette) + 1e-6 >= Math.Min(band, leftRoom), $"{what}: insufficient left shadow band.");
                                Assert.True(Canvas.GetTop(palette) + 1e-6 >= Math.Min(band, topRoom), $"{what}: insufficient top shadow band.");
                                Assert.True(window.Width - Canvas.GetLeft(palette) - palette.Width + 1e-6 >= Math.Min(band, rightRoom), $"{what}: insufficient right shadow band.");
                                Assert.True(window.Height - Canvas.GetTop(palette) - palette.Height + 1e-6 >= Math.Min(band, bottomRoom), $"{what}: insufficient bottom shadow band.");
                                RenderTargetBitmap image = Render(window, scale);
                                Save(image, $"recording-shadow-{theme}-{state}-{Percent(scale)}.png");
                                var pixels = new Pixels(image);
                                (int edge, double farthest) = RecordingShadowPixels(pixels, paletteRect, frameRect, scale, band);
                                ShadowMeasurement($"recording-shadow-{theme}-{state}-{Percent(scale)}.txt",
                                    FormattableString.Invariant($"{what}: band edge maximum alpha={edge}; farthest clickable shadow pixel center={farthest:F4} DIP (Euclidean distance from the palette; rows below the frame only)."));
                                Assert.True(edge <= BarelyVisibleShadowEdge,
                                    $"{what}: palette-band bottom/side edge has alpha {edge}, expected <= {BarelyVisibleShadowEdge}.");
                                Assert.True(farthest > 0, $"{what}: no clickable shadow pixel was rendered outside the palette.");
                            }
                        }
                        finally { window.CompleteAndClose(); }
                    }
                }
            }
        }), typeof(CaptureFlowPolishTests), hungAfter: TimeSpan.FromMinutes(5));

    // Isolated like the other WithTheme tests: its Application shuts down at the end, after which
    // this process could no longer create the windows of the tests that follow.
    [Fact]
    public Task RecordingShadowBandIsTheNarrowestWhoseEdgeIsBarelyVisible() => SettingsThemePreviewTests.RunIsolated(
        nameof(RecordingShadowBandIsTheNarrowestWhoseEdgeIsBarelyVisible), () => WithTheme(() =>
    {
        // A stand-in with the palette's silhouette and shadow (its pill, Surface.Floating, the
        // 1-DIP Border.Subtle edge and Shadow.Floating), in room wider than any band so that no
        // window edge cuts the shadow being measured. 216 and 560 DIP are the compact and full widths.
        const double Room = 48, Height = 56;
        double band = RecordingControlWindow.ShadowBand;
        string[] sides = ["top", "left", "bottom", "right"];
        int narrowerEdge = 0;
        var report = new System.Text.StringBuilder();
        foreach (AppTheme theme in ShadowThemes)
        {
            ThemeService.Apply(theme);
            foreach (double width in new[] { 216.0, 560.0 })
                foreach (double scale in Scales)
                {
                    var strip = new Border
                    {
                        Width = width,
                        Height = Height,
                        Background = (Brush)Application.Current.FindResource("Surface.Floating"),
                        BorderBrush = (Brush)Application.Current.FindResource("Border.Subtle"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(Height / 2),
                        Effect = (System.Windows.Media.Effects.Effect)Application.Current.FindResource("Shadow.Floating"),
                    };
                    var room = new Canvas { Width = width + (2 * Room), Height = Height + (2 * Room) };
                    Canvas.SetLeft(strip, Room);
                    Canvas.SetTop(strip, Room);
                    room.Children.Add(strip);
                    room.Measure(new Size(room.Width, room.Height));
                    room.Arrange(new Rect(0, 0, room.Width, room.Height));
                    room.UpdateLayout();
                    var pixels = new Pixels(Render(room, scale));
                    var palette = new Rect(Room, Room, width, Height);
                    int[] at = BandEdges(pixels, palette, band, scale);
                    int[] narrower = BandEdges(pixels, palette, band - 1, scale);
                    string narrowest = string.Join(", ", sides.Select((side, i) => FormattableString.Invariant(
                        $"{side} {Enumerable.Range(1, (int)Room - 1).First(b => BandEdges(pixels, palette, b, scale)[i] <= BarelyVisibleShadowEdge)}")));
                    string what = FormattableString.Invariant($"{theme}, {width:0}-DIP palette, {Percent(scale)}%");
                    report.AppendLine(FormattableString.Invariant(
                        $"{what}: edge alpha top/left/bottom/right at {band:0} DIP={string.Join('/', at)}, at {band - 1:0} DIP={string.Join('/', narrower)}; narrowest band per side with edge <= {BarelyVisibleShadowEdge}: {narrowest} DIP."));
                    Assert.True(at.Max() <= BarelyVisibleShadowEdge,
                        $"{what}: the {band}-DIP band ends at alpha {at.Max()}, expected <= {BarelyVisibleShadowEdge}.");
                    narrowerEdge = Math.Max(narrowerEdge, narrower.Max());
                }
        }
        ShadowMeasurement("recording-shadow-band.txt", report.ToString().TrimEnd());
        Assert.True(narrowerEdge > BarelyVisibleShadowEdge,
            $"A band 1 DIP narrower than {band} also ends at alpha {narrowerEdge}, at most {BarelyVisibleShadowEdge}: "
            + "the band takes more of the recorded app's clicks than it needs to.");
    }), typeof(CaptureFlowPolishTests));

    [Fact]
    public Task RecordingShadowLeavesTheRecordedRegionClickThrough() => SettingsThemePreviewTests.RunIsolated(
        nameof(RecordingShadowLeavesTheRecordedRegionClickThrough), () => WithTheme(() =>
        {
            using var mode = new ExclusionMode(true);
            MonitorInfo monitor = MonitorEnumerator.GetPrimary();
            RectD work = monitor.WorkArea;
            double width = Math.Round(Math.Min(640 * monitor.ScaleFactor, work.Width / 3));
            double height = Math.Round(Math.Min(360 * monitor.ScaleFactor, work.Height / 3));
            double left = Math.Round(work.Left + (work.Width / 3));
            double band = RecordingControlWindow.ShadowBand;
            var report = new System.Text.StringBuilder();
            // Every case renders and is measured; the failures are reported together at the end.
            var failures = new List<string>();
            void Expect(bool condition, string message) { if (!condition) failures.Add(message); }
            foreach ((string placement, RectD region) in new[]
            {
                ("below", new RectD(left, Math.Round(work.Top + (work.Height / 4)), width, height)),
                // No room below a region on the work area's bottom: the palette goes above it.
                ("above", new RectD(left, work.Bottom - height, width, height)),
                // No room around a full-screen region: the palette sits inside it.
                ("inside", work),
            })
                foreach (AppTheme theme in ShadowThemes)
                {
                    ThemeService.Apply(theme);
                    foreach (string state in RecordingShadowStates)
                    {
                        RecordingControlWindow window = ShadowRecording(region, state);
                        try
                        {
                            ShowShadowFixture(window);
                            Border palette = ShadowField<Border>(window, "_controlStrip");
                            Border frame = ShadowField<Border>(window, "_regionFrame");
                            foreach (double scale in Scales)
                            {
                                AtScale(window, scale);
                                string what = $"{theme}, recording {state}, palette {placement} the region, {Percent(scale)}%";
                                Rect paletteRect = ElementRect(palette, window);
                                Rect frameRect = ElementRect(frame, window);
                                // Until recording starts with the window excluded, the frame lies outside the region.
                                Rect regionRect = frameRect;
                                regionRect.Inflate(-frame.BorderThickness.Left, -frame.BorderThickness.Top);
                                Expect(placement switch
                                {
                                    "below" => paletteRect.Top >= frameRect.Bottom - 1e-6,
                                    "above" => paletteRect.Bottom <= frameRect.Top + 1e-6,
                                    _ => Rect.Intersect(paletteRect, regionRect) is { IsEmpty: false } inner
                                        && Math.Abs((inner.Width * inner.Height) - (paletteRect.Width * paletteRect.Height)) < 1e-3,
                                }, $"{what}: palette {paletteRect} is not {placement} the region {regionRect}.");
                                RenderTargetBitmap image = Render(window, scale);
                                Save(image, $"recording-region-{placement}-{theme}-{state}-{Percent(scale)}.png");
                                (int regionAlpha, int regionPixels, int shadow, double farthest, double reach) =
                                    RegionShadowPixels(new Pixels(image), paletteRect, frameRect, regionRect, scale);
                                report.AppendLine(FormattableString.Invariant(
                                    $"{what}: region pixels={regionPixels}, region maximum alpha={regionAlpha}; shadow pixels outside the region and frame={shadow}, farthest center={farthest:F2} DIP (Euclidean), {reach:F2} DIP (along an axis) from the palette."));
                                Expect(regionPixels > 0, $"{what}: no pixel lies wholly inside the region {regionRect}.");
                                Expect(regionAlpha == 0,
                                    $"{what}: a pixel over the recorded region has alpha {regionAlpha}; it takes a click meant for the recorded app.");
                                if (placement != "inside")
                                    Expect(shadow > 0, $"{what}: the palette lost its shadow outside the region.");
                                Expect(reach <= band + (1 / scale),
                                    $"{what}: a shadow pixel {reach:F2} DIP from the palette lies past its {band}-DIP band and takes clicks there.");
                            }
                        }
                        finally { window.CompleteAndClose(); }
                    }
                }
            ShadowMeasurement("recording-region-shadow.txt", report.ToString().TrimEnd());
            Assert.True(failures.Count == 0, $"{failures.Count} recording region cases failed:\n" + string.Join("\n", failures.Take(12)));
        }), typeof(CaptureFlowPolishTests), hungAfter: TimeSpan.FromMinutes(8));

    [Fact]
    public void QuickAccessShadowBandPreservesVisibleCardPlacement()
    {
        double maximum = 0;
        int compared = 0;
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (Point origin in new[] { new Point(0, 0), new Point(-1920, 0), new Point(-1920, -1040) })
            {
                var workDip = new RectD(origin.X, origin.Y, 1920, 1040);
                RectD work = ScaleRect(workDip, scale);
                var regions = new List<RectD?>
                {
                    null, work,
                    // A capture on the adjacent monitor exercises the final work-area clamp.
                    ScaleRect(new RectD(origin.X - 500, origin.Y - 300, 320, 180), scale),
                    ScaleRect(new RectD(origin.X + 1850, origin.Y + 1000, 640, 360), scale),
                };
                foreach (double x in new[] { 0d, 1, 40, 320, 960, 1599, 1879, 1919 })
                    foreach (double y in new[] { 0d, 1, 40, 200, 520, 839, 999, 1039 })
                        foreach (Size size in new[] { new Size(1, 1), new Size(160, 90), new Size(640, 360) })
                            regions.Add(ScaleRect(new RectD(origin.X + x, origin.Y + y,
                                Math.Min(size.Width, 1920 - x), Math.Min(size.Height, 1040 - y)), scale));
                foreach (RectD? region in regions)
                {
                    string what = $"card placement, {Percent(scale)}%, work {work}, region {region?.ToString() ?? "tray default"}";
                    Rect old = QuickAccessWindow.Position(work, scale, 344, 192, region, extraBand: 0);
                    Rect current = QuickAccessWindow.Position(work, scale, 352, 200, region);
                    Rect legacy = PreviousCardPosition(work, scale, 344, 192, region, out Point unclamped);
                    Assert.True(old == legacy, $"{what}: extraBand=0 changed the prior layered algorithm: {old} versus {legacy}.");
                    double dx = current.Left + 12 - old.Left - 8, dy = current.Top + 12 - old.Top - 8;
                    maximum = Math.Max(maximum, Math.Max(Math.Abs(dx), Math.Abs(dy)));
                    compared++;
                    Assert.True(Math.Abs(dx) <= 4 + 1e-9 && Math.Abs(dy) <= 4 + 1e-9,
                        $"{what}: visible card moved by ({dx:R}, {dy:R}) DIP, beyond the 4-DIP clamp allowance.");
                    bool noClamp = unclamped.X - 4 >= workDip.Left && unclamped.Y - 4 >= workDip.Top
                        && unclamped.X - 4 + 352 <= workDip.Right && unclamped.Y - 4 + 200 <= workDip.Bottom;
                    if (noClamp || region is null)
                    {
                        CloseDip(dx, 0, what + ": unclamped visible card x");
                        CloseDip(dy, 0, what + ": unclamped visible card y");
                    }
                    Rect opaque = QuickAccessWindow.Position(work, scale, 328, 200, region, extraBand: 0);
                    Rect oldOpaque = PreviousCardPosition(work, scale, 328, 200, region, out _);
                    Assert.True(opaque == oldOpaque, $"{what}: opaque placement changed from {oldOpaque} to {opaque}.");
                }
            }
        ShadowMeasurement("card-placement-maximum.txt", FormattableString.Invariant($"Compared {compared} region/monitor/scale cases; maximum visible-card move={maximum:F4} DIP per axis; unclamped and tray-default move=0 DIP."));
        Assert.True(maximum == 4, $"card placement sweep, 100/125/150/200%: boundary cases exercised {maximum:R} DIP, expected the exact 4-DIP clamp.");
    }

    [Fact]
    public Task RatingInvitationKeepsTheCardShadowInsideItsWindow() => SettingsThemePreviewTests.RunIsolated(
        nameof(RatingInvitationKeepsTheCardShadowInsideItsWindow), () => WithTheme(() =>
        {
            using var mode = new ExclusionMode(true);
            var sample = (BitmapSource)typeof(SettingsThemePreviewTests).GetMethod("FeedbackSample", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            foreach (AppTheme theme in ShadowThemes)
            {
                ThemeService.Apply(theme);
                foreach (double scale in Scales)
                {
                    var offscreen = new RectD(-12000, -12000, 1280, 720);
                    var monitor = new MonitorInfo("shadow-test", offscreen, offscreen, 96, true);
                    var card = new QuickAccessWindow(sample, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                        () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"),
                        excludeFromCapture: false, targetMonitor: monitor);
                    try
                    {
                        card.IsKept = true;
                        card.OfferRatingInvitation(() => Task.CompletedTask, () => { });
                        ShowShadowFixture(card);
                        string what = $"{theme}, rating invitation, {Percent(scale)}%";
                        Assert.True(card.RatingInvitationForTest.Row.IsVisible, $"{what}: rating invitation was not visible when shown.");
                        // Its production DPI handler otherwise selects a real monitor. The synthetic
                        // monitor still owns placement; only the render root's DPI changes while hidden.
                        card.Hide();
                        AtScale(card, scale);
                        card.Show();
                        ControlStateMotionTests.Drain(card);
                        Assert.True(card.IsVisible && card.Left < -1000 && card.Top < -1000,
                            $"{what}: capture card is not visible in the offscreen fixture.");
                        CloseDip(VisualTreeHelper.GetDpi(card).DpiScaleX, scale, what + ": rendered DPI");
                        var surface = card.Content as Border;
                        Assert.True(surface is not null, $"{what}: capture card has no surface border.");
                        Assert.True(surface.CornerRadius == ThemedShellPresenter.CardCorner,
                            $"{what}: surface corners {surface.CornerRadius} differ from card corners {ThemedShellPresenter.CardCorner}.");
                        Grid invitation = card.RatingInvitationForTest.Row;
                        Rect row = ElementRect(invitation, surface);
                        Assert.True(invitation.IsVisible && row.Width > 0 && row.Height > 0,
                            $"{what}: invitation has visibility {invitation.Visibility} and bounds {row}.");
                        Assert.True(row.Left >= 0 && row.Top >= 0 && row.Right <= surface.ActualWidth + 1e-6 && row.Bottom <= surface.ActualHeight + 1e-6,
                            $"{what}: invitation {row} leaves surface {surface.RenderSize}.");
                        RenderTargetBitmap image = Render(card, scale);
                        Save(image, $"card-rating-invite-{theme}-{Percent(scale)}.png");
                        var pixels = new Pixels(image);
                        int edge = pixels.EdgeAlpha();
                        Rect cardSurface = ElementRect(surface, card);
                        int interior = pixels.Alpha((int)((cardSurface.Left + cardSurface.Width / 2) * scale),
                            (int)((cardSurface.Top + 16) * scale));
                        ShadowMeasurement($"card-rating-invite-{theme}-{Percent(scale)}.txt", $"{what}: outermost ring maximum alpha={edge}; invitation bounds={row}.");
                        Assert.True(interior == 255, $"{what}: card interior alpha is {interior}; expected an opaque rendered surface.");
                        Assert.True(edge <= 1, $"{what}: outermost window ring alpha is {edge}, expected <= 1.");
                    }
                    finally { card.CloseNow(); }
                }
            }
        }), typeof(CaptureFlowPolishTests));

    [Fact]
    public Task CaptureReceiptTextSharesTheHintLeftEdge() => SettingsThemePreviewTests.RunIsolated(
        nameof(CaptureReceiptTextSharesTheHintLeftEdge), () => WithTheme(() =>
        {
            using var mode = new ExclusionMode(true);
            using var presenter = new ThemedShellPresenter(() => false);
            foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                foreach (string language in new[] { "ko-KR", "en" })
                {
                    using var culture = UiText.UseLanguage(language);
                    string message = language == "ko-KR" ? "캡처가 저장되었습니다." : "Capture saved.";
                    string tip = language == "ko-KR" ? "필요한 작업을 선택하십시오." : "Choose the next action.";
                    foreach (string state in new[] { "pin-tip", "location-hint", "no-tip" })
                    {
                        Window window = presenter.CreateCaptureReceipt(message, () => { }, state == "no-tip" ? null : tip,
                            pin: state == "pin-tip" ? () => { } : null);
                        try
                        {
                            ShowShadowFixture(window);
                            string what = $"{theme}, receipt {state}, {language}, 100%";
                            Button title = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Open");
                            var content = title.Parent as StackPanel;
                            Assert.True(content is not null, $"{what}: receipt content panel is missing.");
                            TextBlock hint = content.Children.OfType<TextBlock>().Last();
                            TextBlock titleText = Descendants(title).OfType<TextBlock>().Single();
                            double hintLeft = hint.TransformToAncestor(window).Transform(default(Point)).X;
                            double titleLeft = titleText.TransformToAncestor(window).Transform(default(Point)).X;
                            Thickness toolBorder = (Thickness)window.FindResource("Border.ToolThickness");
                            double offset = theme == AppTheme.HighContrast ? toolBorder.Left : 0;
                            CloseDip(offset, theme == AppTheme.HighContrast ? 1 : 0, what + ": tool border");
                            CloseDip(titleLeft - hintLeft, offset, what + ": title text versus hint", theme == AppTheme.HighContrast ? 1e-6 : 0.5);
                            double? tipLeft = null;
                            if (state == "pin-tip")
                            {
                                Button pin = content.Children.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Pin");
                                TextBlock pinText = Descendants(pin).OfType<TextBlock>().Single();
                                tipLeft = pinText.TransformToAncestor(window).Transform(default(Point)).X;
                                CloseDip(tipLeft.Value - hintLeft, offset, what + ": pin text versus hint", theme == AppTheme.HighContrast ? 1e-6 : 0.5);
                            }
                            else if (state == "location-hint")
                            {
                                TextBlock location = content.Children.OfType<TextBlock>().First();
                                tipLeft = location.TransformToAncestor(window).Transform(default(Point)).X;
                                CloseDip(tipLeft.Value, hintLeft, what + ": location hint versus keyboard hint", 0.5);
                            }
                            CloseDip(window.Width - 2 * ThemedShellPresenter.ShadowMargin, state == "no-tip" ? 320 : 360, what + ": content width");
                            ShadowMeasurement($"receipt-align-{theme}-{language}-{state}.txt",
                                FormattableString.Invariant($"{what}: title left={titleLeft:F4}; tip left={tipLeft:F4}; hint left={hintLeft:F4}; button border offset={offset:F4} DIP."));
                        }
                        finally { window.Close(); }
                    }
                }
            }
        }), typeof(CaptureFlowPolishTests));

    private static ContextMenu ShadowTray(ThemedShellPresenter presenter, AppTheme theme)
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false);
        return presenter.CreateMenu(TrayMenuLayout.Build(NoCommands(), NewInstallDefaults.Create().Hotkeys,
            new TrayMenuState(ClipboardHasContent: false)), resources);
    }

    private static void LayoutShadowMenu(ContextMenu menu, double scale)
    {
        VisualTreeHelper.SetRootDpi(menu, new DpiScale(scale, scale));
        LayOut(menu);
    }

    private static bool Near(Color actual, Color expected) =>
        actual.A == expected.A && Math.Abs(actual.R - expected.R) <= 8 && Math.Abs(actual.G - expected.G) <= 8
        && Math.Abs(actual.B - expected.B) <= 8;

    private static void ScreenPointNear(Point actual, Point expected, string what) =>
        Assert.True(Math.Abs(actual.X - expected.X) <= 1 && Math.Abs(actual.Y - expected.Y) <= 1,
            $"{what}: actual {actual}, expected {expected}; tolerance 1 physical pixel per axis.");

    private static void CloseDip(double actual, double expected, string what, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: actual {actual:R}, expected {expected:R}, tolerance {tolerance:R}.");

    private static RectD ScaleRect(RectD value, double scale) => new(value.Left * scale, value.Top * scale, value.Width * scale, value.Height * scale);

    private static RectD IntersectRect(RectD first, RectD second) => new(Math.Max(first.Left, second.Left), Math.Max(first.Top, second.Top),
        Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left), Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));

    private static void ContainsRect(RectD outer, RectD inner, string what) => Assert.True(
        outer.Left <= inner.Left + 1e-6 && outer.Top <= inner.Top + 1e-6 && outer.Right + 1e-6 >= inner.Right && outer.Bottom + 1e-6 >= inner.Bottom,
        $"{what}: window {outer} does not contain {inner}.");

    private static Rect ElementRect(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static Rect DeclaredCanvasRect(FrameworkElement element) =>
        new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    private static T ShadowField<T>(object instance, string name) => (T)instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance)!;

    private static RecordingControlWindow ShadowRecording(RectD region, string state)
    {
        var window = new RecordingControlWindow(region, new RecordingSettings(),
            () => throw new InvalidOperationException($"Recording {state} shadow fixture must never start a recorder."),
            () => throw new InvalidOperationException($"Recording {state} shadow fixture must never create a recording."), NullLogger.Instance)
        { ReadPauseHotkey = () => Hotkey.None };
        if (state is "recording" or "paused" or "saving") window.PreviewRecordingForTest(state == "paused", TimeSpan.FromSeconds(63));
        if (state == "saving")
        {
            typeof(RecordingControlWindow).GetField("_stopping", PrivateInstance)!.SetValue(window, true);
            typeof(RecordingControlWindow).GetField("_finished", PrivateInstance)!.SetValue(window, true);
            window.PauseButtonForTest.IsEnabled = false;
            window.ShowCompletionStatus(UiText.Get("Text_528544494302"));
        }
        if (state == "saved") window.PreviewSavedForTest(TimeSpan.FromSeconds(63));
        foreach ((string eventName, string methodName) in new[] { ("SourceInitialized", "OnSourceInitialized"), ("ContentRendered", "OnContentRendered"), ("DpiChanged", "OnDpiChanged") })
        {
            EventInfo evt = typeof(Window).GetEvent(eventName)!;
            MethodInfo method = typeof(RecordingControlWindow).GetMethod(methodName, PrivateInstance | BindingFlags.DeclaredOnly)!;
            evt.RemoveEventHandler(window, method.CreateDelegate(evt.EventHandlerType!, window));
        }
        return window;
    }

    private static void ShowShadowFixture(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        FluidMotion.SetWindowEntrance(window, false);
        TestDpiContext.Initialize96DpiWindow(window);
        window.Show();
        ControlStateMotionTests.Drain(window);
    }

    private static (int Edge, double Farthest) RecordingShadowPixels(Pixels pixels, Rect palette, Rect frame, double scale, double band)
    {
        int left = Math.Max(0, (int)Math.Floor((palette.Left - band) * scale));
        int right = Math.Min(pixels.Width - 1, (int)Math.Ceiling((palette.Right + band) * scale) - 1);
        int bottom = Math.Min(pixels.Height - 1, (int)Math.Ceiling((palette.Bottom + band) * scale) - 1);
        int belowFrame = Math.Max(0, (int)Math.Ceiling(frame.Bottom * scale));
        int edge = 0;
        for (int x = left; x <= right; x++) edge = Math.Max(edge, pixels.Alpha(x, bottom));
        for (int y = belowFrame; y <= bottom; y++) edge = Math.Max(edge, Math.Max(pixels.Alpha(left, y), pixels.Alpha(right, y)));
        double farthest = 0;
        for (int y = belowFrame; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
            {
                if (pixels.Alpha(x, y) < 1) continue;
                double px = (x + 0.5) / scale, py = (y + 0.5) / scale;
                double dx = Math.Max(Math.Max(palette.Left - px, px - palette.Right), 0);
                double dy = Math.Max(Math.Max(palette.Top - py, py - palette.Bottom), 0);
                farthest = Math.Max(farthest, Math.Sqrt(dx * dx + dy * dy));
            }
        return (edge, farthest);
    }

    /// <summary>The highest alpha on each side (top, left, bottom, right) of the outermost pixels of a band around the palette.</summary>
    private static int[] BandEdges(Pixels pixels, Rect palette, double band, double scale)
    {
        int left = (int)Math.Round((palette.Left - band) * scale), top = (int)Math.Round((palette.Top - band) * scale);
        int right = (int)Math.Round((palette.Right + band) * scale) - 1, bottom = (int)Math.Round((palette.Bottom + band) * scale) - 1;
        int[] edges = new int[4];
        for (int x = left; x <= right; x++)
        {
            edges[0] = Math.Max(edges[0], pixels.Alpha(x, top));
            edges[2] = Math.Max(edges[2], pixels.Alpha(x, bottom));
        }
        for (int y = top; y <= bottom; y++)
        {
            edges[1] = Math.Max(edges[1], pixels.Alpha(left, y));
            edges[3] = Math.Max(edges[3], pixels.Alpha(right, y));
        }
        return edges;
    }

    /// <summary>
    /// Over the region, the highest alpha of the pixels wholly inside it and their number, leaving
    /// out the palette's own pixels. Outside the region and its frame, the number of shadow pixels
    /// (alpha 1 or more) and how far the farthest of their centers lies from the palette: straight
    /// (Euclidean) and along one axis, as the band is measured.
    /// </summary>
    private static (int RegionAlpha, int RegionPixels, int Shadow, double Farthest, double Reach) RegionShadowPixels(
        Pixels pixels, Rect palette, Rect frame, Rect region, double scale)
    {
        Rect pill = palette;
        pill.Inflate(1 / scale, 1 / scale);
        int regionAlpha = 0, regionPixels = 0, shadow = 0;
        double farthest = 0, reach = 0;
        for (int y = 0; y < pixels.Height; y++)
            for (int x = 0; x < pixels.Width; x++)
            {
                var footprint = new Rect(x / scale, y / scale, 1 / scale, 1 / scale);
                if (footprint.IntersectsWith(pill)) continue;
                int alpha = pixels.Alpha(x, y);
                if (region.Contains(footprint))
                {
                    regionPixels++;
                    regionAlpha = Math.Max(regionAlpha, alpha);
                    continue;
                }
                if (alpha < 1 || footprint.IntersectsWith(frame)) continue;
                shadow++;
                double px = (x + 0.5) / scale, py = (y + 0.5) / scale;
                double dx = Math.Max(Math.Max(palette.Left - px, px - palette.Right), 0);
                double dy = Math.Max(Math.Max(palette.Top - py, py - palette.Bottom), 0);
                farthest = Math.Max(farthest, Math.Sqrt((dx * dx) + (dy * dy)));
                reach = Math.Max(reach, Math.Max(dx, dy));
            }
        return (regionAlpha, regionPixels, shadow, farthest, reach);
    }

    // Frozen pre-band algorithm: this checks the explicit extraBand:0 compatibility path too.
    private static Rect PreviousCardPosition(RectD work, double scale, double width, double height, RectD? capturedRegion, out Point unclamped)
    {
        double left = work.Left / scale, top = work.Top / scale, right = work.Right / scale, bottom = work.Bottom / scale;
        width = Math.Min(width, right - left);
        height = Math.Min(height, bottom - top);
        double x = right - width - 12, y = bottom - height - 12;
        if (capturedRegion is { } region && (region.Width < work.Width || region.Height < work.Height))
        {
            x = region.Right / scale + 8;
            y = region.Bottom / scale + 8;
            if (x + width > right - 4) x = region.Right / scale - width;
            if (y + height > bottom - 4) y = region.Bottom / scale - height - 8;
        }
        unclamped = new Point(x, y);
        return new Rect(Math.Clamp(x, left, Math.Max(left, right - width)), Math.Clamp(y, top, Math.Max(top, bottom - height)), width, height);
    }

    private static void ShadowMeasurement(string name, string text)
    {
        string? folder = Environment.GetEnvironmentVariable(EvidenceGate);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Assert.True(Path.IsPathFullyQualified(folder), $"Shadow measurements for {name}: evidence output must be absolute.");
        File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(folder), name), text + Environment.NewLine);
    }
}
