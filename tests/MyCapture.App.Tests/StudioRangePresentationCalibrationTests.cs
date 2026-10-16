using System.IO;
using System.Windows.Controls;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StudioRangePresentationCalibrationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void PendingCalibrationUpdatesBothPlayersAndRespectsAChangedClip(bool needsExpansion, bool hardware) => StaTestHost.Run(() =>
    {
        StudioRangePresentationCalibration previous = StudioRangeVideoEffect.Calibration;
        Func<bool> previousHardware = StudioRangeVideoEffect.HardwareShaderAvailable;
        var measured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var gallery = new MediaElement();
            var editor = new MediaElement();
            var switched = new MediaElement();
            StudioRangeVideoEffect.Apply(gallery, studioRange: false);
            Assert.Equal(0, calls); // Full-range clips alone never start calibration.
            foreach (MediaElement media in new[] { gallery, editor, switched })
            {
                StudioRangeVideoEffect.Apply(media, studioRange: true);
                Assert.Equal(hardware, media.Effect is StudioRangeVideoEffect);
            }
            StudioRangeVideoEffect.Apply(switched, studioRange: false);
            measured.SetResult(needsExpansion);
            RangePatternClip.WaitForCalibration();
            Assert.Equal(1, calls);
            Assert.Equal(needsExpansion && hardware, gallery.Effect is StudioRangeVideoEffect);
            Assert.Equal(needsExpansion && hardware, editor.Effect is StudioRangeVideoEffect);
            Assert.Null(switched.Effect);

            var later = new MediaElement();
            StudioRangeVideoEffect.Apply(later, studioRange: true);
            Assert.Equal(needsExpansion && hardware, later.Effect is StudioRangeVideoEffect);
            Assert.Equal(1, calls); // New players reuse the completed process decision immediately.
        }
        finally
        {
            StudioRangeVideoEffect.Calibration = previous;
            StudioRangeVideoEffect.HardwareShaderAvailable = previousHardware;
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentRequestsMeasureOnceAndCacheEitherOutcome(bool needsExpansion)
    {
        int calls = 0;
        var calibration = new StudioRangePresentationCalibration(async _ =>
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            return needsExpansion;
        });
        Assert.True(calibration.NeedsExpansion);
        Assert.Equal(0, calls);
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(calibration.GetResultAsync)));
        Assert.All(results, result => Assert.Equal(needsExpansion, result));
        Assert.Equal(needsExpansion, calibration.NeedsExpansion);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrTimeoutRetainsCorrectionAndLogsOnlyOnce(bool timeout)
    {
        int calls = 0, logs = 0;
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calibration = new StudioRangePresentationCalibration(token =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult(token);
            return timeout ? Task.Delay(Timeout.Infinite, token).ContinueWith(_ => false, TaskScheduler.Default)
                : Task.FromException<bool>(new IOException("fixture failure"));
        }, TimeSpan.FromMilliseconds(150), _ => Interlocked.Increment(ref logs));
        Task<bool> first = calibration.GetResultAsync();
        Assert.Same(first, calibration.GetResultAsync());
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(calibration.NeedsExpansion);
        Assert.True(await calibration.GetResultAsync());
        Assert.Equal(1, calls);
        Assert.Equal(1, logs);
        Assert.True((await entered.Task.WaitAsync(TimeSpan.FromSeconds(3))).IsCancellationRequested);
        if (timeout) Assert.IsType<TimeoutException>(calibration.Failure);
        else Assert.IsType<IOException>(calibration.Failure);
    }

    [Theory]
    [InlineData(16, 71, 181, 235, true)]
    [InlineData(18, 73, 183, 237, true)]
    [InlineData(0, 64, 192, 255, false)]
    [InlineData(2, 66, 194, 253, false)]
    [InlineData(0, 0, 0, 0, null)]
    [InlineData(255, 255, 255, 255, null)]
    [InlineData(8, 68, 187, 245, null)]
    public void ClassifierRequiresTheWholeKnownPattern(byte dark, byte midDark, byte midLight, byte light, bool? expected)
    {
        byte[] patches = [dark, dark, dark, 255, midDark, midDark, midDark, 255,
            midLight, midLight, midLight, 255, light, light, light, 255];
        Assert.Equal(expected, StudioRangeCalibrationProbe.Classify(patches));
        patches[3] = 0;
        Assert.Null(StudioRangeCalibrationProbe.Classify(patches));
    }
}
