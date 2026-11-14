namespace MyCapture.Core.Recording;

/// <summary>Pure downmix and fixed-gain arithmetic; no capture, playback or clock objects.</summary>
public static class RecordingAudioMixer
{
    public const float SourceGain = 0.5f;
    private const float SurroundGain = 0.70710678f;

    public static short ToPcm16(float value)
    {
        if (!float.IsFinite(value)) return 0;
        value = Math.Clamp(value, -1, 1);
        return (short)Math.Clamp((int)MathF.Round(value * 32768, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
    }

    /// <summary>Empty input means silence; the other source keeps the same fixed gain.</summary>
    public static void Mix(ReadOnlySpan<float> systemStereo, ReadOnlySpan<float> microphoneStereo, Span<short> output)
    {
        if (output.Length % 2 != 0 || (!systemStereo.IsEmpty && systemStereo.Length != output.Length)
            || (!microphoneStereo.IsEmpty && microphoneStereo.Length != output.Length))
            throw new ArgumentException("Mixer buffers must contain matching stereo frames.");
        for (int i = 0; i < output.Length; i++)
        {
            float system = systemStereo.IsEmpty || !float.IsFinite(systemStereo[i]) ? 0 : systemStereo[i];
            float microphone = microphoneStereo.IsEmpty || !float.IsFinite(microphoneStereo[i]) ? 0 : microphoneStereo[i];
            output[i] = ToPcm16((system * SourceGain) + (microphone * SourceGain));
        }
    }

    /// <summary>
    /// WAVEFORMATEXTENSIBLE speaker order follows ascending mask bits. Mono is duplicated;
    /// stereo passes through. Multichannel center and surrounds use -3 dB; LFE is excluded.
    /// Each side's weights are normalized so a correlated full-scale signal cannot overflow.
    /// </summary>
    public static void Downmix(ReadOnlySpan<float> source, int channels, uint speakerMask, Span<float> stereo)
    {
        if (channels is < 1 or > 8 || source.Length % channels != 0 || stereo.Length != source.Length / channels * 2)
            throw new ArgumentException("Invalid audio channel or buffer dimensions.");
        if (channels == 1)
        {
            for (int frame = 0; frame < source.Length; frame++) stereo[frame * 2] = stereo[frame * 2 + 1] = Finite(source[frame]);
            return;
        }
        if (channels == 2)
        {
            for (int i = 0; i < source.Length; i++) stereo[i] = Finite(source[i]);
            return;
        }
        const uint supported = 0x0000063F; // FL, FR, FC, LFE, BL, BR, SL, SR.
        if ((speakerMask & ~supported) != 0 || System.Numerics.BitOperations.PopCount(speakerMask) != channels)
            throw new ArgumentException("A supported speaker mask is required for multichannel audio.", nameof(speakerMask));
        Span<float> left = stackalloc float[8];
        Span<float> right = stackalloc float[8];
        float leftTotal = 0, rightTotal = 0;
        int channel = 0;
        for (uint bit = 1; bit <= 0x400; bit <<= 1)
        {
            if ((speakerMask & bit) == 0) continue;
            left[channel] = bit switch { 1 => 1, 4 or 0x10 or 0x200 => SurroundGain, _ => 0 };
            right[channel] = bit switch { 2 => 1, 4 or 0x20 or 0x400 => SurroundGain, _ => 0 };
            leftTotal += left[channel]; rightTotal += right[channel]; channel++;
        }
        for (int frame = 0; frame < source.Length / channels; frame++)
        {
            float l = 0, r = 0;
            for (int c = 0; c < channels; c++)
            {
                float value = Finite(source[frame * channels + c]);
                l += value * left[c]; r += value * right[c];
            }
            stereo[frame * 2] = leftTotal == 0 ? 0 : l / leftTotal;
            stereo[frame * 2 + 1] = rightTotal == 0 ? 0 : r / rightTotal;
        }
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
}
