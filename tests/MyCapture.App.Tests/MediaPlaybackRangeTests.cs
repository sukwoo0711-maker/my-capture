using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// WPF may retain studio RGB or expand either input range. In-app previews verify each player
/// and show the source colours within the limits of presenter clipping.
/// </summary>
public sealed class MediaPlaybackRangeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StudioRangeCorrectionRunsOnlyWithHardwareShaders(bool hardware) => StaTestHost.Run(() =>
    {
        Func<bool> previous = StudioRangeVideoEffect.HardwareShaderAvailable;
        StudioRangePresentationCalibration previousCalibration = StudioRangeVideoEffect.Calibration;
        StudioRangeVideoEffect.Calibration = new(_ => Task.FromResult(default(VideoRangeCalibrationResult)));
        StudioRangeVideoEffect.HardwareShaderAvailable = () => hardware;
        try
        {
            var media = new System.Windows.Controls.MediaElement();
            StudioRangeVideoEffect.Apply(media, studioRange: true);
            Assert.Equal(hardware, media.Effect is StudioRangeVideoEffect);
            StudioRangeVideoEffect.Apply(media, studioRange: false);
            Assert.Null(media.Effect);
        }
        finally
        {
            StudioRangeVideoEffect.HardwareShaderAvailable = previous;
            StudioRangeVideoEffect.Calibration = previousCalibration;
        }
    });

    [Fact]
    public void StudioRangeShaderBytecodeIsItsCheckedInSource()
    {
        byte[] compiled = ShaderCompiler.Compile(StudioRangeVideoEffect.Source, "main", "ps_2_0");
        Assert.Equal(
            ShaderCompiler.Disassemble(compiled),
            ShaderCompiler.Disassemble(StudioRangeVideoEffect.BytecodeForTest.ToArray()));
    }

    [Fact]
    public void FullRangeShaderBytecodeIsItsCheckedInSource()
    {
        byte[] compiled = ShaderCompiler.Compile(FullRangeVideoEffect.Source, "main", "ps_2_0");
        Assert.Equal(
            ShaderCompiler.Disassemble(compiled),
            ShaderCompiler.Disassemble(FullRangeVideoEffect.BytecodeForTest.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    public void FullRangeCompressionPreservesPremultipliedAlpha(byte alpha) => StaTestHost.Run(() =>
    {
        byte[] pixels = [0, (byte)(56 * alpha / 255), (byte)(205 * alpha / 255), alpha];
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, 4);
        var image = new System.Windows.Controls.Image
        {
            Width = 16, Height = 16, Source = bitmap, Effect = new FullRangeVideoEffect(),
        };
        image.Measure(new Size(16, 16));
        image.Arrange(new Rect(0, 0, 16, 16));
        var shown = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        shown.Render(image);
        byte[] actual = new byte[4];
        shown.CopyPixels(new Int32Rect(8, 8, 1, 1), actual, 4, 0);
        Assert.Equal(alpha, actual[3]);
        for (int channel = 0; channel < 3; channel++)
        {
            double expected = pixels[channel] * (219.0 / 255.0) + alpha * (16.0 / 255.0);
            Assert.InRange(Math.Abs(actual[channel] - expected), 0, 2);
        }
    });

    [Theory]
    [InlineData(640, 360, false)]
    [InlineData(1280, 720, false)]
    [InlineData(640, 360, true)]
    [InlineData(1280, 720, true)]
    public void SourceInfoTellsStudioFromFullRangeAndKeepsTheContainerDuration(int width, int height, bool fullRange)
    {
        string dir = OwnedTestDirectory.Create("mc-range-info-");
        try
        {
            string clip = RangePatternClip.Encode(Path.Combine(dir, "clip.mp4"), width, height, fullRange);
            VideoSourceInfo info = MediaFoundationVideoFrameReader.ReadSourceInfo(clip);
            Assert.Equal(!fullRange, info.StudioRange);
            Assert.Equal(MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip), info.DurationMs);
        }
        finally
        {
            OwnedTestDirectory.Delete(dir);
        }
    }

    // lateOpen: MediaOpened arrives after the open fallback has made the editor ready, as on a
    // loaded CI host where it took over 5 s. The fallback marks the editor ready before the range
    // is known or a frame is decoded, so readiness alone is not the signal for either.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EditorPreviewShowsTheDecodedColoursOfStudioAndFullRangeClips(bool fullRange, bool lateOpen) =>
        VerifyEditorPreview(fullRange, lateOpen, false);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EditorMeasuredShaderReproducesSourceThroughRealPlayer(bool fullRange) =>
        VerifyEditorPreview(fullRange, false, true);

    private static void VerifyEditorPreview(bool fullRange, bool lateOpen, bool forceMeasured) => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-range-editor-");
        VideoEditorWindow? editor = null;
        bool previousForce = MediaElementRangeVerification.ForceMeasuredForTest;
        MediaElementRangeVerification.ForceMeasuredForTest = forceMeasured;
        try
        {
            const int width = 1280, height = 720;
            string clip = RangePatternClip.Encode(Path.Combine(dir, "clip.mp4"), width, height, fullRange);
            var recording = new RecordingResult(clip, 1000, 30, 30, width, height);
            editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(dir), NullLoggerFactory.Instance)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 1100, Height = 760,
                ShowActivated = false, ShowInTaskbar = false,
            };
            if (lateOpen) editor.OpenTimeoutForTest = TimeSpan.Zero;
            editor.Show();
            Assert.True(RangePatternClip.PumpUntil(() => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(15))
                && editor.IsMediaReadyForTest, editor.MediaFailureForTest);
            // The range follows the source probe, with or without MediaOpened, and enables per-player verification.
            Assert.True(RangePatternClip.PumpUntil(() => editor.IsSourceRangeKnownForTest, TimeSpan.FromSeconds(15)), "the source probe did not finish");
            RangePatternClip.WaitForVerification(editor.MediaForTest);
            VideoRangeCorrection correction = RangePatternClip.AssertVerifiedEffect(editor.MediaForTest, !fullRange);
            bool hardware = StudioRangeVideoEffect.HardwareShaderAvailable();
            if (forceMeasured)
            {
                Assert.True(hardware, "Real measured shader test requires hardware PS 2.0.");
                Assert.Equal("measured", StudioRangeVideoEffect.VerificationFor(editor.MediaForTest)!.LastResult!.Kind);
                Assert.IsType<MeasuredRangeVideoEffect>(editor.MediaForTest.Effect);
            }
            // Without hardware shaders (hosted CI, Remote Desktop) a needed correction is skipped by
            // design, so only the effect decision is checked there.
            if (!hardware && !fullRange) return;
            Assert.True(RangePatternClip.PumpUntil(() => editor.HasMediaOpenedForTest || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(30))
                && editor.HasMediaOpenedForTest, editor.HasMediaFailedForTest ? editor.MediaFailureForTest : "MediaOpened did not arrive");

            BitmapSource frame = RangePatternClip.WaitForPresentedFrame(editor.RenderCurrentFrameForTest, width, height, TimeSpan.FromSeconds(10));
            // A full-range clip shown without an effect relies on the platform decoder: the software
            // H.264 decoder on hosts without hardware decoding (GitHub-hosted runners) lifts
            // full-range zero by up to 10 steps (cyan read (10,254,254)). Studio-range recordings keep
            // the strict 8. Compression only accounts for irrecoverably clipped endpoints.
            RangePatternClip.AssertMatchesSource(frame, width, height, tolerance: fullRange ? 12 : 8, correction: correction,
                verification: StudioRangeVideoEffect.VerificationFor(editor.MediaForTest)?.LastResult);
        }
        catch (Exception ex)
        {
            var measured = editor is null ? null : StudioRangeVideoEffect.VerificationFor(editor.MediaForTest)?.LastResult;
            throw new Xunit.Sdk.XunitException($"{ex}\nPlayer verification: {measured?.Details}\n{measured?.Evidence}");
        }
        finally
        {
            editor?.Close();
            MediaElementRangeVerification.ForceMeasuredForTest = previousForce;
            OwnedTestDirectory.Delete(dir);
        }
    });
}

