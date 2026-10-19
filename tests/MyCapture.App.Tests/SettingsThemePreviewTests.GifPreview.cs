using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task GifPreview_ExportDialogPlaysTheSampleAndRecommendsInEveryTheme() =>
        RunIsolated(nameof(GifPreview_ExportDialogPlaysTheSampleAndRecommendsInEveryTheme), VerifyGifPreviewDialog);

    private static void VerifyGifPreviewDialog()
    {
        // As inside the app's dispatcher, awaited sample and original renders resume on the UI thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = OwnedTestDirectory.Create("mc-gif-preview-dialog-");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<bool>? previousReader = ThemeService.SystemLightThemeReaderForTest;
        ThemeService.SystemLightThemeReaderForTest = static () => false;
        try
        {
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "clip.mp4"),
                GifPreviewFixtureClips.Kind.DocumentScroll, 1280, 720, 15, 4_000);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            document.TextOverlays.Add(new TimedTextOverlay { Text = "GIF 미리보기", StartMs = 500, EndMs = 3_500 });
            foreach (AppTheme theme in Enum.GetValues<AppTheme>())
            {
                // High contrast doubles as the reduced-motion run: the sample must start paused.
                bool animate = theme != AppTheme.HighContrast;
                VerifyGifPreviewTheme(recording, document, theme, animate, "ko-KR");
            }

            VerifyGifPreviewTheme(recording, document, AppTheme.Glass, animate: true, "en-US");
            ThemeService.SystemLightThemeReaderForTest = static () => true;
            VerifyGifPreviewTheme(recording, document, AppTheme.System, animate: true, "en-US");

            RecordingResult longer = GifPreviewFixtureClips.Encode(Path.Combine(root, "long.mp4"),
                GifPreviewFixtureClips.Kind.Mixed, 1280, 720, 15, 12_000);
            VerifyGifPreviewLongRange(longer);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            ThemeService.SystemLightThemeReaderForTest = previousReader;
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void VerifyGifPreviewTheme(RecordingResult recording, VideoEditDocument document, AppTheme theme, bool animate, string language)
    {
        using var scope = UiText.UseLanguage(language);
        bool korean = language.StartsWith("ko", StringComparison.Ordinal);
        ThemeService.Apply(theme);
        FluidMotion.AnimationsEnabledOverrideForTest = animate;
        IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(ThemeService.ResolveTheme(theme));
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Height = 980,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        try
        {
            dialog.Show();
            PumpUntil(dialog, () => dialog.SamplePanel.HasSample, "the preview sample");
            GifPreviewPanel panel = dialog.SamplePanel;
            Assert.True(panel.FrameCount > 30, $"{theme}: {panel.FrameCount} frames");
            Assert.True(dialog.HasMeasuredResult, $"{theme}: a whole-range sample must be the exact export at once");
            Assert.True(dialog.CanExport && !dialog.IsProgressVisible, $"{theme}: 내보내기 is available before any encode");
            Assert.Contains(korean ? "측정" : "measured", dialog.EstimateText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(dialog.AdviceText));
            Assert.Equal(animate, panel.IsPlaying);
            Assert.Equal(!animate, panel.InfoText.Contains(korean ? "애니메이션 효과가 꺼져" : "animation effects are off", StringComparison.Ordinal));
            Button play = Descendants(panel).OfType<Button>().Single();
            Assert.Equal(UiText.Get(animate ? "GifPreview_Pause" : "GifPreview_Play"), AutomationProperties.GetName(play));
            Assert.True(play.IsEnabled && play.Focusable && play.IsTabStop);
            Slider position = Descendants(panel).OfType<Slider>().Single();
            Assert.Equal(UiText.Get("GifPreview_Position"), AutomationProperties.GetName(position));
            Assert.Equal(panel.FrameCount - 1, position.Maximum);
            AssertBrush(panel.Background, colors["Surface.Sunken"]);
            AssertBrush(Assert.IsType<TextBlock>(Descendants(panel).OfType<TextBlock>().First()).Foreground, colors["Text.Primary"]);
            ComboBox quality = Descendants(dialog).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == UiText.Get("MediaExport_GifPreset"));
            Assert.Contains(UiText.Get("GifPreview_Recommended"), string.Join("|", quality.Items.Cast<object>()), StringComparison.Ordinal);

            if (!animate)
            {
                play.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(panel.IsPlaying);
                Assert.Equal(UiText.Get("GifPreview_Pause"), AutomationProperties.GetName(play));
            }

            // Comparison is a still view of the same moment from the source recording.
            ToggleButton compare = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_Compare"));
            position.Value = 12;
            compare.IsChecked = true;
            Assert.False(panel.IsPlaying);
            PumpUntil(dialog, () => panel.OriginalFrame is not null, "the original comparison frame");
            Assert.Equal(12, panel.FrameIndex);
            Assert.Equal((recording.Width, recording.Height), (panel.OriginalFrame!.PixelWidth, panel.OriginalFrame.PixelHeight));
            ToggleButton actual = Descendants(panel).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == UiText.Get("GifPreview_ActualSize"));
            actual.IsChecked = true;
            Drain(dialog);
            Image gif = Descendants(panel).OfType<Image>().Single(image => AutomationProperties.GetName(image) == UiText.Get("GifPreview_GifFrame"));
            Assert.Equal(960 / VisualTreeHelper.GetDpi(panel).DpiScaleX, gif.Width, 3);
            SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}-compare.png");
            actual.IsChecked = false;
            compare.IsChecked = false;
            position.Value = 20;
            Drain(dialog);
            SaveGifPreviewEvidence(dialog, $"gif-preview-{theme}-{language}.png");

            // A different preset invalidates the measured result and renders a new sample.
            dialog.QualityIndex = 3;
            Assert.False(dialog.HasMeasuredResult);
            Assert.False(panel.HasSample);
            PumpUntil(dialog, () => panel.HasSample, "the 480 px sample");
            Assert.True(dialog.HasMeasuredResult);
        }
        finally
        {
            dialog.Close();
            Drain(dialog);
        }
    }

    private static void VerifyGifPreviewLongRange(RecordingResult recording)
    {
        using var scope = UiText.UseLanguage("ko-KR");
        ThemeService.Apply(AppTheme.Glass);
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Height = 1000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(dialog, false);
        try
        {
            dialog.Show();
            PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the sample and its estimate segments");
            GifPreviewPanel panel = dialog.SamplePanel;
            // 12 s is longer than one sample: a centred 3 s window, an estimate range, no exact result yet.
            Assert.False(dialog.HasMeasuredResult);
            Assert.True(dialog.CanExport);
            Assert.StartsWith("원본 4.5~7.5초", panel.InfoText, StringComparison.Ordinal);
            Assert.Contains("예상 크기", dialog.EstimateText, StringComparison.Ordinal);
            Assert.Contains("~", dialog.EstimateText, StringComparison.Ordinal);
            Slider position = Descendants(dialog).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == UiText.Get("GifPreview_SamplePosition"));
            Assert.True(position.IsVisible);
            SaveGifPreviewEvidence(dialog, "gif-preview-long-Glass-ko-KR.png");

            // A 5 MB limit cannot hold 12 s at 960/10: the advice names another preset or MP4.
            dialog.ShareTargetIndex = 0;
            Drain(dialog);
            GifRecommendation recommendation = Assert.IsType<GifRecommendation>(dialog.Recommendation);
            Assert.NotEqual(GifRecommendationKind.CurrentFits, recommendation.Kind);
            Button apply = Descendants(dialog).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == UiText.Get(recommendation.Kind == GifRecommendationKind.UseMp4 ? "GifPreview_SwitchToMp4" : "GifPreview_ApplyAdvice"));
            Assert.True(apply.IsVisible);
            SaveGifPreviewEvidence(dialog, "gif-preview-long-Glass-ko-KR-5mb.png");
            apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (recommendation.Kind == GifRecommendationKind.UseMp4)
            {
                Assert.False(dialog.IsGif);
            }
            else
            {
                Assert.Equal(GifSharePolicy.Presets.ToList().IndexOf(recommendation.Quality), dialog.QualityIndex);
                PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the recommended preset sample");
                Assert.True(dialog.Recommendation is { Kind: GifRecommendationKind.CurrentFits } or { Kind: GifRecommendationKind.SwitchPreset },
                    dialog.AdviceText);
            }

            // Moving the sample re-renders the preview without touching the settings.
            if (dialog.IsGif)
            {
                position.Value = 0;
                PumpUntil(dialog, () => dialog.SamplePanel.HasSample && dialog.SampleCompletion.IsCompleted, "the moved sample");
                Assert.StartsWith("원본 0.0~3.0초", panel.InfoText, StringComparison.Ordinal);
            }
        }
        finally
        {
            dialog.Close();
            Drain(dialog);
        }
    }

    private static void PumpUntil(Window window, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        Drain(window);
    }

    private static void SaveGifPreviewEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_GIF_PREVIEW_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Drain(window);
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
