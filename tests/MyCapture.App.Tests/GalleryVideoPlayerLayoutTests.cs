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
                Size smallVideoSize = default;
                foreach ((double width, double height) in new[] { (420d, 360d), (1000d, 750d), (1400d, 900d) })
                {
                    player.Width = width;
                    player.Height = height;
                    FlushLayout(player);
                    AssertVideoFillsRemainingSpace(player, new Size(width, height));
                    if (smallVideoSize == default) smallVideoSize = frame.RenderSize;
                }
                Assert.True(frame.ActualHeight > smallVideoSize.Height && frame.ActualWidth > smallVideoSize.Width,
                    $"Video did not grow from {smallVideoSize} to {frame.RenderSize}. " + DescribeLayout(player, new Size(1400, 900)));
                player.Width = 420;
                player.Height = 360;
                FlushLayout(player);
                AssertVideoFillsRemainingSpace(player, new Size(420, 360));
                DpiScale dpi = VisualTreeHelper.GetDpi(player);
                Assert.InRange(Math.Abs(frame.ActualHeight - smallVideoSize.Height), 0, 1 / dpi.DpiScaleY);
                Assert.InRange(Math.Abs(frame.ActualWidth - smallVideoSize.Width), 0, 1 / dpi.DpiScaleX);
            }
            finally { player.Close(); }
        });

    [Fact]
    public Task AWindowSizeLimitStillGivesTheVideoAllAvailableSpace() => RunIsolated(
        nameof(AWindowSizeLimitStillGivesTheVideoAllAvailableSpace), () =>
        {
            GalleryVideoPlayerWindow player = CreatePlayer();
            try
            {
                // Exercise the same boundary as a hosted desktop limiting native window size,
                // even when the local monitor can accommodate the requested 1400 x 900 window.
                player.MaxWidth = Math.Min(720, SystemParameters.WorkArea.Width);
                player.MaxHeight = Math.Min(520, SystemParameters.WorkArea.Height);
                player.Show();
                player.Width = 1400;
                player.Height = 900;
                FlushLayout(player);
                DpiScale dpi = VisualTreeHelper.GetDpi(player);
                Assert.InRange(player.ActualWidth, 1, player.MaxWidth + 1 / dpi.DpiScaleX);
                Assert.InRange(player.ActualHeight, 1, player.MaxHeight + 1 / dpi.DpiScaleY);
                AssertVideoFillsRemainingSpace(player, new Size(1400, 900));
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
                    Button saveFrame = Assert.Single(Descendants(player).OfType<Button>(), button => button.Name == "PlayerSaveFrame");
                    Assert.True(saveFrame.IsVisible);
                    Assert.Equal("Shift+S", AutomationProperties.GetAcceleratorKey(saveFrame));
                    // Successful capture adds a navigation action. It must fit at the same narrow sizes.
                    Button showFrame = Assert.Single(Descendants(player).OfType<Button>(), button => button.Name == "PlayerShowFrame");
                    Assert.Equal(Visibility.Collapsed, showFrame.Visibility);
                    showFrame.Visibility = Visibility.Visible;
                    FlushLayout(player);
                    AssertTransportInside(player);
                    AssertVideoFillsRemainingSpace(player, new Size(720 / scale, 720 / scale));
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

    private static void AssertVideoFillsRemainingSpace(GalleryVideoPlayerWindow player, Size requested)
    {
        var content = Assert.IsType<Border>(player.Content);
        var frame = Assert.IsType<Border>(player.MediaForDiagnostic.Parent);
        var layout = Assert.IsType<Grid>(frame.Parent);
        FrameworkElement header = Assert.Single(layout.Children.OfType<FrameworkElement>(), child => Grid.GetRow(child) == 0);
        FrameworkElement transport = Assert.Single(layout.Children.OfType<FrameworkElement>(), child => Grid.GetRow(child) == 2);
        // Native sizing can clamp the request. The video must fill the actual client space
        // left by the header and transport, including when the transport wraps.
        double expectedWidth = content.ActualWidth - content.Padding.Left - content.Padding.Right;
        double expectedHeight = content.ActualHeight - content.Padding.Top - content.Padding.Bottom
            - header.ActualHeight - transport.ActualHeight;
        Assert.True(expectedWidth > 0 && expectedHeight > 0,
            $"No video space remains: {expectedWidth} x {expectedHeight}. " + DescribeLayout(player, requested));
        Rect actual = frame.TransformToAncestor(content).TransformBounds(new Rect(frame.RenderSize));
        var expected = new Rect(content.Padding.Left, content.Padding.Top + header.ActualHeight, expectedWidth, expectedHeight);
        DpiScale dpi = VisualTreeHelper.GetDpi(player);
        string diagnostic = $"Video {actual}; remaining client {expected}. " + DescribeLayout(player, requested);
        Assert.True(Math.Abs(actual.X - expected.X) <= 1 / dpi.DpiScaleX
            && Math.Abs(actual.Width - expected.Width) <= 1 / dpi.DpiScaleX
            && Math.Abs(actual.Y - expected.Y) <= 1 / dpi.DpiScaleY
            && Math.Abs(actual.Height - expected.Height) <= 1 / dpi.DpiScaleY, diagnostic);
        Assert.True(double.IsNaN(frame.Height));
        Assert.Equal(Stretch.Uniform, player.MediaForDiagnostic.Stretch);
        Assert.Equal(SizeToContent.Manual, player.SizeToContent);
        AssertTransportInside(player);
    }

    private static string DescribeLayout(GalleryVideoPlayerWindow player, Size requested) =>
        $"Requested {requested}; window {player.RenderSize}; client {((FrameworkElement)player.Content).RenderSize}; "
        + $"work area {SystemParameters.WorkArea}; maximum track {SystemParameters.MaximumWindowTrackWidth} x {SystemParameters.MaximumWindowTrackHeight}; "
        + $"DPI scale {VisualTreeHelper.GetDpi(player).DpiScaleX} x {VisualTreeHelper.GetDpi(player).DpiScaleY}.";

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
            element.IsVisible && (element is Button or ToggleButton or Slider || element is TextBlock { Text: var text } && text.Contains(" / ", StringComparison.Ordinal))))
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
