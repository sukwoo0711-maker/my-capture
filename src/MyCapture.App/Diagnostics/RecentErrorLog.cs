using Microsoft.Extensions.Logging;

namespace MyCapture.App.Diagnostics;

internal readonly record struct SafeErrorRecord(DateTimeOffset Utc, int EventId, int? HResult);

/// <summary>No message formatting, exception text, stack, user input or file I/O on the log path.</summary>
internal sealed class RecentErrorLog
{
    internal const int Capacity = 12;
    internal static RecentErrorLog Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Queue<SafeErrorRecord> _errors = new(Capacity);

    internal void Record(int eventId, Exception? exception)
    {
        lock (_gate)
        {
            if (_errors.Count == Capacity) _errors.Dequeue();
            _errors.Enqueue(new(DateTimeOffset.UtcNow, eventId, exception?.HResult));
        }
    }

    internal SafeErrorRecord[] Snapshot()
    {
        lock (_gate) return _errors.ToArray();
    }
}

internal sealed class RecentErrorLogProvider(RecentErrorLog errors) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SafeLogger(errors);
    public void Dispose() { }

    private sealed class SafeLogger(RecentErrorLog errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) errors.Record(eventId.Id, exception);
        }
    }
}
