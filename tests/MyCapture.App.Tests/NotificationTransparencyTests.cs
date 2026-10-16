using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Layered shell notifications draw their own shadow inside a transparent window. Opaque
/// notifications have square corners and no shadow margin. The implicit
/// window style turns the modern chrome on, which used to paint Surface.Base over that
/// transparent background on SourceInitialized and showed the shadow margin as an opaque frame.
/// Both tests give the window a Surface.Base brush and the chrome-enabling style the app's
/// implicit window style applies, so the old behaviour reproduces here.
/// </summary>
public sealed class NotificationTransparencyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void NotificationWindowKeepsItsTransparentBackgroundAfterInit(bool? supported) => StaTestHost.Run(() =>
    {
        bool layered = supported ?? NativeExclusion.SupportsLayeredWindows;
        using var mode = supported.HasValue ? new ExclusionMode(supported.Value) : null;
        using var presenter = new ThemedShellPresenter(() => false);
        Window window = presenter.CreateActionNotification("MyCapture", "Message", "Open", () => { });
        try
        {
            window.Resources["Surface.Base"] = Brushes.Red;
            window.Style = ChromeStyle();
            new WindowInteropHelper(window).EnsureHandle();
            Assert.False(ModernWindowChrome.GetEnabled(window));
            NativeExclusion.AssertWindow(window, layered);
            if (layered) Assert.Same(Brushes.Transparent, window.Background);
            Grid surface = Assert.IsType<Grid>(window.Content);
            Assert.Equal(new Thickness(layered ? ThemedShellPresenter.ShadowMargin : 0), surface.Margin);
            Assert.All(surface.Children.OfType<Border>(), border =>
            {
                Assert.Equal(layered ? ThemedShellPresenter.CardCorner : new CornerRadius(0), border.CornerRadius);
                if (!layered) Assert.Null(border.Effect);
            });
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
