using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ux410PolishTests
{
    [Fact]
    public void WindowTitlesKeepContextWithoutVersionOrRepeatedBrand()
    {
        Assert.Equal("MyCapture — Settings", AppIdentity.FormatWindowTitle(AppIdentity.Label + " — Settings"));
        Assert.Equal("MyCapture — Settings", AppIdentity.FormatWindowTitle("MyCapture — Settings"));
        Assert.Equal("MyCapture", AppIdentity.FormatWindowTitle(null));
        Assert.Equal("MyCapture — v4.0.2 screenshot.png", AppIdentity.FormatWindowTitle("v4.0.2 screenshot.png"));
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void HelpAndWelcomeUseFixedChromeAndAccessibleKeycaps(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var help = new ShortcutHelpWindow();
        Assert.Equal(ResizeMode.NoResize, help.ResizeMode);
        help.Close();
        var welcome = new ShortcutHelpWindow(true);
        Assert.Equal(ResizeMode.NoResize, welcome.ResizeMode);
        var panel = ((DockPanel)welcome.Content).Children.OfType<StackPanel>().Single();
        var chord = Assert.IsType<WrapPanel>(panel.Children.OfType<ContentControl>().Single(c => c is not Button).Content);
        Assert.Equal(ShortcutHelpWindow.ReadHotkeys().Capture.ToString(), AutomationProperties.GetName(chord));
        Assert.NotEmpty(chord.Children.OfType<Border>());
        welcome.Close();
    });

    [Fact]
    public void ErrorHasOneContextHeadingAndAProductCaption() => StaTestHost.Run(() =>
    {
        var dialog = new ThemedDialog("Try another location.", "MyCapture — Save failed", MessageBoxButton.OK, MessageBoxResult.OK);
        Assert.Equal("MyCapture", dialog.Title);
        var body = (StackPanel)((ScrollViewer)dialog.Content).Content;
        Assert.Equal("Save failed", body.Children.OfType<TextBlock>().First().Text);
        dialog.Close();
    });

    [Fact]
    public void FloatingCardHasAccessibleCloseAndKeepsNonActivation() => StaTestHost.Run(() =>
    {
        var bitmap = BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[16],8);
        var card = new QuickAccessWindow(bitmap,()=>Task.FromResult(true),()=>{},()=>Task.CompletedTask,
            ()=>Task.FromResult(true),()=>{},()=>Task.FromResult("unused.png"));
        Assert.Equal(WindowStyle.None,card.WindowStyle);
        Assert.False(card.ShowActivated);
        Assert.False(card.ShowInTaskbar);
        var panel = (StackPanel)((Border)card.Content).Child;
        var close = panel.Children.OfType<Button>().Single();
        Assert.Equal(UiText.Get("Dialog.Close"),AutomationProperties.GetName(close));
        Assert.False(close.Focusable);
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    });
}
