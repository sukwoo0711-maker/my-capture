using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ToolTipTimerGuardTests
{
    /// <summary>
    /// The dispatcher stall behind the intermittent packaged video-editor hang: a short timer falls
    /// due while other work runs (a tooltip popup opening), then the tooltip's show-duration timer
    /// starts. At WPF's default duration the short timer never fires; at the guarded bound it does.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue, false)]
    [InlineData(ToolTipTimerGuard.MaximumShowDurationMs, true)]
    public void LongTimerStartedWhileAnotherIsOverdue_StopsTheShortTimerOnlyAtTheUnguardedDuration(
        int showDurationMs,
        bool expectedShortTick)
    {
        bool shortTicked = false;
        StaTestHost.Run(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var shortTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            shortTimer.Tick += (_, _) =>
            {
                shortTimer.Stop();
                shortTicked = true;
                frame.Continue = false;
            };
            var longTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(showDurationMs) };
            using var deadline = new Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false));
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                shortTimer.Start();
                Thread.Sleep(50);
                longTimer.Start();
                _ = deadline.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            });
            Dispatcher.PushFrame(frame);
            longTimer.Stop();
            shortTimer.Stop();
        });

        Assert.Equal(expectedShortTick, shortTicked);
    }

    [Fact]
    public void Install_BoundsTheDefaultAndExplicitShowDurationsButKeepsShorterOnes() => StaTestHost.Run(() =>
    {
        ToolTipTimerGuard.Install();
        ToolTipTimerGuard.Install();

        Assert.Equal(ToolTipTimerGuard.MaximumShowDurationMs, ToolTipService.GetShowDuration(new Button()));
        Assert.Equal(ToolTipTimerGuard.MaximumShowDurationMs, ToolTipService.GetShowDuration(new Hyperlink()));

        var unbounded = new Border();
        ToolTipService.SetShowDuration(unbounded, int.MaxValue);
        Assert.Equal(ToolTipTimerGuard.MaximumShowDurationMs, ToolTipService.GetShowDuration(unbounded));

        var gallery = new Border();
        ToolTipService.SetShowDuration(gallery, 120000);
        Assert.Equal(120000, ToolTipService.GetShowDuration(gallery));
    });
}
