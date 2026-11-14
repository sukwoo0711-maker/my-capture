using System.Runtime.InteropServices;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Synthetic PCM and MF DSP only; no WASAPI objects, files or playback devices.</summary>
public sealed class RecordingAudioConversionTests
{
    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void NominalConverterKeepsPacketPhaseLengthAndSignalAlignment(int rate)
    {
        float[] input = Tone(rate, 1_000, rate / 5);
        float[] whole = Convert(input, rate, input.Length / 2);
        float[] packets = Convert(input, rate, rate / 100);
        Assert.InRange(Math.Abs(whole.Length / 2 - 9_600), 0, 1);
        Assert.Equal(whole.Length, packets.Length);
        double squaredError = 0;
        for (int i = 0; i < whole.Length; i++) squaredError += Math.Pow(whole[i] - packets[i], 2);
        Assert.InRange(Math.Sqrt(squaredError / whole.Length), 0, 0.0001);
        // Ignore only the finite FIR edge transients; a delayed output waveform fails this
        // comparison even when its duration and container timestamp happen to match.
        double error = 0;
        for (int frame = 480; frame < 9_120; frame++)
        {
            double expected = .5 * Math.Sin(2 * Math.PI * 1_000 * frame / 48_000);
            error += Math.Pow(packets[frame * 2] - expected, 2);
        }
        Assert.InRange(Math.Sqrt(error / 8_640), 0, 0.03);
    }

    [Fact]
    public void Nominal96kConversionRejectsAboveNyquistEnergy()
    {
        float[] pass = Convert(Tone(96_000, 1_000, 19_200), 96_000, 960);
        float[] rejected = Convert(Tone(96_000, 32_000, 19_200), 96_000, 960);
        double passRms = Rms(pass.AsSpan(960, pass.Length - 1_920));
        double stopRms = Rms(rejected.AsSpan(960, rejected.Length - 1_920));
        Assert.InRange(passRms, .32, .38);
        Assert.True(stopRms < passRms / 20);
    }

    [Theory]
    [InlineData(1, 16, 16, false, 4)]
    [InlineData(2, 24, 20, false, 3)]
    [InlineData(2, 32, 24, false, 3)]
    [InlineData(2, 32, 32, true, 3)]
    [InlineData(6, 32, 32, true, 63)]
    public void NativeFormatDecodesMonoStereoAnd51WithValidBits(int channels, int bits, int validBits, bool floating, uint mask)
    {
        IntPtr format = Marshal.AllocCoTaskMem(40);
        IntPtr pcm = Marshal.AllocCoTaskMem(channels * bits / 8);
        try
        {
            WriteFormat(format, 44_100, channels, bits, validBits, floating, mask);
            var native = RecordingNativeAudioFormat.Read(format);
            for (int channel = 0; channel < channels; channel++)
            {
                int at = channel * bits / 8;
                if (floating) Marshal.WriteInt32(pcm, at, BitConverter.SingleToInt32Bits(.5f));
                else if (bits == 16) Marshal.WriteInt16(pcm, at, 16_384);
                else if (bits == 24)
                { Marshal.WriteByte(pcm, at, 0); Marshal.WriteByte(pcm, at + 1, 0); Marshal.WriteByte(pcm, at + 2, 64); }
                else Marshal.WriteInt32(pcm, at, 1 << 30);
            }
            float[] result = native.Decode(pcm, 1);
            Assert.Equal(44_100, native.SampleRate);
            Assert.Equal(2, result.Length);
            Assert.All(result, sample => Assert.InRange(sample, .4999f, .5001f));
        }
        finally { Marshal.FreeCoTaskMem(pcm); Marshal.FreeCoTaskMem(format); }
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(6, 0x8000003f)]
    public void UnknownMultichannelMaskFailsInsteadOfReinterpreting(int channels, uint mask)
    {
        IntPtr format = Marshal.AllocCoTaskMem(40);
        try
        {
            WriteFormat(format, 48_000, channels, 32, 32, true, mask);
            Assert.Throws<NotSupportedException>(() => RecordingNativeAudioFormat.Read(format));
        }
        finally { Marshal.FreeCoTaskMem(format); }
    }

    private static float[] Convert(float[] input, int rate, int packetFrames)
    {
        using var converter = new MediaFoundationRecordingResampler(rate);
        List<float> output = [];
        for (int offset = 0; offset < input.Length; offset += packetFrames * 2)
            output.AddRange(converter.Process(input.AsSpan(offset, Math.Min(packetFrames * 2, input.Length - offset))));
        output.AddRange(converter.Process([], drain: true));
        return output.ToArray();
    }

    private static float[] Tone(int rate, int frequency, int frames)
    {
        float[] result = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
            result[frame * 2] = result[frame * 2 + 1] = (float)(.5 * Math.Sin(2 * Math.PI * frequency * frame / rate));
        return result;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    { double sum = 0; foreach (float sample in samples) sum += sample * sample; return Math.Sqrt(sum / samples.Length); }

    private static void WriteFormat(IntPtr format, int rate, int channels, int bits, int validBits, bool floating, uint mask)
    {
        Marshal.Copy(new byte[40], 0, format, 40);
        Marshal.WriteInt16(format, unchecked((short)0xfffe));
        Marshal.WriteInt16(format, 2, (short)channels);
        Marshal.WriteInt32(format, 4, rate);
        Marshal.WriteInt32(format, 8, rate * channels * bits / 8);
        Marshal.WriteInt16(format, 12, (short)(channels * bits / 8));
        Marshal.WriteInt16(format, 14, (short)bits);
        Marshal.WriteInt16(format, 16, 22);
        Marshal.WriteInt16(format, 18, (short)validBits);
        Marshal.WriteInt32(format, 20, unchecked((int)mask));
        Marshal.StructureToPtr(new Guid(floating ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71"), format + 24, false);
    }
}
