using System.Windows.Threading;

namespace MyCapture.App.Tests;

/// <summary>Runs WPF resource and visual assertions on a dedicated STA thread.</summary>
internal static class StaTestHost
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        Thread thread = WpfTestThread.Create(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}

/// <summary>
/// Test threads that may create WPF objects. Any <c>DispatcherObject</c> lazily creates the
/// thread's Dispatcher, its message-only window and, after rendering, MediaContext windows and
/// channels. A thread that exits with them alive lets teardown dispatch a pending message to
/// their managed window procedure after the runtime has released the thread, which aborts the
/// test host ("Attempt to execute managed code after the .NET runtime thread state has been
/// destroyed"). These threads shut down the Dispatcher they created before they end.
/// </summary>
internal static class WpfTestThread
{
    internal static Thread Create(ThreadStart body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new Thread(() =>
        {
            try
            {
                body();
            }
            finally
            {
                ShutdownDispatcher();
            }
        });
    }

    /// <summary>Shuts down this thread's Dispatcher if WPF created one; never creates one.</summary>
    internal static void ShutdownDispatcher()
    {
        try
        {
            if (Dispatcher.FromThread(Thread.CurrentThread) is { HasShutdownStarted: false } dispatcher)
            {
                dispatcher.InvokeShutdown();
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a test thread would end the whole test process.
            Console.Error.WriteLine($"Test thread dispatcher shutdown failed: {ex}");
        }
    }
}
