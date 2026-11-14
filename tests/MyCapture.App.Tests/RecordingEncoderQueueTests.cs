using System.IO;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingEncoderQueueTests
{
    [Fact]
    public void ExclusiveWatermarkOrdersStreamsAndWaitsWithoutWritingAhead()
    {
        var sink = new Sink();
        using var queue = new RecordingEncoderQueue(sink);
        Assert.True(queue.TryEnqueueVideo(Video(20)));
        Assert.True(queue.TryEnqueueAudio(Audio(480, 480)));
        Assert.True(queue.TryEnqueueAudio(Audio(960, 480)));
        queue.DrainThrough(100_000);
        Assert.Empty(sink.Writes);
        queue.DrainThrough(200_000);
        Assert.Equal([("audio", 100_000L)], sink.Writes);
        queue.Complete();
        Assert.Equal([("audio", 100_000L), ("video", 200_000L), ("audio", 200_000L)], sink.Writes);
        Assert.Equal(1, sink.Completions);
        queue.Complete();
        Assert.Equal(1, sink.Completions);
        Assert.Equal(0, queue.BufferedBytes);
    }

    [Fact]
    public void ProducersTransferCopiesAndOnlyOwnerCallsSink()
    {
        var sink = new Sink();
        using var queue = new RecordingEncoderQueue(sink);
        byte[] pixels = [7, 0, 0, 255];
        short[] pcm = [123, -123];
        Exception? failure = null;
        var producer = new Thread(() =>
        {
            failure = Record.Exception(() =>
            {
                Assert.True(queue.TryEnqueueVideo(new(pixels, 1, 1, 4, 0)));
                Assert.True(queue.TryEnqueueAudio(new(0, pcm)));
            });
        });
        producer.Start(); producer.Join();
        Assert.Null(failure);
        pixels[0] = 99; pcm[0] = 999;
        Assert.Empty(sink.Writes);
        queue.Complete();
        Assert.Equal((byte)7, sink.Pixel);
        Assert.Equal((short)123, sink.Sample);
        Assert.All(sink.Threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
    }

    [Fact]
    public void CapacityLimitsPacketsFramesAndBytesWithoutConsumingRejectedInput()
    {
        using var queue = new RecordingEncoderQueue(new Sink(), videoCapacity: 1, audioCapacity: 1,
            maximumAudioFrames: 2, maximumBufferedBytes: 12);
        Assert.True(queue.TryEnqueueVideo(Video(0)));
        Assert.False(queue.TryEnqueueVideo(Video(10)));
        Assert.False(queue.TryEnqueueAudio(Audio(0, 3)));
        Assert.True(queue.TryEnqueueAudio(Audio(0, 2)));
        Assert.False(queue.TryEnqueueAudio(Audio(2, 1)));
        Assert.Equal(12, queue.BufferedBytes);
        Assert.Equal(12, queue.PeakBufferedBytes);
        Assert.Equal(2, queue.PeakBufferedAudioFrames);
        Assert.True(queue.MaximumQueuedLead100ns > 0);
        queue.DrainThrough(1);
        Assert.True(queue.TryEnqueueVideo(Video(10)));
        Assert.True(queue.TryEnqueueAudio(Audio(2, 2)));
        Assert.Equal(1, queue.PendingVideoCount);
        Assert.Equal(1, queue.PendingAudioCount);
    }

    [Fact]
    public void ByteCapacityIncludesVideoAndAudio()
    {
        using var queue = new RecordingEncoderQueue(new Sink(), maximumBufferedBytes: 7);
        Assert.True(queue.TryEnqueueVideo(Video(0)));
        Assert.False(queue.TryEnqueueAudio(Audio(0, 1)));
        Assert.Equal(4, queue.BufferedBytes);
        queue.DrainThrough(1);
        Assert.True(queue.TryEnqueueAudio(Audio(1, 1)));
    }

    [Fact]
    public void StaleBackwardsAndOverlappingInputNeverReachesSink()
    {
        var sink = new Sink();
        using var queue = new RecordingEncoderQueue(sink);
        Assert.True(queue.TryEnqueueAudio(Audio(480, 480)));
        Assert.False(queue.TryEnqueueAudio(Audio(479, 480)));
        Assert.False(queue.TryEnqueueAudio(Audio(959, 480)));
        Assert.True(queue.TryEnqueueVideo(Video(10)));
        Assert.False(queue.TryEnqueueVideo(Video(10)));
        queue.DrainThrough(300_000);
        Assert.False(queue.TryEnqueueAudio(Audio(960, 480)));
        Assert.False(queue.TryEnqueueVideo(Video(29)));
        Assert.True(queue.TryEnqueueAudio(Audio(1440, 480)));
        Assert.True(queue.TryEnqueueVideo(Video(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.DrainThrough(299_999));
        queue.Complete();
        Assert.Equal(sink.Writes.Select(item => item.Time).Order(), sink.Writes.Select(item => item.Time));
        Assert.Equal(4, sink.Writes.Count);
    }

    [Fact]
    public void WrongThreadCannotDrainCompleteOrDispose()
    {
        var sink = new Sink();
        using var queue = new RecordingEncoderQueue(sink);
        Exception? drain = null, complete = null, dispose = null;
        var thread = new Thread(() =>
        {
            drain = Record.Exception(() => queue.DrainThrough(1));
            complete = Record.Exception(queue.Complete);
            dispose = Record.Exception(queue.Dispose);
        });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(drain);
        Assert.IsType<InvalidOperationException>(complete);
        Assert.IsType<InvalidOperationException>(dispose);
        Assert.Empty(sink.Threads);
        Assert.True(queue.TryEnqueueVideo(Video(0)));
        queue.Complete();
    }

    [Fact]
    public void PostStartWriteFailurePropagatesAndPreventsFurtherSubmission()
    {
        var sink = new Sink { FailWrite = true };
        var queue = new RecordingEncoderQueue(sink);
        Assert.True(queue.TryEnqueueAudio(Audio(0, 480)));
        Assert.Throws<IOException>(() => queue.DrainThrough(1));
        Assert.Throws<InvalidOperationException>(() => queue.TryEnqueueVideo(Video(20)));
        Assert.Throws<InvalidOperationException>(queue.Complete);
        queue.Dispose(); queue.Dispose();
        Assert.Equal(1, sink.Disposals);
    }

    [Fact]
    public void DefaultsBoundPcmToOneHundredMilliseconds()
    {
        using var queue = new RecordingEncoderQueue(new Sink());
        Assert.False(queue.TryEnqueueAudio(Audio(0, 4801)));
        Assert.True(queue.TryEnqueueAudio(Audio(0, 4800)));
        Assert.Equal(100 * 10_000, queue.MaximumQueuedLead100ns);
        Assert.False(queue.TryEnqueueAudio(Audio(4800, 1)));
    }

    private static EncoderFrame Video(double time) => new([1, 2, 3, 255], 1, 1, 4, time);
    private static RecordingAudioBlock Audio(long start, int frames) => new(start, new short[frames * 2]);

    private sealed class Sink : IAudioVideoEncoder
    {
        public int Width => 1;
        public int Height => 1;
        public AudioEncoderState AudioState => AudioEncoderState.Available;
        public string? AudioUnavailableReason => null;
        public List<(string Kind, long Time)> Writes { get; } = [];
        public List<int> Threads { get; } = [];
        public byte Pixel { get; private set; }
        public short Sample { get; private set; }
        public int Completions { get; private set; }
        public int Disposals { get; private set; }
        public bool FailWrite { get; init; }
        public void WriteFrame(in EncoderFrame frame)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (FailWrite) throw new IOException("Injected sink failure.");
            Writes.Add(("video", (long)(frame.TimestampMs * 10_000))); Pixel = frame.Pixels[0];
        }
        public void WriteAudio(in RecordingAudioBlock block)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (FailWrite) throw new IOException("Injected sink failure.");
            Writes.Add(("audio", block.Timestamp100ns)); Sample = block.Samples.Span[0];
        }
        public void Complete() { Threads.Add(Environment.CurrentManagedThreadId); Completions++; }
        public void Dispose() { Threads.Add(Environment.CurrentManagedThreadId); Disposals++; }
    }
}
