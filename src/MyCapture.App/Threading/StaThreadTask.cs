using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyCapture.App.Threading;

/// <summary>
/// Runs one synchronous Windows/OLE operation on an isolated background STA thread.
/// </summary>
/// <remarks>
/// WPF clipboard calls and some imaging primitives can perform their own blocking native
/// retries. Keeping those calls on a short-lived STA preserves COM requirements without ever
/// sleeping the UI dispatcher. Callers must pass only immutable/frozen inputs and return only
/// detached or frozen results.
/// </remarks>
internal static class StaThreadTask
{
    internal static Task<T> RunAsync<T>(Func<T> action, string? threadName = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            T result = default!;
            Exception? failure = null;
            try
            {
                result = action();
                if (result is BitmapSource bitmap && !bitmap.IsFrozen)
                    throw new InvalidOperationException("STA operations must return frozen bitmaps.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            try
            {
                // Imaging can lazily create a Dispatcher/MediaContext even without a
                // message loop. Thread exit alone does not run their shutdown callbacks.
                // Inspect only this worker; never create a dispatcher just for cleanup.
                Dispatcher? dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is { HasShutdownFinished: false }) dispatcher.InvokeShutdown();
            }
            catch (Exception cleanupFailure)
            {
                failure = failure is null ? cleanupFailure : new AggregateException(
                    "The STA operation and dispatcher cleanup both failed.", failure, cleanupFailure);
            }

            // Consumers cannot start another operation before native WPF teardown completes.
            if (failure is null) completion.TrySetResult(result);
            else completion.TrySetException(failure);
        })
        {
            IsBackground = true,
            Name = threadName ?? "MyCapture STA worker",
        };

        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }
}
