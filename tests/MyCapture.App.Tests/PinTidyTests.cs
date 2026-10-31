using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTidyTests
{
    [Theory]
    [InlineData("ko-KR", "숨기기", "이 핀만 보기")]
    [InlineData("en-US", "Hide", "Show only this pin")]
    public void MenuGroupsAndAvailabilityFollowOtherShownPins(string language, string hide, string only) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var fixture = new Fixture();
        PinWindow first = fixture.Open();
        ContextMenu menu = first.ContextMenu;
        int index = menu.Items.IndexOf(Item(first, "Onboard_PinAllowClickThrough"));
        Assert.IsType<Separator>(menu.Items[++index]);
        MenuItem hideItem = Assert.IsAssignableFrom<MenuItem>(menu.Items[++index]);
        MenuItem onlyItem = Assert.IsAssignableFrom<MenuItem>(menu.Items[++index]);
        Assert.Equal(hide, hideItem.Header);
        Assert.Equal(only, onlyItem.Header);
        Assert.IsType<Separator>(menu.Items[++index]);
        Assert.Same(first.RestoreClosedMenuItemForTest, menu.Items[++index]);
        Assert.Equal("Esc", Assert.IsAssignableFrom<MenuItem>(menu.Items[++index]).InputGestureText);
        Assert.Equal(UiText.Get("Text_E657405F6B2D"), Assert.IsAssignableFrom<MenuItem>(menu.Items[++index]).Header);
        foreach (MenuItem item in new[] { hideItem, onlyItem })
        {
            Assert.Null(item.Icon);
            Assert.True(string.IsNullOrEmpty(item.InputGestureText));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(item)));
        }
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(onlyItem.IsEnabled);
        PinWindow other = fixture.Open();
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(onlyItem.IsEnabled);
        other.HidePin();
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(onlyItem.IsEnabled);
        other.ShowPin();
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.True(onlyItem.IsEnabled);
        other.Close();
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(onlyItem.IsEnabled);
    });

    [Fact]
    public void HideChangesOnlyChosenPinAndNotifiesAndNewPinStaysShown() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("ko-KR");
        using var fixture = new Fixture();
        PinWindow chosen = fixture.Open();
        PinWindow other = fixture.Open();
        var before = Presentation(chosen);
        int notifications = 0;
        fixture.Manager.OpenPinsChanged += () => notifications++;
        Item(chosen, "Pin_Hide").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(chosen.State.IsHidden);
        Assert.False(chosen.IsVisible);
        Assert.False(other.State.IsHidden);
        Assert.True(other.IsVisible);
        Assert.Equal(before, Presentation(chosen));
        Assert.True(fixture.Manager.AnyHidden);
        Assert.True(notifications > 0);
        Assert.Contains(Build(fixture.Manager), entry => entry.Label == "핀 모두 보이기");
        PinWindow added = fixture.Manager.PinImage(Image());
        Assert.True(added.IsVisible);
        Assert.False(added.State.IsHidden);
        Assert.True(fixture.Manager.AnyHidden);
        int beforeReveal = notifications;
        chosen.ShowPin();
        Assert.True(notifications > beforeReveal);
        Assert.False(fixture.Manager.AnyHidden);
        Assert.Contains(Build(fixture.Manager), entry => entry.Label == "핀 모두 숨기기");
    });

    [Fact]
    public void ShowOnlyPreservesChosenPresentationAndMixedToggleRestoresAll() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        PinWindow first = fixture.Open();
        PinWindow chosen = fixture.Open();
        PinWindow last = fixture.Open();
        Item(chosen, "Onboard_PinZoomIn").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Item(chosen, "Onboard_PinMoreTransparent").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var before = Presentation(chosen);
        chosen.RefreshShowOnlyMenuItem();
        Item(chosen, "Pin_ShowOnly").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(first.State.IsHidden);
        Assert.True(last.State.IsHidden);
        Assert.True(chosen.IsVisible);
        Assert.False(chosen.State.IsHidden);
        Assert.Equal(before, Presentation(chosen));
        first.ApplyClickThrough(true);
        chosen.ApplyClickThrough(true);
        fixture.Manager.HideOrShowAll();
        Assert.False(fixture.Manager.AnyHidden);
        foreach (PinWindow pin in new[] { first, chosen, last })
        {
            Assert.True(pin.IsVisible);
            Assert.False(pin.State.IsHidden);
            Assert.False(pin.State.IsClickThrough);
        }
        fixture.Manager.HideOrShowAll();
        Assert.All(new[] { first, chosen, last }, pin => Assert.True(pin.State.IsHidden));
    });

    [Fact]
    public void ImageTitlesPreferSourceAndKeepWholeGraphemes() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        using var fixture = new Fixture();
        string grapheme = "e\u0301";
        string longTitle = string.Concat(Enumerable.Repeat(grapheme, 42));
        PinWindow source = fixture.Open(CaptureProvenance.Create(windowTitle: "  release_notes - Chrome  "));
        PinWindow fallback = fixture.Open(CaptureProvenance.Create(windowTitle: " \t "));
        PinWindow longSource = fixture.Open(CaptureProvenance.Create(windowTitle: longTitle));
        var rows = Branch(Build(fixture.Manager)).Children!;
        Assert.Equal(string.Concat(Enumerable.Repeat(grapheme, 39)) + "… · Image", rows[0].Label);
        Assert.Equal("320 × 180 px · Image", rows[1].Label);
        Assert.Equal("release_notes - Chrome · Image", rows[2].Label);
        Assert.All(rows, entry => Assert.True(entry.LiteralLabel));
        Assert.Same(source.PinnedContent.Image, rows[2].Thumbnail);
        Assert.Same(fallback.PinnedContent.Image, rows[1].Thumbnail);
        Assert.Same(longSource.PinnedContent.Image, rows[0].Thumbnail);
        var text = PinContent.FromText(Image(), "First line\nSecond line", false)
            .WithProvenance(CaptureProvenance.Create(windowTitle: "Source title"));
        fixture.Manager.RestoreSessionPin(new PinManager.ClosedPin(text, 320, 180, 1, 1, null));
        Assert.Equal("First line · Text", Branch(Build(fixture.Manager)).Children![0].Label);
    });

    [Theory]
    [InlineData("ko-KR", "숨김")]
    [InlineData("en-US", "Hidden")]
    public void HiddenRowsKeepEnabledThumbnailsAndAccessibleState(string language, string status) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var fixture = new Fixture();
        PinWindow pin = fixture.Open();
        pin.HidePin();
        var row = Assert.Single(Branch(Build(fixture.Manager)).Children!);
        Assert.True(row.IsEnabled);
        Assert.True(row.IsMuted);
        Assert.Equal(status, row.Gesture);
        Assert.Equal(status, row.ItemStatus);
        using var presenter = new ThemedShellPresenter(() => false);
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: false);
        ContextMenu menu = presenter.CreateMenu([row], resources);
        menu.Measure(new Size(600, 200));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        MenuItem item = Assert.IsAssignableFrom<MenuItem>(menu.Items[0]);
        Assert.Equal(status, AutomationProperties.GetItemStatus(item));
        Assert.Equal(string.Empty, AutomationProperties.GetAcceleratorKey(item));
        Assert.True(item.IsEnabled);
        Assert.Equal(menu.Resources["Text.Secondary"], item.Foreground);
        Border box = Assert.IsType<Border>(item.Icon);
        Assert.Equal(new Thickness(1), box.BorderThickness);
        Assert.Equal(new CornerRadius(3), box.CornerRadius);
        Assert.Equal(.5, box.Opacity);
        Assert.Equal(32, box.ActualWidth);
        Assert.Equal(24, box.ActualHeight);
        ContentPresenter iconHost = Assert.IsType<ContentPresenter>(item.Template.FindName("IconHost", item));
        Assert.Equal(32, iconHost.ActualWidth);
        Assert.Equal(24, iconHost.ActualHeight);
        Image image = Assert.IsType<Image>(box.Child);
        Assert.Same(pin.PinnedContent.Image, image.Source);
        Assert.Equal(Stretch.Uniform, image.Stretch);
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
        Assert.True(image.ActualWidth <= 30 && image.ActualHeight <= 22);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(pin.IsVisible);
        Assert.True(pin.FindCueRunning);
    });

    [Fact]
    public void OverflowRowHasNoThumbnail() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 16; i++) fixture.Open();
        var rows = Branch(Build(fixture.Manager)).Children!;
        Assert.Equal(16, rows.Count);
        Assert.All(rows.Take(15), row => Assert.NotNull(row.Thumbnail));
        Assert.Null(rows[^1].Thumbnail);
        Assert.False(rows[^1].IsEnabled);
    });

    [Fact]
    public void FindCuePulsesTwiceAndRetainsMeasuredFilmstrip() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        PinWindow pin = fixture.Open();
        pin.Resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Glass, pin.Resources, systemHighContrast: false);
        pin.SetChromeActiveForTest(true);
        pin.UpdateLayout();
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        pin.PlayFindCue();
        Assert.True(pin.LandingRenderingAttached);
        Assert.Null(pin.LandingDecoration);
        double[] targets = [0, 83, 167, 250, 333, 500, 610, 700, 850, 1000, 1220];
        var captures = new List<(double Target, double Measured, BitmapSource Image)>();
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TIDY_EVIDENCE");
        var clock = Stopwatch.StartNew();
        int rises = 0, falls = 0;
        var frameColors = new HashSet<Color>();
        bool high = false;
        void Observe()
        {
            double time = clock.Elapsed.TotalMilliseconds;
            bool next = pin.LandingAccentAmount > 0;
            frameColors.Add(pin.FrameColorForTest);
            if (next && !high) rises++;
            if (!next && high) falls++;
            high = next;
            if (output is not null && captures.Count < targets.Length && time >= targets[captures.Count])
            {
                var chrome = (FrameworkElement)pin.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(chrome.ActualWidth), (int)Math.Ceiling(chrome.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(chrome);
                bitmap.Freeze();
                var crop = new CroppedBitmap(bitmap, new Int32Rect(0, 0, 100, 40));
                crop.Freeze();
                captures.Add((targets[captures.Count], time, crop));
            }
        }
        Observe();
        PollUntil(() => !pin.FindCueRunning && (output is null || captures.Count == targets.Length), Observe);
        Assert.Equal(2, rises);
        Assert.Equal(2, falls);
        Assert.True(frameColors.Count > 2, "The activated pin's actual frame must change color during its cue.");
        Assert.Equal(0, pin.LandingAccentAmount);
        Assert.False(pin.LandingRenderingAttached);
        Assert.Null(pin.LandingDecoration);
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 1200, 4000);
        if (output is not null) SaveFilmstrip(output, captures);
    });

    [Fact]
    public void ReducedMotionHoldsWithoutRenderingThenResets() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        PinWindow pin = fixture.Open();
        pin.StartLandingEffect();
        Assert.True(pin.LandingStarted);
        pin.PlayFindCue();
        Assert.Equal(1, pin.LandingAccentAmount);
        Assert.False(pin.LandingRenderingAttached);
        Assert.Null(pin.LandingDecoration);
        var clock = Stopwatch.StartNew();
        PollUntil(() => !pin.FindCueRunning);
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 950, 4000);
        Assert.Equal(0, pin.LandingAccentAmount);
    });

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("hide")]
    [InlineData("close")]
    public void FindCueStopsOnOwnerChanges(string change) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        PinWindow pin = fixture.Open();
        pin.PlayFindCue();
        Assert.Equal(1, pin.LandingAccentAmount);
        switch (change)
        {
            case "move": pin.Left += 20; break;
            case "resize": pin.Width += 20; pin.UpdateLayout(); break;
            case "hide": pin.HidePin(); break;
            case "close": pin.Close(); break;
        }
        Assert.False(pin.FindCueRunning);
        Assert.Equal(0, pin.LandingAccentAmount);
        Assert.False(pin.LandingRenderingAttached);
    });

    [Fact]
    public void FindCueIgnoresHiddenClosedAndUnshownPins() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        PinWindow pin = fixture.Open();
        pin.HidePin();
        pin.PlayFindCue();
        Assert.False(pin.FindCueRunning);
        pin.Close();
        pin.PlayFindCue();
        Assert.False(pin.FindCueRunning);
        var unshown = new PinWindow(PinContent.FromImage(Image()), new PinViewState(320, 180, 1, 1, .1), 0, 0);
        try { unshown.PlayFindCue(); Assert.False(unshown.FindCueRunning); }
        finally { unshown.Close(); }
    });

    private static void PollUntil(Func<bool> complete, Action? observe = null)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { observe?.Invoke(); if (complete() || elapsed.ElapsedMilliseconds >= 4000) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); Assert.True(complete(), "Condition did not complete within 4 seconds."); }
        finally { timer.Stop(); }
    }

    private static void SaveFilmstrip(string output, List<(double Target, double Measured, BitmapSource Image)> captures)
    {
        string root = DiagnosticOutputPaths.Create(output);
        var strip = new WrapPanel { Width = 650 };
        strip.Resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Glass, strip.Resources, systemHighContrast: false);
        strip.SetResourceReference(Panel.BackgroundProperty, "Surface.Sunken");
        foreach (var capture in captures)
        {
            var cell = new StackPanel { Width = 122, Margin = new Thickness(4) };
            cell.Children.Add(new Image { Source = capture.Image, Width = 100, Height = 40, Stretch = Stretch.None });
            var label = new TextBlock { Text = $"{capture.Measured:0.0} ms\n(target {capture.Target:0})", TextAlignment = TextAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            cell.Children.Add(label);
            strip.Children.Add(cell);
        }
        strip.Measure(new Size(650, double.PositiveInfinity));
        strip.Arrange(new Rect(strip.DesiredSize));
        strip.UpdateLayout();
        SettingsGuideTrayEvidenceTests.Save(strip, root, "find-cue-filmstrip.png");
        File.WriteAllLines(DiagnosticOutputPaths.Child(root, "find-cue-times.csv"),
            new[] { "target_ms,measured_ms" }.Concat(captures.Select(item => $"{item.Target.ToString(CultureInfo.InvariantCulture)},{item.Measured.ToString("F3", CultureInfo.InvariantCulture)}")));
    }

    private static object Presentation(PinWindow pin) => new { pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.State.Opacity };
    private static MenuItem Item(PinWindow pin, string key) => pin.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get(key)));
    private static ThemedShellPresenter.MenuEntry Branch(IReadOnlyList<ThemedShellPresenter.MenuEntry> menu) => menu.Single(entry => entry.Label == UiText.Get("Tray_OpenPins"));
    private static IReadOnlyList<ThemedShellPresenter.MenuEntry> Build(PinManager manager)
    {
        Action noop = () => { };
        return TrayMenuLayout.Build(new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop,
            HideOrShowAllPins: manager.HideOrShowAll, ActivateOpenPin: manager.ActivateOpenPin), new HotkeySettings(),
            new TrayMenuState(PinCount: manager.Count, PinsHidden: manager.AnyHidden, OpenPins: manager.SnapshotOpenPinWindows()));
    }
    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 180 * 4], 320 * 4);
        image.Freeze();
        return image;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool? _motion = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly List<WindowTrackSizeScope> _scopes = [];
        internal PinManager Manager { get; }
        internal Fixture()
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var settings = NewInstallDefaults.Create().Pin;
            settings.ClosedWindowRestoreLimit = 0;
            Manager = new PinManager(() => settings, NullLogger.Instance)
            {
                BeforeShowForTest = pin =>
                {
                    pin.ShowActivated = false;
                    pin.Left = pin.Top = -10000;
                    _scopes.Add(new WindowTrackSizeScope(pin, new Size(800, 600)));
                },
                ActivatePinForTest = _ => { },
            };
        }
        internal PinWindow Open(CaptureProvenance? provenance = null)
        {
            var snapshot = new PinManager.ClosedPin(PinContent.FromImage(Image(), provenance: provenance), 320, 180, 1, 1, null);
            PinWindow pin = Manager.RestoreSessionPin(snapshot);
            pin.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            return pin;
        }
        public void Dispose()
        {
            Manager.CloseAllForExit();
            foreach (var scope in _scopes) scope.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = _motion;
        }
    }
}
