using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyCapture.App.Pinning;

/// <summary>Fresh worker-owned pixels that the STA can adopt as a DIB without copying.</summary>
internal sealed class NativePinSettleBuffer : IDisposable
{
    private static int _liveCount;
    private static long _liveBytes;
    private IntPtr _section;
    internal IntPtr Pointer { get; private set; }
    internal IntPtr Section => _section;
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal int ByteLength { get; }
    internal static int LiveCount => Volatile.Read(ref _liveCount);
    internal static long LiveBytes => Interlocked.Read(ref _liveBytes);

    internal NativePinSettleBuffer(int width, int height)
    {
        Width = width; Height = height; Stride = checked(width * 4); ByteLength = checked(Stride * height);
        _section = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 4, 0, checked((uint)ByteLength), null);
        if (_section == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Pointer = MapViewOfFile(_section, 6, 0, 0, (nuint)ByteLength);
        if (Pointer == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _ = CloseHandle(_section); _section = IntPtr.Zero;
            throw new Win32Exception(error);
        }
        Interlocked.Increment(ref _liveCount);
        Interlocked.Add(ref _liveBytes, ByteLength);
    }

    /// <summary>The caller already created a DIB backed by Section and assumes its ownership.</summary>
    internal IntPtr TransferToDisplay()
    {
        if (Pointer == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativePinSettleBuffer));
        if (!UnmapViewOfFile(Pointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Pointer = IntPtr.Zero;
        IntPtr section = _section; _section = IntPtr.Zero;
        Interlocked.Decrement(ref _liveCount);
        Interlocked.Add(ref _liveBytes, -ByteLength);
        return section;
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            if (!UnmapViewOfFile(Pointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Pointer = IntPtr.Zero;
            Interlocked.Decrement(ref _liveCount);
            Interlocked.Add(ref _liveBytes, -ByteLength);
        }
        if (_section != IntPtr.Zero)
        {
            if (!CloseHandle(_section)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _section = IntPtr.Zero;
        }
    }

    internal static void ReleaseSection(IntPtr section)
    {
        if (section != IntPtr.Zero && !CloseHandle(section)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr security, uint protection, uint high, uint low, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint high, uint low, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr pointer);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>A finished worker picture of <paramref name="View"/> of the surface (the part the pin holds at rest).</summary>
internal sealed record NativePinSettleFrame(NativePinSettleBuffer Buffer, NativePinEdgeCache Edges,
    int Generation, double RasterMilliseconds, NativePinRaster.View View) : IDisposable
{
    public void Dispose() => Buffer.Dispose();
}

/// <summary>One render and at most one unpublished result; cancellation never waits for the worker.</summary>
internal sealed class NativePinSettleWork
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    // Internal resource signal only. Its Task.WhenAll observers can finish inline
    // without scheduling extra pool threads after the dedicated worker has drained.
    private readonly TaskCompletionSource _drained = new();
    private NativePinSettleFrame? _result;
    private bool _finished, _discard, _cancelDisposed, _ready;
    private int _bufferCount;
    private int _peakBufferCount;
    private long _bufferBytes;
    private Task _execution = Task.CompletedTask, _discardExecution = Task.CompletedTask;
    internal Exception? Error { get; private set; }
    internal Task Drained => Task.WhenAll(_drained.Task, _execution, _discardExecution);
    internal int BufferCount => Volatile.Read(ref _bufferCount);
    internal int PeakBufferCount => Volatile.Read(ref _peakBufferCount);
    /// <summary>The bytes of this work's section while it still owns one, otherwise zero.</summary>
    internal long BufferBytes => BufferCount == 0 ? 0 : Interlocked.Read(ref _bufferBytes);
    internal int Generation { get; }
    internal bool IsReadyToComplete { get { lock (_gate) return _ready; } }

    internal NativePinSettleWork(int generation) => Generation = generation;

    internal void Start(Func<CancellationToken, NativePinSettleFrame> render)
    {
        // A bounded dedicated thread exits with this render. Growing the shared
        // pool here would keep its thread/kernel resources after every pin closes.
        _execution = Task.Factory.StartNew(() =>
        {
            NativePinSettleFrame? result = null;
            try { result = render(_cancel.Token); }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception error) { Error = error; }
            bool discard;
            lock (_gate)
            {
                _finished = true;
                discard = _discard || _cancel.IsCancellationRequested;
                if (!discard) { _result = result; _ready = true; }
            }
            if (discard) DisposeDiscarded(result);
            else if (result is null) FinishTransfer();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal void BufferAllocated(long bytes)
    {
        Interlocked.Exchange(ref _bufferBytes, bytes);
        Volatile.Write(ref _peakBufferCount, 1);
        Volatile.Write(ref _bufferCount, 1);
    }

    internal void Cancel()
    {
        NativePinSettleFrame? discarded = null;
        lock (_gate)
        {
            if (!_cancelDisposed) _cancel.Cancel();
            _discard = true;
            if (_finished && _result is not null)
            {
                discarded = _result; _result = null; _ready = false;
            }
        }
        // Releasing a completed mapped frame can page through many megabytes. Keep
        // it off the STA as well, and do not allow the next worker before it drains.
        if (discarded is not null)
            _discardExecution = Task.Factory.StartNew(() => DisposeDiscarded(discarded),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal NativePinSettleFrame? TakeResult()
    {
        lock (_gate) { var result = _result; _result = null; return result; }
    }

    internal void FinishTransfer()
    {
        lock (_gate)
        {
            Volatile.Write(ref _bufferCount, 0);
            _ready = true;
            if (!_cancelDisposed) { _cancel.Dispose(); _cancelDisposed = true; }
        }
        // Publish only after all protected state is consistent and the lock is free.
        _drained.TrySetResult();
    }

    private void DisposeDiscarded(NativePinSettleFrame? frame)
    {
        try { frame?.Dispose(); }
        catch (Exception error) { Error ??= error; }
        finally
        {
            FinishTransfer();
        }
    }
}
