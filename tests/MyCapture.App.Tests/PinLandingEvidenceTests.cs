using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinLandingEvidenceTests
{
    [Fact]
    public void PinLanding_ActualVisualSnapshots_WhenRequested()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_LANDING_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        StaTestHost.Run(() =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            const string source = "public void Capture()\r\n{\r\n\tSave();\r\n}";
            const string html = "<div style=\"color: #d4d4d4; background-color: #1e1e1e; white-space: pre;\"><div><span style=\"color: #569cd6;\">public void</span> Capture()</div><div>{</div><div>    Save();</div><div>}</div></div>";
            BitmapSource code = ClipboardCodeRenderer.TryRender(source, html) ?? throw new InvalidOperationException("Code fixture failed.");
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                foreach (double time in new[] { 0d, 83, 150, 195, 300, 420, 560 })
                    SaveSnapshot(code, source, scale, time, output);
        });
    }

    private static void SaveSnapshot(BitmapSource code, string source, double scale, double time, string output)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        var pin = new PinWindow(PinContent.FromText(code, source, false), new PinViewState(code.PixelWidth, code.PixelHeight, 1, 1, .1),
            -10000, -10000, () => new PinSettings()) { ShowActivated = false };
        PinLandingDecoration? decoration = null;
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml") });
            ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: false, accent: AppAccent.Red);
            pin.Resources = resources;
            // Establish the real owner HWND off screen, then hide it before rendering the
            // detached product visuals. No activation, clipboard, or input is requested.
            pin.Show();
            pin.Hide();
            PinLandingFrame state = PinLandingDecoration.Render(time);
            typeof(PinWindow).GetField("_landingAccentAmount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pin, state.BorderAccentAmount);
            typeof(PinWindow).GetMethod("UpdateChromeColor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, [false]);
            var chrome = (FrameworkElement)pin.Content;
            pin.Content = null;
            chrome.Opacity = Math.Clamp(time / 83, 0, 1);
            chrome.Width = pin.Width;
            chrome.Height = pin.Height;
            var layout = PinLandingDecoration.LayoutFor(new RectD(64 * scale, 64 * scale, pin.Width * scale, pin.Height * scale), scale);
            decoration = new PinLandingDecoration(pin, layout);
            // Render the actual product drawing surface without showing the ornament.
            decoration.Advance(Math.Min(time, 559.999));
            var ornament = (FrameworkElement)decoration.Content;
            decoration.Content = null;
            ornament.Width = layout.PhysicalBounds.Width / scale;
            ornament.Height = layout.PhysicalBounds.Height / scale;
            if (time >= 560) ornament.Visibility = Visibility.Hidden;
            var canvas = new Canvas { Background = Brushes.White, Width = ornament.Width, Height = ornament.Height };
            Canvas.SetLeft(chrome, layout.PinBoundsDip.Left);
            Canvas.SetTop(chrome, layout.PinBoundsDip.Top);
            canvas.Children.Add(chrome);
            canvas.Children.Add(ornament);
            VisualTreeHelper.SetRootDpi(canvas, new DpiScale(scale, scale));
            var size = new Size(canvas.Width, canvas.Height);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"pin-code-{scale * 100:0}-landing-{time:0}.png"));
            encoder.Save(stream);
        }
        finally
        {
            if (decoration is { IsClosed: false }) decoration.Close();
            pin.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    }
}
