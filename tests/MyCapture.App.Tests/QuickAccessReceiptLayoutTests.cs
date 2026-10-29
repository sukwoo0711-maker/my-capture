using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class QuickAccessReceiptLayoutTests
{
    [Theory]
    [InlineData("ko", false, "저장됨")]
    [InlineData("ko", true, "복사됨")]
    [InlineData("en", false, "Saved")]
    [InlineData("en", true, "Copied")]
    public void InitialReceiptReportsTheActualClipboardOutcome(string language, bool copied, string expected) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        int copyCalls = 0, saveCalls = 0;
        var card = new QuickAccessWindow(Image(32, 32), () => { copyCalls++; return Task.FromResult(true); },
            () => { }, () => Task.CompletedTask, () => { saveCalls++; return Task.FromResult(true); },
            () => { }, () => Task.FromResult("unused.png"), copiedToClipboard: copied);
        try
        {
            Assert.Equal((expected, FeedbackKind.Confirmation, true, false), card.StatusForTest);
            Assert.Equal((0, 0), (copyCalls, saveCalls));
            Assert.Equal(QuickAccessWindow.Lifetime, card.RemainingForTest);
            Assert.False(card.IsVisible);
        }
        finally { card.Close(); }
    });

    public static IEnumerable<object[]> PreviewCases()
    {
        foreach ((int width, int height) in new[] { (2400, 120), (120, 2400), (24, 16) })
        foreach (double bitmapDpi in new[] { 96.0, 144.0 })
        foreach (double displayScale in new[] { 1.0, 1.5, 2.0 })
            yield return [width, height, bitmapDpi, displayScale];
    }

    [Theory]
    [MemberData(nameof(PreviewCases))]
    public void PreviewPreservesAspectAndCapsOriginalPixelsAtEveryDisplayScale(int width, int height, double dpi, double scale) => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = Image(width, height, dpi);
        QuickAccessWindow card = Card(bitmap, scale: scale);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, scale);
            var preview = Assert.Single(QuickAccessDetachedFixture.Descendants(host).OfType<Image>());
            var surface = Assert.IsType<Border>(preview.Parent);
            Assert.Same(bitmap, preview.Source);
            Assert.Equal(Stretch.Uniform, preview.Stretch);
            Assert.InRange(surface.ActualHeight, 0.1, 180);
            Assert.True(surface.ClipToBounds);

            // Inspect WPF's actual image drawing, including its Uniform stretch result.
            ImageDrawing drawing = RenderedPreview(host, preview, scale);
            Assert.Same(bitmap, drawing.ImageSource);
            Assert.Equal(bitmap.Width / bitmap.Height, drawing.Rect.Width / drawing.Rect.Height, 6);
            Assert.InRange(drawing.Rect.Height, 0.01, 180);
            Assert.True(drawing.Rect.Width * scale <= bitmap.PixelWidth * 2 + 0.01);
            Assert.True(drawing.Rect.Height * scale <= bitmap.PixelHeight * 2 + 0.01);
            if (width == 24)
            {
                AssertPhysicalExtent(bitmap.PixelWidth * 2, drawing.Rect.Width * scale);
                AssertPhysicalExtent(bitmap.PixelHeight * 2, drawing.Rect.Height * scale);
            }
            Rect bounds = preview.TransformToAncestor(surface).TransformBounds(drawing.Rect);
            Assert.True(bounds.Left >= -0.01 && bounds.Top >= -0.01
                && bounds.Right <= surface.ActualWidth + 0.01 && bounds.Bottom <= surface.ActualHeight + 0.01,
                $"Preview leaves its surface: {bounds} in {surface.RenderSize}");
            Assert.False(card.IsVisible);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    public void PreviewReflowsOnDisplayDpiChangesAndKeepsTheCapWhenItsImageChanges(double bitmapDpi) => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = Image(24, 16, bitmapDpi);
        QuickAccessWindow card = Card(bitmap);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card);
            Image preview = Assert.Single(QuickAccessDetachedFixture.Descendants(host).OfType<Image>());
            double previousScale = 1;
            foreach (double scale in new[] { 1.5, 2.0, 1.0, 2.0 })
            {
                typeof(QuickAccessWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(card, [new DpiScale(previousScale, previousScale), new DpiScale(scale, scale)]);
                LayoutAtScale(host, card.Width, scale);
                ImageDrawing drawing = RenderedPreview(host, preview, scale);
                Assert.Same(bitmap, drawing.ImageSource);
                AssertPhysicalExtent(48, drawing.Rect.Width * scale);
                AssertPhysicalExtent(32, drawing.Rect.Height * scale);
                Assert.InRange(((Border)preview.Parent).ActualHeight, 0.01, 180);
                previousScale = scale;
            }

            bitmap = Image(30, 18, bitmapDpi);
            card.UpdatePreview(bitmap);
            LayoutAtScale(host, card.Width, previousScale);
            ImageDrawing updated = RenderedPreview(host, preview, previousScale);
            Assert.Same(bitmap, updated.ImageSource);
            AssertPhysicalExtent(60, updated.Rect.Width * previousScale);
            AssertPhysicalExtent(36, updated.Rect.Height * previousScale);
            Assert.False(card.IsVisible);
            Assert.False(card.CanActivateByHotkey);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("ko", 2400, 120)]
    [InlineData("en", 2400, 120)]
    [InlineData("ko", 120, 2400)]
    [InlineData("en", 120, 2400)]
    public void ReceiptAndActionsFitA1366By768DisplayAt150Percent(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        QuickAccessWindow card = Card(Image(width, height), UiText.Get("Onboard_FirstCaptureTip"), scale: 1.5);
        try
        {
            Border host = QuickAccessDetachedFixture.Layout(card, scale: 1.5);
            Assert.Equal(1.5, VisualTreeHelper.GetDpi(host).DpiScaleX);
            Assert.True(host.ActualWidth * 1.5 <= 1366);
            Assert.True(host.ActualHeight * 1.5 <= 768);
            Rect position = QuickAccessWindow.Position(new RectD(0, 0, 1366, 768), 1.5,
                host.ActualWidth, host.ActualHeight);
            Assert.True(position.Left >= 0 && position.Top >= 0
                && position.Right <= 1366 / 1.5 && position.Bottom <= 768 / 1.5);

            var buttons = QuickAccessDetachedFixture.Descendants(host).OfType<Button>().ToArray();
            Button close = Assert.Single(buttons, button => AutomationProperties.GetName(button) == UiText.Get("Dialog.Close"));
            Assert.Equal("Esc", AutomationProperties.GetAcceleratorKey(close));
            Button library = Assert.Single(buttons, button => AutomationProperties.GetName(button) == UiText.Get("QuickAccess.Library"));
            Assert.Equal(UiText.Get("QuickAccess.Library"), Assert.IsType<TextBlock>(QuickAccessActionPanel.LabelOf(library)).Text);

            FrameworkElement[] controls = [.. card.ActionButtonsForTest, library, close, card.KeepToggleForTest];
            foreach (FrameworkElement control in controls)
            {
                Rect bounds = Bounds(control, host);
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= host.ActualWidth + 0.5
                    && bounds.Bottom <= host.ActualHeight + 0.5, $"Control outside card: {bounds}");
                Assert.True(control.ActualWidth >= 28 && control.ActualHeight >= 28);
            }

            var actions = card.ActionButtonsForTest.Append(library).ToArray();
            for (int i = 1; i < actions.Length; i++)
                Assert.True(Bounds(actions[i - 1], host).Right <= Bounds(actions[i], host).Left,
                    "Adjacent receipt actions overlap");
            Assert.Contains(QuickAccessDetachedFixture.Descendants(host).OfType<TextBlock>(),
                text => text.Text == $"{width} × {height}");
            TextBlock tip = Assert.Single(QuickAccessDetachedFixture.Descendants(host).OfType<TextBlock>(),
                text => text.Text == UiText.Get("Onboard_FirstCaptureTip"));
            Assert.Equal(Visibility.Visible, tip.Visibility);
            Assert.True(tip.ActualHeight > 0);
            Assert.True(Bounds(tip, host).Bottom <= host.ActualHeight);
            Assert.False(card.IsVisible);
            Assert.False(card.CanActivateByHotkey);
        }
        finally { card.Close(); }
    });

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void CardMinimumYieldsToASmallerWorkAreaAndReturnsWhenTheMonitorChanges(double scale) => StaTestHost.Run(() =>
    {
        QuickAccessWindow card = Card(Image(2400, 120), scale: scale);
        try
        {
            double normalWidth = card.MinWidth;
            var work = new RectD(-640, 80, 280 * scale, 420 * scale);
            var small = new MonitorInfo("small", work, work, (uint)(96 * scale), true);
            card.ConstrainToWorkArea(small);
            card.UpdatePreview(Image(2400, 120));
            Border host = QuickAccessDetachedFixture.Layout(card, scale);
            Assert.Equal(280, card.Width);
            Assert.Equal(280, card.MinWidth);
            Assert.Equal(280, card.MaxWidth);
            Assert.Equal(420, card.MaxHeight);
            Assert.Equal(280, host.ActualWidth);
            Assert.True(card.PreviewForTest.ActualWidth <= host.ActualWidth);
            Rect position = QuickAccessWindow.Position(work, scale, normalWidth, 500,
                new RectD(work.Right - 1, work.Bottom - 1, 1, 1));
            // Fractional-DIP origins at 150% scaling can round a summed edge by about
            // 3e-14 DIP. Compare physical edges with the same subpixel tolerance as the preview.
            const double tolerance = 0.00001;
            Assert.True(position.Left * scale >= work.Left - tolerance && position.Top * scale >= work.Top - tolerance);
            Assert.True(position.Right * scale <= work.Right + tolerance && position.Bottom * scale <= work.Bottom + tolerance);

            var larger = new MonitorInfo("large", new RectD(0, 0, 1366, 768), new RectD(0, 0, 1366, 728),
                (uint)(96 * scale), true);
            card.ConstrainToWorkArea(larger);
            Assert.Equal(normalWidth, card.MinWidth);
            Assert.Equal(normalWidth, card.Width);
        }
        finally { card.Close(); }
    });

    private static void AssertPhysicalExtent(double expected, double actual)
    {
        // WPF's 144-DPI image conversion can produce 47.999998569 pixels for a
        // 48-pixel extent. This tolerance is in physical pixels, not display DIPs.
        Assert.InRange(Math.Abs(actual - expected), 0, 0.00001);
    }

    private static IEnumerable<ImageDrawing> ImageDrawings(Drawing drawing)
    {
        if (drawing is ImageDrawing image) yield return image;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (ImageDrawing nested in ImageDrawings(child)) yield return nested;
    }

    private static ImageDrawing RenderedPreview(Border host, Image preview, double scale)
    {
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale),
            (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rendered.Render(host);
        return Assert.Single(ImageDrawings(VisualTreeHelper.GetDrawing(preview)!));
    }

    private static void LayoutAtScale(Border host, double width, double scale)
    {
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
        host.InvalidateMeasure();
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    private static QuickAccessWindow Card(BitmapSource image, string? tip = null, double scale = 1) =>
        new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true),
            () => { }, () => Task.FromResult("unused.png"), firstCaptureTip: tip,
            targetMonitor: new MonitorInfo("test", new RectD(0, 0, 1366, 768), new RectD(0, 0, 1366, 768),
                (uint)(96 * scale), true));

    private static BitmapSource Image(int width, int height, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>Measures the shipped card content without an HWND, activation, hotkeys or clipboard access.</summary>
internal static class QuickAccessDetachedFixture
{
    internal static Border Layout(QuickAccessWindow card, double scale = 1)
    {
        Ocr410Fixture.LoadTheme(card);
        var content = (UIElement)card.Content;
        card.Content = null;
        var host = new Border
        {
            Child = content,
            Resources = card.Resources,
            UseLayoutRounding = card.UseLayoutRounding,
            SnapsToDevicePixels = card.SnapsToDevicePixels,
        };
        TextElement.SetFontFamily(host, card.FontFamily);
        TextElement.SetFontSize(host, card.FontSize);
        TextElement.SetForeground(host, card.Foreground);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
        host.Measure(new Size(card.Width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, card.Width, host.DesiredSize.Height));
        host.UpdateLayout();
        return host;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
