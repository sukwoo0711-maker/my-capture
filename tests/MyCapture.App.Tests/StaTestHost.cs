using System.Reflection;
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

    /// <summary>
    /// Like <see cref="Run"/> for a process that ends right after <paramref name="action"/>: the STA
    /// thread shuts its dispatcher down and then waits for process exit instead of exiting itself.
    /// Process exit ends it without the thread-exit callbacks in which msctf (TSF) can still call
    /// WPF's managed text stores (see <see cref="WpfTestThread.ShutdownDispatcher"/>).
    /// </summary>
    public static void RunUntilProcessExit(Action action)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
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
        }, finished);
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        finished.Wait();

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
    // With keepUntilProcessExit the thread signals it once its dispatcher has shut down and then
    // never exits; it must be a background thread of a process that ends soon after.
    internal static Thread Create(ThreadStart body, ManualResetEventSlim? keepUntilProcessExit = null)
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

            if (keepUntilProcessExit is not null)
            {
                keepUntilProcessExit.Set();
                Thread.Sleep(Timeout.Infinite);
            }
        });
    }

    /// <summary>Shuts down this thread's Dispatcher if WPF created one; never creates one.</summary>
    /// <remarks>
    /// WPF registers a TSF text store for each text editor (a TextBox, a RichTextBox, an editable
    /// ComboBox) that has rendered on this thread. Dispatcher shutdown unregisters the stores of
    /// editors that are still alive; an editor the GC already collected queues its unregistration
    /// from its finalizer, and WPF drops it if the dispatcher has finished by then. The store then
    /// stays registered, and when this thread exits msctf calls its managed thread focus sink after
    /// the runtime has released the thread, which fails fast with the message above (.NET 10 WPF,
    /// dotnet/wpf#11785). So a thread that used text services first collects and finalizes, and
    /// the drain below runs those unregistrations.
    /// </remarks>
    internal static void ShutdownDispatcher()
    {
        try
        {
            if (Dispatcher.FromThread(Thread.CurrentThread) is not { HasShutdownStarted: false } dispatcher)
            {
                return;
            }

            if (UsedTextServices())
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
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

    /// <summary>
    /// True once a TSF text store has registered on this thread (WPF keeps a per-thread
    /// TextServicesHost from then until its thread manager is deactivated). Unknown internals
    /// count as true.
    /// </summary>
    internal static bool UsedTextServices()
    {
        try
        {
            Type? editor = typeof(System.Windows.Controls.Primitives.TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor");
            object? store = editor?.GetProperty("_ThreadLocalStore", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            PropertyInfo? host = store?.GetType().GetProperty("TextServicesHost", BindingFlags.Instance | BindingFlags.NonPublic);
            return host is null || host.GetValue(store) is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
