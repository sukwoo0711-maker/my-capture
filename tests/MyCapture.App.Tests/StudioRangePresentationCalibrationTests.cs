using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StudioRangePresentationCalibrationTests
{
    public static TheoryData<bool, int, int, bool> RangeOutcomes
    {
        get
        {
            var data = new TheoryData<bool, int, int, bool>();
            foreach (bool studioRange in new[] { false, true })
            foreach (int studio in Enumerable.Range(0, 3))
            foreach (int full in Enumerable.Range(0, 3))
            foreach (bool hardware in new[] { false, true })
                data.Add(studioRange, studio, full, hardware);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RangeOutcomes))]
    public void InjectedOutcomesUpdateBothRangesAndChangedClips(bool studioRange, int studio, int full, bool hardware) => StaTestHost.Run(() =>
    {
        StudioRangePresentationCalibration previous = StudioRangeVideoEffect.Calibration;
        Func<bool> previousHardware = StudioRangeVideoEffect.HardwareShaderAvailable;
        var measured = new TaskCompletionSource<VideoRangeCalibrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var calibration = new StudioRangePresentationCalibration(_ =>
        {
            Interlocked.Increment(ref calls);
            return measured.Task;
        });
        StudioRangeVideoEffect.Calibration = calibration;
        StudioRangeVideoEffect.HardwareShaderAvailable = () => hardware;
        try
        {
            Assert.False(calibration.IsStarted);
            var first = new MediaElement();
            var second = new MediaElement();
            var switched = new MediaElement();
            var cleared = new MediaElement();
            foreach (MediaElement media in new[] { first, second, switched, cleared })
            {
                StudioRangeVideoEffect.Apply(media, studioRange);
                RangePatternClip.AssertEffect(media, hardware && studioRange ? VideoRangeCorrection.Expand : VideoRangeCorrection.None);
            }
            Assert.True(calibration.IsStarted); // Either known input range starts the shared run.
            StudioRangeVideoEffect.Apply(switched, !studioRange);
            StudioRangeVideoEffect.Clear(cleared);
            measured.SetResult(new((VideoRangePresentation)studio, (VideoRangePresentation)full));
            RangePatternClip.WaitForCalibration();
            Assert.Equal(1, calls);
            VideoRangeCorrection expected = Expected(studioRange ? studio : full, hardware);
            RangePatternClip.AssertEffect(first, expected);
            RangePatternClip.AssertEffect(second, expected);
            RangePatternClip.AssertEffect(switched, Expected(studioRange ? full : studio, hardware));
            Assert.Null(cleared.Effect); // An unknown replacement clip must not get a late update.

            var later = new MediaElement();
            StudioRangeVideoEffect.Apply(later, studioRange);
            RangePatternClip.AssertEffect(later, expected);
            StudioRangeVideoEffect.Apply(later, !studioRange);
            RangePatternClip.AssertEffect(later, Expected(studioRange ? full : studio, hardware));
            Assert.Equal(1, calls);
        }
        finally
        {
            StudioRangeVideoEffect.Calibration = previous;
            StudioRangeVideoEffect.HardwareShaderAvailable = previousHardware;
        }
    });

    private static VideoRangeCorrection Expected(int outcome, bool hardware) => !hardware ? VideoRangeCorrection.None : outcome switch
    {
        0 => VideoRangeCorrection.None,
        1 => VideoRangeCorrection.Compress,
        2 => VideoRangeCorrection.Expand,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    [Fact]
    public Task PreviewWindowsDoNotStartCalibrationBeforeAClipIsOpened() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PreviewWindowsDoNotStartCalibrationBeforeAClipIsOpened), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            StudioRangePresentationCalibration previous = StudioRangeVideoEffect.Calibration;
            var calibration = new StudioRangePresentationCalibration(_ =>
                Task.FromResult(new VideoRangeCalibrationResult(VideoRangePresentation.Correct, VideoRangePresentation.Correct)));
            StudioRangeVideoEffect.Calibration = calibration;
            GalleryVideoPlayerWindow? gallery = null;
            VideoEditorWindow? editor = null;
            string root = OwnedTestDirectory.Create("mc-range-lazy-");
            try
            {
                string missing = Path.Combine(root, "not-opened.mp4");
                gallery = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000 };
                editor = new VideoEditorWindow(new RecordingResult(missing, 1000, 30, 30, 640, 360),
                    AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
                Assert.Null(editor.MediaForTest.Effect);
                Assert.False(calibration.IsStarted);
                gallery.Show();
                Assert.False(gallery.Open(Guid.NewGuid(), "missing", missing, autoPlay: false));
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(100));
                Assert.False(calibration.IsStarted);
            }
            finally
            {
                gallery?.Close();
                editor?.Close();
                StudioRangeVideoEffect.Calibration = previous;
                application.Shutdown();
                OwnedTestDirectory.Delete(root);
            }
        }, typeof(StudioRangePresentationCalibrationTests));

    [Fact]
    public async Task ConcurrentRequestsMeasureBothRangesOnceAndCacheTheResult()
    {
        int calls = 0;
        var expected = new VideoRangeCalibrationResult(VideoRangePresentation.Correct, VideoRangePresentation.ExpandedAsStudio);
        var calibration = new StudioRangePresentationCalibration(async _ =>
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return expected;
        });
        Assert.Equal(VideoRangeCorrection.Expand, calibration.CorrectionFor(true));
        Assert.Equal(VideoRangeCorrection.None, calibration.CorrectionFor(false));
        Assert.False(calibration.IsStarted);
        VideoRangeCalibrationResult[] results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(calibration.GetResultAsync)));
        Assert.All(results, result => Assert.Equal(expected, result));
        Assert.Equal(VideoRangeCorrection.None, calibration.CorrectionFor(true));
        Assert.Equal(VideoRangeCorrection.Compress, calibration.CorrectionFor(false));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrTimeoutRetainsBothDefaultsAndLogsOnlyOnce(bool timeout)
    {
        int calls = 0, logs = 0;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calibration = new StudioRangePresentationCalibration(async token =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult(token);
            if (timeout) await Task.Delay(Timeout.Infinite, token);
            throw new IOException("fixture failure");
        }, TimeSpan.FromMilliseconds(150), _ => Interlocked.Increment(ref logs));
        Task<VideoRangeCalibrationResult> first = calibration.GetResultAsync();
        Assert.Same(first, calibration.GetResultAsync());
        Assert.Equal(default, await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(VideoRangeCorrection.Expand, calibration.CorrectionFor(true));
        Assert.Equal(VideoRangeCorrection.None, calibration.CorrectionFor(false));
        Assert.Equal(default, await calibration.GetResultAsync());
        Assert.Equal(1, calls);
        Assert.Equal(1, logs);
        Assert.True((await entered.Task.WaitAsync(TimeSpan.FromSeconds(3))).IsCancellationRequested);
        if (timeout) Assert.IsType<TimeoutException>(calibration.Failure);
        else Assert.IsType<IOException>(calibration.Failure);
    }

    [Fact]
    public async Task SynchronouslyBlockedMeasurementDoesNotBlockTheCallerOrReplaceTimedOutFallback()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calibration = new StudioRangePresentationCalibration(_ =>
        {
            entered.SetResult();
            release.Wait();
            exited.SetResult();
            return Task.FromResult(new VideoRangeCalibrationResult(VideoRangePresentation.Correct, VideoRangePresentation.ExpandedAsStudio));
        }, TimeSpan.FromMilliseconds(150), _ => { });
        try
        {
            Task<VideoRangeCalibrationResult> result = calibration.GetResultAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(default, await result.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsType<TimeoutException>(calibration.Failure);
        }
        finally
        {
            release.Set();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(VideoRangeCorrection.Expand, calibration.CorrectionFor(true));
        Assert.Equal(VideoRangeCorrection.None, calibration.CorrectionFor(false));
    }

    [Theory]
    [InlineData(16, 71, 181, 235, 2)]
    [InlineData(18, 73, 183, 237, 2)]
    [InlineData(0, 64, 192, 255, 0)]
    [InlineData(2, 66, 194, 253, 0)]
    [InlineData(0, 59, 197, 255, 0)]
    [InlineData(0, 56, 205, 255, 1)]
    [InlineData(2, 58, 207, 253, 1)]
    [InlineData(0, 61, 200, 255, 1)]
    [InlineData(0, 60, 198, 255, -1)]
    [InlineData(0, 60, 199, 255, -1)]
    [InlineData(0, 0, 0, 0, -1)]
    [InlineData(255, 255, 255, 255, -1)]
    [InlineData(8, 68, 187, 245, -1)]
    [InlineData(0, 64, 205, 255, -1)]
    public void ClassifierRequiresTheWholeKnownPattern(byte dark, byte midDark, byte midLight, byte light, int expected)
    {
        byte[] patches = Patches(dark, midDark, midLight, light);
        Assert.Equal(expected < 0 ? null : (VideoRangePresentation?)expected, StudioRangeCalibrationProbe.Classify(patches));
        patches[3] = 0;
        Assert.Null(StudioRangeCalibrationProbe.Classify(patches));
    }

    [Fact]
    public void ClassifierRejectsMissingPixelsAndAColourChannelMismatch()
    {
        Assert.Null(StudioRangeCalibrationProbe.Classify([]));
        Assert.Null(StudioRangeCalibrationProbe.Classify(new byte[15]));
        Assert.Null(StudioRangeCalibrationProbe.Classify(new byte[17]));
        byte[] patches = Patches(0, 64, 192, 255);
        patches[5] = 90;
        Assert.Null(StudioRangeCalibrationProbe.Classify(patches));
    }

    [Theory]
    [InlineData(0, 64, 192, 255, 0)]
    [InlineData(0, 56, 205, 255, 1)]
    [InlineData(16, 71, 181, 235, 2)]
    public void ConfirmationRequiresTwoConsecutiveConsistentSamples(byte dark, byte midDark, byte midLight, byte light, int expected)
    {
        var samples = new StudioRangeCalibrationProbe.ConsistentSamples();
        byte[] patches = Patches(dark, midDark, midLight, light);
        byte[] different = expected == 0 ? Patches(16, 71, 181, 235) : Patches(0, 64, 192, 255);
        Assert.Null(samples.Add(patches));
        Assert.Null(samples.Add(different));
        Assert.Null(samples.Add(patches));
        Assert.Null(samples.Add(new byte[16]));
        Assert.Null(samples.Add(patches));
        Assert.Equal((VideoRangePresentation)expected, samples.Add(patches));
    }

    private static byte[] Patches(byte dark, byte midDark, byte midLight, byte light) =>
        [dark, dark, dark, 255, midDark, midDark, midDark, 255,
         midLight, midLight, midLight, 255, light, light, light, 255];
}
