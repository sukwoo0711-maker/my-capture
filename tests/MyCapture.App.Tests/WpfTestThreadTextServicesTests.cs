using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// WPF test threads must not end with a TSF text store still registered: msctf would call it from
/// the thread-exit cleanup after the runtime released the thread, a fail-fast for the whole host.
/// The check runs in an isolated process whose thread is kept, so a regression fails only it.
/// </summary>
public sealed class WpfTestThreadTextServicesTests
{
    private static readonly ManualResetEventSlim FinalizerBlocked = new();

    [Fact]
    public Task ShutdownDispatcher_UnregistersTheStoreOfAnEditorFinalizedAfterItsWindowClosed() =>
        SettingsThemePreviewTests.RunIsolated(nameof(ShutdownDispatcher_UnregistersTheStoreOfAnEditorFinalizedAfterItsWindowClosed),
            VerifyCollectedEditorStoreIsUnregistered, typeof(WpfTestThreadTextServicesTests));

    private static void VerifyCollectedEditorStoreIsUnregistered()
    {
        var holder = new Window?[1];
        WeakReference? textBox = ShowTextBox(holder);
        if (textBox is null) return; // No text services on this machine: nothing registers.

        // Hold the finalizer thread first, so the editor collected below is finalized only after
        // its dispatcher would have finished without the extra finalization step.
        FinalizerBlocked.Reset();
        AbandonFinalizerBlocker();
        GC.Collect();
        Assert.True(FinalizerBlocked.Wait(TimeSpan.FromSeconds(10)), "The finalizer thread never reached the blocker.");

        holder[0]!.Close();
        holder[0] = null;
        // Work queued for the closed window still references its editor until it runs.
        for (int attempt = 0; attempt < 5 && textBox.IsAlive; attempt++)
        {
            Pump(TimeSpan.FromMilliseconds(40));
            GC.Collect();
        }
        Assert.False(textBox.IsAlive, "The closed window's TextBox must be collectible for this check.");
        Assert.True(RegisteredTextStores() > 0);

        WpfTestThread.ShutdownDispatcher();

        Assert.True(Dispatcher.FromThread(Thread.CurrentThread)!.HasShutdownFinished);
        Assert.Equal(0, RegisteredTextStores());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference? ShowTextBox(Window?[] holder)
    {
        var box = new TextBox { Text = "글꼴 미리보기" };
        var window = new Window
        {
            Content = box,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 240, Height = 80,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        // WPF registers the store at Background priority after the editor's first layout.
        for (int attempt = 0; attempt < 40 && RegisteredTextStores() == 0; attempt++)
            Pump(TimeSpan.FromMilliseconds(25));
        holder[0] = window;
        if (RegisteredTextStores() > 0) return new WeakReference(box);
        window.Close();
        holder[0] = null;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFinalizerBlocker() => _ = new FinalizerBlocker();

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.ContextIdle, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>WPF's count of TSF text stores registered on this thread, read through its internals.</summary>
    private static int RegisteredTextStores()
    {
        Type editor = typeof(System.Windows.Controls.Primitives.TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor", throwOnError: true)!;
        object store = editor.GetProperty("_ThreadLocalStore", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object? host = store.GetType().GetProperty("TextServicesHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store);
        return host is null ? 0 : (int)host.GetType().GetField("_registeredtextstorecount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    }

    private sealed class FinalizerBlocker
    {
        ~FinalizerBlocker()
        {
            FinalizerBlocked.Set();
            Thread.Sleep(1500);
        }
    }
}
