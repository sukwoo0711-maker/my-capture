using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    public Task VideoExport_OneStepDialogRendersIdleExportingAndSavedStates() =>
        RunIsolated(nameof(VideoExport_OneStepDialogRendersIdleExportingAndSavedStates), VerifyVideoExportDialog);

    private static void VerifyVideoExportDialog()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = OwnedTestDirectory.Create("mc-video-export-dialog-");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            // 12 s at 15 fps: long enough for the progress row to show a remaining-time estimate.
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "clip.mp4"),
                GifPreviewFixtureClips.Kind.DocumentScroll, 1280, 720, 15, 12_000);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            document.TextOverlays.Add(new TimedTextOverlay { Text = "내보내기 미리보기", StartMs = 500, EndMs = 11_500 });
            foreach ((AppTheme theme, string language) in new[] { (AppTheme.Midnight, "ko-KR"), (AppTheme.Glass, "ko-KR"), (AppTheme.Glass, "en-US") })
                VerifyVideoExportTheme(recording, document, root, theme, language);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void VerifyVideoExportTheme(RecordingResult recording, VideoEditDocument document, string root, AppTheme theme, string language)
    {
        using var scope = UiText.UseLanguage(language);
        bool korean = language.StartsWith("ko", StringComparison.Ordinal);
        ThemeService.Apply(theme);
        string destination = Path.Combine(root, $"export-{theme}-{language}.mp4");

        // MP4: the summary line is computed from the bitrate model; nothing is encoded until 내보내기.
        VideoExportDialog mp4 = CreateExportDialog(recording, document, gif: false);
        try
        {
            mp4.Show();
            Drain(mp4);
            Assert.StartsWith(korean ? "예상 크기 약" : "Estimated size about", mp4.EstimateText, StringComparison.Ordinal);
            Assert.Contains("MB", mp4.EstimateText, StringComparison.Ordinal);
            Assert.Contains("0:12.000", mp4.EstimateText, StringComparison.Ordinal);
            Assert.False(mp4.IsProgressVisible);
            Assert.True(mp4.CanExport);
            Image preview = Descendants(mp4).OfType<Image>().Single(image => AutomationProperties.GetName(image) == UiText.Get("MediaExport_SourcePreview"));
            PumpUntil(mp4, () => preview.Source is not null, "the MP4 preview frame");
            Assert.True(preview.IsVisible);
            AssertFormFits(mp4, $"{theme}/{language} MP4");
            Button[] footer = Descendants(mp4).OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) is var name
                    && (name == UiText.Get("MediaExport_Export") || name == UiText.Get("MediaExport_Close"))).ToArray();
            Assert.Equal(new[] { UiText.Get("MediaExport_Export"), UiText.Get("MediaExport_Close") }, footer.Select(AutomationProperties.GetName).ToArray());
            Assert.True(footer[0].TranslatePoint(new Point(0, 0), mp4).X < footer[1].TranslatePoint(new Point(0, 0), mp4).X, "내보내기 is left of 닫기");
            SaveVideoExportEvidence(mp4, $"export-mp4-{theme}-{language}.png");

            mp4.DestinationChooserForTest = () => destination;
            Task export = mp4.ExportAsync();
            Assert.True(mp4.IsProgressVisible && mp4.IsExporting && !mp4.CanExport, "exporting shows progress and disables 내보내기");
            ProgressBar bar = Descendants(mp4).OfType<ProgressBar>().Single();
            Assert.True(bar.IsVisible);
            PumpUntil(mp4, () => export.IsCompleted || (bar.Value >= 35 && mp4.RemainingText.Length > 0), "export progress");
            if (!export.IsCompleted)
            {
                Assert.True(bar.IsVisible && bar.Value > 0);
                Assert.Contains(korean ? "남은 시간" : "left", mp4.RemainingText, StringComparison.Ordinal);
                AssertFormFits(mp4, $"{theme}/{language} MP4 exporting");
                SaveVideoExportEvidence(mp4, $"export-mp4-{theme}-{language}-exporting.png");
            }

            PumpUntil(mp4, () => export.IsCompleted, "the export");
            export.GetAwaiter().GetResult();
            Assert.True(File.Exists(destination), mp4.StatusText);
            Assert.Equal(UiText.Format("MediaExport_Saved", Path.GetFileName(destination)), mp4.StatusText);
            Assert.False(mp4.IsProgressVisible);
            Assert.True(mp4.CanExport);
            Button open = Descendants(mp4).OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiText.Get("MediaExport_OpenFolder"));
            Assert.True(open.IsVisible && open.IsEnabled);
            Assert.Empty(Directory.GetFiles(root, ".export-*"));
            SaveVideoExportEvidence(mp4, $"export-mp4-{theme}-{language}-saved.png");
        }
        finally
        {
            mp4.Close();
            Drain(mp4);
        }

        // GIF: the animated sample, a one-line recommendation and the same footer.
        VideoExportDialog gif = CreateExportDialog(recording, document, gif: true);
        try
        {
            gif.Show();
            PumpUntil(gif, () => gif.SamplePanel.HasSample && gif.SampleCompletion.IsCompleted, "the GIF sample and its estimate");
            Assert.StartsWith(korean ? "예상 크기" : "Estimated size", gif.EstimateText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(gif.AdviceText));
            Assert.DoesNotContain(Environment.NewLine, gif.AdviceText, StringComparison.Ordinal);
            Assert.False(gif.IsProgressVisible);
            Assert.True(gif.CanExport);
            SaveVideoExportEvidence(gif, $"export-gif-{theme}-{language}.png");
        }
        finally
        {
            gif.Close();
            Drain(gif);
        }
    }

    private static VideoExportDialog CreateExportDialog(RecordingResult recording, VideoEditDocument document, bool gif)
    {
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        if (gif) dialog.Height = 980;
        FluidMotion.SetWindowEntrance(dialog, false);
        return dialog;
    }

    /// <summary>The MP4 form opens without a scrollbar: every row is inside the window.</summary>
    private static void AssertFormFits(Window window, string what)
    {
        Drain(window);
        ScrollViewer scroll = Descendants(window).OfType<ScrollViewer>().First();
        Assert.True(scroll.ScrollableHeight <= 0.5, $"{what}: the form needs {scroll.ScrollableHeight:0} px of scrolling");
    }

    private static void SaveVideoExportEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_EXPORT_EVIDENCE");
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
