using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryVideoPlayerLayoutTests
{
    [Fact]
    public Task ResizingTheWindowGivesTheVideoTheRemainingSpace() => RunIsolated(
        nameof(ResizingTheWindowGivesTheVideoTheRemainingSpace), () =>
        {
            GalleryVideoPlayerWindow player = CreatePlayer();
            try
            {
                player.Show();
                var frame = Assert.IsType<Border>(player.MediaForDiagnostic.Parent);
                double previousHeight = 0;
                double previousWidth = 0;
                foreach ((double width, double height) in new[] { (420d, 360d), (1000d, 750d), (1400d, 900d) })
                {
                    player.Width = width;
                    player.Height = height;
                    FlushLayout(player);
                    Assert.True(frame.ActualHeight > previousHeight + 100,
                        $"Video height {frame.ActualHeight} after {previousHeight} at {width} x {height}.");
                    Assert.True(frame.ActualWidth > previousWidth + 100);
                    Assert.True(double.IsNaN(frame.Height));
                    Assert.Equal(Stretch.Uniform, player.MediaForDiagnostic.Stretch);
                    Assert.Equal(SizeToContent.Manual, player.SizeToContent);
                    AssertTransportInside(player);
                    previousHeight = frame.ActualHeight;
                    previousWidth = frame.ActualWidth;
                }
                player.Width = 420;
                player.Height = 360;
                FlushLayout(player);
                Assert.InRange(frame.ActualHeight, 100, previousHeight - 100);
                AssertTransportInside(player);
            }
            finally { player.Close(); }
        });

    [Fact]
    public Task NarrowWindowsKeepAllTransportActionsAndTimeVisible() => RunIsolated(
        nameof(NarrowWindowsKeepAllTransportActionsAndTimeVisible), () =>
        {
            foreach (string language in new[] { "ko-KR", "en-US" })
            foreach (double scale in new[] { 1d, 1.5d, 2d })
            {
                using var culture = UiText.UseLanguage(language);
                GalleryVideoPlayerWindow player = CreatePlayer();
                try
                {
                    // The same 720 x 720 physical work area has fewer DIPs at higher DPI.
                    player.Width = 720 / scale;
                    player.Height = 720 / scale;
                    player.FontSize = 18;
                    player.Show();
                    FlushLayout(player);
                    AssertTransportInside(player);
                    var frame = Assert.IsType<Border>(player.MediaForDiagnostic.Parent);
                    Assert.True(frame.ActualHeight >= 100, $"Video height {frame.ActualHeight} at {language}, {scale}x.");
                    ToggleButton fullScreen = Descendants(player).OfType<ToggleButton>().Single();
                    Assert.Equal(UiText.Get("CaptureGuide2_FullScreen"), AutomationProperties.GetName(fullScreen));
                    Assert.Equal("F11", AutomationProperties.GetAcceleratorKey(fullScreen));
                }
                finally { player.Close(); }
            }
        });

    [Fact]
    public Task SmallWorkAreasConstrainTheInitialSizeMinimumAndPosition() => RunIsolated(
        nameof(SmallWorkAreasConstrainTheInitialSizeMinimumAndPosition), () =>
        {
            foreach (Rect work in new[] { new Rect(0, 0, 320, 240), new Rect(-600, 100, 500, 400), new Rect(0, 0, 1280, 720) })
            {
                GalleryVideoPlayerWindow player = CreatePlayer();
                try
                {
                    player.Left = work.Right - 10;
                    player.Top = work.Bottom - 10;
                    player.ConstrainToWorkArea(work);
                    Assert.True(player.MinWidth <= work.Width && player.MinHeight <= work.Height);
                    Assert.True(player.Width >= player.MinWidth && player.Height >= player.MinHeight);
                    Assert.True(work.Contains(new Rect(player.Left, player.Top, player.Width, player.Height)));
                }
                finally { player.Close(); }
            }
        });

    [Fact]
    public Task FullScreenKeyboardAndVisibleButtonRestoreTheWindowAndKeepTheSameMedia() => RunIsolated(
        nameof(FullScreenKeyboardAndVisibleButtonRestoreTheWindowAndKeepTheSameMedia), () =>
        {
            GalleryVideoPlayerWindow player = CreatePlayer();
            try
            {
                player.Left = 40;
                player.Top = 40;
                player.Width = 600;
                player.Height = 450;
                player.Show();
                FlushLayout(player);
                MediaElement media = player.MediaForDiagnostic;
                var bounds = new Rect(player.Left, player.Top, player.ActualWidth, player.ActualHeight);
                ToggleButton button = Descendants(player).OfType<ToggleButton>().Single();

                Assert.True(Press(player, Key.F11).Handled);
                FlushLayout(player);
                Assert.True(button.IsChecked);
                Assert.Equal(WindowStyle.None, player.WindowStyle);
                Assert.Equal(WindowState.Maximized, player.WindowState);
                AssertTransportInside(player);
                Assert.True(Press(player, Key.Escape).Handled);
                AssertWindowRestored(player, bounds, media, button);

                button.IsChecked = true;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(WindowStyle.None, player.WindowStyle);
                Assert.True(Press(player, Key.F11).Handled);
                AssertWindowRestored(player, bounds, media, button);
            }
            finally { player.Close(); }
        });

    private static Task RunIsolated(string name, Action verify) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resourceName in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resourceName}.xaml") });
        verify();
    }, typeof(GalleryVideoPlayerLayoutTests));

    private static GalleryVideoPlayerWindow CreatePlayer() => new()
    {
        Left = 0, Top = 0, Topmost = false, ShowActivated = false, Opacity = 0,
    };

    private static void FlushLayout(Window player) =>
        player.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(player.UpdateLayout));

    private static KeyEventArgs Press(Window player, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(player), 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        player.RaiseEvent(args);
        return args;
    }

    private static void AssertWindowRestored(GalleryVideoPlayerWindow player, Rect bounds, MediaElement media, ToggleButton button)
    {
        FlushLayout(player);
        Assert.False(button.IsChecked);
        Assert.Equal(WindowStyle.ToolWindow, player.WindowStyle);
        Assert.Equal(WindowState.Normal, player.WindowState);
        Assert.Equal(ResizeMode.CanResize, player.ResizeMode);
        Assert.InRange(Math.Abs(player.Left - bounds.Left), 0, 1);
        Assert.InRange(Math.Abs(player.Top - bounds.Top), 0, 1);
        Assert.InRange(Math.Abs(player.ActualWidth - bounds.Width), 0, 1);
        Assert.InRange(Math.Abs(player.ActualHeight - bounds.Height), 0, 1);
        Assert.Same(media, player.MediaForDiagnostic);
        AssertTransportInside(player);
    }

    private static void AssertTransportInside(GalleryVideoPlayerWindow player)
    {
        var content = Assert.IsType<Border>(player.Content);
        foreach (FrameworkElement control in Descendants(content).OfType<FrameworkElement>().Where(element =>
            element is Button or ToggleButton or Slider || element is TextBlock { Text: var text } && text.Contains(" / ", StringComparison.Ordinal)))
        {
            Rect bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5
                && bounds.Right <= content.ActualWidth + 0.5 && bounds.Bottom <= content.ActualHeight + 0.5,
                $"{control.GetType().Name} is outside the client: {bounds}, client {content.RenderSize}.");
            Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
