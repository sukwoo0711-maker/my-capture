using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Shell notifications draw their own shadow inside a transparent, layered window. The implicit
/// window style turns the modern chrome on, which used to paint Surface.Base over that
/// transparent background on SourceInitialized and showed the shadow margin as an opaque frame.
/// Both tests give the window a Surface.Base brush and the chrome-enabling style the app's
/// implicit window style applies, so the old behaviour reproduces here.
/// </summary>
public sealed class NotificationTransparencyTests
{
    [Fact]
    public void NotificationWindowKeepsItsTransparentBackgroundAfterInit() => StaTestHost.Run(() =>
    {
        Window window = ThemedShellPresenter.CreateNotificationWindow(new Border(), "MyCapture", new Thickness(16));
        try
        {
            window.Resources["Surface.Base"] = Brushes.Red;
            window.Style = ChromeStyle();
            new WindowInteropHelper(window).EnsureHandle();
            Assert.False(ModernWindowChrome.GetEnabled(window));
            Assert.Same(Brushes.Transparent, window.Background);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ChromeTurnedOffBeforeInitDoesNotPaintTheBackground() => StaTestHost.Run(() =>
    {
        var window = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent };
        window.Resources["Surface.Base"] = Brushes.Red;
        try
        {
            ModernWindowChrome.SetEnabled(window, true);
            ModernWindowChrome.SetEnabled(window, false);
            new WindowInteropHelper(window).EnsureHandle();
            Assert.Same(Brushes.Transparent, window.Background);
        }
        finally
        {
            window.Close();
        }
    });

    private static Style ChromeStyle()
    {
        var style = new Style(typeof(Window));
        style.Setters.Add(new Setter(ModernWindowChrome.EnabledProperty, true));
        return style;
    }
}
