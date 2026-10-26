using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders the click-through recovery handle on a pin in both languages, both default themes and
/// two display scales, off screen. Set MYCAPTURE_PIN_HANDLE_EVIDENCE to a directory to keep the PNGs.
/// </summary>
public sealed class PinClickThroughHandleEvidenceTests
{
    private const double Margin = 20;
    private const double Gap = 24;

    [Fact]
    public Task HandleAndHintRenderOverThePinInEveryLanguageThemeAndScale() => SettingsThemePreviewTests.RunIsolated(
        nameof(HandleAndHintRenderOverThePinInEveryLanguageThemeAndScale), RenderEvidence,
        typeof(PinClickThroughHandleEvidenceTests));

    private static void RenderEvidence()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_HANDLE_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(output)) Directory.CreateDirectory(output);
        // Real window styles use Application resources, so this runs in its own process.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        using var scope = new PinClickThroughHandleTests.HandleScope();
        foreach (string language in new[] { "ko-KR", "en-US" })
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Glass, "dark"), (AppTheme.GlassLight, "light") })
        foreach (double scale in new[] { 1.0, 1.5 })
        {
            using var culture = UiText.UseLanguage(language);
            ThemeService.ApplyResources(theme, app.Resources, systemHighContrast: false, accent: AppAccent.Red);
            BitmapSource intro = Panel(theme, scale, hover: false);
            // A new installation leaves the shortcut unassigned, so the tooltip has no parentheses.
            BitmapSource hover = Panel(theme, scale, hover: true);
            Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
            var keys = new HotkeySettings { ToggleClickThrough = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54) };
            ShortcutHelpWindow.ReadHotkeys = () => keys;
            BitmapSource hoverWithShortcut;
            try { hoverWithShortcut = Panel(theme, scale, hover: true); }
            finally { ShortcutHelpWindow.ReadHotkeys = previous; }
            BitmapSource sheet = SideBySide([intro, hover, hoverWithShortcut], scale, theme);
            Assert.Equal(intro.PixelWidth + hover.PixelWidth + hoverWithShortcut.PixelWidth + 2 * (int)Math.Ceiling(Gap * scale), sheet.PixelWidth);
            if (string.IsNullOrWhiteSpace(output)) continue;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using FileStream stream = File.Create(Path.Combine(output,
                $"pin-handle-{language[..2]}-{themeName}-{scale * 96:0}dpi.png"));
            encoder.Save(stream);
        }
    }

    private static BitmapSource Panel(AppTheme theme, double scale, bool hover)
    {
        var settings = new PinSettings();
        var pin = new PinWindow(PinContent.FromImage(EvidenceImage()), new PinViewState(480, 280, 1, 1, .1),
            -10000, -10000, () => settings)
        {
            ShowActivated = false,
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
        try
        {
            pin.Show(); Drain();
            pin.ApplyClickThrough(true); Drain();
            PinClickThroughHandle handle = Assert.IsType<PinClickThroughHandle>(pin.ClickThroughHandleForTest);
            Assert.True(handle.HintForTest.IsVisible);
            if (hover)
            {
                handle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                Assert.Equal(1, handle.Opacity);
                handle.HintForTest.Text.Text = handle.TooltipText;
            }

            // Compose the product visuals on a detached surface at the requested scale.
            var chrome = (FrameworkElement)pin.Content;
            double width = chrome.ActualWidth, height = chrome.ActualHeight;
            pin.Content = null;
            Button button = handle.RecoveryButton;
            double handleOpacity = handle.AllowsTransparency ? handle.Opacity : 1;
            handle.Content = null;
            var hint = (Border)handle.HintForTest.Content;
            handle.HintForTest.Content = null;

            RectD handleRect = PinClickThroughHandle.Placement(new RectD(0, 0, width, height), 1);
            var canvas = new Canvas
            {
                Width = width + 2 * Margin,
                Height = height + 2 * Margin,
                Background = new SolidColorBrush(theme == AppTheme.Glass ? Color.FromRgb(0x20, 0x22, 0x26) : Color.FromRgb(0xE4, 0xE6, 0xEA)),
            };
            Place(canvas, chrome, Margin, Margin);
            VisualTreeHelper.SetRootDpi(canvas, new DpiScale(scale, scale));
            BitmapSource before = Render(canvas, scale);

            button.Opacity *= handleOpacity;
            Place(canvas, button, Margin + handleRect.Left, Margin + handleRect.Top);
            Place(canvas, hint, Margin, Margin + handleRect.Top);
            // Measure the hint at the rendered scale, then seat it left of the handle like the product does.
            Render(canvas, scale);
            double hintWidth = hint.ActualWidth, hintHeight = hint.ActualHeight;
            double hintLeft = handleRect.Left - hintWidth - PinClickThroughHandle.EdgeGap;
            Canvas.SetLeft(hint, Margin + hintLeft);
            BitmapSource after = Render(canvas, scale);

            Assert.True(ChangedPixels(before, after, Margin + handleRect.Left, Margin + handleRect.Top, 24, 24, scale) > 250 * scale * scale,
                "The handle must be visible over the pin.");
            Assert.True(ChangedPixels(before, after, Margin + hintLeft, Margin + handleRect.Top,
                hintWidth, hintHeight, scale) > 400 * scale * scale,
                "The hint must be visible beside the handle.");
            Assert.True(hintLeft > 0, "The hint must stay inside the pin.");
            return after;
        }
        finally { pin.Close(); }
    }

    private static void Place(Canvas canvas, FrameworkElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }

    private static BitmapSource Render(Canvas canvas, double scale)
    {
        var size = new Size(canvas.Width, canvas.Height);
        canvas.Measure(size);
        canvas.Arrange(new Rect(size));
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource SideBySide(IReadOnlyList<BitmapSource> panels, double scale, AppTheme theme)
    {
        int gap = (int)Math.Ceiling(Gap * scale);
        int pixelWidth = panels.Sum(panel => panel.PixelWidth) + gap * (panels.Count - 1);
        int pixelHeight = panels.Max(panel => panel.PixelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(theme == AppTheme.Glass ? Color.FromRgb(0x20, 0x22, 0x26) : Color.FromRgb(0xE4, 0xE6, 0xEA)),
                null, new Rect(0, 0, pixelWidth / scale, pixelHeight / scale));
            int x = 0;
            foreach (BitmapSource panel in panels)
            {
                context.DrawImage(panel, new Rect(x / scale, 0, panel.PixelWidth / scale, panel.PixelHeight / scale));
                x += panel.PixelWidth + gap;
            }
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int ChangedPixels(BitmapSource before, BitmapSource after, double left, double top, double width, double height, double scale)
    {
        int x = (int)Math.Floor(left * scale), y = (int)Math.Floor(top * scale);
        int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
        var rect = new Int32Rect(x, y, Math.Min(w, before.PixelWidth - x), Math.Min(h, before.PixelHeight - y));
        int stride = rect.Width * 4;
        byte[] a = new byte[stride * rect.Height], b = new byte[stride * rect.Height];
        before.CopyPixels(rect, a, stride, 0);
        after.CopyPixels(rect, b, stride, 0);
        int changed = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 12) changed++;
        }

        return changed;
    }

    /// <summary>A small document: dark header across the right half, light body with text rows.</summary>
    private static BitmapSource EvidenceImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 480, 280));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F)), null, new Rect(0, 0, 480, 44));
            var ink = new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66));
            for (int row = 0; row < 7; row++)
                context.DrawRectangle(ink, null, new Rect(24, 64 + row * 28, 260 + (row * 37) % 150, 8));
        }

        var bitmap = new RenderTargetBitmap(480, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
