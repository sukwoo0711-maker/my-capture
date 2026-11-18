using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ElementSelectionControllerTests
{
    internal static readonly RectD Root = new(-800, 0, 600, 500), Panel = new(-750, 40, 450, 400), Button = new(-700, 80, 100, 60);
    internal static readonly PointD Point = new(-660, 100);
    internal static readonly WindowCandidate Window = new(new IntPtr(123), Root, "", ProcessId: uint.MaxValue);
    private static ElementGeometryResult Chain(params RectD[] rects) => new(ElementGeometryStatus.Success, rects.Select(r => new ElementGeometry(r, 50000)).ToArray());

    [Fact]
    public void HoverAndMovementAtDepthZeroDoNoWork()
    {
        using var h = new Harness(); h.Track(Point); h.Track(Point.Offset(1, 1)); h.Track(Point.Offset(2, 2));
        h.Clock.Advance(1000);
        Assert.Equal(0, h.Created); Assert.Equal(0, h.Source.Calls); Assert.Equal(Root, h.Controller.Highlight);
    }
    [Fact]
    public void FractionsAndPendingNotchesNarrowThenWidenOneLevel()
    {
        using var h = new Harness(); h.Track(Point);
        h.Controller.Wheel(30); h.Controller.Wheel(60);
        Assert.Equal(0, h.Source.Calls);
        h.Controller.Wheel(30); Assert.Equal(Root, h.Controller.Highlight);
        h.Controller.Wheel(120); Assert.Equal(1, h.Source.Calls);
        h.Complete(Chain(Root, Panel, Button));
        Assert.Equal(Button, h.Controller.Highlight); Assert.Equal(2, h.Controller.Depth);
        h.Controller.Wheel(-120); Assert.Equal(Panel, h.Controller.Highlight);
        h.Controller.Step(-1); Assert.Equal(Root, h.Controller.Highlight);
        h.Controller.Step(-1); Assert.Equal(0, h.Controller.Depth); Assert.Equal(1, h.Source.Calls);
    }
    [Fact]
    public void MissingDeeperLevelsClampToNearestAvailableParent()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(8); h.Complete(Chain(Root, Panel));
        Assert.Equal(1, h.Controller.Depth); Assert.Equal(Panel, h.Controller.Highlight);
    }
    [Fact]
    public void MovesInsideHighlightDoNothingAndSiblingUsesDebounceAtSameDepth()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(2); h.Complete(Chain(Root, Panel, Button, Button.Inflate(-10)));
        h.Controller.Step(-1); // This unchanged same-depth contract now explicitly exercises fixed mode.
        h.Track(Point.Offset(3, 3)); h.Clock.Advance(100);
        Assert.Equal(1, h.Source.Calls); Assert.Equal(Button, h.Controller.Highlight);
        var sibling = new RectD(-500, 100, 100, 60);
        h.Track(sibling.Center); h.Clock.Advance(59); Assert.Equal(1, h.Source.Calls);
        h.Clock.Advance(1); Assert.Equal(2, h.Source.Calls);
        h.Complete(Chain(Root, Panel, sibling));
        Assert.Equal(sibling, h.Controller.Highlight); Assert.Equal(2, h.Controller.Depth);
    }
    [Fact]
    public void PendingResultSurvivesJitterInsideItsDeepestRectangle()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1);
        h.Track(Point.Offset(1, 0)); h.Clock.Advance(20); Assert.False(h.Source.Cancellation.IsCancellationRequested);
        h.Complete(Chain(Root, Panel, Button)); Assert.Equal(Button, h.Controller.Highlight);
        h.Controller.Step(-1); // The old-point result is immediate; fixed mode cancels its finer-point refresh.
        h.Clock.Advance(60); Assert.Equal(1, h.Source.Calls);
    }
    [Fact]
    public void LeavingWindowResetsDepthAndDropsLateResponse()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(2);
        h.Controller.Track(Window with { Handle = new IntPtr(456) }, Root, Root, Point);
        h.Complete(Chain(Root, Panel, Button));
        Assert.Equal(0, h.Controller.Depth); Assert.Equal(Root, h.Controller.Highlight);
        h.Clock.Advance(1000); Assert.Equal(1, h.Source.Calls);
    }
    [Fact]
    public void SelectionStartDropsLateResultWithoutChangingClickRectangle()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1);
        RectD click = h.Controller.Highlight; h.Controller.Stop(); h.Complete(Chain(Root, Panel));
        Assert.Equal(click, h.Controller.Highlight); Assert.Equal(Root, click); Assert.Equal(1, h.Source.Calls);
    }
    [Fact]
    public void CloseCancelsDisposesSourceAndIgnoresLateCompletion()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Controller.Dispose();
        Assert.True(h.Source.Cancellation.IsCancellationRequested); Assert.True(h.Source.Disposed);
        h.Complete(Chain(Root, Panel)); Assert.Equal(default, h.Controller.Highlight); Assert.Equal(0, h.Changes);
    }
    [Fact]
    public void FirstTimeoutAllowsRetryAndSecondDisablesOnlyThatWindowForTheSession()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1);
        h.Complete(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut));
        Assert.Equal(Root, h.Controller.Highlight); Assert.Equal(0, h.Controller.Depth);
        Assert.True(h.Controller.ShowHint);
        h.Controller.Reset(); h.Track(Point); h.Controller.Step(1); Assert.Equal(2, h.Source.Calls);
        h.Complete(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut));
        Assert.False(h.Controller.ShowHint);
        h.Controller.Reset(); h.Track(Point); h.Controller.Step(1); Assert.Equal(2, h.Source.Calls);
        h.Controller.Track(Window with { Handle = new IntPtr(456) }, Root, Root, Point);
        h.Controller.Step(1); Assert.Equal(3, h.Source.Calls);
    }
    [Fact]
    public void TimeoutAfterPointerMoveRestoresRootWithoutAutomaticRetry()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Track(Point.Offset(5, 5));
        h.Complete(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut)); h.Clock.Advance(1000);
        Assert.Equal(0, h.Controller.Depth); Assert.Equal(Root, h.Controller.Highlight); Assert.Equal(1, h.Source.Calls);
    }
    [Theory]
    [InlineData(ElementGeometryStatus.Unavailable)]
    [InlineData(ElementGeometryStatus.Cancelled)]
    public void FailureKeepsWindowWithoutError(ElementGeometryStatus status)
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(ElementGeometryResult.Empty(status));
        Assert.Equal(Root, h.Controller.Highlight); Assert.Equal(0, h.Controller.Depth);
    }
    [Fact]
    public void OwnProcessIsNeverQueried()
    {
        using var h = new Harness(); h.Controller.Track(Window with { ProcessId = (uint)Environment.ProcessId }, Root, Root, Point);
        h.Controller.Step(1); Assert.Equal(0, h.Created); Assert.Equal(0, h.Source.Calls);
    }
    [Fact]
    public void FiltersEmptyOffscreenOversizedDuplicateAndNonFiniteLevelsInPhysicalSpace()
    {
        RectD frame = new(-780, 0, 580, 450);
        RectD clippedRoot = ElementGeometryBounds.Intersect(Root, frame);
        RectD panel = new(-750.2, 40.4, 450.1, 380.2), rounded = new(-751, 40, 451, 381);
        var input = Chain(Root, RectD.Empty, new(double.NaN, 0, 10, 10), new(900, 900, 20, 20),
            new(-900, -30, 900, 700), clippedRoot, clippedRoot.Inflate(-1), panel, panel.Inflate(-1), Button);
        var chain = ElementSelectionController.Filter(Root, frame, Point, input.Chain);
        Assert.Equal(new[] { clippedRoot, rounded, Button }, chain);
    }

    [Fact]
    public void FirstForwardChoosesDeepestAndPendingCommandsKeepTheirOrder()
    {
        using var h = new Harness(); h.Track(Point);
        h.Controller.Step(1); h.Controller.Step(1); h.Controller.Step(-1);
        h.Complete(Chain(Root, Panel, Button));
        Assert.Equal(Panel, h.Controller.Highlight); Assert.False(h.Controller.Finest);
        h.Controller.Step(1); Assert.Equal(Button, h.Controller.Highlight); Assert.True(h.Controller.Finest);
        h.Controller.Step(1); Assert.Equal(Button, h.Controller.Highlight); Assert.Equal(1, h.Source.Calls);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(40, 20)]
    [InlineData(80, 0)]
    public void DifferentTargetRetainsHighlightAndWaitsOnlyRemainingDebounce(int elapsed, int remaining)
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1);
        var sibling = new RectD(-500, 100, 100, 60);
        h.Track(sibling.Center); h.Clock.Advance(elapsed);
        h.Complete(Chain(Root, Panel, Button)); Assert.Equal(Root, h.Controller.Highlight);
        if (remaining > 0)
        {
            h.Clock.Advance(remaining - 1); Assert.Equal(1, h.Source.Calls);
            h.Clock.Advance(1);
        }
        Assert.Equal(2, h.Source.Calls);
        h.Complete(Chain(Root, Panel, sibling)); Assert.Equal(sibling, h.Controller.Highlight);
    }

    [Fact]
    public void PendingFixedDepthReusesContainingPrefixAfterPointerMove()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Controller.Step(-1);
        h.Track(new PointD(-500, 150)); h.Complete(Chain(Root, Panel, Button));
        Assert.Equal(Panel, h.Controller.Highlight); Assert.False(h.Controller.Finest); Assert.Equal(1, h.Source.Calls);
        h.Controller.Step(-1); h.Controller.Step(1); // Root to finest needs the new point's leaf.
        Assert.Equal(2, h.Source.Calls);
    }

    [Fact]
    public void CacheCanWidenAfterMovingWithinHighlightAndFixedDepthSurvivesSiblingChanges()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root, Panel, Button));
        h.Track(Point.Offset(2, 2)); h.Controller.Step(-1);
        Assert.Equal(Panel, h.Controller.Highlight); Assert.Equal(1, h.Source.Calls);
        h.Track(new PointD(-280, 100)); h.Clock.Advance(60);
        RectD siblingPanel = new(-295, 40, 80, 400), siblingLeaf = new(-290, 80, 30, 50);
        h.Complete(Chain(Root, siblingPanel, siblingLeaf));
        Assert.Equal(siblingPanel, h.Controller.Highlight); Assert.False(h.Controller.Finest);
        h.Controller.Step(1); Assert.Equal(siblingLeaf, h.Controller.Highlight); Assert.True(h.Controller.Finest);
    }

    [Fact]
    public void EmptyCompletedLookupHidesHintUntilLeavingWindowButPartialDoesNot()
    {
        using var h = new Harness(); h.Track(Point); Assert.True(h.Controller.ShowHint);
        h.Controller.Step(1); h.Complete(Chain(Root) with { Partial = true }); Assert.True(h.Controller.ShowHint);
        h.Controller.Step(1); Assert.Equal(2, h.Source.Calls); h.Complete(Chain(Root));
        Assert.False(h.Controller.ShowHint);
        h.Controller.Reset(); h.Track(Point); Assert.True(h.Controller.ShowHint);
    }

    [Theory]
    [InlineData(1.0, 4)]
    [InlineData(1.5, 6)]
    [InlineData(2.0, 8)]
    public void DuplicateToleranceUsesWindowMonitorScale(double scale, double pixels)
    {
        var result = ElementSelectionController.Filter(Root, Root, Point,
            Chain(Root.Inflate(-pixels), Root.Inflate(-pixels - 1), Button).Chain, scale);
        Assert.Equal(new[] { Root, Root.Inflate(-pixels - 1), Button }, result);
    }

    [Fact]
    public void PartialResultSelectsKnownLeafWithoutCountingAsTimeout()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1);
        h.Complete(Chain(Root, Panel) with { Partial = true });
        Assert.Equal(Panel, h.Controller.Highlight); Assert.True(h.Controller.Finest); Assert.True(h.Controller.CanStep);
        h.Controller.Step(-1); h.Controller.Reset(); h.Track(Point); h.Controller.Step(1);
        h.Complete(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut));
        Assert.True(h.Controller.CanStep);
    }

    [Fact]
    public void FinestRestInsideHighlightFindsChildAndReturningToPaddingWidensAgain()
    {
        using var h = new Harness(); PointD padding = Panel.Center;
        h.Track(padding); h.Controller.Step(1); h.Complete(Chain(Root, Panel));
        h.Track(Button.Center); h.Clock.Advance(59);
        Assert.Equal(1, h.Source.Calls); Assert.Equal(Panel, h.Controller.Highlight);
        h.Track(Button.Center.Offset(1, 0)); h.Clock.Advance(59); Assert.Equal(1, h.Source.Calls);
        h.Clock.Advance(1); Assert.Equal(2, h.Source.Calls);
        h.Complete(Chain(Root, Panel, Button)); Assert.Equal(Button, h.Controller.Highlight);
        h.Track(padding); h.Clock.Advance(60); Assert.Equal(3, h.Source.Calls);
        h.Complete(Chain(Root, Panel)); Assert.Equal(Panel, h.Controller.Highlight); Assert.True(h.Controller.Finest);
    }

    [Fact]
    public void FixedLevelInsideMovementDoesNotScheduleLookupOrNotification()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root, Panel, Button));
        h.Controller.Step(-1); int changes = h.Changes;
        h.Track(Panel.Center); h.Clock.Advance(100);
        Assert.Equal(1, h.Source.Calls); Assert.Equal(changes, h.Changes); Assert.Equal(Panel, h.Controller.Highlight);
        Assert.False(h.Controller.Pending); Assert.False(h.Controller.Finest);
    }

    [Fact]
    public void UnchangedFinestResultAndRepeatedForwardDoNotNotify()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root, Panel));
        int changes = h.Changes; Assert.Equal(1, changes);
        h.Track(Point.Offset(1, 0)); h.Clock.Advance(60); Assert.Equal(2, h.Source.Calls);
        h.Complete(Chain(Root, Panel)); Assert.Equal(changes, h.Changes); Assert.False(h.Controller.Pending);
        h.Controller.Step(1); Assert.Equal(changes, h.Changes); Assert.Equal(2, h.Source.Calls);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(80)]
    public void ForwardToFinestShowsCachedLeafAndRefreshesOnlyAfterRest(int elapsed)
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root, Panel, Button));
        h.Controller.Step(-1); h.Track(Point.Offset(1, 0)); h.Clock.Advance(elapsed); Assert.Equal(1, h.Source.Calls);
        h.Controller.Step(1); Assert.Equal(Button, h.Controller.Highlight); Assert.True(h.Controller.Finest);
        if (elapsed < 60)
        {
            h.Clock.Advance(60 - elapsed - 1); Assert.Equal(1, h.Source.Calls);
            h.Clock.Advance(1);
        }
        Assert.Equal(2, h.Source.Calls);
        h.Complete(Chain(Root, Panel, Button, Button.Inflate(-10)));
        Assert.Equal(Button.Inflate(-10), h.Controller.Highlight);
    }

    [Fact]
    public void EmptyChainOnlyAnswersItsLookupPointAndSuccessfulLookupRestoresHint()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root));
        Assert.False(h.Controller.ShowHint); int changes = h.Changes;
        h.Controller.Step(1); Assert.Equal(1, h.Source.Calls); Assert.Equal(changes, h.Changes);
        h.Track(Button.Center); h.Clock.Advance(100); Assert.Equal(1, h.Source.Calls);
        h.Controller.Step(1); Assert.Equal(2, h.Source.Calls); h.Complete(Chain(Root, Panel, Button));
        Assert.True(h.Controller.ShowHint); Assert.Equal(Button, h.Controller.Highlight);
        h.Track(Point); h.Clock.Advance(60); h.Complete(Chain(Root));
        Assert.False(h.Controller.ShowHint); Assert.Equal(Root, h.Controller.Highlight);
        h.Track(Button.Center); h.Controller.Step(1); changes = h.Changes; h.Complete(Chain(Root));
        Assert.Equal(changes, h.Changes); // Another empty point changes neither rectangle nor hint.
    }

    [Fact]
    public void ReturningToExactCachedPointCancelsUnneededFinestRefresh()
    {
        using var h = new Harness(); h.Track(Point); h.Controller.Step(1); h.Complete(Chain(Root, Panel));
        h.Track(Point.Offset(2, 0)); h.Clock.Advance(30); h.Track(Point); h.Clock.Advance(100);
        Assert.Equal(1, h.Source.Calls); Assert.False(h.Controller.Pending);
    }

    private sealed class Harness : IDisposable
    {
        internal readonly FakeSource Source = new(); internal readonly Clock Clock = new();
        internal readonly ElementSelectionController Controller;
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _posts = new();
        internal int Created, Changes;
        internal Harness() => Controller = new(() => Changes++, () => { Created++; return Source; }, action => _posts.Add(action), Clock.Schedule, () => Clock.Now);
        internal void Track(PointD point) => Controller.Track(Window, Root, Root, point);
        internal void Complete(ElementGeometryResult result)
        {
            Source.Complete(result);
            Assert.True(_posts.TryTake(out Action? post, TimeSpan.FromSeconds(2)));
            post!();
        }
        public void Dispose() => Controller.Dispose();
    }
    internal sealed class FakeSource : IElementGeometrySource
    {
        private TaskCompletionSource<ElementGeometryResult>? _completion;
        internal int Calls; internal bool Disposed; internal CancellationToken Cancellation;
        public Task<ElementGeometryResult> ReadAsync(WindowCandidate window, PointD point, CancellationToken cancellation)
        {
            Calls++; Cancellation = cancellation;
            _completion = new(); return _completion.Task;
        }
        internal void Complete(ElementGeometryResult result) => _completion!.SetResult(result);
        public void Dispose() => Disposed = true;
    }
    private sealed class Clock
    {
        private int _now; private readonly List<Due> _queue = [];
        internal TimeSpan Now => TimeSpan.FromMilliseconds(_now);
        internal IDisposable Schedule(TimeSpan delay, Action action)
        { var due = new Due(_now + (int)delay.TotalMilliseconds, action); _queue.Add(due); return due; }
        internal void Advance(int milliseconds)
        {
            _now += milliseconds;
            foreach (Due due in _queue.Where(d => d.At <= _now && !d.Cancelled).ToArray()) { due.Dispose(); due.Action(); }
        }
        private sealed class Due(int at, Action action) : IDisposable
        { internal int At = at; internal Action Action = action; internal bool Cancelled; public void Dispose() => Cancelled = true; }
    }
}
