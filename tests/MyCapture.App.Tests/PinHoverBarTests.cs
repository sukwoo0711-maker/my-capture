using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pinned image shows copy, save, opacity and close in its corner while the pointer is over it;
/// that bar never reaches a screenshot or a recording, and the right-click menu keeps its keys in
/// the right-hand column.
/// </summary>
public sealed class PinHoverBarTests
{
    [Fact]
    public void BarSitsInsideALargePinAndAboveOrBelowASmallOne()
    {
        var work = new RectD(0, 0, 1920, 1040);
        // Large pin: inside its top-right corner, one gap from both edges.
        Assert.Equal(new RectD(1000 + 600 - 6 - 200, 300 + 6, 200, 36), PinHoverBar.Place(new RectD(1000, 300, 600, 400), 200, 36, work, 1));
        // Small pin: just above it, right-aligned, so the bar never hides the image.
        Assert.Equal(new RectD(1000 + 120 - 200, 300 - 36 - 6, 200, 36), PinHoverBar.Place(new RectD(1000, 300, 120, 90), 200, 36, work, 1));
        // A small pin at the top of the work area takes the bar below it.
        Assert.Equal(new RectD(100 + 120 - 200, 10 + 90 + 6, 200, 36), PinHoverBar.Place(new RectD(100, 10, 120, 90), 200, 36, work, 1));
        // Kept on the work area, and the gap follows the display scale.
        Assert.Equal(new RectD(0, 300 + 9, 200, 36), PinHoverBar.Place(new RectD(-50, 0, 150, 300), 200, 36, work, 1.5));
    }

