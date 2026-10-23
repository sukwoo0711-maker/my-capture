using System.IO;
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
            string clip = Path.Combine(dir, "clip.mp4");
            FrameIndexClip.Encode(clip, 320, 240, Fps, FrameIndexClip.Constant(Fps, Frames));
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            editor = new VideoEditorWindow(
                new RecordingResult(clip, containerMs, Fps, Frames, 320, 240),
                AppPaths.CreateForRoot(dir),
                NullLoggerFactory.Instance)
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

            MediaElementPreviewEngine engine = editor.PreviewEngineForTest;
            int opened = 0;
            editor.MediaForTest.MediaOpened += (_, _) => opened++;
            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();

            void Check(string step, double target)
            {
                PreviewSeekCoordinator coordinator = editor!.PreviewSeekCoordinatorForTest;
                long generation = coordinator.RequestExact(target);
                DateTime until = DateTime.UtcNow.AddSeconds(10);
                while (coordinator.PresentedGeneration != generation && DateTime.UtcNow < until)
                    RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));
                if (coordinator.PresentedGeneration != generation)
                {
                    failures.Add($"{step}: the seek to {target:F3} ms was not presented");
                    return;
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
            }

            Check("before", 1500);
            Assert.Equal(0, engine.ReopenCountForTest);

            // The self-test's final-frame seek, met by the player's ended report.
            engine.TreatNextReadAsEndedForTest = true;
            Check("ended", containerMs - (500.0 / Fps));
            Assert.Equal(1, engine.ReopenCountForTest);
            Assert.Equal(1, opened);

            Check("after", 600);
            Assert.Equal(1, engine.ReopenCountForTest);
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
}
