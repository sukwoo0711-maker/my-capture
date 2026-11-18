using System.Windows.Threading;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorStartupTests
{
    [Fact]
    public void InputTurnPrecedesChromeAndCloseCancelsDeferredCreation() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        using var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => order.Add("input"), () => order.Add("chrome"));
        Pump(DispatcherPriority.Input);
        Assert.Equal(["input"], order);
        startup.Dispose();
        Pump(DispatcherPriority.Background);
        Assert.Equal(["input"], order);
    });

    [Fact]
    public void EarlyCloseOrCloseInsideInputNeverCreatesChrome() => StaTestHost.Run(() =>
    {
        int input = 0, chrome = 0;
        using (var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher, () => input++, () => chrome++))
            startup.Dispose();
        Pump(DispatcherPriority.Background);
        Assert.Equal(0, input);
        Assert.Equal(0, chrome);

        NativeSelectorStartup? closing = null;
        closing = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => { input++; closing!.Dispose(); }, () => chrome++);
        using (closing) Pump(DispatcherPriority.Background);
        Assert.Equal(1, input);
        Assert.Equal(0, chrome);
    });

    [Fact]
    public void OpenSessionRunsEachPhaseOnceInOrder() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        using var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => order.Add("input"), () => order.Add("chrome"));
        Pump(DispatcherPriority.Input);
        Assert.Equal(["input"], order);
        Pump(DispatcherPriority.Background);
        Assert.Equal(["input", "chrome"], order);
        Pump(DispatcherPriority.Background);
        Assert.Equal(2, order.Count);
    });

    // WPF holds Input and Background work while Win32 input is queued and then waits for its background
    // timer. The selector raises the next step once its window procedure has drained input and paint.
    [Fact]
    public void QueuedInputOrPaintKeepsTheStepAtItsPriority() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        using var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => order.Add("input"), () => order.Add("chrome"), hasQueuedInputOrPaint: () => true);
        startup.QueueDrained();
        Assert.Equal(DispatcherPriority.Input, startup.PendingPriorityForTest);
        Assert.Empty(order);
    });

    [Fact]
    public void DrainedQueueRaisesTheInputTurnThenTheChromeBelowRender() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        using var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => order.Add("input"), () => order.Add("chrome"), hasQueuedInputOrPaint: () => false);
        startup.QueueDrained();
        Assert.Equal(DispatcherPriority.Loaded, startup.PendingPriorityForTest);

        // Layout and render work already queued still runs first.
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => order.Add("render")));
        Pump(DispatcherPriority.Loaded);
        Assert.Equal(["render", "input"], order);
        Assert.Equal(DispatcherPriority.Background, startup.PendingPriorityForTest);

        startup.QueueDrained();
        Assert.Equal(DispatcherPriority.Loaded, startup.PendingPriorityForTest);
        Pump(DispatcherPriority.Loaded);
        Assert.Equal(["render", "input", "chrome"], order);
        Assert.Null(startup.PendingPriorityForTest);
        startup.QueueDrained();
        Pump(DispatcherPriority.Background);
        Assert.Equal(3, order.Count);
    });

    [Fact]
    public void ClosedStartupNeitherRaisesNorRuns() => StaTestHost.Run(() =>
    {
        var order = new List<string>();
        var startup = new NativeSelectorStartup(Dispatcher.CurrentDispatcher,
            () => order.Add("input"), () => order.Add("chrome"), hasQueuedInputOrPaint: () => false);
        startup.Dispose();
        startup.QueueDrained();
        Pump(DispatcherPriority.Background);
        Assert.Empty(order);
        Assert.Null(startup.PendingPriorityForTest);
    });

    private static void Pump(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