/// <summary>A clip of flat grey steps and colour patches with known source RGB.</summary>
internal static class RangePatternClip
{
    // Legacy injection helper for the separately owned frame-save tests only.
    internal static void WaitForCalibration()
    {
        Assert.True(StudioRangeVideoEffect.Calibration.IsStarted, "Opening a clip with known range must start calibration.");
        Task<VideoRangeCalibrationResult> result = StudioRangeVideoEffect.Calibration.GetResultAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);
        while (!result.IsCompleted && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(result.IsCompletedSuccessfully, "MediaElement range calibration did not finish within its bound.");
        // Let the completion update the player on its dispatcher before inspecting the effect.
        Pump(TimeSpan.FromMilliseconds(80));
    }

    internal static void WaitForVerification(System.Windows.Controls.MediaElement media)
    {
        if (!StudioRangeVideoEffect.HardwareShaderAvailable()) return;
        Assert.True(PumpUntil(() => StudioRangeVideoEffect.VerificationFor(media)?.Completion.IsCompleted == true,
            TimeSpan.FromSeconds(20)), "Player verification did not complete after presenting a frame.");
        var result = StudioRangeVideoEffect.VerificationFor(media)!.Completion.GetAwaiter().GetResult();
        string details = result.Details + Environment.NewLine + result.Evidence;
        if (result.Correction is null || result.IsFallback)
        {
            var sample = MediaElementRangeVerification.Sample(media);
            var original = MediaElementRangeVerification.WithoutEffect(media, () => VideoRangeColourPattern.Observe(media));
            details += $" actual={media.ActualWidth}x{media.ActualHeight}; natural={media.NaturalVideoWidth}x{media.NaturalVideoHeight}; "
                + $"bounds={VisualTreeHelper.GetContentBounds(media)}; raw="
                + string.Join(";", original.Patches.Select(p => $"{p.R},{p.G},{p.B}"));
            if (sample is not null) details += "; sampled=" + string.Join(";", Enumerable.Range(0, 16).Select(i =>
            {
                var (x, y) = Centre(i, sample.Width, sample.Height);
                int o = (y * sample.Width + x) * 4;
                return $"{sample.Pixels[o+2]},{sample.Pixels[o+1]},{sample.Pixels[o]}";
            }));
        }
        Assert.True(result.Correction.HasValue && !result.IsFallback, details);
        Pump(TimeSpan.FromMilliseconds(80));
    }

