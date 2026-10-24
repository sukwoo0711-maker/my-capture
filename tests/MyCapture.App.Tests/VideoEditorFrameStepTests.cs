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
/// Steps the real editor frame by frame through clips that carry each frame's index, and checks
/// the preview against the frame an export decodes for the same playhead time.
/// </summary>
public sealed class VideoEditorFrameStepTests
{
    private static readonly TimeSpan PresentTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The reported case: at 1.60 s of a 15 fps clip the next frame starts at 1666.667 ms, which
    /// Windows Media Player's seek reached a tick early, so the preview kept frame 24.
    /// </summary>
    [Theory]
    [InlineData(15, 45)]
    [InlineData(30, 60)]
    [InlineData(60, 60)]
    public void EveryStepShowsTheNextFrameAcrossAConstantRateClip(int fps, int frames) => StaTestHost.Run(() =>
        StepThrough(fps, FrameIndexClip.Constant(fps, frames), constantRate: true));

    /// <summary>
    /// A recording that fell behind: dropped frames leave gaps, and a stall moves the following
    /// frames off the nominal grid. A step may then hold a frame, exactly as the export does.
    /// </summary>
    [Fact]
    public void StepsShowWhatTheExportTakesInAVariableRateClip() => StaTestHost.Run(() =>
    {
        const int fps = 30;
        double frameMs = 1000.0 / fps;
        var times = new List<double>();
        for (int slot = 0; slot < 12; slot++) times.Add(slot * frameMs);
        times.Add(15 * frameMs);                       // two frames dropped
        times.Add((16 * frameMs) + 7);                 // a late frame re-anchors the grid
        for (int slot = 17; slot < 30; slot++) times.Add((slot * frameMs) + 7);
        times.Add(33 * frameMs);                       // another gap, back near the grid
        times.Add((33 * frameMs) + 9);                 // two frames inside one nominal slot
        for (int slot = 35; slot < 45; slot++) times.Add(slot * frameMs);
        StepThrough(fps, times, constantRate: false);
    });

    /// <summary>
    /// A playhead left just before a frame boundary, as an overview click or a pause during
    /// playback leaves it. Windows Media Player ignores a paused seek that close to where it stands
    /// and keeps its picture for one that close to the position it was last given, so the next
    /// frame, and the seek back across the boundary, kept showing the frame before.
    /// </summary>
    [Fact]
    public void ASeekJustAcrossAFrameBoundaryShowsTheFrameOnTheOtherSide() => StaTestHost.Run(() =>
    {
        const int fps = 15;
        string dir = OwnedTestDirectory.Create("mc-frame-boundary-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = Path.Combine(dir, "clip.mp4");
            FrameIndexClip.Encode(clip, 320, 180, fps, FrameIndexClip.Constant(fps, 38));
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            editor = new VideoEditorWindow(
                new RecordingResult(clip, containerMs, fps, 38, 320, 180),
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

            using var export = new MediaFoundationVideoFrameReader(clip);
            var failures = new List<string>();
            MethodInfo seek = typeof(VideoEditorWindow).GetMethod("Seek", BindingFlags.Instance | BindingFlags.NonPublic)!;

            void Check(string step, int expectedFrame)
            {
                double playhead = editor!.TimelineForTest.PlayheadMs;
                DecodedVideoFrame decoded = export.ReadFrameAt(playhead);
                int expected = FrameIndexClip.Read(decoded.Pixels, decoded.Width, decoded.Height, decoded.Stride);
                Assert.Equal(expectedFrame, expected);
                int shown = WaitForFrame(editor, expected);
                if (shown != expected)
                {
                    failures.Add($"{step}: playhead {playhead:F3} ms shows frame {shown}, the export takes {expected}");
                }

                double presented = editor.PreviewSeekCoordinatorForTest.PresentedPositionMs;
                if (Math.Abs(presented - playhead) > 0.5)
                {
                    failures.Add($"{step}: the preview reported {presented:F3} ms for {playhead:F3} ms");
                }
            }

            foreach (int frame in new[] { 10, 19, 23 })
            {
                double boundary = frame * 1000.0 / fps;
                foreach (double before in new[] { 0.3, 0.6, 0.9 })
                {
                    seek.Invoke(editor, [boundary - 30]);
                    Check($"away from frame {frame}", frame - 1);
                    seek.Invoke(editor, [boundary - before]);
                    Check($"{before} ms before frame {frame}", frame - 1);
                    editor.ShortcutActions["NextFrame"]();
                    Check($"next frame from {before} ms before frame {frame}", frame);
                    seek.Invoke(editor, [boundary - before]);
                    Check($"back to {before} ms before frame {frame}", frame - 1);
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(dir);
        }
    });

    private static void StepThrough(int fps, IReadOnlyList<double> timestamps, bool constantRate)
    {
        string dir = OwnedTestDirectory.Create("mc-frame-step-");
        VideoEditorWindow? editor = null;
        try
        {
            string clip = Path.Combine(dir, "clip.mp4");
            FrameIndexClip.Encode(clip, 320, 180, fps, timestamps);
            double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
            editor = new VideoEditorWindow(
                new RecordingResult(clip, containerMs, fps, timestamps.Count, 320, 180),
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

            using var export = new MediaFoundationVideoFrameReader(clip);
            double duration = editor.DurationMsForTest;
            int last = FrameStepCalculator.LastFrameIndex(fps, duration);
            var failures = new List<string>();

            void Check(string step, int expectedSlot)
            {
                double playhead = editor!.TimelineForTest.PlayheadMs;
                int slot = FrameStepCalculator.FrameIndexAt(playhead, fps, duration);
                if (slot != expectedSlot)
                {
                    failures.Add($"{step}: playhead {playhead:F3} ms is frame slot {slot}, expected {expectedSlot}");
                }

                DecodedVideoFrame decoded = export.ReadFrameAt(playhead);
                int expected = FrameIndexClip.Read(decoded.Pixels, decoded.Width, decoded.Height, decoded.Stride);
                if (constantRate && expected != expectedSlot)
                {
                    failures.Add($"{step}: the export decoder took frame {expected} at {playhead:F3} ms");
                }

                int shown = WaitForFrame(editor, expected);
                if (shown != expected)
                {
                    failures.Add($"{step}: playhead {playhead:F3} ms shows frame {shown}, the export takes {expected}");
                }
            }

            editor.ShortcutActions["First"]();
            Check("first", 0);
            for (int slot = 1; slot <= last + 1; slot++)
            {
                editor.ShortcutActions["NextFrame"]();
                Check($"forward {slot}", Math.Min(slot, last));
            }

            editor.ShortcutActions["Last"]();
            for (int slot = last - 1; slot >= -1; slot--)
            {
                editor.ShortcutActions["PreviousFrame"]();
                Check($"back {slot}", Math.Max(slot, 0));
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(dir);
        }
    }

    /// <summary>Waits for the preview to present <paramref name="expected"/>; returns what it shows.</summary>
    private static int WaitForFrame(VideoEditorWindow editor, int expected)
    {
        Task idle = editor.PreviewSeekCoordinatorForTest.WaitForIdleForTestAsync();
        DateTime deadline = DateTime.UtcNow + PresentTimeout;
        while (!idle.IsCompleted && DateTime.UtcNow < deadline)
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(5));

        int shown;
        do
        {
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
            shown = FrameIndexClip.Read(editor.RenderCurrentFrameForTest()
                ?? throw new Xunit.Sdk.XunitException("the preview rendered no frame"));
        }
        while (shown != expected && DateTime.UtcNow < deadline);
        return shown;
    }
}
