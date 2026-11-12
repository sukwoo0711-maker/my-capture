using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Diagnostics;

internal readonly record struct SafeErrorRecord(DateTimeOffset Utc, int EventId, int? HResult)
{
    internal LogLevel Level { get; init; } = LogLevel.Error;
    internal string Category { get; init; } = SafeLogLine.Unknown;
    internal string Template { get; init; } = SafeLogLine.Unknown;
    internal string ExceptionType { get; init; } = SafeLogLine.Unknown;
    internal string BaseExceptionType { get; init; } = SafeLogLine.Unknown;
    internal int? BaseHResult { get; init; }
    internal string Site { get; init; } = SafeLogLine.Unknown;
    internal string AppFrame { get; init; } = SafeLogLine.Unknown;
    internal string StackSignature { get; init; } = SafeLogLine.Unknown;

    // Template is an already hashed template identifier, never a rendered message.
    internal static SafeErrorRecord From(DateTimeOffset utc, LogLevel level, int eventId,
        string? category, string? template, Exception? exception)
    {
        var result = new SafeErrorRecord(utc, eventId, exception?.HResult)
        {
            Level = level,
            Category = SafeLogLine.Token(category),
            Template = HashToken(template),
            ExceptionType = SafeLogLine.Token(exception?.GetType().FullName),
        };
        if (exception is null) return result;
        try
        {
            MethodBase? site = exception.TargetSite;
            result = result with { Site = SafeLogLine.Token(site?.DeclaringType?.FullName is { } type
                ? type + "." + site.Name : site?.Name) };
        }
        catch (Exception) { }
        try
        {
            Exception cause = exception.GetBaseException();
            if (!ReferenceEquals(cause, exception))
                result = result with { BaseExceptionType = SafeLogLine.Token(cause.GetType().FullName), BaseHResult = cause.HResult };
        }
        catch (Exception) { }
        try
        {
            StackFrame[] frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            if (frames.Length == 0) return result;
            var signature = new StringBuilder();
            string appFrame = SafeLogLine.Unknown;
            for (int i = 0; i < frames.Length && (i < 6 || appFrame == SafeLogLine.Unknown); i++)
            {
                MethodBase? method = frames[i].GetMethod();
                Type? type = method?.DeclaringType;
                if (type?.IsGenericType == true) type = type.GetGenericTypeDefinition();
                // A frame without metadata (dynamic or emitted code) keeps its position as "?" so the
                // rest of the path still identifies the failure.
                string? identifier = method is not null && type?.FullName is { } name ? name + "." + method.Name : null;
                if (i < 6) signature.Append(identifier ?? "?").Append('\n');
                if (identifier is not null && appFrame == SafeLogLine.Unknown
                    && type!.Namespace?.StartsWith("MyCapture.", StringComparison.Ordinal) == true)
                    appFrame = SafeLogLine.Token(identifier);
            }
            result = result with
            {
                AppFrame = appFrame,
                StackSignature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())).AsSpan(0, 4)),
            };
        }
        catch (Exception) { }
        return result;
    }

    internal static string HashToken(string? value) => value is { Length: 8 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? value : SafeLogLine.Unknown;
}

/// <summary>Bounded error identifiers in memory; never formats messages or performs file I/O.</summary>
internal sealed class RecentErrorLog
{
    internal const int Capacity = 12;
    internal static RecentErrorLog Shared { get; } = new();
    private readonly object _gate = new();
    private readonly SafeErrorRecord[] _errors = new SafeErrorRecord[Capacity];
    private readonly Func<DateTimeOffset> _clock;
    private int _next;
    private int _count;

    internal RecentErrorLog(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal void Record(int eventId, Exception? exception)
        => Record(LogLevel.Error, eventId, null, null, exception);

    internal void Record(LogLevel level, int eventId, string? category, string? template, Exception? exception)
    {
        SafeErrorRecord record = SafeErrorRecord.From(_clock(), level, eventId, category, template, exception);
        lock (_gate)
        {
            _errors[_next] = record;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    internal SafeErrorRecord[] Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new SafeErrorRecord[_count];
            for (int i = 0; i < _count; i++) snapshot[i] = _errors[(_next - _count + Capacity + i) % Capacity];
            return snapshot;
        }
    }
}

internal sealed class RecentErrorLogProvider(RecentErrorLog errors) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SafeLogger(errors, SafeLogLine.Token(categoryName));
    public void Dispose() { }

    private sealed class SafeLogger(RecentErrorLog errors, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string? template = SafeLogLine.TemplateOf(state);
            errors.Record(logLevel, eventId.Id, category, template is null ? null : SafeLogLine.TemplateId(template), exception);
        }
    }
}
