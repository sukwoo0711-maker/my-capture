using System.Diagnostics;
using System.IO;
using System.Windows;
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
/// Renders a pin's translation message with its bar in both languages and both default themes, off screen: sliding
/// before a count, filling with its glint, and still without animations. Set MYCAPTURE_PIN_TRANSLATION_PROGRESS_EVIDENCE
/// to a directory to keep the PNGs.
/// </summary>
public sealed class PinTranslationProgressEvidenceTests
{
    [Fact]
    public Task BarStandsOutUnderTheCountInEveryLanguageAndTheme() => SettingsThemePreviewTests.RunIsolated(
        nameof(BarStandsOutUnderTheCountInEveryLanguageAndTheme), RenderEvidence, typeof(PinTranslationProgressEvidenceTests));

    private static void RenderEvidence()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TRANSLATION_PROGRESS_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(output)) Directory.CreateDirectory(output);
        // The message and its bar use Application resources, so this runs in its own process.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        foreach (string language in new[] { "ko-KR", "en-US" })
        foreach ((AppTheme theme, string themeName) in new[] { (AppTheme.Glass, "dark"), (AppTheme.GlassLight, "light") })
        {
            using var culture = UiText.UseLanguage(language);
            ThemeService.ApplyResources(theme, app.Resources, systemHighContrast: false, accent: AppAccent.Red);
            var pin = new PinWindow(PinContent.FromImage(EvidenceImage()), new PinViewState(480, 280, 1, 1, .1),
                -10000, -10000, () => new PinSettings())
            {
                ShowActivated = false,
                ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
            };
            try
            {
                FluidMotion.AnimationsEnabledOverrideForTest = true;
                pin.Show();
                Drain();
                pin.SetTranslationState(PinTranslationState.Translating);
                pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
                long start = Stopwatch.GetTimestamp();
                pin.ShowTranslationProgress(0, 12, start);
                Pump(TimeSpan.FromMilliseconds(700));
                BitmapSource sliding = Render(pin);

                pin.ShowTranslationProgress(3, 12, start + Stopwatch.Frequency);
                pin.FlushTranslationProgressForTest();
                pin.ShowTranslationProgress(4, 12, start + 2 * Stopwatch.Frequency);
                pin.FlushTranslationProgressForTest();
                Pump(TimeSpan.FromMilliseconds(500));
                Assert.False(pin.TranslationStripForTest.IsEasing);
                BitmapSource filling = Render(pin);

                // Without animations the same message keeps a still bar at the latest share.
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                pin.ShowTranslationProgress(6, 12, start + 3 * Stopwatch.Frequency);
                pin.FlushTranslationProgressForTest();
                Drain();
                Assert.False(pin.TranslationStripForTest.HasRunningAnimations);
                BitmapSource still = Render(pin);
                AssertBarStandsOut(pin, still, share: 0.5, $"{language} {themeName}");

                if (string.IsNullOrWhiteSpace(output)) continue;
                foreach ((BitmapSource image, string state) in new[] { (sliding, "sliding"), (filling, "filling"), (still, "still") })
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using FileStream stream = File.Create(Path.Combine(output,
                        $"pin-translation-progress-{language[..2]}-{themeName}-{state}.png"));
                    encoder.Save(stream);
                }
            }
            finally
            {
                pin.Close();
                FluidMotion.AnimationsEnabledOverrideForTest = null;
            }
        }
    }

    /// <summary>The filled part meets 3:1 against the message surface and differs from the empty track beside it.</summary>
    private static void AssertBarStandsOut(PinWindow pin, BitmapSource image, double share, string context)
    {
        var chrome = (FrameworkElement)pin.Content;
        PinProgressStrip bar = pin.TranslationStripForTest;
        Rect bounds = bar.TransformToAncestor(chrome).TransformBounds(new Rect(bar.RenderSize));
        Assert.True(bounds.Width > 40, $"{context}: the bar is {bounds.Width:0} DIP wide.");
        double middle = bounds.Top + bounds.Height / 2;
        Color fill = Average(image, new Rect(bounds.Left + 3, middle - 0.5, bounds.Width * share - 6, 1));
        Color track = Average(image, new Rect(bounds.Left + bounds.Width * share + 3, middle - 0.5, bounds.Width * (1 - share) - 6, 1));
        Color surface = Average(image, new Rect(bounds.Left + 3, bounds.Top - 2.5, bounds.Width - 6, 1));
        double fillContrast = Contrast(fill, surface), trackContrast = Contrast(fill, track);
        Console.WriteLine($"{context}: fill {fill} track {track} surface {surface}; fill/surface {fillContrast:0.00}, fill/track {trackContrast:0.00}");
        Assert.True(fillContrast >= 3, $"{context}: the filled part has {fillContrast:0.00}:1 against the message surface.");
        Assert.True(trackContrast >= 2, $"{context}: the filled part has {trackContrast:0.00}:1 against the empty track.");
    }

    private static BitmapSource Render(PinWindow pin)
    {
        var chrome = (FrameworkElement)pin.Content;
        chrome.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(chrome.ActualWidth), (int)Math.Ceiling(chrome.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chrome);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Average(BitmapSource image, Rect area)
    {
        var rect = new Int32Rect((int)Math.Round(area.Left), (int)Math.Round(area.Top),
            Math.Max(1, (int)Math.Round(area.Width)), Math.Max(1, (int)Math.Round(area.Height)));
        byte[] pixels = new byte[rect.Width * rect.Height * 4];
        image.CopyPixels(rect, pixels, rect.Width * 4, 0);
        double b = 0, g = 0, r = 0;
        int count = pixels.Length / 4;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            b += pixels[index];
            g += pixels[index + 1];
            r += pixels[index + 2];
        }
        return Color.FromRgb((byte)Math.Round(r / count), (byte)Math.Round(g / count), (byte)Math.Round(b / count));
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double share = value / 255d;
            return share <= 0.03928 ? share / 12.92 : Math.Pow((share + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>A small document: dark header, light body with text rows; the message sits over the body.</summary>
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

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
