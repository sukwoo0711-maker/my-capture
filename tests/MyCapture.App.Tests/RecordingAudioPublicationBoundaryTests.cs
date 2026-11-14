using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioPublicationBoundaryTests
{
    [Fact]
    public void FutureAacBlockCannotHideAnAudioGapAtTheVideoEnd()
    {
        string root = OwnedTestDirectory.Create("mc-audio-tail-gap-");
        string source = Path.Combine(root, "tail-gap.mp4");
        using (var encoder = new MediaFoundationVideoEncoder(new(source, 64, 64, 10, 1_000_000)
        { Audio = new(), AllowHardwareEncoder = false }, NullLogger.Instance))
        {
            Assert.Equal(AudioEncoderState.Available, encoder.AudioState);
            byte[] pixels = new byte[64 * 64 * 4];
            for (int frame = 0; frame < 10; frame++)
                encoder.WriteFrame(new(pixels, 64, 64, 256, frame * 100d));
            short[] pcm = Enumerable.Repeat((short)2000, 9600 * 2).ToArray();
            encoder.WriteAudio(new(0, pcm));
            encoder.WriteAudio(new(96_000, pcm));
            encoder.Complete();
        }
        List<DecodedAudioBlock> decoded = [];
        using (var reader = new MediaFoundationAudioReader(source))
            while (reader.ReadNext() is { } block) decoded.Add(block);
        const long end = 10_000_000;
        const long codecFrameTicks = 1_024L * 10_000_000 / 48_000;
        TestDiagnosticArtifacts.WriteJson(root, "decoded-block-times.json", decoded.Select(block => new
        {
            block.Timestamp100ns, block.EndTimestamp100ns, block.FrameCount,
        }).ToArray());
        Assert.NotEmpty(decoded);
        Assert.InRange(Math.Abs(decoded[0].Timestamp100ns), 0, codecFrameTicks);
        Assert.Contains(decoded, block => block.Timestamp100ns >= 19_000_000);
        Assert.DoesNotContain(decoded, block => block.Timestamp100ns <= end
            && block.EndTimestamp100ns + codecFrameTicks >= end);
        Assert.Throws<RecordingAudioPreservationException>(() =>
            new RecordingAudioExpectation(true, 48_000, 2).Validate(source, 1000));
    }

    [Theory]
    [InlineData("mf")]
    [InlineData("com")]
    [InlineData("service")]
    public void NativeProbeFailuresKeepTheLocalizedPreservationBoundary(string kind)
    {
        string root = OwnedTestDirectory.Create("mc-audio-probe-failure-");
        string source = Path.Combine(root, "source.mp4");
        Exception cause = kind switch
        {
            "mf" => new InvalidOperationException("Injected Media Foundation probe failure"),
            "com" => new COMException("Injected native source failure", unchecked((int)0x80004005)),
            _ => new DllNotFoundException("Injected Media Foundation service unavailable"),
        };
        int probes = 0;
        var failure = Assert.Throws<RecordingAudioPreservationException>(() =>
            RecordingAudioExpectation.FromSource(source, path =>
            {
                Assert.Equal(source, path);
                probes++;
                throw cause;
            }));
        Assert.Equal(1, probes);
        Assert.Same(cause, failure.InnerException);
        Assert.Equal(UiText.Get("RecordingAudio_SaveFailed"), failure.Message);
        Assert.IsAssignableFrom<IOException>(failure);
    }

    [Fact]
    public void DamagedSourceProbeFailsClosedAndPreservesItsBytes()
    {
        string root = OwnedTestDirectory.Create("mc-audio-damaged-source-");
        string source = Path.Combine(root, "damaged.mp4");
        byte[] damaged = [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0];
        File.WriteAllBytes(source, damaged);
        var failure = Assert.Throws<RecordingAudioPreservationException>(() => RecordingAudioExpectation.FromSource(source));
        Assert.NotNull(failure.InnerException);
        Assert.Equal(damaged, File.ReadAllBytes(source));
        Assert.Equal(UiText.Get("RecordingAudio_SaveFailed"), failure.Message);
    }

    [Fact]
    public void SaveCopyRejectsSilentStagingWithTheDefaultAudioBudget()
    {
        string root = OwnedTestDirectory.Create("mc-audio-copy-refusal-");
        string source = Path.Combine(root, "source.mp4");
        RecordingAudioEditingTests.Encode(source);
        string original = EditDraftStore.HashFile(source);
        string destination = Path.Combine(root, "export.mp4");
        byte[] previous = [1, 2, 3, 4];
        File.WriteAllBytes(destination, previous);
        VideoExportCalculation result = VideoExportCalculation.Calculate(0, 3000, 1_000_000,
            (path, _, _, _) => RecordingAudioEditingTests.Encode(path, audioSeconds: 0), CancellationToken.None);
        Assert.Null(MediaFoundationAudioReader.ReadSourceInfo(result.ResultPath));
        Assert.Throws<RecordingAudioPreservationException>(() =>
            result.SaveCopy(destination, source, CancellationToken.None));
        Assert.Equal(previous, File.ReadAllBytes(destination));
        Assert.Equal(original, EditDraftStore.HashFile(source));
        Assert.Equal(2, Directory.GetFiles(root).Length);
        Assert.True(File.Exists(result.ResultPath));
    }

    [Fact]
    public void GifCopyDoesNotProbeOrOpenAudio()
    {
        string root = OwnedTestDirectory.Create("mc-audio-gif-copy-");
        string source = Path.Combine(root, "source.mp4");
        RecordingAudioEditingTests.Encode(source);
        long opened = MediaFoundationAudioReader.OpenedReaders;
        int active = MediaFoundationAudioReader.ActiveReaders;
        VideoExportCalculation result = VideoExportCalculation.CalculateGif(
            path => File.WriteAllBytes(path, "GIF89a"u8.ToArray()), CancellationToken.None);
        string target = Path.Combine(root, "copy.gif");
        result.SaveCopy(target, source, CancellationToken.None);
        Assert.Equal("GIF89a"u8.ToArray(), File.ReadAllBytes(target));
        Assert.Equal(opened, MediaFoundationAudioReader.OpenedReaders);
        Assert.Equal(active, MediaFoundationAudioReader.ActiveReaders);
    }
}
