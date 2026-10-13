using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Real Media Foundation MP4s whose length is not a whole number of seconds. WPF's
/// MediaElement and MediaPlayer take NaturalDuration from Windows Media Player, which truncates
/// these clips to whole seconds (2133 ms opens as 2000 ms; 667 ms has no duration). The editor,
/// trim and both exports must work on the container duration instead.
/// </summary>
public sealed class VideoDurationAccuracyTests : KoreanCaptionTest
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 15;

    [Theory]
    [InlineData(15, 10)] // 666.7 ms: WPF reports no duration
    [InlineData(15, 17)] // 1133.3 ms: WPF reports 1000 ms
    [InlineData(15, 32)] // 2133.3 ms: WPF reports 2000 ms
    [InlineData(30, 64)] // 2133.3 ms at 30 fps
    public void ContainerDuration_IsTheEncodedSpan_AndWpfIsOnlyAWholeSecondLowerBound(int fps, int frames) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-duration-");
        try
        {
            string clip = EncodeClip(Path.Combine(root, "clip.mp4"), fps, frames);
            double encodedMs = frames * 1000.0 / fps;

            // The editor reads it on a thread-pool thread while MediaElement opens the file.
            double containerMs = Task.Run(() => MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip))
                .GetAwaiter()
                .GetResult();
            // One MP4 timescale unit (1/(fps*1000) s) plus the 100 ns sample clock.
            Assert.InRange(containerMs, encodedMs - 0.2, encodedMs + 0.01);

            double? playerMs = WpfNaturalDuration(clip);
            if (playerMs is double player)
            {
                Assert.InRange(containerMs, player, player + VideoDurationResolver.PlayerResolutionMs);
            }

            Assert.Equal(containerMs, VideoDurationResolver.Resolve(containerMs, playerMs, encodedMs + 150));
        }
        finally { DeleteAfterMediaRelease(root); }
    });

    [Theory]
    [InlineData(10)] // sub-second: the editor used to fall back to the recording metadata
    [InlineData(17)]
    [InlineData(32)]
    public void Editor_UsesContainerDuration_ForTimelineTrimAndPlaybackEnd(int frames) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-editor-duration-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(Path.Combine(root, "clip.mp4"), Fps, frames);
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            // Wall-clock recording metadata includes encoder start-up, so it is longer than the clip.
            var recording = new RecordingResult(clip, containerMs + 150, Fps, frames, Width, Height);
            editor = OpenEditor(recording, root);

            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.Equal(containerMs, editor.DurationMsForTest);
            Assert.Equal(containerMs, timeline.OutMs);
            VideoEditDocument document = BuildCurrentDocument(editor);
            Assert.Equal(containerMs, document.SourceDurationMs);
            Assert.Equal(containerMs, document.TrimOutMs);
            Assert.False(document.HasEdits);
            Assert.Equal(frames, (int)Invoke(editor, "TotalFrameCount")!);

            // The final fraction is reachable by the trim handles.
            double frameMs = 1000.0 / Fps;
            Assert.True(timeline.SetTrimRange(containerMs - (3 * frameMs), containerMs));
            Assert.Equal(containerMs, timeline.OutMs);
            Assert.True(timeline.SetTrimRange(0, containerMs));

            // Play the last 300 ms. Windows Media Player reports the end position at its
            // whole-second length, so the editor must settle the playhead on the trim end itself.
            Invoke(editor, "Seek", Math.Max(0, containerMs - 300));
            PumpFor(TimeSpan.FromMilliseconds(200));
            Invoke(editor, "TogglePlay");
            bool started = PumpUntil(() => (bool)Field(editor, "_isPlaying")!, TimeSpan.FromSeconds(10));
            Assert.True(started, "playback did not start");
            bool stopped = PumpUntil(() => !(bool)Field(editor, "_isPlaying")!, TimeSpan.FromSeconds(10));
            Assert.True(stopped, "playback did not reach the end");
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert.Equal(containerMs, timeline.PlayheadMs);
        }
        finally
        {
            editor?.Close();
            DeleteAfterMediaRelease(root);
        }
    });

    [Fact]
    public void Mp4AndGifExports_KeepTheFinalFraction_WhenRecordingMetadataIsShorter() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-export-duration-");
        try
        {
            // 32 frames at 15 fps; only the final frame (2066.7-2133.3 ms) is bright, which is
            // exactly the span a whole-second duration (2000 ms) cuts off.
            string clip = EncodeClip(Path.Combine(root, "clip.mp4"), Fps, 32, brightLastFrame: true);
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            var recording = new RecordingResult(clip, 2000, Fps, 32, Width, Height);
            var document = VideoEditDocument.CreateFor(Width, Height, containerMs);

            string mp4 = Path.Combine(root, "export.mp4");
            int emitted = TrimReencoder.Reencode(
                clip,
                mp4,
                document.TrimInMs,
                document.TrimOutMs,
                recording,
                options => new MediaFoundationVideoEncoder(options, NullLogger.Instance),
                NullLogger.Instance);
            Assert.Equal(32, emitted);
            Assert.InRange(MediaFoundationVideoFrameReader.ReadContainerDurationMs(mp4), containerMs - 0.2, containerMs + 0.2);
            using (var reader = new MediaFoundationVideoFrameReader(mp4))
            {
                Assert.True(MeanLuma(reader.ReadFrameAt(containerMs - 10)) > 180, "MP4 export dropped the final frame");
            }

            string gif = Path.Combine(root, "export.gif");
            _ = AnimatedGifExporter.Export(recording, document, gif);
            using var stream = File.OpenRead(gif);
            var decoded = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            int totalCentiseconds = decoded.Frames.Sum(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay")));
            Assert.Equal((int)Math.Round(containerMs / 10.0, MidpointRounding.AwayFromZero), totalCentiseconds);
            Assert.Contains(decoded.Frames, frame => MeanLuma(frame) > 180);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static string EncodeClip(string path, int fps, int frames, bool brightLastFrame = false)
    {
        var options = new VideoEncoderOptions(path, Width, Height, fps, VideoEncoderOptions.DeriveBitrate(Width, Height, fps));
        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            for (int i = 0; i < frames; i++)
            {
                byte level = brightLastFrame
                    ? (byte)(i == frames - 1 ? 235 : 30)
                    : (byte)(30 + (i * 5 % 180));
                Array.Fill(pixels, level);
                encoder.WriteFrame(new EncoderFrame(pixels, Width, Height, stride, i * 1000.0 / fps));
            }

            encoder.Complete();
        }

        return path;
    }

    private static VideoEditorWindow OpenEditor(RecordingResult recording, string root)
    {
        var editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Width = 920,
            Height = 680,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        editor.Show();
        PumpUntil(() => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(15));
        Assert.False(editor.HasMediaFailedForTest, "editor reported media failure: " + editor.MediaFailureForTest);
        Assert.True(editor.IsMediaReadyForTest, "editor did not become ready");
        return editor;
    }

    private static double? WpfNaturalDuration(string path)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        bool opened = false, failed = false;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, _) => failed = true;
        player.Open(new Uri(path, UriKind.Absolute));
        try
        {
            // Opening is slower on GPU-less CI hosts under load; only the wait grows, not the check.
            Assert.True(PumpUntil(() => opened || failed, TimeSpan.FromSeconds(30)) && opened, "WPF could not open the clip");
            return player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalMilliseconds : null;
        }
        finally { player.Close(); }
    }

    private static double MeanLuma(DecodedVideoFrame frame)
    {
        long sum = 0;
        int count = 0;
        for (int y = 0; y < frame.Height; y += 8)
        {
            for (int x = 0; x < frame.Width; x += 8)
            {
                int p = (y * frame.Stride) + (x * 4);
                sum += frame.Pixels[p] + frame.Pixels[p + 1] + frame.Pixels[p + 2];
                count += 3;
            }
        }

        return (double)sum / count;
    }

    private static double MeanLuma(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        long sum = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }

        return (double)sum / (pixels.Length / 4 * 3);
    }

    private static VideoEditDocument BuildCurrentDocument(VideoEditorWindow editor) =>
        (VideoEditDocument)Invoke(editor, "BuildCurrentDocument")!;

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private static object? Invoke(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    /// <summary>
    /// MediaPlayer and MediaElement release the clip asynchronously after Close, so recycling the
    /// fixture straight away can fail with a sharing violation. Pump until no file is held.
    /// </summary>
    private static void DeleteAfterMediaRelease(string root)
    {
        PumpUntil(() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).All(IsUnlocked), TimeSpan.FromSeconds(10));
        OwnedTestDirectory.Delete(root);

        static bool IsUnlocked(string path)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpFor(TimeSpan.FromMilliseconds(20));
        }

        return condition();
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
