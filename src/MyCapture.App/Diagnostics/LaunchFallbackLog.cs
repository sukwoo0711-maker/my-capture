using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Secondary launches and failures before service construction use logs/launch, capped at two
/// 1 MiB files. They never share the resident logger's persistent stream. Each short-lived writer
/// is serialized across processes and closed before releasing the mutex, including during rotation.
/// </summary>
internal sealed class LaunchFallbackLog(Func<string> logsRoot, string? mutexName = null) : IDisposable
{
    private const string MutexName = @"Local\MyCapture.LaunchLog.{6F2A1C34-9B7E-4D51-8A0C-3E5D7B912F48}";
    private readonly object _gate = new();
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    internal void Append(string line)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // No disk I/O on the launch notice's message loop or the startup dispatcher.
            _pending = _pending.ContinueWith(_ => Write(line), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    internal Task Completion { get { lock (_gate) return _pending; } }

    public void Dispose()
    {
        Task pending;
        lock (_gate) { _disposed = true; pending = _pending; }
        // Exit remains bounded if another process or the volume stops responding.
        _ = pending.Wait(TimeSpan.FromSeconds(3));
    }

    private void Write(string line)
    {
        try
        {
            string folder = Path.Combine(logsRoot(), "launch");
            using var mutex = new Mutex(false, mutexName ?? MutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try
            {
                using var log = new RollingFileLog(folder, maximumFiles: 2);
                log.Append(line);
                // Dispose drains and closes before the next process opens a fresh stream.
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception)
        {
            // Optional diagnostics must never replace the startup failure dialog or stop activation.
        }
    }
}
