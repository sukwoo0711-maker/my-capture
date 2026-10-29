using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureCommandMenuTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void MenuOffersAllFourCommandsWithCurrentAssignedShortcuts(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var settings = new HotkeySettings
        {
            Capture = new Hotkey(HotkeyModifiers.Alt, Hotkey.VkC),
            CaptureFullScreen = Hotkey.None,
            CaptureWindow = new Hotkey(HotkeyModifiers.Control, Hotkey.VkF3),
        };
        ContextMenu menu = CaptureCommandMenu.Create(() => settings, _ => { });
        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(new[] { GlobalHotkeyCommand.CaptureRegion, GlobalHotkeyCommand.CaptureFullScreen,
            GlobalHotkeyCommand.CaptureWindow, GlobalHotkeyCommand.RecordRegion }, items.Select(item => (GlobalHotkeyCommand)item.Tag));
        Assert.Equal(settings.Capture.ToString(), items[0].InputGestureText);
        Assert.Equal(string.Empty, items[1].InputGestureText);
        Assert.Equal(settings.CaptureWindow.ToString(), items[2].InputGestureText);
        Assert.Equal(settings.RecordRegion.ToString(), items[3].InputGestureText);
        Assert.All(items, item => Assert.DoesNotContain("CaptureEntry.", Assert.IsType<string>(item.Header)));
        var anchor = new Button { ContextMenu = menu };
        var window = new Window { Content = anchor, Width = 320, Height = 140,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000,
            ShowActivated = false, ShowInTaskbar = false };
        foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
        menu.Resources = window.Resources;
        try
        {
            window.Show();
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
            menu.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            menu.UpdateLayout();
            foreach ((MenuItem item, string key) in items.Zip(new[] { "Icon.Capture", "Icon.CaptureFullScreen", "Icon.CaptureWindow", "Icon.Record" }))
            {
                SymbolIcon icon = Assert.IsType<SymbolIcon>(item.Icon);
                Assert.Equal(16, icon.Size);
                Assert.True(icon.IsVisible && icon.ActualWidth > 0 && icon.ActualHeight > 0);
                Assert.Same(menu.FindResource(key), icon.Data);
            }
            menu.IsOpen = false;
            settings.CaptureFullScreen = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
            menu.IsOpen = true;
            Assert.Equal(settings.CaptureFullScreen.ToString(), items[1].InputGestureText);
        }
        finally { menu.IsOpen = false; window.Close(); }
    });

    [Theory]
    [InlineData(false, "ko-KR")]
    [InlineData(true, "ko-KR")]
    [InlineData(false, "en-US")]
    [InlineData(true, "en-US")]
    public void SplitMainInvokesRegionOnceAndOnlyChevronOpensMenu(bool compact, string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var calls = new List<GlobalHotkeyCommand>();
        var settings = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Alt, Hotkey.VkC) };
        var split = Assert.IsType<StackPanel>(CaptureCommandMenu.CreateSplitButton(compact, () => settings, calls.Add));
        Button main = Assert.IsType<Button>(split.Children[0]);
        Button options = Assert.IsType<Button>(split.Children[1]);
        Assert.Equal("NewCapture", main.Name);
        Assert.Equal("CaptureOptions", options.Name);
        Assert.True(main.Focusable && main.IsTabStop && options.Focusable && options.IsTabStop);
        Assert.Equal(UiText.Get("CaptureEntry.NewCapture"), AutomationProperties.GetName(main));
        Assert.Equal(UiText.Get("CaptureEntry.MoreOptions"), AutomationProperties.GetName(options));
        Assert.Equal(UiText.Format("LibraryUx_CommandShortcut", UiText.Get("CaptureEntry.Region"), settings.Capture.ToString()), main.ToolTip);
        var window = new Window { Content = split, Width = 320, Height = 140,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            main.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([GlobalHotkeyCommand.CaptureRegion], calls);
            Assert.Null(main.ContextMenu);
            Assert.Null(options.ContextMenu);
            // UI Automation invokes the same Button activation used by keyboard Enter/Space.
            var peer = new ButtonAutomationPeer(options);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            options.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.True(options.ContextMenu?.IsOpen);
            Assert.Same(options, options.ContextMenu!.PlacementTarget);
            Assert.Single(calls);
            Assert.Equal(4, options.ContextMenu.Items.Count);
        }
        finally { if (options.ContextMenu is { } menu) menu.IsOpen = false; window.Close(); }
    });

    [Fact]
    public void SplitCornersFollowThemeRadiusAndOnlyFlattenTheJoinedEdges() => StaTestHost.Run(() =>
    {
        var main = new Button();
        var options = new Button();
        var ordinary = new Button();
        ButtonSizing.SetSplitPart(main, SplitButtonPart.Main);
        ButtonSizing.SetSplitPart(options, SplitButtonPart.Options);
        foreach (Button button in new[] { main, options, ordinary })
            ButtonSizing.SetCornerRadius(button, new CornerRadius(4, 5, 6, 7));
        Assert.Equal(new CornerRadius(4, 0, 0, 7), ButtonSizing.GetChromeCornerRadius(main));
        Assert.Equal(new CornerRadius(0, 5, 6, 0), ButtonSizing.GetChromeCornerRadius(options));
        Assert.Equal(new CornerRadius(4, 5, 6, 7), ButtonSizing.GetChromeCornerRadius(ordinary));
        ButtonSizing.SetCornerRadius(main, new CornerRadius(11));
        Assert.Equal(new CornerRadius(11, 0, 0, 11), ButtonSizing.GetChromeCornerRadius(main));
    });

    [Theory]
    [InlineData(0, GlobalHotkeyCommand.CaptureRegion)]
    [InlineData(1, GlobalHotkeyCommand.CaptureFullScreen)]
    [InlineData(2, GlobalHotkeyCommand.CaptureWindow)]
    [InlineData(3, GlobalHotkeyCommand.RecordRegion)]
    public void MenuDispatchesOnceAfterThePopupCloses(int index, GlobalHotkeyCommand expected) => StaTestHost.Run(() =>
    {
        var calls = new List<GlobalHotkeyCommand>();
        ContextMenu? menu = null;
        menu = CaptureCommandMenu.Create(execute: command =>
        {
            Assert.False(menu!.IsOpen);
            calls.Add(command);
        });
        var anchor = new Button();
        var window = new Window { Content = anchor, Width = 200, Height = 100,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
            Assert.True(menu.IsOpen);
            ((MenuItem)menu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(menu.IsOpen);
            Assert.Empty(calls);
            menu.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.Equal(new[] { expected }, calls);
        }
        finally { menu.IsOpen = false; window.Close(); }
    });
}
