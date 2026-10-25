using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Windows Media Player's media-ended state: after the stream reaches its end, the player can stay
/// stopped there, MediaElement.Position reads the player's whole-second length for every seek,
/// and no seek, pause or play presents a frame. The preview engine opens the clip again when a
/// read is that report. The state comes from a race inside the player, so the report is injected.
/// </summary>
public sealed class VideoEditorMediaEndedRecoveryTests
{
    private const int Fps = 15;
    private const int Frames = 38; // 2533.3 ms, which Windows Media Player opens as 2000 ms

    [Fact]
    public void EndedReport_OpensTheClipAgain_AndTheSeekPresentsTheRequestedFrame() => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-media-ended-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(dir, out double containerMs);
            editor = OpenEditor(clip, containerMs, dir);
            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
            int opened = 0;
            editor.MediaForTest.MediaOpened += (_, _) => opened++;
            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();

            // Every reopen answers one ended report: the injected one, or the player's own when
            // a seek into the last frame ends the stream again.
            Check(editor, export, failures, "before", 1500);
            Assert.Equal(0, engine.ReopenCountForTest);

            // The self-test's final-frame seek, met by the player's ended report.
            engine.TreatNextReadAsEndedForTest = true;
            Check(editor, export, failures, "ended", containerMs - (500.0 / Fps));
            Assert.Equal(1 + engine.EndedReportCountForTest, engine.ReopenCountForTest);
            Assert.Equal(engine.ReopenCountForTest, opened);

