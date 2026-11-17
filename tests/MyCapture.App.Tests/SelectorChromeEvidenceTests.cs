using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in evidence of the restyled selector chrome (mode bar, hint, read-out, start-point chip, hover
/// tab, magnifier and the stamped chips), in a dark and a light theme, at 100 % and 150 %, with
/// Windows 11's rounded DWM frame emulated and with Windows 10's square one. Set
/// MYCAPTURE_VISUAL_EVIDENCE_DIR to a folder below the repository's artifacts folder.
/// </summary>
public sealed class SelectorChromeEvidenceTests
{
    private const string Gate = "MYCAPTURE_VISUAL_EVIDENCE_DIR";

    [Fact]
    public async Task RenderRestyledSelectorChrome()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Gate))) return;
        // The chrome reads the UI font, accent and theme from Application resources: a fresh process
        // keeps that Application away from the other WPF tests in this assembly.
        await SettingsThemePreviewTests.RunIsolated(nameof(RenderRestyledSelectorChrome), RenderAll,
            typeof(SelectorChromeEvidenceTests), hungAfter: TimeSpan.FromMinutes(5));
    }

    private static void RenderAll()
    {
        string run = TestDiagnosticArtifacts.Create(Gate, "selector-chrome")!;
        using var language = UiText.UseLanguage("ko-KR");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        AppTheme previousTheme = ThemeService.Current;
        bool? highContrast = ThemeService.SystemHighContrastOverrideForTest;
        bool? rounding = NativeSelectorChrome.DwmRoundingOverrideForTest;
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        ThemeService.SystemHighContrastOverrideForTest = false;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Daylight })
            {
                ThemeService.Apply(theme);
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    foreach (bool rounded in new[] { true, false })
                    {
                        NativeSelectorChrome.DwmRoundingOverrideForTest = rounded;
                        string tag = $"{theme.ToString().ToLowerInvariant()}-{scale * 100:0}-{(rounded ? "win11" : "win10")}";
                        Drag(run, tag, scale, color: false);
                        Drag(run, tag, scale, color: true);
                        Hover(run, tag, scale);
                    }
                    NativeSelectorChrome.DwmRoundingOverrideForTest = false;
                    Stamped(run, $"{theme.ToString().ToLowerInvariant()}-{scale * 100:0}", scale);
                }
            }
        }
        finally
        {
            NativeSelectorChrome.DwmRoundingOverrideForTest = rounding;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            ThemeService.SystemHighContrastOverrideForTest = highContrast;
            ThemeService.Apply(previousTheme);
            app.Shutdown();
        }
    }

    // A drag from (300, 250) to (620, 430): the mode bar and hint, the read-out, the start-point chip
    // and the magnifier. In the colour mode the hint names two keys and the magnifier names the colour.
    private static void Drag(string run, string tag, double scale, bool color)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        MonitorInfo[] monitors = [Monitor(bounds, scale)];
        PointD start = new(300, 250), end = new(620, 430), pointer = start;
        using var owner = new NativeCaptureOverlay(bounds, false, true)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            CopyColorText = _ => Task.FromResult(true), TextCaptureAvailable = true,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        string name = $"{tag}-{(color ? "color" : "drag")}";
        if (color)
        {
            owner.SelectMode(CaptureSelectorMode.Color);
            pointer = new PointD(700, 330);
            owner.UpdatePointer(false, false);
            owner.FlushFeedbackForTest();
            chrome.Update(pointer, Int32Rect.Empty);
        }
        else
        {
            owner.UpdatePointer(false, false);
            owner.BeginSelection(start);
            pointer = end;
            owner.UpdatePointer(false, false);
            owner.MoveSelection(end);
            owner.FlushFeedbackForTest();
            chrome.Update(pointer, owner.SelectionRectangle);
        }
        BitmapSource scene = Render(owner, chrome);
        TestDiagnosticArtifacts.SavePng(run, name + ".png", scene);
        RectD bar = chrome.BoundsForTest(chrome.Windows[0]), hint = chrome.BoundsForTest(chrome.Windows[2]);
        Crop(run, name + "-bar-hint", scene, Union(bar, hint), 12 * scale);
        Crop(run, name + "-magnifier", scene, chrome.BoundsForTest(chrome.Windows[3]), 10 * scale);
        if (!color && chrome.AnchorChipBoundsForTest is { } anchor)
        {
            var chip = new RectD(anchor.X * scale, anchor.Y * scale, anchor.Width * scale, anchor.Height * scale);
            Crop(run, name + "-readout-anchor", scene, Union(Union(chrome.SizeChipBoundsForTest, chip),
                new RectD(start.X - 4, start.Y - 4, 40, 40)), 14 * scale);
        }
    }

    // The pointer over the browser window: the hover tab above its frame.
    private static void Hover(string run, string tag, double scale)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        MonitorInfo[] monitors = [Monitor(bounds, scale)];
        PointD pointer = new(900, 420);
        using var owner = new NativeCaptureOverlay(bounds, false, false, showSelectionGrid: false)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            ReadClock = () => TimeSpan.Zero, DetectWindows = true,
            SourceWindows = [CaptureCueEvidenceTests.Browser, CaptureCueEvidenceTests.Notes],
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, false, false);
        owner.PreparePointerFeedback();
        owner.PrepareVisualFeedback();
        owner.UpdatePointer(false, false);
        owner.RefreshHover();
        owner.FlushFeedbackForTest();
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        chrome.FlushHoverTabForTest();
        BitmapSource scene = Render(owner, chrome);
        string name = tag + "-hover";
        TestDiagnosticArtifacts.SavePng(run, name + ".png", scene);
        RectD browser = CaptureCueEvidenceTests.Browser.ScreenBounds;
        Crop(run, name + "-tab", scene, Union(chrome.SizeChipBoundsForTest,
            new RectD(browser.Left, browser.Top, 260, 40)), 14 * scale);
    }

    // The stamped read-out and hint of the bitmap-only strategy, which never get a DWM frame.
    private static void Stamped(string run, string tag, double scale)
    {
        FrozenFrame frame = CaptureCueEvidenceTests.Desktop();
        RectD bounds = frame.ScreenBounds;
        MonitorInfo[] monitors = [Monitor(bounds, scale)];
        PointD start = new(300, 250), end = new(620, 430), pointer = start;
        using var owner = new NativeCaptureOverlay(bounds, false, false)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            ChromeStrategy = NativeSelectorChromeStrategy.Dib,
        };
        owner.AttachFrame(frame);
        using var chrome = new NativeSelectorChrome(owner, false, false);
        owner.UpdatePointer(false, false);
        owner.BeginSelection(start);
        pointer = end;
        owner.UpdatePointer(false, false);
        owner.MoveSelection(end);
        owner.FlushFeedbackForTest();
        chrome.Update(pointer, owner.SelectionRectangle);
        BitmapSource scene = Render(owner, chrome);
        string name = tag + "-stamped";
        TestDiagnosticArtifacts.SavePng(run, name + ".png", scene);
        Crop(run, name + "-readout", scene, new RectD(end.X - 8, end.Y - 8, 220 * scale, 80 * scale), 0);
        Crop(run, name + "-bar-hint", scene, new RectD(bounds.Width / 2 - 340 * scale, 0, 680 * scale, 120 * scale), 0);
    }

    private static MonitorInfo Monitor(RectD bounds, double scale) =>
        new("evidence", bounds, new RectD(bounds.Left, bounds.Top, bounds.Width, 672), (uint)Math.Round(96 * scale), true);

    /// <summary>
    /// The selector's own pixels with each chrome window over them. A window that DWM rounds is
    /// clipped to its corner radius and gets DWM's one-pixel edge in the chip border colour, as
    /// Windows 11 composes it; a square window is drawn as is.
    /// </summary>
    private static BitmapSource Render(NativeCaptureOverlay owner, NativeSelectorChrome chrome)
    {
        NativeSelectorDib dib = Assert.IsType<NativeSelectorDib>(owner.DisplayDib);
        using var painted = new NativeSelectorDib(owner.Frame!.Bitmap, new SolidColorBrush(Colors.Transparent));
        owner.PaintTo(painted.Dc);
        Assert.True(NativeSelectorInterop.GdiFlush());
        var pixels = new byte[dib.ByteLength];
        System.Runtime.InteropServices.Marshal.Copy(painted.Bits, pixels, 0, pixels.Length);
        BitmapSource desktop = BitmapSource.Create(dib.Width, dib.Height, 96, 96, PixelFormats.Bgr32, null, pixels, dib.Stride);
        var border = (SolidColorBrush)CaptureGuidePalette.Resolve()["CaptureGuide.ChipBorder"];
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(desktop, new Rect(0, 0, dib.Width, dib.Height));
            foreach (Window window in chrome.Windows)
            {
                DwmCornerPreference? corners = chrome.CornersForTest(window);
                if (corners == DwmCornerPreference.RoundSmall && !chrome.IsReadoutVisibleForTest) continue;
                Size logical = chrome.LogicalSizeForTest(window);
                if (logical.Width <= 2 || logical.Height <= 2) continue;
                FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                RectD bounds = chrome.BoundsForTest(window);
                if (bounds.Right <= owner.ScreenBounds.Left) continue;
                double scale = chrome.ScaleForTest(window);
                double left = bounds.Left - owner.ScreenBounds.Left, top = bounds.Top - owner.ScreenBounds.Top;
                DpiScale previousDpi = VisualTreeHelper.GetDpi(content);
                Transform previousTransform = content.LayoutTransform;
                window.Content = null;
                try
                {
                    content.LayoutTransform = Transform.Identity;
                    VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                    content.Measure(logical);
                    content.Arrange(new Rect(logical));
                    content.UpdateLayout();
                    int width = Math.Max(1, (int)Math.Ceiling(bounds.Width)), height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                    var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    if (chrome.MagnifierForTest is { } magnifier && content is Border { Child: Image })
                    {
                        var overlay = new DrawingVisual();
                        using (DrawingContext overlayContext = overlay.RenderOpen())
                        {
                            overlayContext.PushTransform(new TranslateTransform(magnifier.ImageOffsetForTest.X, magnifier.ImageOffsetForTest.Y));
                            overlayContext.DrawDrawing(magnifier.DrawingForTest);
                            overlayContext.Pop();
                        }
                        bitmap.Render(overlay);
                    }
                    var outer = new Rect(left, top, width, height);
                    double radius = corners switch
                    {
                        DwmCornerPreference.Round => 8 * scale,
                        DwmCornerPreference.RoundSmall => 4 * scale,
                        _ => 0,
                    };
                    bool rounded = chrome.IsRoundedForTest(window) && radius > 0;
                    if (rounded) dc.PushClip(new RectangleGeometry(outer, radius, radius));
                    dc.DrawRectangle(window.Background, null, outer);
                    dc.DrawImage(bitmap, outer);
                    if (rounded)
                    {
                        dc.Pop();
                        dc.DrawRoundedRectangle(null, new Pen(border, 1), Rect.Inflate(outer, -0.5, -0.5), radius - 0.5, radius - 0.5);
                    }
                }
                finally
                {
                    VisualTreeHelper.SetRootDpi(content, previousDpi);
                    content.LayoutTransform = previousTransform;
                    window.Content = content;
                }
            }
        }
        var rendered = new RenderTargetBitmap(dib.Width, dib.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static RectD Union(RectD first, RectD second)
    {
        if (first.IsEmpty) return second;
        if (second.IsEmpty) return first;
        double left = Math.Min(first.Left, second.Left), top = Math.Min(first.Top, second.Top);
        return new RectD(left, top, Math.Max(first.Right, second.Right) - left, Math.Max(first.Bottom, second.Bottom) - top);
    }

    // A detail, enlarged three times without smoothing so single pixels stay visible.
    private static void Crop(string run, string name, BitmapSource scene, RectD area, double padding)
    {
        int left = Math.Clamp((int)Math.Floor(area.Left - padding), 0, scene.PixelWidth - 1);
        int top = Math.Clamp((int)Math.Floor(area.Top - padding), 0, scene.PixelHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling(area.Right + padding), left + 1, scene.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(area.Bottom + padding), top + 1, scene.PixelHeight);
        var cropped = new FormatConvertedBitmap(new CroppedBitmap(scene, new Int32Rect(left, top, right - left, bottom - top)),
            PixelFormats.Pbgra32, null, 0);
        int width = cropped.PixelWidth, height = cropped.PixelHeight;
        var source = new byte[width * height * 4];
        cropped.CopyPixels(source, width * 4, 0);
        var enlarged = new byte[source.Length * 9];
        for (int y = 0; y < height * 3; y++)
        for (int x = 0; x < width * 3; x++)
            Buffer.BlockCopy(source, ((y / 3) * width + x / 3) * 4, enlarged, (y * width * 3 + x) * 4, 4);
        BitmapSource target = BitmapSource.Create(width * 3, height * 3, 96, 96, PixelFormats.Pbgra32, null, enlarged, width * 12);
        TestDiagnosticArtifacts.SavePng(run, name + ".png", target);
    }
}
