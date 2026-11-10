using MyCapture.App.Diagnostics;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioDiagnosticToneTests
{
    [Fact]
    public void PcmIsOneSecondOfIdenticalStereoAtMinus60Dbfs()
    {
        short[] samples = RecordingAudioDiagnosticTone.CreateSamples();
        Assert.Equal(48_000, RecordingAudioFormat.SampleRate);
        Assert.Equal(48_000 * 2, samples.Length);
        Assert.Equal(-60, RecordingAudioDiagnosticTone.AmplitudeDbfs);
        int peak = 0;
        long sum = 0;
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            Assert.Equal(samples[frame * 2], samples[frame * 2 + 1]);
            peak = Math.Max(peak, Math.Abs((int)samples[frame * 2]));
            sum += samples[frame * 2];
        }
        Assert.Equal(33, peak); // PCM16 rounds the requested 32.767 peak to the nearest integer.
        Assert.Equal(0, sum);
        Assert.InRange(20 * Math.Log10((double)peak / short.MaxValue), -60.1, -59.9);
    }

    [Fact]
    public void ExactOneKilohertzPeriodLoopsWithoutPhaseOrSampleDiscontinuity()
    {
        short[] samples = RecordingAudioDiagnosticTone.CreateSamples();
        const int framesPerCycle = 48;
        Assert.Equal(0, samples[0]);
        Assert.Equal(33, samples[12 * 2]);
        Assert.Equal(0, samples[24 * 2]);
        Assert.Equal(-33, samples[36 * 2]);
        for (int frame = 0; frame < samples.Length / 2; frame++)
        {
            Assert.Equal(samples[(frame % framesPerCycle) * 2], samples[frame * 2]);
            short next = samples[((frame + 1) % (samples.Length / 2)) * 2];
            Assert.InRange(Math.Abs(next - samples[frame * 2]), 0, 5);
        }
        Assert.Equal(samples[(framesPerCycle - 1) * 2], samples[^2]);
    }

    [Fact]
    public void IndependentCallsReturnIndependentPcmBuffers()
    {
        short[] first = RecordingAudioDiagnosticTone.CreateSamples();
        short[] second = RecordingAudioDiagnosticTone.CreateSamples();
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        first[0] = short.MaxValue;
        Assert.Equal(0, second[0]);
    }
}
