using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Size-capped, rotating log files under the data root's logs folder: mycapture.log, then
/// mycapture.1.log (older) up to <see cref="MaximumFiles"/> files of <see cref="MaximumFileBytes"/>.
/// </summary>
/// <remarks>
/// Callers hand over finished <see cref="SafeLogLine"/> lines; a thread-pool drain does the disk I/O
/// so a log call on the UI thread never waits for the disk. <see cref="Flush"/> drains on the caller
/// for a terminating process. Logging never throws: an unwritable folder drops lines and retries later.
/// </remarks>
internal sealed class RollingFileLog : IDisposable
{
    internal const string FileStem = "mycapture";
    internal const long DefaultMaximumFileBytes = 1024 * 1024;
    internal const int DefaultMaximumFiles = 5;
    internal const int MaximumPendingLines = 4096;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly object _ioGate = new();
    private readonly Func<DateTimeOffset> _clock;
    private FileStream? _stream;
    private DateTimeOffset _retryAt;
    private int _pendingCount;
    private int _drainScheduled;
    private long _dropped;
    private bool _disposed;

    internal RollingFileLog(string directory, long maximumFileBytes = DefaultMaximumFileBytes,
        int maximumFiles = DefaultMaximumFiles, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        Folder = Path.GetFullPath(directory);
        MaximumFileBytes = maximumFileBytes;
        MaximumFiles = maximumFiles;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal string Folder { get; }
    internal long MaximumFileBytes { get; }
    internal int MaximumFiles { get; }

    internal string CurrentPath => FilePath(0);

    /// <summary>mycapture.log for the current file, mycapture.N.log for the N-th older one.</summary>
    internal string FilePath(int generation) =>
        Path.Combine(Folder, generation == 0 ? FileStem + ".log" : $"{FileStem}.{generation}.log");

    /// <summary>Existing log files, oldest first, so their lines read in time order.</summary>
    internal IReadOnlyList<string> ExistingFiles() =>
        Enumerable.Range(0, MaximumFiles).Reverse().Select(FilePath).Where(File.Exists).ToArray();

    internal void Append(string line)
    {
        if (Volatile.Read(ref _disposed)) return;
        if (Interlocked.Increment(ref _pendingCount) > MaximumPendingLines)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _pending.Enqueue(line);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static log => log.Drain(), this, preferLocal: false);
    }

    /// <summary>Writes every queued line now, on the calling thread. Gives up after <paramref name="timeout"/>.</summary>
    internal bool Flush(TimeSpan timeout)
    {
        bool entered = false;
        try
        {
            Monitor.TryEnter(_ioGate, timeout, ref entered);
            if (!entered) return false;
            WritePendingLocked();
            return true;
        }
        finally
        {
            if (entered) Monitor.Exit(_ioGate);
        }
    }

    internal void Flush() => Flush(Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        lock (_ioGate)
        {
            if (_disposed) return;
            WritePendingLocked();
            Volatile.Write(ref _disposed, true);
            CloseLocked();
        }
    }

    private void Drain()
    {
        lock (_ioGate)
        {
            // Clear the flag first: a line queued while this drain runs schedules the next one.
            Volatile.Write(ref _drainScheduled, 0);
            WritePendingLocked();
        }
    }

    private void WritePendingLocked()
    {
        if (_disposed || _pending.IsEmpty) return;
        if (!EnsureOpenLocked())
        {
            while (_pending.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _dropped);
            }
            return;
        }

        try
        {
            long dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) WriteLineLocked(SafeLogLine.Dropped(_clock(), dropped));
            while (_pending.TryDequeue(out string? line))
            {
                Interlocked.Decrement(ref _pendingCount);
                WriteLineLocked(line);
            }
            _stream!.Flush();
        }
        catch (Exception)
        {
            // Logging never throws, least of all on the thread pool where it would end the process.
            Interlocked.Increment(ref _dropped);
            CloseLocked();
            _retryAt = _clock() + RetryDelay;
        }
    }

    private void WriteLineLocked(string line)
    {
        byte[] bytes = Utf8.GetBytes(line + "\r\n");
        if (_stream!.Length > 0 && _stream.Length + bytes.Length > MaximumFileBytes)
        {
            RotateLocked();
            if (!EnsureOpenLocked()) throw new IOException("The log file could not be reopened after rotation.");
        }
        _stream!.Write(bytes);
    }

    private bool EnsureOpenLocked()
    {
        if (_stream is not null) return true;
        if (_clock() < _retryAt) return false;
        try
        {
            Directory.CreateDirectory(Folder);
            // Delete sharing lets a second process (a diagnostic run) rotate while this one appends.
            _stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
            return true;
        }
        catch (Exception)
        {
            _retryAt = _clock() + RetryDelay;
            return false;
        }
    }

    private void RotateLocked()
    {
        CloseLocked();
        global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(FilePath(MaximumFiles - 1));
        for (int generation = MaximumFiles - 2; generation >= 0; generation--)
        {
            string source = FilePath(generation);
            if (File.Exists(source)) File.Move(source, FilePath(generation + 1), overwrite: true);
        }
    }

    private void CloseLocked()
    {
        FileStream? stream = _stream;
        _stream = null;
        try { stream?.Dispose(); }
        catch (IOException) { }
    }
}

/// <summary>Writes Information and above to a <see cref="RollingFileLog"/> through <see cref="SafeLogLine"/>.</summary>
internal sealed class RollingFileLogProvider(RollingFileLog log, Func<DateTimeOffset>? clock = null) : ILoggerProvider
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public ILogger CreateLogger(string categoryName) => new SafeFileLogger(log, SafeLogLine.Token(categoryName), _clock);

    public void Dispose() { }

    private sealed class SafeFileLogger(RollingFileLog log, string category, Func<DateTimeOffset> clock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

        // The formatter is never called: it would substitute argument values such as paths or titles.
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            log.Append(SafeLogLine.Format(clock(), logLevel, category, eventId.Id,
                SafeLogLine.TemplateOf(state), exception));
        }
    }
}
