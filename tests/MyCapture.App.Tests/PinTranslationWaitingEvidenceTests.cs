using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders a pin's message in every waiting state and failure, in both languages, at a usual and a narrow width on
/// a 1024x768 desktop, off screen, and checks that no message is clipped. Set
/// MYCAPTURE_PIN_TRANSLATION_WAIT_EVIDENCE to a directory to keep the PNGs.
/// </summary>
public sealed class PinTranslationWaitingEvidenceTests
{
    private const long ModelBytes = 246_256_561;

    [Fact]
    public Task EveryWaitingStateAndFailureFitsThePinInBothLanguages() => SettingsThemePreviewTests.RunIsolated(
        nameof(EveryWaitingStateAndFailureFitsThePinInBothLanguages), RenderEvidence, typeof(PinTranslationWaitingEvidenceTests));

    private static void RenderEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TRANSLATION_WAIT_EVIDENCE");
        string? output = string.IsNullOrWhiteSpace(requested) ? null : DiagnosticOutputPaths.Create(requested);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        ThemeService.ApplyResources(AppTheme.Glass, app.Resources, systemHighContrast: false, accent: AppAccent.Red);
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (string language in new[] { "ko-KR", "en-US" })
            foreach (double width in new[] { 480d, 300d })
            {
                using var culture = UiText.UseLanguage(language);
                var pin = new PinWindow(PinContent.FromImage(EvidenceImage((int)width)), new PinViewState(width, 280, 1, 1, .1),
                    -10000, -10000, () => new PinSettings())
                {
                    ShowActivated = false,
                    // The CI test desktop: the pin sits inside a 1024x768 work area.
                    ReadWorkArea = _ => new RectD(-10200, -10200, 1024, 768),
                };
                try
                {
                    pin.Show();
                    Drain();
                    foreach ((string state, Action show) in States(pin))
                    {
                        pin.SetTranslationState(PinTranslationState.Available);
                        Drain();
                        show();
                        pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
                        Drain();
                        string context = $"{language} {width} {state}";
                        Assert.True(pin.IsFeedbackVisible, $"{context}: no message is shown.");
                        BitmapSource image = Render(pin);
                        AssertNotClipped(pin, context);
                        if (output is null) continue;
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(output,
                            $"pin-wait-{language[..2]}-{width:0}-{state}.png"));
                        encoder.Save(stream);
                    }
                }
                finally { pin.Close(); }
            }
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = null; }
    }

    private static IEnumerable<(string State, Action Show)> States(PinWindow pin)
    {
        yield return ("download-37", () =>
        {
            pin.SetTranslationState(PinTranslationState.Downloading, ModelBytes);
            pin.ShowTranslationDownloadProgress((long)(ModelBytes * 0.37), ModelBytes);
        });
        yield return ("reading", () =>
        {
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.BeginTranslationReading();
        });
        yield return ("translating-23-of-64", () =>
        {
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.ShowTranslationProgress(23, 64, Stopwatch.GetTimestamp());
            pin.FlushTranslationProgressForTest();
        });
        yield return ("cancelled", () => pin.ReportTranslationNote(UiText.Get("Pin_TranslationCancelled")));
        foreach ((string name, TranslationModelError error) in new[]
        {
            ("failed-network", TranslationModelError.Network), ("failed-stalled", TranslationModelError.Stalled),
            ("failed-disk-full", TranslationModelError.DiskFull), ("failed-disk", TranslationModelError.Disk),
            ("failed-corrupt", TranslationModelError.Corrupt),
        })
            yield return (name, () => pin.SetTranslationFailed(
                PinManager.TranslationFailureMessage(new TranslationModelException(error, "Evidence."), ModelBytes)));
        yield return ("failed-general", () => pin.SetTranslationFailed(
            PinManager.TranslationFailureMessage(new InvalidOperationException("Evidence."), ModelBytes)));
        yield return ("nothing-to-translate", () =>
        {
            pin.SetTranslationDisabled(UiText.Get("Pin_TranslationNothing"));
            pin.ReportTranslationNote(UiText.Get("Pin_TranslationNothing"));
        });
    }

    /// <summary>The message lies inside the pin, and its text has the room it asks for at the width it was given.</summary>
    private static void AssertNotClipped(PinWindow pin, string context)
    {
        var chrome = (FrameworkElement)pin.Content;
        FrameworkElement surface = pin.FeedbackSurfaceForTest;
        Rect bounds = surface.TransformToAncestor(chrome).TransformBounds(new Rect(surface.RenderSize));
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= chrome.ActualWidth + 0.5
            && bounds.Bottom <= chrome.ActualHeight + 0.5, $"{context}: the message {bounds} leaves the pin {chrome.RenderSize}.");
        TextBlock text = ((Panel)((Border)surface).Child).Children.OfType<TextBlock>().Single();
        double width = text.ActualWidth, height = text.ActualHeight;
        // Layout rounding may arrange the text up to a pixel wider than its rounded ActualWidth.
        text.Measure(new Size(width + 1, double.PositiveInfinity));
        Size wanted = text.DesiredSize;
        Assert.True(wanted.Width <= width + 1 && wanted.Height <= height + 0.5,
            $"{context}: the text wants {wanted} but has {width:0.#}x{height:0.#}.");
        Console.WriteLine($"{context}: message {bounds.Width:0}x{bounds.Height:0} at {bounds.Left:0},{bounds.Top:0} in {chrome.ActualWidth:0}x{chrome.ActualHeight:0}");
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

    /// <summary>A small document: dark header, light body with text rows; the message sits over the body.</summary>
    private static BitmapSource EvidenceImage(int width)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, 280));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x5F)), null, new Rect(0, 0, width, 44));
            var ink = new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x66));
            for (int row = 0; row < 7; row++)
                context.DrawRectangle(ink, null, new Rect(24, 64 + row * 28, Math.Min(width - 48, 160 + (row * 37) % 150), 8));
        }
        var bitmap = new RenderTargetBitmap(width, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
