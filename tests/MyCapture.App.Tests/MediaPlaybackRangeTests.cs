using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// WPF may retain studio RGB or expand either input range. In-app players calibrate both
/// ranges and show the source colours within the limits of presenter clipping.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorPreviewShowsTheDecodedColoursOfStudioAndFullRangeClips(bool fullRange) => StaTestHost.Run(() =>
    {
        string dir = OwnedTestDirectory.Create("mc-range-editor-");
        VideoEditorWindow? editor = null;
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
            editor.Show();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
            Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
            RangePatternClip.WaitForCalibration();
            VideoRangeCorrection correction = RangePatternClip.AssertCalibratedEffect(editor.MediaForTest, !fullRange);
            bool hardware = StudioRangeVideoEffect.HardwareShaderAvailable();
            // Software rendering keeps the existing no-effect policy.
            if (!hardware && StudioRangeVideoEffect.Calibration.CorrectionFor(!fullRange) != VideoRangeCorrection.None) return;
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(500));

            BitmapSource frame = editor.RenderCurrentFrameForTest() ?? throw new Xunit.Sdk.XunitException("no preview frame");
            // Keep the existing tolerances: full-range decoding may differ by up to 12 steps
            // on software decoders. Compression only accounts for irrecoverably clipped endpoints.
            RangePatternClip.AssertMatchesSource(frame, width, height, tolerance: fullRange ? 12 : 8, correction: correction);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(dir);
        }
    });
}

/// <summary>A clip of flat grey steps and colour patches with known source RGB.</summary>
internal static class RangePatternClip
{
    internal static void WaitForCalibration()
    {
        Assert.True(StudioRangeVideoEffect.Calibration.IsStarted, "Opening a clip with known range must start calibration.");
        Task<VideoRangeCalibrationResult> result = StudioRangeVideoEffect.Calibration.GetResultAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!result.IsCompleted && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(result.IsCompletedSuccessfully, "MediaElement range calibration did not finish within its bound.");
        // Let the completion update the player on its dispatcher before inspecting the effect.
        Pump(TimeSpan.FromMilliseconds(80));
    }

    internal static VideoRangeCorrection AssertCalibratedEffect(System.Windows.Controls.MediaElement media, bool studioRange)
    {
        StudioRangePresentationCalibration calibration = StudioRangeVideoEffect.Calibration;
        bool hardware = StudioRangeVideoEffect.HardwareShaderAvailable();
        if (hardware)
        {
            Assert.Null(calibration.Failure);
            VideoRangeCalibrationResult result = calibration.GetResultAsync().GetAwaiter().GetResult();
            Assert.NotNull(result.Studio);
            Assert.NotNull(result.Full);
        }
        VideoRangeCorrection correction = hardware ? calibration.CorrectionFor(studioRange) : VideoRangeCorrection.None;
        AssertEffect(media, correction);
        return correction;
    }

    internal static void AssertEffect(System.Windows.Controls.MediaElement media, VideoRangeCorrection correction)
    {
        switch (correction)
        {
            case VideoRangeCorrection.Expand: Assert.IsType<StudioRangeVideoEffect>(media.Effect); break;
            case VideoRangeCorrection.Compress: Assert.IsType<FullRangeVideoEffect>(media.Effect); break;
            default: Assert.Null(media.Effect); break;
        }
    }

    internal static readonly (string Name, byte R, byte G, byte B)[] Patches =
    [
        ("black", 0, 0, 0), ("grey 16", 16, 16, 16), ("grey 64", 64, 64, 64), ("grey 128", 128, 128, 128),
        ("grey 192", 192, 192, 192), ("grey 235", 235, 235, 235), ("white", 255, 255, 255), ("red", 255, 0, 0),
        ("green", 0, 255, 0), ("blue", 0, 0, 255), ("yellow", 255, 255, 0), ("cyan", 0, 255, 255),
        ("magenta", 255, 0, 255), ("orange", 0xF2, 0xB1, 0x34), ("navy", 0x1D, 0x2A, 0x3C), ("accent", 0x2E, 0x6F, 0xD8),
    ];

    internal static string Encode(string path, int width, int height, bool fullRange)
    {
        int stride = width * 4;
        byte[] frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (_, byte r, byte g, byte b) = Patches[(y * 2 / height * 8) + (x * 8 / width)];
                int o = (y * stride) + (x * 4);
                frame[o] = b;
                frame[o + 1] = g;
                frame[o + 2] = r;
                frame[o + 3] = 255;
            }
        }

        var options = new VideoEncoderOptions(path, width, height, 30, VideoEncoderOptions.DeriveBitrate(width, height, 30))
        {
            FullRange = fullRange,
        };
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int i = 0; i < 30; i++)
        {
            encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * 1000.0 / 30));
        }

        encoder.Complete();
        return path;
    }

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
    internal static void AssertMatchesSource(BitmapSource shown, int width, int height, int tolerance = 8, VideoRangeCorrection correction = VideoRangeCorrection.None)
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
            + $"; studio={StudioRangeVideoEffect.Calibration.CorrectionFor(true)}, full={StudioRangeVideoEffect.Calibration.CorrectionFor(false)}, calibrationFailure={StudioRangeVideoEffect.Calibration.Failure}");
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
