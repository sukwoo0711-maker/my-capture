using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class QuickAccessHierarchyTests
{
    [Theory]
    [InlineData("ko", 1.0)]
    [InlineData("en", 1.0)]
    [InlineData("ko", 1.5)]
    [InlineData("en", 1.5)]
    public void MaterialPrecedesTheThreeCommandsAndCloseOccupiesThePreviewControlLane(string language, double scale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        foreach ((int width, int height) in new[] { (640, 360), (360, 640), (6000, 20), (20, 6000), (1, 1), (1920, 1080) })
        {
            var card = Card(Image(width, height), scale);
            try
            {
                Border host = QuickAccessDetachedFixture.Layout(card, scale);
                Assert.Equal(["Pin", "Edit", "Copy"], QuickAccessWindow.ActionOrder);
                Rect preview = Bounds(card.PreviewForTest, host);
                Rect close = Bounds(card.CloseButtonForTest, host);
                var previewArea = Assert.IsType<Grid>(Assert.IsType<DockPanel>(card.CloseButtonForTest.Parent).Parent);
                Rect area = Bounds(previewArea, host);
                Assert.True(area.Contains(close));
                Assert.True(close.Bottom <= preview.Top);
                Assert.True(close.Right <= area.Right);
                Assert.True(preview.Bottom <= Bounds(card.ActionButtonsForTest[0], host).Top);
                Assert.Equal((double)width / height, card.PreviewForTest.Width / card.PreviewForTest.Height, 6);
                Assert.True(card.PreviewForTest.Width * scale <= width * 2 + 0.00001);
                Assert.True(card.PreviewForTest.Height * scale <= height * 2 + 0.00001);
                Assert.Same(host.FindResource("Button.Primary"), card.ActionButtonsForTest[0].Style);
                Assert.All(card.ActionButtonsForTest.Skip(1), button => Assert.Same(host.FindResource("Button.Ghost"), button.Style));
                Assert.DoesNotContain(QuickAccessDetachedFixture.Descendants(host).OfType<Button>(),
                    button => AutomationProperties.GetAutomationId(button) is "QuickAccess.Library" or "QuickAccess.Save" or "QuickAccess.CopyText");
            }
            finally { card.Close(); }
        }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void LibraryRemainsReachableThroughTheExistingMenuAndClosesTheCard(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        int opened = 0, closed = 0;
        var card = new QuickAccessWindow(Image(160, 90), () => Task.FromResult(true), () => { },
            () => Task.CompletedTask, () => Task.FromResult(true), () => opened++, () => Task.FromResult("unused.png"));
        card.Closed += (_, _) => closed++;
        try
        {
            MenuItem library = card.LibraryMenuItemForTest;
            Assert.Equal(UiText.Get("QuickAccess.Library"), library.Header);
            Assert.Equal(library.Header, AutomationProperties.GetName(library));
            Assert.True(library.IsEnabled);
            library.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, opened);
            Assert.Equal(1, closed);
            Assert.True(card.ChoseLibraryEditOrPin);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Theory]
    [InlineData(96.0, 192.0, 1.0)]
    [InlineData(192.0, 96.0, 1.5)]
    public void UnequalBitmapDpiMetadataCannotChangeTheRenderedPixelAspect(double dpiX, double dpiY, double scale) => StaTestHost.Run(() =>
    {
        BitmapSource source = BitmapSource.Create(120, 80, dpiX, dpiY, PixelFormats.Bgra32, null, new byte[120 * 80 * 4], 120 * 4);
        source.Freeze();
        var card = Card(source, scale);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, scale);
            _ = QuickAccessLabelFixture.Render(host, scale);
            Image preview = card.PreviewForTest;
            var drawing = Assert.Single(Images(VisualTreeHelper.GetDrawing(preview)!));
            Assert.Same(source, drawing.ImageSource);
            Assert.Equal(1.5, drawing.Rect.Width / drawing.Rect.Height, 6);
            Assert.Equal(preview.RenderSize, drawing.Rect.Size);
            Assert.True(drawing.Rect.Width * scale <= 240 + 0.00001);
            Assert.True(drawing.Rect.Height * scale <= 160 + 0.00001);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void HighContrastUsesSystemChromeWithoutChangingTheSourceImage() => SystemHighContrastThemeTests.Simulate(() =>
    {
        BitmapSource source = Image(160, 90);
        var card = Card(source);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card);
            ThemeService.ApplyResources(AppTheme.Glass, host.Resources, systemHighContrast: true);
            host.UpdateLayout();
            Assert.Same(source, card.PreviewForTest.Source);
            Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.Window],
                Assert.IsType<SolidColorBrush>(card.SurfaceForTest.Background).Color);
            Button primary = card.ActionButtonsForTest[0];
            Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.Highlight],
                Assert.IsType<SolidColorBrush>(primary.Background).Color);
            Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.HighlightText],
                Assert.IsType<SolidColorBrush>(primary.Foreground).Color);
            Assert.Equal(36, card.CloseButtonForTest.ActualHeight);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void AltQEntersOnPinThenTabTraversesTheCommandsAndEnterPinsOnce() => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var router = new ReceiptKeyboardRouter(_ => new QuickAccessContextMenuTests.NoHotkeys());
        int pinned = 0;
        var card = new QuickAccessWindow(Image(160, 90), () => Task.FromResult(true), () => { },
            () => { pinned++; return Task.CompletedTask; }, () => Task.FromResult(true), () => { },
            () => Task.FromResult("unused.png"), excludeFromCapture: false, targetMonitor: Offscreen(), keyboardRouter: router);
        try
        {
            Ocr410Fixture.LoadTheme(card);
            card.IsKept = true;
            card.Show();
            card.UpdateLayout();
            Assert.True(card.HandleShortcut(Key.Q, ModifierKeys.Alt));
            Button primary = card.ActionButtonsForTest[0];
            IntPtr cardHandle = new WindowInteropHelper(card).Handle;
            Assert.True(primary.IsKeyboardFocused,
                $"Alt+Q: active={card.IsActive}, visible={card.IsVisible}, loaded={card.IsLoaded}, focusable={primary.Focusable}, enabled={primary.IsEnabled}, focused={Keyboard.FocusedElement}, hwnd={cardHandle}, bounds={WindowStyleFacade.GetWindowBounds(cardHandle)}, foreground={NativeExclusion.GetForegroundWindow()}");
            Assert.True(KeyboardFocusCue.GetIsVisible(primary));
            FrameworkElement[] path = [card.ActionButtonsForTest[1], card.ActionButtonsForTest[2], card.MoreButtonForTest,
                card.KeepToggleForTest, card.CloseButtonForTest, primary];
            foreach (FrameworkElement next in path)
            {
                var current = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
                Assert.True(current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(next.IsKeyboardFocused, $"Expected Tab focus on {AutomationProperties.GetAutomationId(next)}");
            }
            Assert.True(EditorKeysTests.Press(card, Key.Enter, ime: false));
            Assert.Equal(1, pinned);
            Assert.True(primary.IsKeyboardFocused);
            Assert.True(card.HandleShortcut(Key.Apps, ModifierKeys.None));
            Assert.True(card.CardMenu.IsOpen);
            Assert.All(card.CardMenu.Items.OfType<MenuItem>(), item => Assert.True(item.Focusable));
            card.CardMenu.IsOpen = false;
            Assert.True(EditorKeysTests.Press(card, Key.Escape, ime: false));
            Assert.False(card.IsVisible);
        }
        finally { card.CardMenu.IsOpen = false; card.Close(); FluidMotion.AnimationsEnabledOverrideForTest = previousMotion; }
    });

    [Fact]
    public void PendingSaveAndFailureKeepOneStatusLineAndTheFullFailureDetail() => StaTestHost.Run(() =>
    {
        var card = QuickAccessWindow.ForPendingCapture(Image(320, 200), copiedToClipboard: true, targetMonitor: Offscreen());
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card);
            Assert.Equal((UiText.Get("QuickAccess.Saving"), FeedbackKind.Progress, true, false), card.StatusForTest);
            TextBlock status = QuickAccessDetachedFixture.Descendants(host).OfType<TextBlock>()
                .Single(text => KoreanWordWrap.Strip(text.Text) == UiText.Get("QuickAccess.Saving"));
            Assert.Equal(TextWrapping.NoWrap, status.TextWrapping);
            const string title = "Capture could not be saved";
            const string detail = "There is not enough space in the selected folder.";
            card.ShowCaptureFailure(title, detail);
            Assert.Equal((title, FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Equal(title + " " + detail, card.LastAnnouncementForTest);
            Assert.Equal((detail, true), card.DetailForTest);
            Assert.Equal(TextWrapping.NoWrap, status.TextWrapping);
            Assert.Equal(title, status.ToolTip);
            card.Advance(TimeSpan.FromMinutes(1), interacting: false);
            Assert.All(card.CaptureActionControlsForTest, control => Assert.False(control.IsEnabled));
        }
        finally { card.Close(); }
    });

    private static Rect Bounds(FrameworkElement control, Visual ancestor) =>
        control.TransformToAncestor(ancestor).TransformBounds(new Rect(control.RenderSize));

    private static IEnumerable<ImageDrawing> Images(Drawing drawing)
    {
        if (drawing is ImageDrawing image) yield return image;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (ImageDrawing childImage in Images(child)) yield return childImage;
    }

    private static QuickAccessWindow Card(BitmapSource source, double scale = 1) =>
        new(source, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true),
            () => { }, () => Task.FromResult("unused.png"), targetMonitor: new MonitorInfo("test", new RectD(0, 0, 1366, 768),
                new RectD(0, 0, 1366, 728), (uint)(96 * scale), true));

    private static MonitorInfo Offscreen() => new("test", new RectD(-32000, -32000, 1600, 1000),
        new RectD(-32000, -32000, 1600, 1000), MonitorEnumerator.GetPrimary().Dpi, false);

    private static BitmapSource Image(int width, int height)
    {
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        source.Freeze();
        return source;
    }
}
