using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace MyCapture.App.Diagnostics;

/// <summary>Opt-in native dispatcher attribution, without adding a render-thread flush.</summary>
internal sealed class SelectorDispatcherTrace : IDisposable
{
    private static readonly PropertyInfo? OperationName = typeof(DispatcherOperation)
        .GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly Dispatcher _dispatcher;
    private readonly long _started;
    private readonly Dictionary<DispatcherOperation, long> _operations = [];

    internal SelectorDispatcherTrace(Dispatcher dispatcher, long started)
    {
        _dispatcher = dispatcher;
        _started = started;
        dispatcher.Hooks.OperationStarted += OnStarted;
        dispatcher.Hooks.OperationCompleted += OnCompleted;
        dispatcher.Hooks.OperationAborted += OnCompleted;
    }

    private void OnStarted(object? sender, DispatcherHookEventArgs args)
    {
        lock (_operations) _operations[args.Operation] = Stopwatch.GetTimestamp();
    }

    private void OnCompleted(object? sender, DispatcherHookEventArgs args)
    {
        long began;
        lock (_operations) if (!_operations.Remove(args.Operation, out began)) return;
        double duration = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (duration < 2 || SelectorTimeline.Mark is not { } mark) return;
        double at = Stopwatch.GetElapsedTime(_started, began).TotalMilliseconds;
        string name = OperationName?.GetValue(args.Operation) as string ?? "?";
        mark($"op {name} [{args.Operation.Priority}] from {at:0.0} for {duration:0.0}");
    }

    public void Dispose()
    {
        _dispatcher.Hooks.OperationStarted -= OnStarted;
        _dispatcher.Hooks.OperationCompleted -= OnCompleted;
        _dispatcher.Hooks.OperationAborted -= OnCompleted;
        lock (_operations) _operations.Clear();
    }
}
