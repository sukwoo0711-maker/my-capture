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

    private static void Pump(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
