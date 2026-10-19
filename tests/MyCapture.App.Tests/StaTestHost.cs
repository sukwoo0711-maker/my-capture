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
            // Theme tests must resolve System independently of the developer's Windows app mode.
            // This seam is thread-local, so initialize it on each WPF thread, including children.
            MyCapture.App.Themes.ThemeService.SystemLightThemeReaderForTest = static () => false;
            try
            {
                body();
            }
            finally
            {
                ShutdownDispatcher();
                MyCapture.App.Themes.ThemeService.SystemLightThemeReaderForTest = null;
            }
        });
    }

    /// <summary>Shuts down this thread's Dispatcher if WPF created one; never creates one.</summary>
    internal static void ShutdownDispatcher()
    {
        try
        {
            if (Dispatcher.FromThread(Thread.CurrentThread) is not { HasShutdownStarted: false } dispatcher)
            {
                return;
            }

            // Let work the test left queued finish first, such as a context menu's popup that
            // was just closed: shutting down with it pending hung inside InvokeShutdown (the
            // ShutdownStarted event ran, ShutdownFinished never did).
            try
            {
                var drained = new DispatcherFrame();
                var limit = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Send, (_, _) => drained.Continue = false, dispatcher);
                _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => drained.Continue = false));
                Dispatcher.PushFrame(drained);
                limit.Stop();
            }
            catch (Exception ex)
            {
                // Late work the test left behind failed, or processing is disabled; shut down anyway.
                Console.Error.WriteLine($"Test thread dispatcher drain failed: {ex}");
            }

            dispatcher.InvokeShutdown();
        }
        catch (Exception ex)
        {
            // An exception escaping a test thread would end the whole test process.
            Console.Error.WriteLine($"Test thread dispatcher shutdown failed: {ex}");
        }
    }
}
