using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Capture;
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
        settings.CaptureFullScreen = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.Equal(settings.CaptureFullScreen.ToString(), items[1].InputGestureText);
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
