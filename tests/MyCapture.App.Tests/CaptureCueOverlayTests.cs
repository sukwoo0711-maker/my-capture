using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// What the selector shows over a detected window, over empty desktop and during a drag, and that
/// none of it reaches the captured pixels.
/// </summary>
public sealed class CaptureCueOverlayTests
{
    // Default neutral white guide and its contrasting black keyline, as BGRA.
    private static readonly byte[] Accent = [0xFF, 0xFF, 0xFF, 0xFF];
    private static readonly byte[] Keyline = [0x00, 0x00, 0x00, 0xFF];

    [Fact]
    public void HoverPressDragAndDesktopEachLightOnlyTheirOwnCue() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        PointD pointer = new(300, 300);
        CaptureOverlayView view = GlassView(frame, () => pointer);
        try
        {
            // Hovering a window: the tab names the click, the click part of the instruction is lit.
            view.InitializePointer();
            RenderTargetBitmap hover = Render(view);
            Assert.Equal(CaptureCueState.Click, view.CueState);
            Assert.Equal(UiText.Get("CaptureGuide2_ClickWindow") + "  620 × 420", view.TargetLabelForTest);
            Assert.Equal(CaptureCueTabPlacement.Above, view.TargetTabPlacementForTest);
            Rect tab = view.TargetTabForTest!.Value;
            Assert.Equal(120, tab.Left, 6);
            Assert.Equal(90, tab.Bottom, 6);
            Assert.DoesNotContain(view.ChromeBoundsForTest, chrome => CaptureCueLayout.Overlaps(chrome, tab));
            Rect instruction = view.ChromeBoundsForTest[0];
            Rect click = view.ActiveCueSegmentForTest!.Value;
            Assert.True(instruction.Contains(click));
            AssertPixel(hover, (int)click.Left + 3, (int)(click.Top + (click.Height / 2)), Accent);
            AssertPixel(hover, (int)tab.Left + 3, (int)tab.Bottom - 3, Accent);

            // A press inside the click threshold still means "capture this window".
            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(302, 301);
            Move(view);
            Render(view);
            Assert.Equal(CaptureCueState.Click, view.CueState);
            Assert.Equal(tab, view.TargetTabForTest);
            Assert.Equal(click, view.ActiveCueSegmentForTest);

            // Past the threshold it is a free-region drag: no tab, the drag part is lit, nothing moves.
            pointer = new PointD(420, 380);
            Move(view);
            RenderTargetBitmap drag = Render(view);
            Assert.Equal(CaptureCueState.Drag, view.CueState);
            Assert.Null(view.TargetTabForTest);
            Assert.Null(view.TargetLabelForTest);
            Assert.Equal(new RectD(300, 300, 120, 80), view.Selection);
            Rect dragPart = view.ActiveCueSegmentForTest!.Value;
            Assert.True(dragPart.Right <= click.Left);
            Assert.Equal(click.Top, dragPart.Top);
            Assert.Equal(instruction, view.ChromeBoundsForTest[0]);
            AssertPixel(drag, (int)dragPart.Left + 3, (int)(dragPart.Top + (dragPart.Height / 2)), Accent);
            Assert.NotEqual(Accent, Pixel(drag, (int)click.Left + 3, (int)(click.Top + (click.Height / 2))));
        }
        finally { view.ReleaseResources(); }

