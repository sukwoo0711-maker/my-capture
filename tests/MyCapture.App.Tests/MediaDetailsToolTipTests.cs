using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Media;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MediaDetailsToolTipTests
{
    [Theory]
    [InlineData("closed", false)]
    [InlineData("closed", true)]
    [InlineData("recycled", false)]
    [InlineData("unloaded", false)]
    [InlineData("hidden", false)]
    [InlineData("owner-closed", false)]
    public void CancelledTooltipDoesNotApplyLateResultsOrThrow(string action, bool readerThrows) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var tooltip = new ToolTip();
        var dataContext = new object();
        var target = new Border { DataContext = dataContext, ToolTip = tooltip };
        var owner = CreateOwner(target);
        var completed = new TaskCompletionSource<IReadOnlyList<MediaDetails>>();
        CancellationToken readToken = default;
        var loader = new MediaDetailsToolTipLoader(owner, (_, token) => { readToken = token; return completed.Task; });
        bool applied = false;
        try
        {
            owner.Show();
            Task loading = loader.LoadAsync(target, dataContext, tooltip, new MediaDetailsSource(null), _ => applied = true);
            switch (action)
            {
                case "closed": tooltip.RaiseEvent(new RoutedEventArgs(ToolTip.ClosedEvent)); break;
                case "recycled": target.DataContext = new object(); break;
                case "unloaded": target.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); break;
                case "hidden": owner.Hide(); break;
                case "owner-closed": owner.Close(); break;
            }
            Assert.True(readToken.IsCancellationRequested);
            if (readerThrows) completed.SetCanceled(readToken);
            else completed.SetResult([Details()]);
            owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.True(loading.IsCompletedSuccessfully);
            Assert.False(applied);
        }
        finally
        {
            owner.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    });

    [Fact]
    public void ReopeningCancelsOnlyTheOldReadAndAppliesTheCurrentResult() => StaTestHost.Run(() =>
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var firstTooltip = new ToolTip();
        var secondTooltip = new ToolTip();
        var dataContext = new object();
        var target = new Border { DataContext = dataContext, ToolTip = firstTooltip };
        var owner = CreateOwner(target);
        var reads = new List<TaskCompletionSource<IReadOnlyList<MediaDetails>>>();
        var tokens = new List<CancellationToken>();
        var loader = new MediaDetailsToolTipLoader(owner, (_, token) =>
        {
            tokens.Add(token);
            var read = new TaskCompletionSource<IReadOnlyList<MediaDetails>>();
            reads.Add(read);
            return read.Task;
        });
        var applied = new List<int>();
        try
        {
            Task first = loader.LoadAsync(target, dataContext, firstTooltip, new MediaDetailsSource(null), _ => applied.Add(1));
            target.ToolTip = secondTooltip;
            Task second = loader.LoadAsync(target, dataContext, secondTooltip, new MediaDetailsSource(null), _ => applied.Add(2));
            Assert.True(tokens[0].IsCancellationRequested);
            Assert.False(tokens[1].IsCancellationRequested);
            reads[0].SetResult([Details()]);
            owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            firstTooltip.RaiseEvent(new RoutedEventArgs(ToolTip.ClosedEvent));
            Assert.False(tokens[1].IsCancellationRequested);
            reads[1].SetResult([Details()]);
            owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.True(first.IsCompletedSuccessfully);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Equal(new[] { 2 }, applied);
            // Completed requests detach callbacks before disposing their cancellation sources.
            target.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            secondTooltip.RaiseEvent(new RoutedEventArgs(ToolTip.ClosedEvent));
        }
        finally
        {
            owner.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    });

    private static Window CreateOwner(UIElement target) => new()
    {
        Content = target, Width = 200, Height = 100, WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -4000, Top = -4000, ShowActivated = false, ShowInTaskbar = false,
    };

    private static MediaDetails Details() => new(new MediaDetailsSource(null), string.Empty,
        123, null, null, 0, 0, string.Empty, false);
}
