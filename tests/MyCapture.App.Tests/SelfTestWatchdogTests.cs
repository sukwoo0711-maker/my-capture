using System.Diagnostics;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SelfTestWatchdogTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Stall_ReportsThePhaseAndStepThatOutlivedItsBudgetOnce()
    {
        var clock = new ManualClock();
        var stalls = new List<SelfTestStall>();
        using var called = new ManualResetEventSlim();
        using var watchdog = new SelfTestWatchdog("start", TimeSpan.FromSeconds(30), stall =>
        {
            lock (stalls)
            {
                stalls.Add(stall);
            }

            called.Set();
        }, Poll, clock.Read);

        watchdog.Enter("scrub", TimeSpan.FromSeconds(60));
        watchdog.Step(123);
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.True(called.Wait(Wait));
        Thread.Sleep(Poll * 10);
        lock (stalls)
        {
            SelfTestStall stall = Assert.Single(stalls);
            Assert.Equal("scrub", stall.Phase);
            Assert.Equal(123, stall.Step);
            Assert.Equal(TimeSpan.FromSeconds(60), stall.Budget);
            Assert.True(stall.Elapsed > stall.Budget);
        }

        Assert.True(watchdog.HasStalled);
    }

    [Fact]
    public void Enter_RestartsTheBudgetForEachPhase()
    {
        var clock = new ManualClock();
        using var called = new ManualResetEventSlim();
        using var watchdog = new SelfTestWatchdog("record", TimeSpan.FromSeconds(30), _ => called.Set(), Poll, clock.Read);

        // Three phases of 20 s each never stall, though together they exceed every budget.
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            watchdog.Enter("phase " + i, TimeSpan.FromSeconds(30));
        }

        Assert.False(called.Wait(Poll * 20));
        Assert.False(watchdog.HasStalled);
        Assert.Equal("phase 2", watchdog.Phase);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(called.Wait(Wait));
    }

    [Fact]
    public void Dispose_StopsWatchingWithoutAStall()
    {
        var clock = new ManualClock();
        using var called = new ManualResetEventSlim();
        var watchdog = new SelfTestWatchdog("start", TimeSpan.FromSeconds(1), _ => called.Set(), Poll, clock.Read);
        watchdog.Dispose();

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(called.Wait(Poll * 20));
    }

    [Fact]
    public void ThreadEvidence_NamesTheUiThreadAndItsCpuTime()
    {
        int threadId = SelfTestStallEvidence.CurrentNativeThreadId();
        string evidence = SelfTestStallEvidence.DescribeThreads(threadId, TimeSpan.FromMilliseconds(50));
        Assert.Contains($"thread {threadId} (UI):", evidence, StringComparison.Ordinal);
        Assert.Contains("cpu +", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherEvidence_ReadsQueuedWorkOfABlockedDispatcher()
    {
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        System.Windows.Threading.Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
            {
                ready.Set();
                release.Wait(Wait);
            }));
            _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(QueuedBehindTheBlock));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(ready.Wait(Wait));
            string evidence = SelfTestStallEvidence.DescribeDispatcher(dispatcher!);
            Assert.Contains("Dispatcher: posted-processing=", evidence, StringComparison.Ordinal);
            Assert.Contains("max-priority=Background", evidence, StringComparison.Ordinal);
            Assert.Contains($"queued: Background {typeof(SelfTestWatchdogTests)}.{nameof(QueuedBehindTheBlock)}", evidence, StringComparison.Ordinal);
            Assert.Contains("process-queue message 0x", evidence, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            dispatcher?.InvokeShutdown();
            thread.Join(Wait);
        }
    }

    [Fact]
    public void DumpRequest_IsADiagnosticsIpcGenerateCoreDumpWithHeap()
    {
        const string Path = @"C:\out\stall.dmp";
        byte[] request = SelfTestStallEvidence.DumpRequest(Path);

        Assert.Equal("DOTNET_IPC_V1\0", System.Text.Encoding.ASCII.GetString(request, 0, 14));
        Assert.Equal(request.Length, BitConverter.ToUInt16(request, 14));
        Assert.Equal(0x01, request[16]); // Dump command set.
        Assert.Equal(0x01, request[17]); // GenerateCoreDump.
        Assert.Equal(Path.Length + 1, BitConverter.ToInt32(request, 20));
        Assert.Equal(Path + "\0", System.Text.Encoding.Unicode.GetString(request, 24, (Path.Length + 1) * 2));
        int tail = 24 + ((Path.Length + 1) * 2);
        Assert.Equal(2u, BitConverter.ToUInt32(request, tail)); // WithHeap.
        Assert.Equal(0u, BitConverter.ToUInt32(request, tail + 4));
        Assert.Equal(tail + 8, request.Length);
    }

    private static void QueuedBehindTheBlock()
    {
    }

    private sealed class ManualClock
    {
        private long _now = Stopwatch.GetTimestamp();

        internal long Read() => Interlocked.Read(ref _now);

        internal void Advance(TimeSpan by) =>
            Interlocked.Add(ref _now, (long)(by.TotalSeconds * Stopwatch.Frequency));
    }
}
