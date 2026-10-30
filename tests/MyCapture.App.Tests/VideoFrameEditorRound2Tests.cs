using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoFrameEditorRound2Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task TrimmedEndSavesTheLastKeptColorAndBoundaryLayersAgain() => Run(nameof(TrimmedEndSavesTheLastKeptColorAndBoundaryLayersAgain), () =>
    {
        var document = VideoEditDocument.CreateFor(640, 360, 2000);
        document.RemovedRanges.Add(new(200, 2000));
        document.FrameEditLayers.Add(Layer(Colors.Red, new(0, 0, .25, .25)));
        document.TextOverlays.Add(Text());
        using var fixture = new Fixture(document);
        fixture.Open();
        Assert.True(fixture.Editor.HandleShortcut(Key.Space, ModifierKeys.None));
        Until(() => Field<bool>(fixture.Editor, "_settlingCutPlaybackEnd"), "Playback did not stop at the kept range end.");
        fixture.WaitForSeek();
        Assert.Equal(200, fixture.Editor.TimelineForTest.PlayheadMs, 3);
        Assert.InRange(fixture.Editor.PreviewSeekCoordinatorForTest.PresentedPositionMs, 99.99, 102);
        AssertPixel(fixture.Preview(), .8, .8, Colors.Lime, 12);

        // The independent fixture contract is frame 0=red, frame 1=green, frame 2=blue.
        // The exclusive end is 200, but the displayed and retained frame starts at 100.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(fixture.Editor.SaveCurrentFrameAsync()));
            Assert.Equal(100, saved.SourceAssetRequestedTimeMs);
            Assert.Equal(100, saved.SourceAssetTimeMs);
            BitmapSource png = fixture.Read(saved);
            AssertPixel(png, .8, .8, Colors.Lime, 5);
            AssertPixel(png, .1, .1, Color.FromRgb(128, 127, 0), 5);
            AssertTextPixels(png);
            Assert.Equal(200, fixture.Editor.TimelineForTest.PlayheadMs, 3);
        }
    });

    [Fact]
    public Task ExactSeekSavesCommittedAndUnsavedLayersWithIndependentAlphaPixels() => Run(nameof(ExactSeekSavesCommittedAndUnsavedLayersWithIndependentAlphaPixels), () =>
    {
        var document = VideoEditDocument.CreateFor(640, 360, 2000);
        document.FrameEditLayers.Add(Layer(Colors.Red, new(0, 0, .25, .25)));
        using var fixture = new Fixture(document);
        fixture.Open();
        VideoEditDocument unsaved = Field<VideoEditDocument>(fixture.Editor, "_editDocument");
        unsaved.FrameEditLayers.Add(Layer(Colors.Blue, new(0, .5, .25, .25)));
        unsaved.TextOverlays.Add(Text());
        // This yellow layer starts at the following frame and must not leak across its boundary.
        unsaved.FrameEditLayers.Add(new FrameEditLayer
        {
            StartMs = 200, EndMs = 300, Bounds = new(.75, .5, .25, .25),
            OverlayPngBase64 = Convert.ToBase64String(ImageCodec.EncodePng(Solid(8, 8, Colors.Yellow))),
        });
        Assert.True(fixture.Editor.HasUnsavedEditsForTest);
        fixture.Editor.TimelineForTest.SeekFromOverview(100);
        fixture.WaitForSeek();
        Assert.Equal(100, fixture.Editor.TimelineForTest.PlayheadMs, 3);
        AssertPixel(fixture.Preview(), .8, .8, Colors.Lime, 12);

        CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(fixture.Editor.SaveCurrentFrameAsync()));
        BitmapSource png = fixture.Read(saved);
        Assert.Equal(640, png.PixelWidth); Assert.Equal(360, png.PixelHeight);
        Assert.Equal(100, saved.SourceAssetRequestedTimeMs); Assert.Equal(100, saved.SourceAssetTimeMs);
        AssertPixel(png, .8, .8, Colors.Lime, 5);
        // Straight alpha 128/255 over green: a second bake would yield 192/63 instead.
        AssertPixel(png, .1, .1, Color.FromRgb(128, 127, 0), 5);
        AssertPixel(png, .1, .6, Color.FromRgb(0, 127, 128), 5);
        AssertPixel(png, .8, .6, Colors.Lime, 5);
        AssertTextPixels(png);
        Assert.False(saved.HasAnnotations);
        Assert.True(fixture.Editor.HasUnsavedEditsForTest);
    });

    [Fact]
    public Task PlayingSaveUsesThePausedMediaPositionInsteadOfTheLastTimelineTick() => Run(nameof(PlayingSaveUsesThePausedMediaPositionInsteadOfTheLastTimelineTick), () =>
    {
        using var fixture = new Fixture();
        fixture.Open();
        fixture.Editor.TimelineForTest.SeekFromOverview(1200);
        fixture.WaitForSeek();
        // Hold only the periodic timeline publication. The real MediaElement continues playing
        // from the independently chosen green interval [1000, 2000).
        Field<DispatcherTimer>(fixture.Editor, "_playbackTimer").Stop();
        fixture.Editor.TimelineForTest.SetPlayhead(0);
        Set(fixture.Editor, "_isPlaying", true);
        fixture.Editor.MediaForTest.Play();
        Until(() => fixture.Editor.MediaForTest.Position.TotalMilliseconds >= 1250, "The real media clock did not advance.");
        double before = fixture.Editor.MediaForTest.Position.TotalMilliseconds;
        Task<CaptureRecord?> pending = fixture.Editor.SaveCurrentFrameAsync();
        double paused = fixture.Editor.MediaForTest.Position.TotalMilliseconds;
        double pinnedPlayhead = fixture.Editor.TimelineForTest.PlayheadMs;
        CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(pending));
        double requested = Assert.IsType<double>(saved.SourceAssetRequestedTimeMs);
        Assert.InRange(requested, before - 2, paused + 2);
        Assert.InRange(requested, 1200, 1800);
        Assert.Equal(requested, pinnedPlayhead, 3);
        Assert.InRange(Assert.IsType<double>(saved.SourceAssetTimeMs), 1200, 1700);
        AssertPixel(fixture.Read(saved), .8, .8, Colors.Lime, 5);
    });

    private static TimedTextOverlay Text() => new()
    {
        StartMs = 100, EndMs = 200, Text = "I", Bounds = new(.4, .1, .4, .3),
    };

    private static FrameEditLayer Layer(Color color, VideoLayerBounds bounds) => new()
    {
        StartMs = 100, EndMs = 200, Bounds = bounds,
        OverlayPngBase64 = Convert.ToBase64String(ImageCodec.EncodePng(Solid(8, 8, Color.FromArgb(128, color.R, color.G, color.B)))),
    };

    private static void AssertTextPixels(BitmapSource bitmap)
    {
        // Caption panel: alpha 200/255, RGB(8,8,8), over RGB(0,255,0).
        // Away from its rounded corners and centered glyph the expected pixel is (6,61,6).
        // A missing caption stays green; a repeated panel is roughly (8,19,8).
        AssertPixel(bitmap, 270d / 640, .25, Color.FromRgb(6, 61, 6), 5);
        byte[] pixels = Pixels(bitmap);
        int white = 0;
        for (int y = 36; y < 144; y++)
        for (int x = 256; x < 512; x++)
        {
            int p = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[p] > 235 && pixels[p + 1] > 235 && pixels[p + 2] > 235) white++;
        }
        Assert.True(white > 20, "The active caption's white glyph is missing.");
    }

    private static void AssertPixel(BitmapSource bitmap, double x, double y, Color expected, int tolerance)
    {
        byte[] pixels = Pixels(bitmap);
        int p = ((int)(bitmap.PixelHeight * y) * bitmap.PixelWidth + (int)(bitmap.PixelWidth * x)) * 4;
        Assert.True(Math.Abs(pixels[p] - expected.B) <= tolerance && Math.Abs(pixels[p + 1] - expected.G) <= tolerance
            && Math.Abs(pixels[p + 2] - expected.R) <= tolerance,
            $"Expected RGB({expected.R},{expected.G},{expected.B}) ± {tolerance}; actual RGB({pixels[p + 2]},{pixels[p + 1]},{pixels[p]}).");
        Assert.Equal(255, pixels[p + 3]);
    }

    private static Task Run(string name, Action verify) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { verify(); }
        finally { application.Shutdown(); }
    }, typeof(VideoFrameEditorRound2Tests));

    private static void Until(Func<bool> condition, string failure)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(condition(), failure);
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted, "The operation did not complete."); task.GetAwaiter().GetResult(); }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = color.A; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(@"C:\GitRepositories\my-capture-libvideo\work\library-video-impl", "editor-r2-" + Guid.NewGuid().ToString("N"));
        internal VideoEditorWindow Editor { get; }
        internal Fixture(VideoEditDocument? document = null)
        {
            AppPaths paths = AppPaths.CreateForRoot(Root); paths.EnsureCreated();
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var source = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Title = "독립 색상 프레임", Width = 640, Height = 360,
                DurationMs = 2000, FrameRate = 10, FrameCount = 20, ContentRevision = 3 };
            source.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(source.Id, source.CreatedAt);
            Directory.CreateDirectory(queue.GetDirectory(source));
            string sourcePath = queue.GetFilePath(source, CaptureFileNames.VideoSource);
            var options = new VideoEncoderOptions(sourcePath, 640, 360, 10, VideoEncoderOptions.DeriveBitrate(640, 360, 10));
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                for (int i = 0; i < 20; i++)
                {
                    Color color = i >= 10 ? Colors.Lime : (i % 3) switch { 0 => Colors.Red, 1 => Colors.Lime, _ => Colors.Blue };
                    encoder.WriteFrame(new EncoderFrame(Pixels(Solid(640, 360, color)), 640, 360, 640 * 4, i * 100));
                }
                encoder.Complete();
            }
            queue.Add(source);
            var service = new VideoFrameCaptureService(queue, (record, bitmap) =>
            {
                File.WriteAllBytes(Path.Combine(Root, record.Id + ".png"), ImageCodec.EncodePng(bitmap));
                queue.Add(record); return Task.FromResult(record);
            });
            Editor = new VideoEditorWindow(new RecordingResult(sourcePath, 2000, 10, 20, 640, 360), paths, NullLoggerFactory.Instance, document)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                Width = 920, Height = 680, ShowActivated = false, Topmost = false,
                FrameCaptureService = service, FrameSourceVideoId = source.Id, FrameSourceRevision = 3,
            };
        }
        internal void Open()
        {
            Editor.Show(); Editor.UpdateLayout();
            Until(() => Editor.IsMediaReadyForTest && Editor.HasMediaOpenedForTest && Editor.IsSourceRangeKnownForTest,
                "The offscreen editor did not complete real MediaOpened/source probe.");
            Assert.False(Editor.HasMediaFailedForTest, Editor.MediaFailureForTest);
            Assert.True(Editor.MediaForTest.HasVideo);
        }
        internal void WaitForSeek()
        {
            Wait(Editor.PreviewSeekCoordinatorForTest.WaitForIdleAsync());
            Wait(Editor.PreviewEngineForTest.WhenSettledAsync());
            Editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Until(() => Field<Button>(Editor, "_saveCurrentFrameButton").IsEnabled, "Save remained disabled after the seek settled.");
        }
        internal BitmapSource Preview()
        {
            MediaElement media = Editor.MediaForTest;
            var target = new RenderTargetBitmap((int)Math.Ceiling(media.ActualWidth), (int)Math.Ceiling(media.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            target.Render(media); target.Freeze(); return target;
        }
        internal BitmapSource Read(CaptureRecord saved)
        {
            using var stream = File.OpenRead(Path.Combine(Root, saved.Id + ".png"));
            BitmapSource image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image.Freeze(); return image;
        }
        public void Dispose() { Editor.Close(); } // All isolated files remain for inspection.
    }
}
