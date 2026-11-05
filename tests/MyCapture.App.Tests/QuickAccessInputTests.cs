using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class QuickAccessInputTests
{
    [Fact]
    public void AltQThenPRunsPin() => StaTestHost.Run(() =>
    {
        int pinned = 0;
        QuickAccessWindow card = Card(pin: () => pinned++);
        try
        {
            Show(card);
            KeyEventArgs activation = EditorKeysTests.KeyArgs(card, Key.Q, ime: false, Keyboard.PreviewKeyDownEvent);
            typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(activation, null);
            Assert.Equal(Key.System, activation.Key);
            Assert.Equal(Key.Q, activation.SystemKey);
            Assert.Equal(Key.Q, QuickAccessWindow.ResolveShortcutKey(activation));
            Assert.True(card.HandleKeyDown(activation, ModifierKeys.Alt));
            Assert.True(card.IsKeyboardFocusWithin);
            Assert.True(EditorKeysTests.Press(card, Key.P, ime: false));
            Assert.Equal(1, pinned);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void PreviewClickKeepsTheCardOutOfKeyboardMode() => StaTestHost.Run(() =>
    {
        int pinned = 0;
        QuickAccessWindow card = Card(pin: () => pinned++);
        try
        {
            Show(card);
            Image preview = Preview(card);
            RaiseMouseButton(preview, UIElement.MouseLeftButtonDownEvent);
            RaiseMouseButton(preview, UIElement.MouseLeftButtonUpEvent);
            Assert.False(card.IsKeyboardFocusWithin);
            Assert.False(EditorKeysTests.Press(card, Key.P, ime: false));
            Assert.Equal(0, pinned);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreviewDragPastEitherThresholdDoesNotEnterKeyboardMode(bool horizontal) => StaTestHost.Run(() =>
    {
        int pinned = 0;
        QuickAccessWindow card = Card(pin: () => pinned++);
        try
        {
            Show(card);
            Image preview = Preview(card);
            DpiScale dpi = VisualTreeHelper.GetDpi(preview);
            double distance = Math.Ceiling(horizontal
                ? SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                : SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY);
            for (int placement = 0; placement < 2; placement++)
            {
                if (placement != 0)
                {
                    Point before = preview.PointToScreen(default);
                    card.Left += 1 / dpi.DpiScaleX;
                    card.UpdateLayout();
                    Assert.Equal(1, preview.PointToScreen(default).X - before.X, 6);
                }
                Point screenStart = preview.PointToScreen(new Point(10, 10));
                screenStart = new(Math.Round(screenStart.X), Math.Round(screenStart.Y));
                Point start = preview.PointFromScreen(screenStart);
                Point end = preview.PointFromScreen(new(screenStart.X + (horizontal ? distance : 0),
                    screenStart.Y + (horizontal ? 0 : distance)));
                card.BeginPreviewGesture(start);
                Assert.True(card.TryBeginPreviewDrag(end));
                Assert.False(card.TryBeginPreviewDrag(end));
                // Returning to the starting point after a drag must not turn mouse-up into a click.
                card.EndPreviewGesture(start);
                Assert.All(card.ActionButtonsForTest, button => Assert.False(button.Focusable));
                Assert.False(EditorKeysTests.Press(card, Key.P, ime: false));
                Assert.Equal(0, pinned);
            }
        }
        finally { card.Close(); }
    });

    [Fact]
    public void PreviewMovementBelowThresholdIsAClickThatKeepsForeground() => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = Card();
        try
        {
            Show(card);
            Point end = new(SystemParameters.MinimumHorizontalDragDistance / 2,
                SystemParameters.MinimumVerticalDragDistance / 2);
            card.BeginPreviewGesture(default);
            Assert.False(card.TryBeginPreviewDrag(end));
            card.EndPreviewGesture(end);
            Assert.False(card.IsKeyboardFocusWithin);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(true, 1, 10)]
    [InlineData(true, -1, 10)]
    [InlineData(false, 1, 10)]
    [InlineData(false, -1, 10)]
    [InlineData(true, 1, -10000)]
    [InlineData(true, -1, -10000)]
    [InlineData(false, 1, -10000)]
    [InlineData(false, -1, -10000)]
    public void PreviewDragBoundaryIsInclusiveWithoutAdmittingTheAdjacentInsidePixel(
        bool horizontal, int direction, double origin) => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = Card();
        try
        {
            Show(card);
            Image preview = Preview(card);
            DpiScale dpi = VisualTreeHelper.GetDpi(preview);
            Point start = new(origin, origin);
            double distance = Math.Ceiling(horizontal
                ? SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                : SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY);
            double boundary = origin + direction * distance;
            double inside = boundary - direction;
            card.BeginPreviewGesture(preview.PointFromScreen(start));
            Assert.False(card.TryBeginPreviewDrag(preview.PointFromScreen(horizontal ? new Point(inside, origin) : new Point(origin, inside))));
            Assert.True(card.TryBeginPreviewDrag(preview.PointFromScreen(horizontal ? new Point(boundary, origin) : new Point(origin, boundary))));
            Assert.False(card.TryBeginPreviewDrag(preview.PointFromScreen(horizontal ? new Point(boundary, origin) : new Point(origin, boundary))));
            card.EndPreviewGesture(preview.PointFromScreen(start));
            Assert.False(card.IsKeyboardFocusWithin);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, -1)]
    [InlineData(false, 1)]
    [InlineData(false, -1)]
    public void PreviewDragHonorsNativePixelBoundaryAcrossScreenPositions(bool horizontal, int direction) => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = Card();
        try
        {
            Show(card);
            Image preview = Preview(card);
            DpiScale dpi = VisualTreeHelper.GetDpi(preview);
            double pixels = horizontal
                ? SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                : SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
            int distance = checked((int)Math.Ceiling(pixels));
            Assert.True(distance > 0);
            // Exercise WPF's actual screen/client transforms, including the preview's offset.
            for (int pixel = -10000; pixel <= 10000; pixel++)
            {
                Point start = new(pixel, pixel);
                Point inside = horizontal ? new(pixel + direction * (distance - 1), pixel) : new(pixel, pixel + direction * (distance - 1));
                Point boundary = horizontal ? new(pixel + direction * distance, pixel) : new(pixel, pixel + direction * distance);
                card.BeginPreviewGesture(preview.PointFromScreen(start));
                Assert.False(card.TryBeginPreviewDrag(preview.PointFromScreen(inside)), $"Inside at pixel {pixel}, distance {distance}, dpi {dpi}.");
                Assert.True(card.TryBeginPreviewDrag(preview.PointFromScreen(boundary)), $"Boundary at pixel {pixel}, distance {distance}, dpi {dpi}.");
                Assert.False(card.TryBeginPreviewDrag(preview.PointFromScreen(boundary)));
                card.EndPreviewGesture(preview.PointFromScreen(start));
            }
            Assert.False(card.IsKeyboardFocusWithin);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(Key.P, true)]
    [InlineData(Key.E, true)]
    [InlineData(Key.T, true)]
    // M and G have no command in the current card catalog, including under the IME.
    [InlineData(Key.M, false)]
    [InlineData(Key.G, false)]
    public void ImeProcessedLettersKeepTheCatalogBehavior(Key key, bool expectedHandled) => StaTestHost.Run(() =>
    {
        int pinned = 0, edited = 0, textCopied = 0;
        QuickAccessWindow card = Card(() => pinned++, () => edited++, () => textCopied++);
        try
        {
            Show(card);
            card.ShortcutActions["Activate"]();
            Assert.True(card.IsActive);
            Assert.True(card.IsKeyboardFocusWithin);
            Assert.All(card.ActionButtonsForTest, button => Assert.True(button.Focusable));
            KeyEventArgs input = EditorKeysTests.KeyArgs(card, key, ime: true, Keyboard.PreviewKeyDownEvent);
            Assert.Equal(key, QuickAccessWindow.ResolveShortcutKey(input));
            card.RaiseEvent(input);
            Assert.Equal(expectedHandled, input.Handled);
            Assert.Equal(key == Key.P ? 1 : 0, pinned);
            Assert.Equal(key == Key.E ? 1 : 0, edited);
            Assert.Equal(key == Key.T ? 1 : 0, textCopied);
            Assert.Equal(expectedHandled, ShortcutCatalog.Match(ShortcutScope.QuickAccess, key, ModifierKeys.None) is not null);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void ImeProcessedLetterDoesNotIntroduceAControlShortcut() => StaTestHost.Run(() =>
    {
        int pinned = 0;
        QuickAccessWindow card = Card(pin: () => pinned++);
        try
        {
            Show(card);
            KeyEventArgs input = EditorKeysTests.KeyArgs(card, Key.P, ime: true, Keyboard.PreviewKeyDownEvent);
            Assert.False(card.HandleKeyDown(input, ModifierKeys.Control));
            Assert.Equal(0, pinned);
        }
        finally { card.Close(); }
    });

    private static void Show(QuickAccessWindow card)
    {
        Ocr410Fixture.LoadTheme(card);
        card.Show();
        card.UpdateLayout();
    }

    private static Image Preview(QuickAccessWindow card) =>
        QuickAccessDetachedFixture.Descendants((DependencyObject)card.Content).OfType<Image>()
            .Single(image => AutomationProperties.GetAutomationId(image) == "QuickAccess.Preview");

    private static void RaiseMouseButton(UIElement target, RoutedEvent routedEvent) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = routedEvent });

    private static QuickAccessWindow Card(Action? pin = null, Action? edit = null, Action? copyText = null)
    {
        var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4);
        image.Freeze();
        return new(image, () => Task.FromResult(true), () => edit?.Invoke(),
            () => { pin?.Invoke(); return Task.CompletedTask; }, () => Task.FromResult(true), () => { },
            () => Task.FromResult("unused.png"), copyText: () =>
            {
                copyText?.Invoke();
                return Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Copied, null, 1));
            });
    }
}
