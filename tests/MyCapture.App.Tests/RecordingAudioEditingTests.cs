using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioEditingTests
{
    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(40, true, true, false)]
    [InlineData(70, false, true, false)]
    [InlineData(90, false, false, false)]
    [InlineData(0, false, false, true)]
    public async Task EveryPresetAndPixelPathKeepsOnlyTheFirstAndThirdTones(
        int reduction, bool nv12, bool layers, bool rejectNv12)
    {
        string root = OwnedTestDirectory.Create("mc-audio-edit-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"));
        byte[] original = SHA256.HashData(File.ReadAllBytes(source.OutputPath));
        string target = Path.Combine(root, "edited.mp4");
        int bitrate = VideoExportCalculation.ReducedBitrate(282_000, 2000, reduction, 1_000_000, 128_000);
        Mp4RenderStatistics result = await StaThreadTask.RunAsync(() => TrimReencoder.ReencodeWithStatistics(
            source.OutputPath, target, 0, 3000, source,
            options => rejectNv12 && options.InputFormat == VideoEncoderInputFormat.Nv12
                ? throw new InvalidOperationException("Injected NV12 rejection")
                : new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = false }, NullLogger.Instance),
            NullLogger.Instance,
            layers ? [new TimedTextOverlay { Text = "sound", StartMs = 100, EndMs = 2900 }] : null,
            layers ? [new FrameEditLayer { StartMs = 100, EndMs = 2900, OverlayPngBase64 = Png() }] : null,
            bitrateBitsPerSecond: bitrate,
            renderOptions: new() { PassThroughNv12 = nv12, PreferHardwareDecode = false },
            removedRanges: [new(1000, 2000)]));
        Assert.Equal(20, result.Frames);
        if (layers) Assert.True(result.ComposedFrames > 0);
        if (!nv12) Assert.Equal("rgb-software", result.FrameSource);
        Assert.Equal((2, 1), MediaFoundationAudioReader.GetStreamCounts(target));
        short[] pcm = Decode(target);
        AssertTone(pcm, 0.2, 300, 900, 1500);
        AssertTone(pcm, 1.2, 1500, 300, 900);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source.OutputPath)));
        Assert.Equal(0, MediaFoundationAudioReader.ActiveReaders);
    }

    [Fact]
    public async Task HardwareFailureRestartsAudioAtZeroAndDisposesBothAttempts()
    {
        string root = OwnedTestDirectory.Create("mc-audio-retry-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"));
        string target = Path.Combine(root, "retry.mp4");
        List<TapEncoder> attempts = [];
        Mp4RenderStatistics result = await StaThreadTask.RunAsync(() => TrimReencoder.ReencodeWithStatistics(
            source.OutputPath, target, 0, 3000, source, options =>
            {
                var tap = new TapEncoder(new MediaFoundationVideoEncoder(
                    options with { AllowHardwareEncoder = false }, NullLogger.Instance), attempts.Count == 0 ? 4 : -1);
                attempts.Add(tap);
                return tap;
            }, NullLogger.Instance, removedRanges: [new(1000, 2000)]));
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt => Assert.True(attempt.Disposed));
        Assert.All(attempts, attempt => Assert.Equal(0, attempt.Starts[0]));
        Assert.Equal("nv12-software", result.FrameSource);
        Assert.Equal(96_000, attempts[1].Pcm.Count / 2);
        AssertTone(Decode(target), 1.2, 1500, 900, 300);
        Assert.Equal(0, MediaFoundationAudioReader.ActiveReaders);
    }

    [Fact]
    public async Task SubFrameMultiCutsUseOneSampleBudgetAndLastFramePaddingIsSilent()
    {
        string root = OwnedTestDirectory.Create("mc-audio-multicut-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"));
        string target = Path.Combine(root, "cut.mp4");
        VideoTimeRange[] cuts = Enumerable.Range(0, 40)
            .Select(index => new VideoTimeRange(100 + index * 50.137, 113.733 + index * 50.137)).ToArray();
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 2833.117, cuts);
        long kept = RecordingAudioCutMap.Create(timeline.KeptRanges)[^1].OutputEndSample;
        TapEncoder? tap = null;
        int frames = await StaThreadTask.RunAsync(() => TrimReencoder.Reencode(
            source.OutputPath, target, 0, 2833.117, source,
            options => tap = new TapEncoder(new MediaFoundationVideoEncoder(
                options with { AllowHardwareEncoder = false }, NullLogger.Instance)),
            NullLogger.Instance, removedRanges: cuts, renderOptions: new() { PreferHardwareDecode = false }));
        Assert.NotNull(tap);
        Assert.Equal(frames * 4800, tap.Pcm.Count / 2);
        Assert.InRange(Math.Abs(kept - timeline.OutputDurationMs * 48), 0, 0.5);
        Assert.True(tap.Pcm.Count / 2 > kept);
        Assert.All(tap.Pcm.Skip(checked((int)kept * 2)), value => Assert.Equal(0, value));
        long next = 0;
        for (int index = 0; index < tap.Starts.Count; index++)
        {
            Assert.Equal(next, tap.Starts[index]);
            next += tap.Counts[index];
        }
        Assert.Equal(frames * 4800, next);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("write")]
    [InlineData("validation")]
    public async Task AudioFailuresNeverReturnSuccessfulRender(string failure)
    {
        string root = OwnedTestDirectory.Create("mc-audio-fail-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"));
        byte[] original = SHA256.HashData(File.ReadAllBytes(source.OutputPath));
        string target = Path.Combine(root, "rejected.mp4");
        await Assert.ThrowsAsync<RecordingAudioPreservationException>(() => StaThreadTask.RunAsync(() =>
            TrimReencoder.Reencode(source.OutputPath, target, 0, 3000, source,
                options => new FaultEncoder(new MediaFoundationVideoEncoder(options with
                {
                    AllowHardwareEncoder = false,
                    Audio = failure == "validation" ? null : options.Audio,
                }, NullLogger.Instance), failure), NullLogger.Instance,
                renderOptions: new() { PreferHardwareDecode = false })));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(source.OutputPath)));
        Assert.Equal(0, MediaFoundationAudioReader.ActiveReaders);
    }

    [Fact]
    public async Task PersistedAudioExpectationRejectsASourceThatLostItsTrack()
    {
        string root = OwnedTestDirectory.Create("mc-audio-missing-source-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"), audioSeconds: 0) with { ExpectedAudio = true };
        await Assert.ThrowsAsync<RecordingAudioPreservationException>(() => StaThreadTask.RunAsync(() =>
            TrimReencoder.Reencode(source.OutputPath, Path.Combine(root, "rejected.mp4"), 0, 3000, source,
                options => new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = false }, NullLogger.Instance),
                NullLogger.Instance, renderOptions: new() { PreferHardwareDecode = false })));
        Assert.Equal(0, MediaFoundationAudioReader.ActiveReaders);
    }

    [Fact]
    public async Task GifAndFrameExtractionOpenNoAudioReaderAndVideoDurationIgnoresLongAudio()
    {
        string root = OwnedTestDirectory.Create("mc-audio-silent-paths-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"), audioSeconds: 4);
        Assert.InRange(MediaFoundationVideoFrameReader.ReadSourceInfo(source.OutputPath).DurationMs, 2999.9, 3000.1);
        using (var reader = new MediaFoundationVideoFrameReader(source.OutputPath))
            Assert.InRange(reader.DurationMs, 2999.9, 3000.1);
        long before = MediaFoundationAudioReader.OpenedReaders;
        await StaThreadTask.RunAsync(() =>
        {
            VideoMediaInfo probe = VideoFrameRenderPipeline.Probe(source.OutputPath);
            Assert.InRange(probe.DurationMs, 2999.9, 3000.1);
            BitmapSource frame = VideoFrameRenderPipeline.CaptureSingleFrame(source.OutputPath, 2400).Bitmap;
            Assert.Equal(64, frame.PixelWidth);
            using var gif = new GifExportSession(source, VideoEditDocument.CreateFor(64, 64, 3000),
                options: new() { PreferHardwareDecode = false });
            Assert.True(gif.Export(Path.Combine(root, "silent.gif")) > 0);
            return true;
        });
        Assert.Equal(before, MediaFoundationAudioReader.OpenedReaders);
        Assert.Equal(0, MediaFoundationAudioReader.ActiveReaders);
    }

    [Fact]
    public async Task SilentSourceUsesTheOriginalEncoderContractAndIdenticalBytes()
    {
        string root = OwnedTestDirectory.Create("mc-audio-video-only-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"), audioSeconds: 0);
        long before = MediaFoundationAudioReader.OpenedReaders;
        string actual = Path.Combine(root, "actual.mp4"), reference = Path.Combine(root, "reference.mp4");
        await StaThreadTask.RunAsync(() =>
        {
            TrimReencoder.Reencode(source.OutputPath, actual, 0, 3000, source, options =>
            {
                Assert.Null(options.Audio);
                return new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = false }, NullLogger.Instance);
            }, NullLogger.Instance, renderOptions: new() { PreferHardwareDecode = false });
            IVideoEncoder? encoder = null;
            try
            {
                VideoFrameRenderPipeline.RenderToEncoder(new(source.OutputPath, 0, 3000, 10, 64, 64, 64, 64, [], []),
                    format => encoder = new MediaFoundationVideoEncoder(new(reference, 64, 64, 10, 1_000_000)
                    { InputFormat = format, AllowHardwareEncoder = false }, NullLogger.Instance),
                    new() { PreferHardwareDecode = false }, NullLogger.Instance);
                encoder!.Complete();
            }
            finally { encoder?.Dispose(); }
            return true;
        });
        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(actual));
        Assert.Equal(before, MediaFoundationAudioReader.OpenedReaders);
        Assert.Null(MediaFoundationAudioReader.ReadSourceInfo(actual));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(90)]
    public void AudioBudgetIsReservedAndUnreachableTargetShowsItsFloor(int reduction)
    {
        long baseline = VideoExportDialog.EstimateMp4Bytes(64, 64, 10, 30_000, 0, 128_000);
        int bitrate = VideoExportCalculation.ReducedBitrate(baseline, 30_000, reduction, 1_000_000, 128_000);
        long estimate = VideoExportDialog.EstimateMp4Bytes(64, 64, 10, 30_000, reduction, 128_000);
        Assert.True(estimate >= (64_000 + 128_000) * 30L / 8);
        if (reduction == 90)
        {
            Assert.Equal(64_000, bitrate);
            Assert.True(estimate > VideoExportCalculation.TargetBytes(baseline, reduction));
        }
        Assert.True(baseline > VideoExportDialog.EstimateMp4Bytes(64, 64, 10, 30_000, 0));
    }

    [Fact]
    public async Task StagedSizeRenderIncludesAudioInBothPassesAndCopy()
    {
        string root = OwnedTestDirectory.Create("mc-audio-staged-size-");
        RecordingResult source = Encode(Path.Combine(root, "source.mp4"));
        List<(int Bitrate, bool Baseline)> calls = [];
        VideoExportCalculation calculation = await StaThreadTask.RunAsync(() => VideoExportCalculation.Calculate(
            70, 2000, 1_000_000, (path, bitrate, baseline, token) =>
            {
                calls.Add((bitrate, baseline));
                TrimReencoder.Reencode(source.OutputPath, path, 0, 3000, source,
                    options => new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = false }, NullLogger.Instance),
                    NullLogger.Instance, cancellationToken: token, bitrateBitsPerSecond: bitrate,
                    renderOptions: new() { PreferHardwareDecode = false }, removedRanges: [new(1000, 2000)]);
            }, CancellationToken.None));
        // The production calculation owns these two renders; keep them as diagnostics in this
        // hardware-free test instead of asking the desktop Shell to recycle them.
        Assert.Equal(2, calls.Count);
        Assert.Equal(VideoExportCalculation.ReducedBitrate(calculation.BaselineBytes, 2000, 70, 1_000_000, 128_000),
            calls[1].Bitrate);
        Assert.Equal((2, 1), MediaFoundationAudioReader.GetStreamCounts(calculation.ResultPath));
        string copy = Path.Combine(root, "copy.mp4");
        calculation.SaveCopy(copy, source.OutputPath, CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(calculation.ResultPath), File.ReadAllBytes(copy));
        AssertTone(Decode(copy), 1.2, 1500, 900, 300);
    }

    internal static RecordingResult Encode(string path, int audioSeconds = 3)
    {
        using var encoder = new MediaFoundationVideoEncoder(new(path, 64, 64, 10, 1_000_000)
        { Audio = audioSeconds == 0 ? null : new(), AllowHardwareEncoder = false }, NullLogger.Instance);
        if (audioSeconds > 0) Assert.Equal(AudioEncoderState.Available, encoder.AudioState);
        byte[] pixels = new byte[64 * 64 * 4];
        for (int frame = 0; frame < Math.Max(30, audioSeconds * 10); frame++)
        {
            if (frame < 30)
            {
                Array.Fill(pixels, (byte)(40 + frame * 5));
                // A fixed top stripe stands for a watermark already burned into the source.
                Array.Fill(pixels, (byte)240, 0, 64 * 4 * 4);
                encoder.WriteFrame(new(pixels, 64, 64, 256, frame * 100d));
            }
            if (frame < audioSeconds * 10)
            {
                short[] pcm = new short[4800 * 2];
                int hz = (frame / 10) switch { 0 => 300, 1 => 900, _ => 1500 };
                for (int sample = 0; sample < 4800; sample++)
                    pcm[sample * 2] = pcm[sample * 2 + 1] = (short)(12_000 * Math.Sin(sample * Math.Tau * hz / 48_000));
                encoder.WriteAudio(new(frame * 4800L, pcm));
            }
        }
        encoder.Complete();
        return new(path, 3000, 10, 30, 64, 64);
    }

    private static short[] Decode(string path)
    {
        using var reader = new MediaFoundationAudioReader(path);
        List<short> pcm = [];
        while (reader.ReadNext() is { } block) pcm.AddRange(block.Samples);
        return pcm.ToArray();
    }

    private static void AssertTone(short[] pcm, double seconds, int expected, params int[] absent)
    {
        double Magnitude(int hz)
        {
            int start = (int)(seconds * 48_000), count = 9600;
            Assert.True((start + count) * 2 <= pcm.Length);
            double real = 0, imaginary = 0;
            for (int index = 0; index < count; index++)
            {
                double phase = Math.Tau * hz * index / 48_000;
                real += pcm[(start + index) * 2] * Math.Cos(phase);
                imaginary += pcm[(start + index) * 2] * Math.Sin(phase);
            }
            return Math.Sqrt(real * real + imaginary * imaginary) / count;
        }
        double wanted = Magnitude(expected);
        Assert.True(wanted > 3000, $"Expected {expected}Hz amplitude, got {wanted}.");
        foreach (int hz in absent) Assert.True(Magnitude(hz) < wanted / 20, $"Removed {hz}Hz leaked into output.");
    }

    private static string Png()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255 }, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private sealed class TapEncoder(IAudioVideoEncoder inner, int failAtFrame = -1) : IAudioVideoEncoder
    {
        private int _frames;
        internal List<long> Starts { get; } = [];
        internal List<int> Counts { get; } = [];
        internal List<short> Pcm { get; } = [];
        internal bool Disposed { get; private set; }
        public int Width => inner.Width;
        public int Height => inner.Height;
        public AudioEncoderState AudioState => inner.AudioState;
        public string? AudioUnavailableReason => inner.AudioUnavailableReason;
        public void WriteFrame(in EncoderFrame frame)
        {
            if (_frames++ == failAtFrame) throw new Mp4HardwareDecodeException(new InvalidOperationException("Injected decoder loss"));
            inner.WriteFrame(frame);
        }
        public void WriteAudio(in RecordingAudioBlock block)
        {
            Starts.Add(block.StartSample);
            Counts.Add(block.FrameCount);
            Pcm.AddRange(block.Samples.ToArray());
            inner.WriteAudio(block);
        }
        public void Complete() => inner.Complete();
        public void Dispose() { inner.Dispose(); Disposed = true; }
    }

    private sealed class FaultEncoder(IAudioVideoEncoder inner, string failure) : IAudioVideoEncoder
    {
        public int Width => inner.Width;
        public int Height => inner.Height;
        public AudioEncoderState AudioState => failure == "setup" ? AudioEncoderState.Unavailable : AudioEncoderState.Available;
        public string? AudioUnavailableReason => failure;
        public void WriteFrame(in EncoderFrame frame) => inner.WriteFrame(frame);
        public void WriteAudio(in RecordingAudioBlock block)
        {
            if (failure == "write") throw new IOException("Injected audio write failure");
            if (failure != "validation") inner.WriteAudio(block);
        }
        public void Complete() => inner.Complete();
        public void Dispose() => inner.Dispose();
    }
}
