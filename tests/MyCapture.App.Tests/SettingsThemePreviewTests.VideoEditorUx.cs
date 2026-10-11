using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// Renders the real recording editor on a real clip: the default state, a trim handle
    /// being dragged, the minimum window and a 1080p display at 200% scale.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_VIDEO_EDITOR_UX_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_VIDEO_EDITOR_UX_PHASE</c> to label them (for example before and after).
    /// </remarks>
    [Fact]
    public Task VideoEditorUx_TrimEditorRendersInEvidenceThemes() =>
        RunIsolated(nameof(VideoEditorUx_TrimEditorRendersInEvidenceThemes), VerifyVideoEditorUxEvidence);

    private static void VerifyVideoEditorUxEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        // Native media callbacks format their captions with the process language, not the
        // context-local override; this test already runs in its own child process.
        UiText.Configure("ko");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_VIDEO_EDITOR_UX_PHASE") is { Length: > 0 } label ? label : "after";
        string root = OwnedTestDirectory.Create("mc-video-ux-evidence-");
        try
        {
            RecordingResult recording = VideoUxClip(Path.Combine(root, "clip.mp4"));
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                RenderVideoEditorScenes(recording, root, AppThemeNames.ToSetting(theme), phase);
            }
        }
        finally
        {
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void RenderVideoEditorScenes(RecordingResult recording, string root, string theme, string phase)
    {
        var window = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1180, Height = 760,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!window.IsMediaReadyForTest && !window.HasMediaFailedForTest && DateTime.UtcNow < deadline)
                VideoUxPump(TimeSpan.FromMilliseconds(25));
            Assert.True(window.IsMediaReadyForTest, window.MediaFailureForTest);
            VideoUxSettle(window);
            SaveVideoUxEvidence(window, $"video-editor-default-{theme}-{phase}.png", 1.0);

            // A start handle mid-drag: the same state and notifications as a pointer drag,
            // without native mouse input on the user's desktop.
            TwoLineTimeline timeline = window.TimelineForTest;
            Assert.True(timeline.SetTrimRange(1_200, 8_400));
            VideoUxSettle(window);
            VideoUxRaise(timeline, "TrimInteractionStarted");
            VideoUxSetDrag(timeline, "TrimIn");
            timeline.SetIn(2_400);
            typeof(TwoLineTimeline).GetMethod("SeekTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(timeline, [timeline.InMs, false, false]);
            VideoUxSettle(window);
            SaveVideoUxEvidence(window, $"video-editor-trim-drag-{theme}-{phase}.png", 1.0);
            VideoUxSetDrag(timeline, "None");
            VideoUxRaise(timeline, "TrimInteractionCompleted");
            VideoUxSettle(window);

            window.MinWidth = 760;
            window.MinHeight = 555;
            window.Width = 760;
            window.Height = 555;
            VideoUxSettle(window);
            SaveVideoUxEvidence(window, $"video-editor-minimum-{theme}-{phase}.png", 1.0);
            SaveVideoUxEvidence(window, $"video-editor-minimum-144dpi-{theme}-{phase}.png", 1.5);

            // 1920x1080 at 200% leaves about 960x516 DIPs of work area.
            window.MinHeight = 516;
            window.Width = 960;
            window.Height = 516;
            VideoUxSettle(window);
            SaveVideoUxEvidence(window, $"video-editor-1080p-200pct-{theme}-{phase}.png", 2.0);
        }
        finally
        {
            window.Close();
        }
    }

    private static RecordingResult VideoUxClip(string path)
    {
        const int width = 640, height = 360, fps = 30, frames = 300;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var typeface = new Typeface("Segoe UI");
        using var encoder = new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps)),
            NullLogger.Instance);
        for (int frame = 0; frame < frames; frame++)
        {
            double ms = frame * 1000.0 / fps;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1D, 0x2A, 0x3C)), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x6F, 0xD8)), null,
                    new Rect(0, height - 18, width * (frame + 1.0) / frames, 18));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x34)), null,
                    new Rect(40 + (frame * 1.7 % (width - 120)), 70, 80, 80), 10, 10);
                dc.DrawText(new FormattedText(TimeSpan.FromMilliseconds(ms).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 64, Brushes.White, 1.0), new Point(200, 180));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.CopyPixels(pixels, stride, 0);
            encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, ms));
        }

        encoder.Complete();
        return new RecordingResult(path, frames * 1000.0 / fps, fps, frames, width, height);
    }

    private static void VideoUxRaise(TwoLineTimeline timeline, string name) =>
        ((EventHandler?)typeof(TwoLineTimeline).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timeline))
            ?.Invoke(timeline, EventArgs.Empty);

    private static void VideoUxSetDrag(TwoLineTimeline timeline, string mode)
    {
        FieldInfo drag = typeof(TwoLineTimeline).GetField("_drag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        drag.SetValue(timeline, Enum.Parse(drag.FieldType, mode));
    }

    private static void VideoUxSettle(Window window)
    {
        // Lets the preview seek, the composition-frame timeline render and layout settle.
        for (int pass = 0; pass < 6; pass++)
        {
            VideoUxPump(TimeSpan.FromMilliseconds(60));
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        }
    }

    private static void VideoUxPump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SaveVideoUxEvidence(Window window, string name, double scale)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_VIDEO_EDITOR_UX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        var content = (FrameworkElement)window.Content;
        var client = (FrameworkElement)VisualTreeHelper.GetParent(content);
        double width = client.ActualWidth, height = client.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            // Glass themes leave the window transparent for Mica; give the evidence an opaque base.
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(backdrop);
        bitmap.Render(client);
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
