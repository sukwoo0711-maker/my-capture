using System.Windows.Threading;
using MyCapture.Core.Storage;

namespace MyCapture.App.Editing;

internal sealed record DraftSnapshot(string Kind, string Document, Dictionary<string, string> Metadata,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Only the debounced snapshot runs on the dispatcher; all durable I/O runs off it.</summary>
internal sealed class EditDraftSession
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> Finishing = new();
    internal static void TrackFinalization(Task task)
    {
        Finishing.TryAdd(task, 0);
        _ = task.ContinueWith(done => { _ = done.Exception; Finishing.TryRemove(done, out _); }, TaskScheduler.Default);
    }
    internal static Task WaitForFinalizationsAsync() => Task.WhenAll(Finishing.Keys.ToArray());
    private readonly EditDraftStore _store;
    private readonly Func<Task<DraftSnapshot?>> _snapshot;
    private readonly Action<Exception> _failed;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _publicationGate = new();
    private long _revision;
    private bool _retired;
    internal Guid Id { get; }
    internal Task LastWrite { get; private set; } = Task.CompletedTask;
    internal EditDraftSession(EditDraftStore store, Dispatcher dispatcher, Func<Task<DraftSnapshot?>> snapshot,
        Action<Exception> failed, Guid? id = null, long initialRevision = 0)
    {
        _store = store; _snapshot = snapshot; _failed = failed; Id = id ?? Guid.NewGuid();
        _dispatcher = dispatcher;
        _revision = initialRevision;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += (_, _) => { _timer.Stop(); LastWrite = WriteAsync(); };
    }
    internal void Changed()
    {
        lock (_publicationGate)
        {
            if (_retired) return;
            Interlocked.Increment(ref _revision);
        }
        _timer.Stop(); _timer.Start();
    }
    internal async Task FlushAsync()
    {
        _timer.Stop();
        await LastWrite;
        LastWrite = WriteAsync();
        await LastWrite;
    }
    private async Task WriteAsync()
    {
        await _writer.WaitAsync();
        try
        {
            if (_retired) return;
            long revision = Interlocked.Read(ref _revision);
            DraftSnapshot? snapshot = await _dispatcher.InvokeAsync(_snapshot).Task.Unwrap();
            if (snapshot is null || _retired || revision != Interlocked.Read(ref _revision)) return;
            await Task.Run(() => _store.WriteAsync(Id, revision, snapshot.Kind, snapshot.Document,
                snapshot.Metadata, snapshot.Files, () => !_retired && revision == Interlocked.Read(ref _revision),
                publicationGate: _publicationGate));
        }
        catch (Exception error) { await _dispatcher.InvokeAsync(() => _failed(error)); }
        finally { _writer.Release(); }
    }
    internal async Task<bool> RetireAsync()
    {
        lock (_publicationGate) _retired = true;
        _timer.Stop();
        await _writer.WaitAsync();
        try { await Task.Run(() => _store.RetireAsync(Id)); return true; }
        catch (Exception error)
        {
            await _dispatcher.InvokeAsync(() => _failed(error));
            bool completed = _store.IsRetired(Id);
            if (!completed) lock (_publicationGate) _retired = false;
            return completed;
        }
        finally { _writer.Release(); }
    }
}