    internal static VideoRangeCorrection AssertVerifiedEffect(System.Windows.Controls.MediaElement media, bool studioRange)
    {
        bool hardware = StudioRangeVideoEffect.HardwareShaderAvailable();
        var result = StudioRangeVideoEffect.VerificationFor(media)?.LastResult;
        if (hardware)
        {
            Assert.NotNull(result);
            Assert.True(result.Status == "verified" && !result.IsFallback && result.Correction.HasValue,
                result.Details + Environment.NewLine + result.Evidence);
        }
        VideoRangeCorrection correction = hardware ? result!.Correction!.Value : VideoRangeCorrection.None;
        AssertEffect(media, correction);
        return correction;
    }

    internal static void AssertEffect(System.Windows.Controls.MediaElement media, VideoRangeCorrection correction)
    {
        switch (correction)
        {
            case VideoRangeCorrection.Expand: Assert.IsType<StudioRangeVideoEffect>(media.Effect); break;
            case VideoRangeCorrection.Compress: Assert.IsType<FullRangeVideoEffect>(media.Effect); break;
            case VideoRangeCorrection.Measured: Assert.IsType<MeasuredRangeVideoEffect>(media.Effect); break;
            default: Assert.Null(media.Effect); break;
        }
    }

    internal static readonly (string Name, byte R, byte G, byte B)[] Patches = VideoRangeColourPattern.Patches;

