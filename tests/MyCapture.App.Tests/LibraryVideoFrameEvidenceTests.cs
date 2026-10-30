using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string FrameEvidenceRoot = @"C:\GitRepositories\my-capture-libvideo\work\library-video-impl";

    [Fact]
    public Task LibraryVideoFramesBefore() => RunIsolated(nameof(LibraryVideoFramesBefore), () => RenderFrameEvidence("before"));

    [Fact]
    public Task LibraryVideoFramesAfter() => RunIsolated(nameof(LibraryVideoFramesAfter), () => RenderFrameEvidence("after"));

    private static void RenderFrameEvidence(string phase)
    {
        Directory.CreateDirectory(FrameEvidenceRoot);
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko-KR");
        Application application = CreateLibraryMotionApplication();
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.Apply(AppTheme.Daylight);
        try
        {
            string clip = Path.Combine(FrameEvidenceRoot, "frame-evidence-" + phase + ".mp4");
            if (!File.Exists(clip)) EncodeEvidencePattern(clip, 640, 360);
            var paths = AppPaths.CreateForRoot(Path.Combine(FrameEvidenceRoot, "editor-evidence-" + phase));
            paths.EnsureCreated();
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var source = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Title = "녹화 예시", Width = 640, Height = 360 };
            source.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(source.Id, source.CreatedAt);
            Directory.CreateDirectory(queue.GetDirectory(source));
            string asset = queue.GetFilePath(source, CaptureFileNames.VideoSource);
            File.Copy(clip, asset);
            queue.Add(source);
            var capture = new VideoFrameCaptureService(queue, (record, _) => Task.FromResult(record));
            var player = new GalleryVideoPlayerWindow();
            if (phase == "after") player.ConfigureFrameCapture(capture, source.ContentRevision);
            PlaceFrameEvidence(player, 850, 560);
            try
            {
                player.Show();
                Assert.True(player.Open(source.Id, "녹화 예시", asset, autoPlay: false));
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!player.IsMediaReadyForDiagnostic && DateTime.UtcNow < deadline) EvidencePump(30);
                Assert.True(player.IsMediaReadyForDiagnostic);
                EvidenceSettle(player);
                SaveFrameEvidence(player, "player-" + phase + ".png");
            }
            finally { player.Close(); }

            var recording = new RecordingResult(asset, MediaFoundationVideoFrameReader.ReadContainerDurationMs(asset), 30, 90, 640, 360);
            var editor = new VideoEditorWindow(recording, paths, NullLoggerFactory.Instance)
            {
                FrameCaptureService = phase == "after" ? capture : null,
                FrameSourceVideoId = source.Id, FrameSourceRevision = source.ContentRevision,
            };
            PlaceFrameEvidence(editor, 1180, 760);
            try
            {
                editor.Show();
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline) EvidencePump(30);
                Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
                EvidenceSettle(editor);
                SaveFrameEvidence(editor, "video-editor-" + phase + ".png");
            }
            finally { editor.Close(); }
        }
        finally { application.Shutdown(); }
    }

    private static void PlaceFrameEvidence(Window window, double width, double height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.Width = width; window.Height = height;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        FluidMotion.SetWindowEntrance(window, false);
    }

    private static void SaveFrameEvidence(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var client = (FrameworkElement)VisualTreeHelper.GetParent(content);
        double width = client.ActualWidth, height = client.ActualHeight;
        Assert.True(width > 100 && height > 100);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
            dc.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(client);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(FrameEvidenceRoot, name));
        encoder.Save(output);
    }
}