    [Fact]
    public void HoverShowsTheCaptureExcludedBarAndLeavingOrClickThroughHidesIt() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        var excluded = new List<Window>();
        PinHoverBar.ApplyCaptureExclusion = window => { excluded.Add(window); return true; };
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinWindow pin = Pin(out BitmapSource image);
        try
        {
            pin.Show();
            Assert.Null(pin.HoverBarForTest);
            Enter(pin);
            PinHoverBar bar = Assert.IsType<PinHoverBar>(pin.HoverBarForTest);
            Assert.True(pin.IsHoverBarShown);
            Assert.Same(pin, bar.Owner);
            Assert.False(bar.ShowActivated);
            Assert.False(bar.ShowInTaskbar);
            // Excluded from capture the moment its window exists: never in a screenshot or recording.
            Assert.Equal([bar], excluded);
            Assert.True(bar.IsCaptureExcluded);
            // Reduced motion: no fade, fully visible at once.
            Assert.Equal(1, bar.Opacity);
            Assert.Equal(["복사", "저장", "투명도", "닫기"],
                new[] { bar.CopyButton, bar.SaveButton, bar.OpacityButton, bar.CloseButton }.Select(AutomationProperties.GetName));
            Assert.Equal("Ctrl+C", AutomationProperties.GetAcceleratorKey(bar.CopyButton));
            Assert.Equal("닫기 (Esc)", bar.CloseButton.ToolTip);
            Assert.Equal(1.0, pin.State.Opacity);

            // Leaving the pin hides it after the short grace period.
            Leave(pin);
            Assert.True(pin.IsHoverBarShown);
            pin.ForceHoverBarHideForTest();
            Assert.False(pin.IsHoverBarShown);

            // A pin that passes clicks through never shows it, and turning that on hides it.
            Enter(pin);
            Assert.True(pin.IsHoverBarShown);
            pin.ApplyClickThrough(true);
            Assert.False(pin.IsHoverBarShown);
            Enter(pin);
            Assert.False(pin.IsHoverBarShown);
            pin.ApplyClickThrough(false);

            // Hiding the pin hides the bar; closing the pin closes it.
            Enter(pin);
            pin.HidePin();
            Assert.False(pin.IsHoverBarShown);
            pin.ShowPin();
            Enter(pin);
            Assert.True(pin.IsHoverBarShown);
            pin.Close();
            Assert.False(bar.IsVisible);
            Assert.Null(pin.HoverBarForTest);
        }
        finally
        {
            pin.Close();
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
        _ = image;
    });

    [Fact]
    public void BarButtonsRunThePinsOwnCopySaveOpacityAndClose() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        PinWindow pin = Pin(out BitmapSource image);
        int copies = 0;
        var saves = new List<PinSaveMode>();
        pin.CopyRequested += (_, request) => { copies++; Assert.Same(image, request.Image); };
        pin.SaveRequested += (_, e) => saves.Add(e.Mode);
        try
        {
            pin.Show();
            Enter(pin);
            PinHoverBar bar = pin.HoverBarForTest!;
            // With Windows animations on, the bar fades in instead of popping.
            Assert.True(bar.HasAnimatedProperties);

            Click(bar.CopyButton);
            Click(bar.SaveButton);
            Assert.Equal(1, copies);
            Assert.Equal([PinSaveMode.QuickSave], saves);

            ContextMenu opacity = bar.CreateOpacityMenu();
            Assert.Equal(["100%", "80%", "60%", "40%"], opacity.Items.OfType<MenuItem>().Select(item => (string)item.Header));
            Assert.True(((MenuItem)opacity.Items[0]).IsChecked);
            ((MenuItem)opacity.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0.6, pin.State.Opacity, 3);
            Assert.True(((MenuItem)bar.CreateOpacityMenu().Items[2]).IsChecked);

            Click(bar.CloseButton);
            Assert.True(pin.IsClosed);
        }
        finally
        {
            pin.Close();
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Fact]
    public void ContextMenuKeepsKeysInTheRightColumnAndUsesTheNewClickThroughName() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        HotkeySettings keys = new() { ToggleClickThrough = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54) };
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => keys;
        PinWindow pin = Pin(out _);
        try
        {
            MenuItem[] items = pin.ContextMenu.Items.OfType<MenuItem>().ToArray();
            string[] headers = items.Select(item => (string)item.Header).ToArray();
            // No key written inside a name.
            Assert.DoesNotContain(headers, header => header.Contains('(', StringComparison.Ordinal));
            Assert.DoesNotContain(headers, header => header.Contains("Esc", StringComparison.Ordinal));
            Assert.DoesNotContain(headers, header => header.Contains("클릭 통과", StringComparison.Ordinal));
            MenuItem Item(string header) => items.Single(item => (string)item.Header == header);
            Assert.Equal("0", Item("100%").InputGestureText);
            Assert.Equal("+", Item("확대").InputGestureText);
            Assert.Equal("-", Item("축소").InputGestureText);
            Assert.Equal("Esc", Item("닫기").InputGestureText);
            Assert.Equal("Ctrl+휠 아래", Item("더 투명하게").InputGestureText);
            Assert.Equal("Ctrl+휠 위", Item("더 불투명하게").InputGestureText);
            Assert.Equal("Ctrl+Alt+T", Item("뒤 창 클릭 허용").InputGestureText);
            Assert.Equal("Ctrl+S", Item("빠른 저장").InputGestureText);
            Assert.Contains("닫은 핀 다시 열기", headers);
            Assert.Equal("드래그: 이동 · 휠: 확대·축소 · 오른쪽 클릭: 메뉴", pin.ToolTip);

            pin.ApplyClickThrough(true);
            Assert.True(pin.IsFeedbackVisible);
        }
        finally
        {
            pin.Close();
            ShortcutHelpWindow.ReadHotkeys = previous;
        }
    });

    [Fact]
    public void ANarrowPinGetsTheCompactBarAndAWideOneKeepsTheNames() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            // Measured before it is ever shown: a never-shown window measured to zero, so
            // every pin kept the names and a narrow pin's bar stuck out past its edges.
            var unshown = new PinHoverBar();
            double labeled = unshown.MeasureLabeledWidth();
            Assert.True(labeled > 150, $"labelled width {labeled}");
            Assert.False(unshown.Compact);
            unshown.Close();

            foreach (var (width, compact) in new[] { (200, true), (600, false) })
            {
                PinWindow pin = Pin(out _, width, 220);
                try
                {
                    pin.Show();
                    Enter(pin);
                    PinHoverBar bar = pin.HoverBarForTest!;
                    bar.UpdateLayout();
                    Assert.Equal(compact, bar.Compact);
                    TextBlock[] names = [.. Descendants(bar.CopyButton).OfType<TextBlock>()];
                    Assert.All(names, name => Assert.Equal(compact ? Visibility.Collapsed : Visibility.Visible, name.Visibility));
                    // The names stay in the tooltips and automation names.
                    Assert.Equal("복사", AutomationProperties.GetName(bar.CopyButton));
                    if (compact) Assert.True(bar.ActualWidth < labeled, $"compact {bar.ActualWidth} vs {labeled}");
                    else Assert.InRange(bar.ActualWidth, labeled - 2, labeled + 2);
                }
                finally { pin.Close(); }
            }
        }
        finally
        {
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }

    private static PinWindow Pin(out BitmapSource image, int width = 360, int height = 220)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0xEE);
        image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return new PinWindow(PinContent.FromImage(image), new PinViewState(width, height, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
        {
            ShowActivated = false,
            // Keep the bar beside the off-screen pin instead of on the desktop.
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
    }

    private static void Enter(PinWindow pin) =>
        pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });

    private static void Leave(PinWindow pin) =>
        pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