    internal static string Encode(string path, int width, int height, bool fullRange) =>
        VideoRangeColourPattern.Encode(path, width, height, fullRange);

    /// <summary>Patch centres in a rendering of the whole frame at <paramref name="width"/> x <paramref name="height"/>.</summary>
    internal static (int X, int Y) Centre(int index, int width, int height) =>
        ((int)(((index % 8) + 0.5) * width / 8.0), (int)(((index / 8) + 0.5) * height / 2.0));

    internal static (byte R, byte G, byte B) Sample(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>
    /// Every patch shows within a few steps of its source colour. Studio range shows within 3 steps;
    /// WPF's own full-range conversion leaves saturated blue up to 7 steps short. Unexpanded studio
    /// range is 16 or more steps off at black, white and every saturated primary. If a presenter
    /// expanded and clipped the source, only its recoverable 16-235 channels can be compared.
    /// </summary>
    internal static void AssertMatchesSource(BitmapSource shown, int width, int height, int tolerance = 8,
        VideoRangeCorrection correction = VideoRangeCorrection.None, RangeVerificationResult? verification = null)
    {
        var failures = new List<string>();
        for (int i = 0; i < Patches.Length; i++)
        {
            (int x, int y) = Centre(i, width, height);
            (byte r, byte g, byte b) = Sample(shown, x, y);
            (string name, byte er, byte eg, byte eb) = Patches[i];
            (byte sourceR, byte sourceG, byte sourceB) = (er, eg, eb);
            // Erroneous studio expansion clips source channels outside 16-235 before the effect.
            // Compare against the recoverable source for that measured path, with unchanged tolerance.
            if (correction == VideoRangeCorrection.Compress)
            {
                er = Math.Clamp(er, (byte)16, (byte)235);
                eg = Math.Clamp(eg, (byte)16, (byte)235);
                eb = Math.Clamp(eb, (byte)16, (byte)235);
            }
            if (Math.Abs(r - er) > tolerance || Math.Abs(g - eg) > tolerance || Math.Abs(b - eb) > tolerance)
            {
                failures.Add($"{name} shows ({r},{g},{b}), source ({sourceR},{sourceG},{sourceB}), recoverable ({er},{eg},{eb})");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures)
            + $"; correction={correction}; {verification?.Details}\n{verification?.Evidence}");
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds; false when <paramref name="timeout"/> passes first.</summary>
    internal static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return false;
            Pump(TimeSpan.FromMilliseconds(20));
        }

        return true;
    }

    /// <summary>
    /// The first frame the preview presents. MediaElement raises nothing when a paused frame
    /// reaches the screen, and until one does the preview renders empty, every patch black, so
    /// the rendering is polled until a patch shows anything else. The frame is returned as
    /// rendered, still empty when <paramref name="timeout"/> passes.
    /// </summary>
    internal static BitmapSource WaitForPresentedFrame(Func<BitmapSource?> render, int width, int height, TimeSpan timeout)
    {
        BitmapSource? frame = null;
        PumpUntil(() => (frame = render()) is not null && !IsEmpty(frame, width, height), timeout);
        return frame ?? throw new Xunit.Sdk.XunitException("no preview frame");
    }

    private static bool IsEmpty(BitmapSource frame, int width, int height)
    {
        for (int i = 0; i < Patches.Length; i++)
        {
            (int x, int y) = Centre(i, width, height);
            if (Sample(frame, x, y) != (0, 0, 0)) return false;
        }

        return true;
    }

    internal static void Pump(TimeSpan duration)
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
