using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pinned image shows copy, text copy, save and close in its corner while the pointer is over it;
/// that bar never reaches a screenshot or a recording, and the right-click menu keeps its keys in
/// the right-hand column.
/// </summary>
public sealed class PinHoverBarTests
{
    // The named width of copy · text copy · save · close (and source), measured with the app theme.
    private const double KoBudget = 342, KoSourceBudget = 408, EnBudget = 355, EnSourceBudget = 421;

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
            Assert.Equal(["이미지 복사", "텍스트 복사", "저장", "닫기"],
                new[] { bar.CopyButton, bar.TextCopyButton, bar.SaveButton, bar.CloseButton }.Select(AutomationProperties.GetName));
            Assert.Equal("Ctrl+C", AutomationProperties.GetAcceleratorKey(bar.CopyButton));
            // The menu's own name and gesture; a mouse gesture is named in the tooltip, not as a key caption.
            Assert.Equal("텍스트 복사 (Ctrl+더블클릭)", bar.TextCopyButton.ToolTip);
            Assert.Empty(AutomationProperties.GetAcceleratorKey(bar.TextCopyButton));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BarButtonsRunThePinsOwnCopyTextCopySaveAndClose(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        PinWindow pin = Pin(out BitmapSource image);
        int copies = 0;
        var saves = new List<PinSaveMode>();
        var recognized = new List<BitmapSource>();
        pin.CopyRequested += (_, request) => { copies++; Assert.Same(image, request.Image); };
        pin.OcrRequested += (_, source) => recognized.Add(source);
        pin.SaveRequested += (_, e) => saves.Add(e.Mode);
        try
        {
            pin.Show();
            Enter(pin);
            PinHoverBar bar = pin.HoverBarForTest!;
            // Only layered windows fade; the opaque capture-excluded surface appears at once.
            Assert.Equal(supported, bar.AllowsTransparency);
            Assert.Equal(supported, bar.HasAnimatedProperties);
            if (!supported) Assert.Equal(1, bar.Opacity);

            Click(bar.CopyButton);
            Click(bar.SaveButton);
            Assert.Equal(1, copies);
            Assert.Equal([PinSaveMode.QuickSave], saves);
            // A picture pin's text copy reads the picture's text, as Ctrl+double-click does.
            Click(bar.TextCopyButton);
            Assert.Same(image, Assert.Single(recognized));
            Assert.Equal(1, copies);

            // The opacity menu stays on the pin's O key; the bar no longer has an opacity button.

            ContextMenu opacity = bar.CreateOpacityMenu();
            Assert.Equal(supported ? typeof(ContextMenu) : typeof(OpaqueCaptureContextMenu), opacity.GetType());
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

    [Theory]
    [InlineData("text")]
    [InlineData("table")]
    [InlineData("markdown")]
    public void TextCopyOnATextPinCopiesItsOriginalTextWithoutReadingThePicture(string kind) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        byte[] pixels = new byte[360 * 220 * 4];
        BitmapSource picture = BitmapSource.Create(360, 220, 96, 96, PixelFormats.Bgra32, null, pixels, 360 * 4);
        picture.Freeze();
        const string original = "| a | b |\n| 1 | 2 |";
        PinContent content = kind switch
        {
            "table" => PinContent.FromText(picture, original, isTable: true),
            "markdown" => PinContent.FromMarkdown(picture, original),
            _ => PinContent.FromText(picture, original, isTable: false),
        };
        var pin = new PinWindow(content, new PinViewState(360, 220, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
        {
            ShowActivated = false,
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
        var texts = new List<string>();
        int recognized = 0, copies = 0;
        pin.OriginalTextCopyRequested += (_, text) => texts.Add(text);
        pin.OcrRequested += (_, _) => recognized++;
        pin.CopyRequested += (_, _) => copies++;
        try
        {
            pin.Show();
            Enter(pin);
            PinHoverBar bar = pin.HoverBarForTest!;
            Click(bar.TextCopyButton);
            // The same route as the menu's copy-text item and Ctrl+double-click: the kept original.
            Assert.Equal([original], texts);
            Assert.Equal(0, recognized);
            Assert.Equal(0, copies);
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal([original, original], texts);
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
            Assert.Equal("드래그: 이동 · 휠·테두리: 크기 · 오른쪽 클릭: 메뉴", pin.ToolTip);

            pin.ApplyClickThrough(true);
            Assert.True(pin.IsFeedbackVisible);
        }
        finally
        {
            pin.Close();
            ShortcutHelpWindow.ReadHotkeys = previous;
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANarrowPinGetsTheCompactBarAndAWideOneKeepsTheNames(bool supported) => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(supported);
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
                    var primary = Assert.IsType<StackPanel>(SurfaceShortcutHintTests.PrimaryContent(bar.CopyButton));
                    TextBlock name = Assert.Single(primary.Children.OfType<TextBlock>());
                    Assert.Equal("복사", name.Text);
                    Assert.Equal(compact ? Visibility.Collapsed : Visibility.Visible, name.Visibility);
                    TextBlock shortcut = Assert.IsAssignableFrom<TextBlock>(Assert.IsType<Grid>(bar.CopyButton.Content).Children[1]);
                    Assert.Equal("Ctrl+C", shortcut.Text);
                    ShortcutMotionTestScope.AssertState(shortcut, false);
                    // The names stay in the tooltips and automation names.
                    // The copy button names what it copies; the visible name and the bar's width stay as they were.
                    Assert.Equal("이미지 복사", AutomationProperties.GetName(bar.CopyButton));
                    Assert.Equal("이미지 복사 (Ctrl+C)", bar.CopyButton.ToolTip);
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

    [Fact]
    public void AShownIconOnlyBarStillMeasuresTheWidthOfItsNames() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var bar = new PinHoverBar { Left = -10000, Top = -10000 };
        try
        {
            // The app's buttons have a fixed height, so a button measured again for its names
            // keeps the icon-only measure of its face unless the names mark the whole way up.
            Ocr410Fixture.LoadTheme(bar);
            bar.SetTranslationAction(true, true, UiText.Get("Pin_Translate"), UiText.Get("Pin_TranslateHelp"));
            bar.Compact = true;
            bar.Show();
            PumpUntil(() => bar.IsLoaded && bar.CopyButton.Content is Grid { Name: "CommandButtonFace" });
            bar.UpdateLayout();
            var surface = Assert.IsType<Border>(bar.Content);
            double icons = surface.ActualWidth;
            // The icon-only layout was reported as the labelled width, so a translation step
            // swapped the bar to names that do not fit and resized it under the pointer.
            double measured = bar.MeasureLabeledWidth();
            bar.UpdateLayout();
            Assert.True(bar.Compact);
            Assert.InRange(surface.ActualWidth, icons - 1, icons + 1);
            bar.Compact = false;
            bar.UpdateLayout();
            double names = surface.ActualWidth;
            Assert.True(icons < names - 20, $"icons {icons} vs names {names}");
            Assert.InRange(measured, names - 1, names + 1);
        }
        finally
        {
            bar.Close();
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Theory]
    [InlineData("ko-KR", false, KoBudget)]
    [InlineData("ko-KR", true, KoSourceBudget)]
    [InlineData("en-US", false, EnBudget)]
    [InlineData("en-US", true, EnSourceBudget)]
    public void TranslationSharesTheAllNamesOrAllIconsRuleAndWidthBudget(string language, bool source, double budget) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var bar = new PinHoverBar(source ? CaptureProvenance.Create(url: "https://example.com") : null);
        try
        {
            bar.SetTranslationAction(true, true, UiText.Get("Pin_Translate"), "Translate on this PC");
            Assert.Equal(budget, bar.BaseLabeledWidthBudget);
            var actions = new[] { bar.CopyButton, bar.TextCopyButton, bar.SaveButton, bar.TranslationButton };
            TextBlock Caption(Button button) => Assert.Single(
                Assert.IsType<StackPanel>(SurfaceShortcutHintTests.PrimaryContent(button)).Children.OfType<TextBlock>());
            foreach (Button button in actions)
            {
                Assert.Equal(Visibility.Visible, Caption(button).Visibility);
                Assert.Single(Assert.IsType<StackPanel>(SurfaceShortcutHintTests.PrimaryContent(button)).Children.OfType<SymbolIcon>());
            }
            double labeled = bar.MeasureLabeledWidth();
            // Offering translation adds the translation button's own named width to the budget,
            // so it never turns this pin kind's names into icons.
            Assert.True(bar.LabeledWidthBudget > budget, $"{bar.LabeledWidthBudget} vs {budget}");
            bar.Compact = bar.NeedsCompact(600, labeled);
            var surface = Assert.IsType<Border>(bar.Content);
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.InRange(surface.DesiredSize.Width, 1, bar.LabeledWidthBudget);
            Assert.All(actions, button => Assert.Equal(bar.Compact ? Visibility.Collapsed : Visibility.Visible, Caption(button).Visibility));
            bar.Compact = true;
            bar.SetTranslationAction(true, true, UiText.Get("Pin_TranslationOriginal"), "View original");
            Assert.All(actions, button => Assert.Equal(Visibility.Collapsed, Caption(button).Visibility));
            Assert.Equal(UiText.Get("Pin_TranslationOriginal"), Caption(bar.TranslationButton).Text);
            Assert.Equal(UiText.Get("Pin_TranslationOriginal"), AutomationProperties.GetName(bar.TranslationButton));
            Assert.Equal("View original", bar.TranslationButton.ToolTip);
            bar.Compact = false;
            Assert.All(actions, button => Assert.Equal(Visibility.Visible, Caption(button).Visibility));
        }
        finally { bar.Close(); }
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

    private static void PumpUntil(Func<bool> condition)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15)) throw new TimeoutException();
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static void Enter(PinWindow pin) =>
        pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });

    private static void Leave(PinWindow pin) =>
        pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
