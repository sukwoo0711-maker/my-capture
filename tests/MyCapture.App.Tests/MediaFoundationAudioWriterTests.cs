using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MediaFoundationAudioWriterTests
{
    // The software H.264 transform rejects the earlier 32x32 fixture before audio setup.
    private const int FrameSide = 64;
    private const int FrameStride = FrameSide * 4;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioSetupFailureRebuildsVideoOnlyBeforeAnySample(bool failBeginWriting)
    {
        AudioEncoderSetupStage failedStage = failBeginWriting ? AudioEncoderSetupStage.BeginWriting : AudioEncoderSetupStage.AudioStream;
        string path = FixturePath();
        int calls = 0;
        using (var encoder = new MediaFoundationVideoEncoder(Options(path, audio: true), NullLogger.Instance, null,
            stage => { calls++; if (stage == failedStage) throw new InvalidOperationException("Injected audio setup failure."); }))
        {
            Assert.Equal(AudioEncoderState.Unavailable, encoder.AudioState);
            Assert.Equal("Injected audio setup failure.", encoder.AudioUnavailableReason);
            WriteVideo(encoder);
            encoder.Complete();
        }
        Assert.Equal(failedStage == AudioEncoderSetupStage.AudioStream ? 1 : 2, calls);
        Assert.Null(MediaFoundationAudioReader.ReadSourceInfo(path));
        using var reader = new MediaFoundationVideoFrameReader(path);
        Assert.Equal(FrameSide, reader.ReadFrameAt(0).Width);
    }

    [Fact]
    public void DefaultOptionsNeverSetUpAudioAndProduceNoAudioStream()
    {
        string path = FixturePath();
        using (var encoder = new MediaFoundationVideoEncoder(Options(path, audio: false), NullLogger.Instance, null,
            _ => throw new InvalidOperationException("The video-only path must not configure audio.")))
        {
            Assert.Equal(AudioEncoderState.Disabled, encoder.AudioState);
            Assert.Null(encoder.AudioUnavailableReason);
            WriteVideo(encoder);
            encoder.Complete();
        }
        Assert.Null(MediaFoundationAudioReader.ReadSourceInfo(path));
        using var reader = new MediaFoundationVideoFrameReader(path);
        Assert.Equal(FrameSide, reader.ReadFrameAt(0).Width);
    }

    [Fact]
    public void RequestedAudioWritesAacAndRejectsOverlappingPcm()
    {
        string path = FixturePath();
        using (var encoder = new MediaFoundationVideoEncoder(Options(path, audio: true), NullLogger.Instance))
        {
            Assert.Equal(AudioEncoderState.Available, encoder.AudioState);
            Assert.Null(encoder.AudioUnavailableReason);
            byte[] pixels = new byte[FrameSide * FrameStride];
            for (int index = 0; index < 10; index++)
            {
                encoder.WriteFrame(new(pixels, FrameSide, FrameSide, FrameStride, index * 100));
                encoder.WriteAudio(new RecordingAudioBlock(index * 4800, new short[9600]));
            }
            Assert.Throws<ArgumentException>(() => encoder.WriteAudio(new RecordingAudioBlock(47_999, new short[2])));
            encoder.Complete();
        }
        AudioSourceInfo info = Assert.IsType<AudioSourceInfo>(MediaFoundationAudioReader.ReadSourceInfo(path));
        Assert.Equal(new Guid("00001610-0000-0010-8000-00aa00389b71"), info.Subtype);
        Assert.Equal(RecordingAudioFormat.SampleRate, info.SampleRate);
        Assert.Equal(RecordingAudioFormat.Channels, info.Channels);
    }

    [Fact]
    public void AudioWriterRequiresItsCreatingThread()
    {
        string path = FixturePath();
        using var encoder = new MediaFoundationVideoEncoder(Options(path, audio: true), NullLogger.Instance);
        Exception? audio = null, video = null, complete = null, dispose = null;
        var thread = new Thread(() =>
        {
            audio = Record.Exception(() => encoder.WriteAudio(new RecordingAudioBlock(0, new short[2])));
            video = Record.Exception(() => WriteVideo(encoder));
            complete = Record.Exception(encoder.Complete);
            dispose = Record.Exception(encoder.Dispose);
        });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(audio);
        Assert.IsType<InvalidOperationException>(video);
        Assert.IsType<InvalidOperationException>(complete);
        Assert.IsType<InvalidOperationException>(dispose);
        WriteVideo(encoder);
        encoder.WriteAudio(new RecordingAudioBlock(0, new short[9600]));
        encoder.Complete();
    }

    private static string FixturePath() => Path.Combine(OwnedTestDirectory.Create("mc-audio-writer-"), "fixture.mp4");
    private static VideoEncoderOptions Options(string path, bool audio) => new(path, FrameSide, FrameSide, 10, 1_000_000)
        { AllowHardwareEncoder = false, Audio = audio ? new AudioEncoderOptions() : null };
    private static void WriteVideo(IVideoEncoder encoder) => encoder.WriteFrame(
        new(new byte[FrameSide * FrameStride], FrameSide, FrameSide, FrameStride, 0));
}
