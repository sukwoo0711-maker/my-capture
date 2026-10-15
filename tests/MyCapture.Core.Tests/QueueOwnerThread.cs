using System.Collections.Concurrent;

namespace MyCapture.Core.Tests;

/// <summary>
/// Runs an async test body on one dedicated thread, the way the app's UI dispatcher owns the
/// capture queue. Queue members marked "call on the queue owner" resume on that context after
/// their awaits; without it the continuations of concurrent operations run on separate
/// thread-pool threads and can touch the record collection at the same time.
/// </summary>
internal static class QueueOwnerThread
{
    internal static Task RunAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new OwnerContext();
            SynchronizationContext.SetSynchronizationContext(context);
            Task task;
            try { task = body(); }
            catch (Exception ex) { task = Task.FromException(ex); }
            task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
            context.Run();
            if (task.IsCanceled) completion.SetCanceled();
            else if (task.Exception is { } failure) completion.SetException(failure.InnerExceptions);
            else completion.SetResult();
        })
        {
            IsBackground = true,
            Name = "Queue owner",
        };
        thread.Start();
        return completion.Task;
    }

    private sealed class OwnerContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); }
            catch (InvalidOperationException)
            {
                // The body already finished; work that outlived it runs where it can.
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("The queue owner never blocks on another thread.");

        public override SynchronizationContext CreateCopy() => this;

        internal void Complete() => _queue.CompleteAdding();

        internal void Run()
        {
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
                callback(state);
        }
    }
}
