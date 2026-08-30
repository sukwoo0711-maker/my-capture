using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TimelineRenderingTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void CompositionScheduler_CoalescesOneHundredRequestsIntoOneFrame() => RunSta(() =>
    {
        int renders = 0;
        using var scheduler = new CompositionFrameScheduler(
            Dispatcher.CurrentDispatcher,
            () => renders++);

        for (int i = 0; i < 100; i++)
        {
            scheduler.Request();
        }

        Assert.Equal(100, scheduler.RequestCount);
        Assert.Equal(99, scheduler.CoalescedRequestCount);
        Assert.Equal(0, scheduler.RenderFrameCount);
        scheduler.FlushForTest();
        Assert.Equal(1, scheduler.RenderFrameCount);
        Assert.Equal(1, renders);
        Assert.False(scheduler.IsPending);
    });

    [Fact]
    public void LoadedTimeline_PlayheadBurstKeepsNineVisualsAndDrawsOnce() => RunSta(() =>
    {
        var timeline = new TwoLineTimeline();
        timeline.Initialize(12_000, 15);
        var host = new Window
        {
            Content = timeline,
            Width = 900,
            Height = 260,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        try
        {
            host.Show();
            PumpFor(TimeSpan.FromMilliseconds(50));
            timeline.FlushRenderForTest();
            Assert.Equal(9, timeline.FixedVisualCountForTest);

            long framesBefore = timeline.RenderFrameCountForTest;
            long transientBefore = timeline.TransientDrawCountForTest;
            for (int i = 0; i < 100; i++)
            {
                timeline.SetPlayhead(i * 10, ensureVisible: false);
            }

            Assert.Equal(framesBefore, timeline.RenderFrameCountForTest);
            timeline.FlushRenderForTest();
            Assert.Equal(framesBefore + 1, timeline.RenderFrameCountForTest);
            Assert.Equal(transientBefore + 2, timeline.TransientDrawCountForTest);
            Assert.True(timeline.CoalescedRenderRequestCountForTest >= 1);
            Assert.Equal(9, timeline.FixedVisualCountForTest);
        }
        finally
        {
            host.Close();
            timeline.Dispose();
        }
    });
}
