using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// Renders the recording editor and the library player on a studio-range test pattern:
    /// the editor at its default, 920 and minimum widths with a text layer selected over the
    /// time readout's corner, and the player.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_MEDIA_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_MEDIA_EVIDENCE_PHASE</c> to label them (for example before and after).
    /// </remarks>
    [Fact]
    public Task MediaPlayback_EditorAndPlayerRenderTheRecordingEvidence() =>
        RunIsolated(nameof(MediaPlayback_EditorAndPlayerRenderTheRecordingEvidence), VerifyMediaPlaybackEvidence);

    private static void VerifyMediaPlaybackEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_EVIDENCE_PHASE") is { Length: > 0 } label ? label : "after";
        string root = OwnedTestDirectory.Create("mc-media-evidence-");
        GalleryVideoPlayerWindow? player = null;
        try
        {
            const int width = 1280, height = 720;
            string clip = Path.Combine(root, "pattern.mp4");
            EncodeEvidencePattern(clip, width, height);
            var recording = new RecordingResult(clip, MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip), 30, 90, width, height);
            var document = VideoEditDocument.CreateFor(width, height, recording.DurationMs);
            var caption = new TimedTextOverlay
            {
                Text = "선택한 텍스트 레이어",
                StartMs = 0,
                EndMs = recording.DurationMs,
                Bounds = new VideoLayerBounds(0.01, 0.82, 0.42, 0.16),
            };
            document.TextOverlays.Add(caption);

            foreach ((double w, double h, string size) in new[] { (1180.0, 760.0, "1180"), (920.0, 680.0, "920"), (760.0, 555.0, "760") })
            {
                var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance, document.Clone())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000, Width = w, Height = h,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                try
                {
                    editor.Show();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
                        EvidencePump(25);
                    Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
                    editor.ShortcutActions["NextFrame"]();
                    editor.ShortcutActions["NextFrame"]();
                    EvidenceSettle(editor);
                    SaveMediaEvidence(editor, $"video-editor-{size}-{phase}.png");

                    object canvas = typeof(VideoEditorWindow).GetField("_layerCanvas", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    canvas.GetType().GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(canvas, [(Guid?)caption.Id]);
                    EvidenceSettle(editor);
                    SaveMediaEvidence(editor, $"video-editor-{size}-layer-selected-{phase}.png");
                }
                finally
                {
                    editor.Close();
                }
            }

            player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000 };
            player.Show();
            Assert.True(player.Open(Guid.NewGuid(), "pattern", clip, autoPlay: false));
            FieldInfo ready = typeof(GalleryVideoPlayerWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
            DateTime playerDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!(bool)ready.GetValue(player)! && DateTime.UtcNow < playerDeadline)
                EvidencePump(25);
            Assert.True((bool)ready.GetValue(player)!);
            EvidenceSettle(player);
            SaveMediaEvidence(player, $"library-player-{phase}.png");
        }
        finally
        {
            player?.Close();
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }

    /// <summary>Grey steps and colour patches with a white border, 3 s at 30 fps, studio range.</summary>
    private static void EncodeEvidencePattern(string path, int width, int height)
    {
        (byte R, byte G, byte B)[] patches =
        [
            (0, 0, 0), (16, 16, 16), (64, 64, 64), (128, 128, 128), (192, 192, 192), (235, 235, 235), (255, 255, 255), (255, 0, 0),
            (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255), (0xF2, 0xB1, 0x34), (0x1D, 0x2A, 0x3C), (0x2E, 0x6F, 0xD8),
        ];
        int stride = width * 4;
        byte[] frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = patches[(y * 2 / height * 8) + (x * 8 / width)];
                int o = (y * stride) + (x * 4);
                frame[o] = b;
                frame[o + 1] = g;
                frame[o + 2] = r;
                frame[o + 3] = 255;
            }
        }

        var options = new VideoEncoderOptions(path, width, height, 30, VideoEncoderOptions.DeriveBitrate(width, height, 30));
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int i = 0; i < 90; i++)
        {
            encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * 1000.0 / 30));
        }

        encoder.Complete();
    }

    private static void EvidenceSettle(Window window)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            EvidencePump(60);
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        }
    }

    private static void EvidencePump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void SaveMediaEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        var content = (FrameworkElement)window.Content;
        var client = (FrameworkElement)VisualTreeHelper.GetParent(content);
        double width = client.ActualWidth, height = client.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
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