        // Empty desktop: the whole monitor, its tab inside that monitor.
        pointer = new PointD(1000, 120);
        CaptureOverlayView desktop = GlassView(frame, () => pointer);
        try
        {
            desktop.InitializePointer();
            Render(desktop);
            Assert.True(desktop.HoverWholeMonitorForTest);
            Assert.Equal(CaptureCueState.Click, desktop.CueState);
            Assert.Equal(UiText.Get("CaptureGuide2_ClickMonitor") + "  1280 × 720", desktop.TargetLabelForTest);
            Assert.Equal(CaptureCueTabPlacement.InsideTop, desktop.TargetTabPlacementForTest);
            Rect monitorTab = desktop.TargetTabForTest!.Value;
            Assert.Equal(new Point(0, 0), monitorTab.TopLeft);
            Assert.True(new Rect(0, 0, Width, Height).Contains(monitorTab));
        }
        finally { desktop.ReleaseResources(); }
    });

    [Fact]
    public void EnglishCuesNameTheWindowAndTheMonitor() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        PointD pointer = new(300, 300);
        CaptureOverlayView view = GlassView(Desktop(), () => pointer);
        try
        {
            view.InitializePointer();
            Render(view);
            Assert.Equal("Click: Capture window  620 × 420", view.TargetLabelForTest);
            pointer = new PointD(1000, 120);
            view.UpdatePointer(false, false);
            Render(view);
            Assert.Equal("Click: Capture monitor  1280 × 720", view.TargetLabelForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void DisabledDetectionKeepsTheDragOnlyInstructionWithoutCues() => StaTestHost.Run(() =>
    {
        PointD pointer = new(300, 300);
        CaptureOverlayView view = GlassView(Desktop(), () => pointer);
        view.DetectWindows = false;
        try
        {
            view.InitializePointer();
            Render(view);
            Assert.Equal(CaptureCueState.None, view.CueState);
            Assert.Null(view.TargetTabForTest);
            Assert.Null(view.ActiveCueSegmentForTest);
            Assert.Null(view.HoverRegionForTest);
            Assert.DoesNotContain("창 선택", CaptureOverlayView.InstructionText, StringComparison.Ordinal);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void TheBarRevealsKeysOnlyDuringAltAndKeepsTooltipsAndAcceleratorKeys() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        CaptureOverlayView shown = EvidenceView(frame, AppTheme.Glass, "window-hover");
        CaptureOverlayView hidden = EvidenceView(frame, AppTheme.Glass, "window-hover");
        Assert.False(shown.ShowShortcutHints);
        hidden.ShowShortcutHints = false;
        try
        {
            Render(shown);
            Render(hidden);
            // The default and an explicitly disabled preference show no key text at rest; Alt reveals the same keys
            // while tooltips and accelerator metadata remain available in every state.
            foreach (CaptureOverlayView view in new[] { shown, hidden })
            {
                CaptureModeBar bar = view.ModeBarForTest!;
                var captions = Descendants(bar).OfType<System.Windows.Controls.TextBlock>().ToArray();
                Assert.Equal(new[] { "T", "C", "Esc" }, captions.Select(text => text.Text));
                Size restingSize = bar.RenderSize;
                FrameworkElement[] commands = [bar.TextButton, bar.ColorButton, bar.CancelButton];
                Size[] restingCommands = commands.Select(command => command.RenderSize).ToArray();
                foreach (bool revealed in new[] { false, true, false })
                {
                    ShortcutPresentation.SetAltRevealed(view, revealed);
                    view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    Render(view);
                    Assert.All(captions, caption => Assert.Equal(revealed ? Visibility.Visible : Visibility.Collapsed, caption.Visibility));
                    string[] visibleText = captions.Where(text => text.Visibility == Visibility.Visible).Select(text => text.Text).ToArray();
                    if (revealed) Assert.Equal(new[] { "T", "C", "Esc" }, visibleText);
                    else Assert.Empty(visibleText);
                    Assert.Equal(restingSize, bar.RenderSize);
                    Assert.Equal(restingCommands, commands.Select(command => command.RenderSize));
                    Assert.All(commands, command => Assert.Equal(36, command.ActualHeight));
                    if (!revealed) continue;
                    for (int index = 0; index < captions.Length; index++)
                    {
                        Rect bounds = captions[index].TransformToAncestor(commands[index]).TransformBounds(new Rect(captions[index].RenderSize));
                        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= commands[index].ActualWidth + 0.5
                            && bounds.Bottom <= commands[index].ActualHeight + 0.5, $"Shortcut caption leaves its button: {bounds}");
                    }
                }
                Assert.Equal($"{UiText.Get("CaptureGuide2_Text")}\n{UiText.Get("CaptureGuide2_TextHelp")} (T)", (string)bar.TextButton.ToolTip);
                Assert.Equal($"{UiText.Get("CaptureGuide2_Cancel")}\n{UiText.Get("CaptureGuide2_CancelTip")} (Esc)", (string)bar.CancelButton.ToolTip);
                Assert.Equal("T", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(bar.TextButton));
                Assert.Equal(UiText.Get("CaptureGuide2_Text"),
                    System.Windows.Automation.AutomationProperties.GetName(bar.TextButton));
                Assert.EndsWith("(T)", System.Windows.Automation.AutomationProperties.GetHelpText(bar.TextButton), StringComparison.Ordinal);
            }

            // The bar alone: no status chip under it, and the setting changes nothing about its size.
            Assert.Single(shown.ChromeBoundsForTest);
            Assert.Equal(shown.ChromeBoundsForTest[0], hidden.ChromeBoundsForTest[0]);

            // State cues and button names are not shortcut hints: they stay.
            Assert.Equal(CaptureCueState.Click, hidden.CueState);
            Assert.Equal(shown.TargetLabelForTest, hidden.TargetLabelForTest);
            Assert.Equal(UiText.Get("CaptureGuide2_Text"),
                System.Windows.Automation.AutomationProperties.GetName(hidden.ModeBarForTest!.TextButton));

            // Without the owners' handlers the text and colour buttons are not offered at all.
            hidden.CopyColorText = null;
            hidden.TextCaptureAvailable = false;
            Assert.Equal(Visibility.Collapsed, hidden.ModeBarForTest.TextButton.Visibility);
            Assert.Equal(Visibility.Collapsed, hidden.ModeBarForTest.ColorButton.Visibility);
        }
        finally
        {
            shown.ReleaseResources();
            hidden.ReleaseResources();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAlwaysShowPreferenceRefreshesTheCaptureBarWithoutChangingItsSize(bool legacyEnabled) => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var settings = new AppSettings { General = { ShowShortcutHints = legacyEnabled } };
        CaptureOverlayView view = EvidenceView(Desktop(), AppTheme.Glass, "window-hover");
        try
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            ShortcutPresentation.Refresh();
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            var captions = Descendants(bar).OfType<System.Windows.Controls.TextBlock>().ToArray();
            FrameworkElement[] commands = [bar.TextButton, bar.ColorButton, bar.CancelButton];
            Assert.Equal(new[] { "T", "C", "Esc" }, captions.Select(caption => caption.Text));
            Size initialSize = bar.RenderSize;
            Size[] commandSizes = commands.Select(command => command.RenderSize).ToArray();
            foreach (bool alwaysShow in new[] { false, true, false })
            {
                settings.General.AlwaysShowShortcutHints = alwaysShow;
                ShortcutPresentation.Refresh();
                foreach (bool alt in new[] { false, true, false })
                {
                    ShortcutPresentation.SetAltRevealed(view, alt);
                    view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    Render(view);
                    Assert.All(captions, caption => Assert.Equal(alwaysShow || alt ? Visibility.Visible : Visibility.Collapsed, caption.Visibility));
                    Assert.Equal(initialSize, bar.RenderSize);
                    Assert.Equal(commandSizes, commands.Select(command => command.RenderSize));
                    Assert.All(commands, command => Assert.Equal(36, command.ActualHeight));
                    if (!alwaysShow && !alt) continue;
                    for (int index = 0; index < captions.Length; index++)
                    {
                        Rect bounds = captions[index].TransformToAncestor(commands[index]).TransformBounds(new Rect(captions[index].RenderSize));
                        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= commands[index].ActualWidth + 0.5
                            && bounds.Bottom <= commands[index].ActualHeight + 0.5, $"Shortcut caption leaves its button: {bounds}");
                    }
                }
            }
        }
        finally
        {
            view.ReleaseResources();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void CoordinatorReadsTheNewAlwaysShowSettingForEachSelector() => StaTestHost.Run(() =>
    {
        var settings = new AppSettings { General = { ShowShortcutHints = true } };
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            AutoDetectWindows = () => true,
            ApplyCaptureExclusion = _ => true,
        };
        Assert.False(coordinator.ShowShortcutHints());
        coordinator.ShowShortcutHints = () => settings.General.AlwaysShowShortcutHints;
        MethodInfo show = typeof(CaptureOverlayCoordinator).GetMethod("ShowOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo active = typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (bool expected in new[] { false, true, false })
        {
            settings.General.AlwaysShowShortcutHints = expected;
            settings.General.ShowShortcutHints = !expected;
            // No frame yet: the selector is created hidden, exactly as before acquisition.
            show.Invoke(coordinator, [new RectD(0, 0, 640, 360), false, false, Stopwatch.StartNew(), null, true]);
            var overlay = (CaptureOverlayWindow)active.GetValue(coordinator)!;
            Assert.False(overlay.IsVisible);
            Assert.True(overlay.DetectWindows);
            Assert.Equal(expected, overlay.ShowShortcutHints);
            Assert.True(overlay.ShowModeBar);
            coordinator.Cancel();
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void BandKeylineAndTabLandOnPhysicalPixelsAtEveryScale(double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        CaptureOverlayView view = EvidenceView(frame, AppTheme.Glass, "window-hover");
        try
        {
            RenderTargetBitmap rendered = Render(view, scale);
            Assert.Equal(3, Field<Pen>(view, "_detectedBorderPen").Thickness * scale, 6);
            Assert.Equal(1, Field<Pen>(view, "_keylinePen").Thickness * scale, 6);

            // Notes spans physical x 120..739 and y 90..509; sample row 400, clear of the guides.
            // Outside: dimmed desktop. Band: 3 accent pixels. Keyline: 1 pixel. Then the window itself.
            Assert.NotEqual(Accent, Pixel(rendered, 119, 400));
            Assert.NotEqual(Pixel(frame.Bitmap, 119, 400), Pixel(rendered, 119, 400));
            for (int x = 120; x <= 122; x++) AssertPixel(rendered, x, 400, Accent);
            AssertPixel(rendered, 123, 400, Keyline);
            AssertPixel(rendered, 124, 400, Pixel(frame.Bitmap, 124, 400));

            Rect tab = view.TargetTabForTest!.Value;
            foreach (double edge in new[] { tab.Left, tab.Top, tab.Right, tab.Bottom })
                Assert.True(Math.Abs((edge * scale) - Math.Round(edge * scale)) < 1e-6, $"{edge} at {scale}x");
            Assert.True(new Rect(0, 0, Width / scale, Height / scale).Contains(tab));
            Assert.DoesNotContain(view.ChromeBoundsForTest, chrome => CaptureCueLayout.Overlaps(chrome, tab));
            if (view.TargetTabPlacementForTest == CaptureCueTabPlacement.Above)
                Assert.Equal(90, tab.Bottom * scale, 6);
            else
                Assert.True(new Rect(120 / scale, 90 / scale, 620 / scale, 420 / scale).Contains(tab));
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ClickCaptureReturnsTheFrozenPixelsWithNoCueInThem() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        var window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false)
        {
            DetectWindows = true,
            SourceWindows = [Browser, Notes, Taskbar, Shell],
        };
        var view = Assert.IsType<CaptureOverlayView>(window.Content);
        PointD pointer = new(300, 300);
        view.ReadCursor = () => pointer;
        view.ReadMonitors = () => Monitors;
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        CaptureSelectionCompletedEventArgs? completed = null;
        window.SelectionCompleted += (_, e) => completed = e;

        view.InitializePointer();
        RenderTargetBitmap shown = Render(view);
        // The cue is really drawn over the window: tab above it, band on its edge.
        Rect tab = view.TargetTabForTest!.Value;
        AssertPixel(shown, (int)tab.Left + 3, (int)tab.Bottom - 3, Accent);
        AssertPixel(shown, 121, 400, Accent);
        Assert.NotEqual(Accent, Pixel(frame.Bitmap, 121, 400));

        Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
        Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
        Overlay.PumpUntil(() => completed is not null);

        Assert.Equal(new RectD(120, 90, 620, 420), completed!.BitmapRegion);
        Assert.Equal("report.txt - Notepad", completed.SourceTitle);
        Assert.Equal(Bgra(new CroppedBitmap(frame.Bitmap, new Int32Rect(120, 90, 620, 420))), Bgra(completed.SelectedBitmap));
    });

    private static CaptureOverlayView GlassView(FrozenFrame frame, Func<PointD> pointer)
    {
        var view = new CaptureOverlayView(frame, showMagnifier: false)
        {
            ReadCursor = pointer,
            DetectWindows = true,
            ReadMonitors = () => Monitors,
            WindowCandidates = [Browser, Notes, Taskbar, Shell],
        };
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        return view;
    }

    private static void Move(CaptureOverlayView view) =>
        Overlay.Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void AssertPixel(BitmapSource bitmap, int x, int y, byte[] expected)
    {
        byte[] actual = Pixel(bitmap, x, y);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(actual[channel] - expected[channel]) <= 1,
                $"({x}, {y}) is {Convert.ToHexString(actual)}, expected {Convert.ToHexString(expected)}");
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(bitmap, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixel, 4, 0);
        return pixel;
    }

    private static byte[] Bgra(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }
}
