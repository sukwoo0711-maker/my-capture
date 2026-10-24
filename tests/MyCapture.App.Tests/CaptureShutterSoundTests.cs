using System.Buffers.Binary;
using System.Text;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The optional capture sound is a short, quiet two-click shutter compiled into the app, ready to
/// play without touching the disk. Nothing here plays it.
/// </summary>
public sealed class CaptureShutterSoundTests
{
    [Fact]
    public void ResourceIsAShortQuietMonoShutterWithTwoClicks()
    {
        byte[] wave = Assert.IsType<byte[]>(CaptureShutterSound.ReadResource());
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wave, 8, 4));

        (int format, int channels, int rate, int bits) = (0, 0, 0, 0);
        short[] samples = [];
        for (int position = 12; position + 8 <= wave.Length;)
        {
            string id = Encoding.ASCII.GetString(wave, position, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(position + 4));
            ReadOnlySpan<byte> body = wave.AsSpan(position + 8, size);
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
            }
            else if (id == "data")
            {
                samples = new short[size / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
            }
            position += 8 + size + (size & 1);
        }

        // 16-bit PCM, mono, 48 kHz, about 110 ms.
        Assert.Equal((1, 1, 48_000, 16), (format, channels, rate, bits));
        Assert.InRange(samples.Length * 1000.0 / rate, 100, 120);
        // Peak around -6 dBFS and never clipped; it starts and ends silent.
        int peak = samples.Max(sample => Math.Abs((int)sample));
        Assert.InRange(20 * Math.Log10(peak / 32768.0), -7, -5);
        Assert.DoesNotContain(samples, sample => sample is short.MaxValue or short.MinValue);
        Assert.InRange(Math.Abs((int)samples[0]), 0, 64);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, 64);

        // Two clicks: one at the start and one 55 ms later, quiet between them.
        int Loudest(double fromMs, double toMs) => samples[(int)(fromMs * 48)..(int)(toMs * 48)].Max(sample => Math.Abs((int)sample));
        Assert.True(Loudest(0, 10) > peak / 2);
        Assert.True(Loudest(55, 70) > peak / 2);
        Assert.True(Loudest(20, 54) < peak / 8);
    }

    [Fact]
    public void PlayerLoadsTheSoundOnce() => Assert.True(CaptureShutterSound.IsAvailable);
}