            Check(editor, export, failures, "after", 600);
            Assert.Equal(1 + engine.EndedReportCountForTest, engine.ReopenCountForTest);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Assert.True(editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest, editor.MediaFailureForTest);
            Assert.Equal(containerMs, editor.DurationMsForTest);
        }
        finally
        {
            editor?.Close();
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(50));
            OwnedTestDirectory.Delete(dir);
        }
    });

    /// <summary>
    /// After the stream has run to or near its end, the next paused seek can stop past its
    /// position, by more than a frame at worst. The engine positions the element again.
    /// </summary>
    [Fact]
    public void LandingPastThePosition_IsSeekedAgain_AndReportsTheRequestedFrame() => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-media-landing-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(dir, out double containerMs);
            editor = OpenEditor(clip, containerMs, dir);
            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();

            Check(editor, export, failures, "before", 1500);
            int positioned = engine.PositionCountForTest;
            engine.MissNextLandingForTest = true;
            Check(editor, export, failures, "missed", 600);
            Assert.True(
                engine.PositionCountForTest - positioned >= 2,
                $"the missed landing was positioned {engine.PositionCountForTest - positioned} time(s), not asked again");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Assert.Equal(engine.EndedReportCountForTest, engine.ReopenCountForTest);
        }
        finally
        {
            editor?.Close();
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(50));
            OwnedTestDirectory.Delete(dir);
        }
    });

    /// <summary>
    /// After a playback to the end, WPF holds the player stopped, and the first paused seek from
    /// there shows the right frame but leaves the player past the position (3 to 74 ms on loaded
    /// runners). That seek reports its frame at once and is then watched until the position stays
    /// put; seeks before the end, and after a watched seek landed, are not. Any paused seek stops
    /// up to a millisecond past its position and a seek closer than that does not move the
    /// player, so the landing is checked to the player's resolution.
    /// </summary>
    [Fact]
    public void FirstSeekAfterPlaybackEnd_StaysAtTheRequestedPosition() => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-media-after-end-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(dir, out double containerMs);
            editor = OpenEditor(clip, containerMs, dir);
            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();

            Check(editor, export, failures, "before", 1500);
            Assert.Equal(0, engine.SettleCountForTest);

            PlayToTheEnd(editor);
            int watched = -1;
            foreach (double target in new[] { 600.0, 1234.5 })
            {
                int before = engine.SettleCountForTest;
                int atPresentation = Check(editor, export, failures, "after end", target).Watches;
                if (atPresentation != before)
                {
                    failures.Add($"after end: the seek to {target:F3} ms was presented after {atPresentation - before} watch(es) ended");
                }

                RangePatternClip.Pump(TimeSpan.FromMilliseconds(300));
                double stopped = editor.MediaForTest.Position.TotalMilliseconds;
                double position = target + MediaElementPreviewEngine.PresentationToleranceMs;
                if (Math.Abs(stopped - position) > MediaElementPreviewEngine.SeekResolutionMs)
                {
                    failures.Add($"after end: the player stopped at {stopped:F3} ms for {position:F3} ms");
                }

                watched = watched < 0 ? engine.SettleCountForTest : watched;
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Assert.True(watched >= 1, "the first seek after the end was not watched");
            Assert.True(
                engine.SettleCountForTest == watched,
                $"the seek after a watched landing was watched again ({engine.SettleCountForTest - watched} more)");
        }
        finally
        {
            editor?.Close();
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(50));
            OwnedTestDirectory.Delete(dir);
        }
    });

    /// <summary>
    /// The first seek after a playback to the end can read past its position at once, as on the
    /// runners after a scrub into the last stretch. It reports its frame without asking again first;
    /// asking again is the watch's job, after the frame is reported.
    /// </summary>
    [Fact]
    public void AFirstSeekAfterTheEndThatReadsPastIt_ReportsItsFrameWithoutWaiting() => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-media-past-after-end-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(dir, out double containerMs);
            editor = OpenEditor(clip, containerMs, dir);
            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();

            PlayToTheEnd(editor);
            int positioned = engine.PositionCountForTest;
            engine.MissNextLandingForTest = true;
            (int watches, int positions) = Check(editor, export, failures, "read past after end", 600);
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            Assert.True(
                positions - positioned == 1,
                $"the seek was positioned {positions - positioned} time(s) before its frame was reported");
            Assert.True(engine.SettleCountForTest > watches, "the seek that read past its position was not watched");
        }
        finally
        {
            editor?.Close();
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(50));
            OwnedTestDirectory.Delete(dir);
        }
    });

    /// <summary>
    /// Play right after a playback to the end starts again at In, through the first seek after
    /// the end. That seek's position is watched after its frame is reported, and playback starts
    /// only once the watch has ended, from where the player was put back.
    /// </summary>
    [Fact]
    public void PlaybackAfterTheEnd_StartsOnceTheFirstSeekStaysPut() => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-media-play-after-end-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = EncodeClip(dir, out double containerMs);
            var starts = new List<(int Watches, bool Settled)>();
            editor = OpenEditor(clip, containerMs, dir, () =>
            {
                MediaElementPreviewEngine watching = editor!.PreviewEngineForTest;
                starts.Add((watching.SettleCountForTest, watching.WhenSettledAsync().IsCompleted));
                editor.MediaForTest.Play();
            });
            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;

            PlayToTheEnd(editor);
            int watches = engine.SettleCountForTest;
            starts.Clear();
            editor.ShortcutActions["Play"]();
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (starts.Count == 0 && DateTime.UtcNow < until)
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));
            editor.ShortcutActions["Play"]();

            Assert.True(starts.Count == 1, $"playback started {starts.Count} time(s) after the end");
            Assert.True(
                starts[0].Settled && starts[0].Watches > watches,
                $"playback started with the first seek after the end still watched ({starts[0].Watches - watches} watch(es) ended)");
        }
        finally
        {
            editor?.Close();
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(50));
            OwnedTestDirectory.Delete(dir);
        }
    });

    // Plays from 1.8 s through the end of the 2.53 s clip, as the user would.
    private static void PlayToTheEnd(VideoEditorWindow editor)
    {
        typeof(VideoEditorWindow).GetMethod("Seek", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, [1800.0]);
        RangePatternClip.Pump(TimeSpan.FromMilliseconds(200));
        editor.ShortcutActions["Play"]();
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        bool started = false;
        while (DateTime.UtcNow < until && !(started && !editor.PlayButtonShowsPauseForTest))
        {
            started |= editor.PlayButtonShowsPauseForTest;
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));
        }

        Assert.True(started && !editor.PlayButtonShowsPauseForTest, "playback did not run to the end");
        RangePatternClip.Pump(TimeSpan.FromMilliseconds(300));
    }

    private static string EncodeClip(string dir, out double containerMs)
    {
        string clip = Path.Combine(dir, "clip.mp4");
        FrameIndexClip.Encode(clip, 320, 240, Fps, FrameIndexClip.Constant(Fps, Frames));
        containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
        return clip;
    }

    private static VideoEditorWindow OpenEditor(string clip, double containerMs, string dir, Action? playMedia = null)
    {
        var editor = new VideoEditorWindow(
            new RecordingResult(clip, containerMs, Fps, Frames, 320, 240),
            AppPaths.CreateForRoot(dir),
            NullLoggerFactory.Instance,
            playMedia: playMedia)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1000, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        editor.Show();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
        return editor;
    }

    /// <summary>
    /// Seeks, checks the report and the frame on screen once any position watch has ended, and
    /// returns how many watches had ended and how often the element was positioned when the seek
    /// was presented.
    /// </summary>
    private static (int Watches, int Positions) Check(
        VideoEditorWindow editor,
        MediaFoundationVideoFrameReader export,
        List<string> failures,
        string step,
        double target)
    {
        PreviewSeekCoordinator coordinator = editor.PreviewSeekCoordinatorForTest;
        MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
        var presentations = new ConcurrentDictionary<long, (int Watches, int Positions)>();
        void OnPresented(PresentedPreviewFrame frame) =>
            presentations.TryAdd(frame.Generation, (engine.SettleCountForTest, engine.PositionCountForTest));
        (int Watches, int Positions) atPresentation;
        bool presented;
        DateTime until;
        coordinator.PreviewPresented += OnPresented;
        try
        {
            long generation = coordinator.RequestExact(target);
            until = DateTime.UtcNow.AddSeconds(10);
            // A delayed dispatcher pump can also run the watch's later repositioning. Capture
            // these counters at the report itself, not when the test next observes its generation.
            while (!presentations.ContainsKey(generation) && DateTime.UtcNow < until)
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));
            presented = presentations.TryGetValue(generation, out atPresentation);
        }
        finally { coordinator.PreviewPresented -= OnPresented; }
        if (!presented)
        {
            failures.Add($"{step}: the seek to {target:F3} ms was not presented");
            return atPresentation;
        }

        Task watch = engine.WhenSettledAsync();
        until = DateTime.UtcNow.AddSeconds(5);
        while (!watch.IsCompleted && DateTime.UtcNow < until)
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));
        if (!watch.IsCompleted)
        {
            failures.Add($"{step}: the position watch after the seek to {target:F3} ms did not end");
        }

        if (Math.Abs(coordinator.PresentedPositionMs - target) > 0.5)
        {
            failures.Add($"{step}: the preview reported {coordinator.PresentedPositionMs:F3} ms for {target:F3} ms");
        }

        DecodedVideoFrame decoded = export.ReadFrameAt(target);
        int expected = FrameIndexClip.Read(decoded.Pixels, decoded.Width, decoded.Height, decoded.Stride);
        int shown;
        until = DateTime.UtcNow.AddSeconds(3);
        do
        {
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
            shown = FrameIndexClip.Read(editor.RenderCurrentFrameForTest()
                ?? throw new Xunit.Sdk.XunitException("the preview rendered no frame"));
        }
        while (shown != expected && DateTime.UtcNow < until);
        if (shown != expected)
        {
            failures.Add($"{step}: the preview shows frame {shown} at {target:F3} ms, the export takes {expected}");
        }

        return atPresentation;
    }
}
