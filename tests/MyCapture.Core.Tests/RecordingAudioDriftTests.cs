using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingAudioDriftTests
{
    [Theory]
    [InlineData(44_100, -500)]
    [InlineData(44_100, -100)]
    [InlineData(44_100, 100)]
    [InlineData(44_100, 500)]
    [InlineData(48_000, -500)]
    [InlineData(48_000, -100)]
    [InlineData(48_000, 100)]
    [InlineData(48_000, 500)]
    [InlineData(96_000, -500)]
    [InlineData(96_000, -100)]
    [InlineData(96_000, 100)]
    [InlineData(96_000, 500)]
    public void ThirtyVirtualMinutes_StayOnQpcGridInsteadOfAccumulatingDeviceDrift(int rate, int ppm)
    {
        var estimator = new RecordingAudioDriftEstimator(rate);
        double actualRate = rate * (1 + ppm / 1_000_000d);
        long previousOutput = 0, totalOutput = 0;
        // At these rates and ppm values, 100 seconds is an exact integer number of device
        // frames and 100 ns ticks. This isolates drift accuracy from timestamp quantization;
        // the varying-drift test below separately exercises dense 10 ms observations.
        for (int second = 0; second <= 1800; second += 100)
        {
            long position = (long)Math.Round(second * actualRate);
            long captureQpc = (long)Math.Round(position / actualRate * 10_000_000);
            Assert.True(estimator.Observe(position, captureQpc));
            long output = RecordingAudioFormat.SampleAt100ns(captureQpc);
            Assert.True(output >= previousOutput);
            totalOutput += output - previousOutput;
            previousOutput = output;
            if (second == 0) continue;
            // Predict between packet timestamps, not at the estimator's own anchor.
            long probeQpc = captureQpc + 123_457;
            double realPosition = probeQpc * actualRate / 10_000_000;
            Assert.InRange(Math.Abs(estimator.DevicePositionAt(probeQpc) - realPosition), 0, 0.02);
            Assert.InRange(Math.Abs(estimator.OutputPhaseError((long)Math.Round(realPosition), probeQpc)), 0, 1);
        }
        Assert.InRange(Math.Abs(totalOutput - 86_400_000), 0, 1);
        Assert.InRange(Math.Abs(estimator.PartsPerMillion - ppm), 0, 0.001);
        // Uncorrected device-count timing would miss by 180 ms or 900 ms here.
        double uncorrectedError = 1800 * ppm / 1_000_000d;
        Assert.True(Math.Abs(uncorrectedError) >= 0.18);
    }

    [Fact]
    public void BadOrStaleClockPairs_DoNotCorruptTheCurrentEstimate()
    {
        var estimator = new RecordingAudioDriftEstimator(48_000);
        Assert.True(estimator.Observe(100, 500));
        Assert.Throws<InvalidOperationException>(() => estimator.DevicePositionAt(1000));
        Assert.True(estimator.Observe(48_100, 10_000_500));
        Assert.False(estimator.Observe(48_000, 10_000_600));
        Assert.False(estimator.Observe(48_200, 10_000_400));
        Assert.False(estimator.Observe(96_100, 10_000_501));
        Assert.False(estimator.Observe(-1, 11_000_000));
        Assert.Equal(1, estimator.RateRatio);
        Assert.Equal(96_100, estimator.DevicePositionAt(20_000_500), 6);
    }

    [Theory]
    [InlineData(-500)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(500)]
    public void FractionalInterpolation_IsIndependentOfPacketBoundaries(int ppm)
    {
        const int frames = 24_000;
        float[] input = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            input[i * 2] = (float)Math.Sin(i * Math.PI / 24);
            input[i * 2 + 1] = (float)Math.Cos(i * Math.PI / 37);
        }
        double ratio = 1 + ppm / 1_000_000d;
        var whole = new RecordingAudioDriftCorrector();
        float[] expected = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(frames) * 2];
        int count = whole.Process(input, expected, ratio);
        var segmented = new RecordingAudioDriftCorrector();
        var actual = new List<float>();
        int[] sizes = [1, 17, 480, 2, 239, 481];
        int offset = 0, turn = 0;
        while (offset < frames)
        {
            int size = Math.Min(sizes[turn++ % sizes.Length], frames - offset);
            float[] output = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(size) * 2];
            int written = segmented.Process(input.AsSpan(offset * 2, size * 2), output, ratio);
            actual.AddRange(output.AsSpan(0, written * 2).ToArray());
            offset += size;
        }
        Assert.Equal(count * 2, actual.Count);
        for (int i = 0; i < actual.Count; i++) Assert.Equal(expected[i], actual[i]);
        Assert.InRange(Math.Abs(segmented.OutputFrames - (long)Math.Round(frames / ratio)), 0, 1);
        Assert.Equal(whole.NextInputPosition, segmented.NextInputPosition);
    }

    [Fact]
    public void RatioChanges_KeepContinuousPhaseAndDoNotInsertWholeSampleSteps()
    {
        var corrector = new RecordingAudioDriftCorrector();
        const int frames = 480;
        float[] input = new float[frames * 2];
        float[] output = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(frames) * 2];
        float previous = 0;
        long outputCount = 0;
        for (int packet = 0; packet < 3000; packet++)
        {
            long start = corrector.InputFrames;
            for (int i = 0; i < frames; i++) input[i * 2] = input[i * 2 + 1] = (float)Math.Sin((start + i) * Math.PI / 240);
            double ratio = packet % 2 == 0 ? 1.0005 : 0.9995;
            int written = corrector.Process(input, output, ratio);
            for (int i = 0; i < written; i++)
            {
                Assert.InRange(Math.Abs(output[i * 2] - previous), 0, 0.014);
                Assert.Equal(output[i * 2], output[i * 2 + 1]);
                previous = output[i * 2];
            }
            outputCount += written;
        }
        Assert.Equal(outputCount, corrector.OutputFrames);
        Assert.InRange(corrector.InputFrames - corrector.NextInputPosition, -0.001, 1.001);
        Assert.Throws<ArgumentOutOfRangeException>(() => corrector.Process(input, output, 1.1));
    }

    [Theory]
    [InlineData(-500)]
    [InlineData(-100)]
    [InlineData(100)]
    [InlineData(500)]
    public void ThirtyMinuteCorrection_ConsumesEveryBlockWithAtMostOneSampleAccountingError(int ppm)
    {
        double ratio = 1 + ppm / 1_000_000d;
        long inputBudget = (long)Math.Round(1800 * 48_000 * ratio);
        float[] input = new float[960];
        float[] output = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(480) * 2];
        var corrector = new RecordingAudioDriftCorrector();
        long written = 0, previousEnd = 0;
        int block = 0;
        while (corrector.InputFrames < inputBudget)
        {
            int frames = (int)Math.Min(480, inputBudget - corrector.InputFrames);
            long timestamp = RecordingAudioFormat.SampleTime100ns(written);
            int count = corrector.Process(input.AsSpan(0, frames * 2), output, ratio);
            Assert.True(timestamp >= previousEnd);
            written += count;
            previousEnd = RecordingAudioFormat.SampleTime100ns(written);
            if (++block % 100 == 0)
            {
                long expected = (long)Math.Round(corrector.InputFrames / ratio);
                Assert.InRange(Math.Abs(written - expected), 0, 1);
                Assert.InRange(Math.Abs(corrector.NextInputPosition - written * ratio), 0, 1);
            }
        }
        Assert.Equal(inputBudget, corrector.InputFrames);
        Assert.Equal(written, corrector.OutputFrames);
        Assert.InRange(Math.Abs(written - 86_400_000), 0, 1);
    }

    [Fact]
    public void ChangingDeviceDrift_EstimatorAndCorrectorRemainWithinFortyMilliseconds()
    {
        var estimator = new RecordingAudioDriftEstimator(48_000);
        var corrector = new RecordingAudioDriftCorrector();
        float[] input = new float[960];
        float[] output = new float[RecordingAudioDriftCorrector.MaximumOutputFrames(480) * 2];
        double qpc = 0;
        long position = 0;
        double maximumSkewSamples = 0;
        Assert.True(estimator.Observe(position, 0));
        // The same one-minute ramp and jumps repeat through a full 30-minute virtual run.
        // Timestamp pairs describe the first sample, with no packet-arrival jitter in them.
        for (int block = 0; block < 180_000; block++)
        {
            int phase = block % 6000;
            double ppm = phase < 2000 ? phase * 0.25 : phase < 4000 ? -500 : 500;
            double actualRatio = 1 + ppm / 1_000_000;
            qpc += 480 / (48_000 * actualRatio) * 10_000_000;
            position += 480;
            long stamp = (long)Math.Round(qpc);
            Assert.True(estimator.Observe(position, stamp));
            corrector.Process(input, output, estimator.RateRatio);
            if (block % 100 == 99)
            {
                double expectedOutput = qpc * 48_000 / 10_000_000;
                maximumSkewSamples = Math.Max(maximumSkewSamples, Math.Abs(corrector.OutputFrames - expectedOutput));
            }
        }
        // Two-second window lag at a 1000 ppm step is bounded by 2 ms; allow one native
        // sample and interpolation lookahead on top. The stored-timeline requirement is 40 ms.
        Assert.InRange(maximumSkewSamples, 0, 48_000 * 0.040);
        Assert.InRange(maximumSkewSamples, 0, 48_000 * 0.002 + 2);
    }
}
