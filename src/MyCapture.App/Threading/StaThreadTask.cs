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
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
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
