using System.Diagnostics;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyCapture.App.Diagnostics;

/// <summary>The same diagnostic render-thread fence used by the selector breakdown.</summary>
internal static class ResidentProbePresentation
{
    private static readonly Type? MediaContext = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
    private static readonly MethodInfo? From = MediaContext?.GetMethod("From",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Dispatcher)]);
    private static readonly MethodInfo? CompleteRender = MediaContext?.GetMethod("CompleteRender",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);

    internal sealed record Frame(double RenderingMs, double? CompleteMs, string? Error);

    // Subscribe before the action. Rendering is pre-submit, so flush on the next Send-priority
    // dispatcher turn, after that render pass. This is an upper bound, not physical scan-out.
    internal static Task<Frame> MeasureAsync(Dispatcher dispatcher, Action action, Func<bool> ready,
        int timeoutMs = 15_000) => MeasureAsync(dispatcher, () =>
        {
            action();
            return Task.CompletedTask;
        }, ready, timeoutMs);

    internal static async Task<Frame> MeasureAsync(Dispatcher dispatcher, Func<Task> action, Func<bool> ready,
        int timeoutMs = 15_000)
    {
        var completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = Stopwatch.GetTimestamp();
        bool queued = false;
        DispatcherOperation? operation = null;
        EventHandler rendering = (_, _) =>
        {
            if (queued || !ready()) return;
            queued = true;
            double first = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            operation = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                try
                {
                    if (CompleteRender is null || From?.Invoke(null, [dispatcher]) is not { } context)
                        completion.TrySetResult(new(first, null, "WPF MediaContext.CompleteRender unavailable"));
                    else
                    {
                        CompleteRender.Invoke(context, null);
                        completion.TrySetResult(new(first, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null));
                    }
                }
                catch (Exception error) when (error is TargetInvocationException or MemberAccessException or InvalidOperationException)
                {
                    completion.TrySetResult(new(first, null, error.GetBaseException().Message));
                }
            }));
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            await action();
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            if (operation?.Status == DispatcherOperationStatus.Pending) operation.Abort();
        }
    }
}
