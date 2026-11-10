using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingAudioContractTests
{
    [Fact]
    public void PcmBlocks_UseAbsoluteSampleEndpointsForTimestamps()
    {
        long total = 0;
        for (long start = 0; start < 1_000_000; start += 17)
        {
            var block = new RecordingAudioBlock(start, new short[34]);
            Assert.Equal(17, block.FrameCount);
            Assert.Equal(start + 17, block.EndSample);
            Assert.Equal(total, block.Timestamp100ns);
            total += block.Duration100ns;
        }
        Assert.Equal(RecordingAudioFormat.SampleTime100ns(1_000_008), total);
        Assert.Throws<ArgumentException>(() => new RecordingAudioBlock(0, new short[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingAudioBlock(-1, new short[2]));
    }

    [Fact]
    public void DeviceFailure_DoesNotClearPreference_AndStaleResultsCannotReactivateSource()
    {
        var source = new RecordingAudioSourceStateMachine(true);
        long generation = source.Status.Generation;
        Assert.Equal(AudioSourceState.Starting, source.Status.State);
        Assert.True(source.TrySetCapturing(generation));
        Assert.True(source.TrySetUnavailable(generation));
        Assert.True(source.Status.Enabled);
        Assert.Equal(AudioSourceState.Unavailable, source.Status.State);
        Assert.True(source.SetPaused(true));
        Assert.Equal(AudioSourceState.Paused, source.Status.State);
        Assert.False(source.TrySetCapturing(generation));
        Assert.True(source.SetPaused(false));
        Assert.Equal(AudioSourceState.Starting, source.Status.State);
        Assert.True(source.TrySetCapturing(source.Status.Generation));
        Assert.True(source.SetEnabled(false));
        Assert.Equal(AudioSourceState.Off, source.Status.State);
        Assert.False(source.TrySetUnavailable(source.Status.Generation));
        Assert.True(source.SetEnabled(true));
        source.End();
        Assert.True(source.Status.Enabled);
        Assert.Equal(AudioSourceState.Off, source.Status.State);
        Assert.False(source.SetEnabled(false));
        Assert.False(source.TrySetCapturing(source.Status.Generation));
    }

    [Fact]
    public void OffSource_StaysOffDuringPause_AndRepeatedCommandsAreIdempotent()
    {
        var source = new RecordingAudioSourceStateMachine(false);
        Assert.Equal(AudioSourceState.Off, source.Status.State);
        Assert.False(source.SetEnabled(false));
        Assert.True(source.SetPaused(true));
        Assert.False(source.SetPaused(true));
        Assert.Equal(AudioSourceState.Off, source.Status.State);
        Assert.True(source.SetEnabled(true));
        Assert.Equal(AudioSourceState.Paused, source.Status.State);
        source.End();
        long generation = source.Status.Generation;
        source.End();
        Assert.Equal(generation, source.Status.Generation);
    }

    [Fact]
    public void LatePackets_AreClippedAfterConsumedSilence_AndOldGenerationsAreRejected()
    {
        var gate = new RecordingAudioPacketGate();
        gate.BeginGeneration(1);
        short[] samples = Enumerable.Range(0, 40).Select(i => (short)i).ToArray();
        var block = new RecordingAudioBlock(100, samples);
        gate.ConsumeThrough(110);
        Assert.True(gate.TryAccept(1, block, out var accepted));
        Assert.Equal(110, accepted.StartSample);
        Assert.Equal(10, accepted.FrameCount);
        Assert.Equal(samples[20..], accepted.Samples.ToArray());
        Assert.Equal(110, gate.ConsumedThroughSample);
        gate.ConsumeThrough(accepted.EndSample);
        Assert.False(gate.TryAccept(1, block, out _));
        gate.BeginGeneration(2);
        Assert.False(gate.TryAccept(1, new(120, samples), out _));
        Assert.True(gate.TryAccept(2, new(140, samples), out accepted));
        Assert.Equal(140, accepted.StartSample); // The missing gap is not stale PCM.
        gate.ConsumeThrough(160);
        gate.ConsumeThrough(150);
        Assert.Equal(160, gate.ConsumedThroughSample);
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.BeginGeneration(2));
    }

    [Fact]
    public void Mixer_FixedGainDoesNotPumpWhenOneSourceDisappears()
    {
        float[] source = [1, -1, 0.5f, -0.5f];
        short[] single = new short[4], both = new short[4];
        RecordingAudioMixer.Mix(source, [], single);
        RecordingAudioMixer.Mix(source, source, both);
        Assert.Equal(new short[] { 16384, -16384, 8192, -8192 }, single);
        Assert.Equal(new short[] { 32767, -32768, 16384, -16384 }, both);
        RecordingAudioMixer.Mix([float.NaN, float.PositiveInfinity, 8, -8], source, both);
        Assert.Equal(new short[] { 16384, -16384, 32767, -32768 }, both);
        Assert.Equal(0, RecordingAudioMixer.ToPcm16(float.NaN));
        Assert.Equal(1, RecordingAudioMixer.ToPcm16(0.5f / 32768));
        Assert.Equal(-1, RecordingAudioMixer.ToPcm16(-0.5f / 32768));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(6, 63)]
    [InlineData(6, 1551)]
    public void Downmix_NormalizesCorrelatedChannelsAndKeepsMonoStereoMeaning(int channels, uint mask)
    {
        float[] source = Enumerable.Repeat(0.75f, channels * 10).ToArray();
        float[] output = new float[20];
        RecordingAudioMixer.Downmix(source, channels, mask, output);
        Assert.All(output, sample => Assert.InRange(sample, 0.749999f, 0.750001f));
        if (channels == 2)
        {
            RecordingAudioMixer.Downmix([0.2f, -0.4f], 2, mask, output.AsSpan(0, 2));
            Assert.Equal(0.2f, output[0]); Assert.Equal(-0.4f, output[1]);
        }
    }

    [Fact]
    public void Downmix_ExcludesLfe_AndRejectsUnknownSpeakerLayouts()
    {
        float[] output = new float[2];
        RecordingAudioMixer.Downmix([0, 0, 0, 1, 0, 0], 6, 0x3f, output);
        Assert.Equal(new float[] { 0, 0 }, output);
        RecordingAudioMixer.Downmix([0, 0, 1, 0, 0, 0], 6, 0x3f, output);
        Assert.InRange(output[0], 0.29289f, 0.29290f);
        Assert.Equal(output[0], output[1]);
        Assert.Throws<ArgumentException>(() => RecordingAudioMixer.Downmix(new float[6], 6, 0, output));
        Assert.Throws<ArgumentException>(() => RecordingAudioMixer.Downmix(new float[6], 6, 0x8000001f, output));
    }
}
