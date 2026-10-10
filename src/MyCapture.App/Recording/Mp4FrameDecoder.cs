using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>What the source reader of an MP4 render decodes to, known once it has opened.</summary>
internal readonly record struct Mp4ReaderInfo(
    DecodedVideoPixelFormat Format,
    int Width,
    int Height,
    VideoYuvEncoding Yuv,
    bool IsHardwareAccelerated);

/// <summary>
/// Decodes an MP4 render's source schedule on its own thread, a few frames ahead of the encoder.
/// </summary>
/// <remarks>
/// <para>
/// Once decoded NV12 goes straight to the encoder, decoding and encoder submission cost about the
/// same per frame, so running them one after the other on the render thread spends twice as long
/// as necessary. This thread owns the Media Foundation reader from creation to disposal, as the
/// GIF engine's decode thread does, and touches nothing from WPF. The render thread keeps every
/// WPF operation and every encoder call.
/// </para>
/// <para>
/// At most <c>capacity</c> decoded frames wait in the queue. Frames are never modified after
/// decoding, so a frame the reader returns twice (output faster than the source) is safe to queue
/// twice. A decode failure, including <see cref="Mp4HardwareDecodeException"/>, is rethrown on the
/// render thread by <see cref="Take"/>.
/// </para>
/// </remarks>
internal sealed class Mp4FrameDecoder : IDisposable
{
    private readonly BlockingCollection<DecodedVideoFrame> _frames;
    private readonly CancellationTokenSource _stop;
    private readonly ManualResetEventSlim _opened = new();
    private readonly Thread _thread;
    private readonly string _sourcePath;
    private readonly MediaFoundationVideoFrameReaderOptions _options;
    private readonly IReadOnlyList<double> _sourceTimes;
    private Mp4ReaderInfo _info;
    private volatile ExceptionDispatchInfo? _failure;
    private bool _disposed;

    private Mp4FrameDecoder(
        string sourcePath,
        MediaFoundationVideoFrameReaderOptions options,
        IReadOnlyList<double> sourceTimes,
        int capacity,
        CancellationToken cancellationToken)
    {
        _sourcePath = sourcePath;
        _options = options;
        _sourceTimes = sourceTimes;
        _frames = new BlockingCollection<DecodedVideoFrame>(new ConcurrentQueue<DecodedVideoFrame>(), capacity);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "MyCapture MP4 decode",
        };
    }

    /// <summary>Opens <paramref name="sourcePath"/> on a new decode thread and starts decoding the schedule.</summary>
    internal static Mp4FrameDecoder Start(
        string sourcePath,
        MediaFoundationVideoFrameReaderOptions options,
        IReadOnlyList<double> sourceTimes,
        int capacity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sourceTimes);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var decoder = new Mp4FrameDecoder(sourcePath, options, sourceTimes, capacity, cancellationToken);
        decoder._thread.Start();
        return decoder;
    }

    /// <summary>Waits until the reader has opened and returns what it decodes to; rethrows an open failure.</summary>
    internal Mp4ReaderInfo WaitForOpen(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _opened.Wait(cancellationToken);
        _failure?.Throw();
        return _info;
    }

    /// <summary>Returns the next scheduled frame; rethrows the decode thread's failure.</summary>
    internal DecodedVideoFrame Take(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frames.TryTake(out DecodedVideoFrame? frame, Timeout.Infinite, cancellationToken))
        {
            return frame;
        }

        _failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidDataException("The video decoder stopped before the end of the schedule.");
    }

    private void Run()
    {
        MediaFoundationVideoFrameReader? reader = null;
        try
        {
            reader = new MediaFoundationVideoFrameReader(_sourcePath, _options);
            _info = new Mp4ReaderInfo(
                reader.OutputFormat,
                reader.Width,
                reader.Height,
                reader.YuvEncoding,
                reader.IsHardwareAccelerated);
            _opened.Set();
            foreach (double sourceTime in _sourceTimes)
            {
                _stop.Token.ThrowIfCancellationRequested();
                _frames.Add(Read(reader, sourceTime), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            reader?.Dispose();
            _frames.CompleteAdding();
            _opened.Set();
        }
    }

    /// <summary>Reads one frame; a hardware decoder failure becomes retryable in software.</summary>
    private DecodedVideoFrame Read(MediaFoundationVideoFrameReader reader, double sourceTimeMs)
    {
        try
        {
            return reader.ReadFrameAt(sourceTimeMs, _stop.Token);
        }
        catch (Exception ex) when (reader.IsHardwareAccelerated
                                   && (ex is InvalidOperationException or InvalidDataException or COMException)
                                   && !_stop.IsCancellationRequested)
        {
            // Device loss or a driver-specific surface problem after a successful hardware
            // open; the re-encoder retries the whole render in software.
            throw new Mp4HardwareDecodeException(ex);
        }
    }

    /// <summary>Stops decoding and waits for the thread, which releases the reader.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _thread.Join();
        _stop.Dispose();
        _opened.Dispose();
        _frames.Dispose();
    }
}
