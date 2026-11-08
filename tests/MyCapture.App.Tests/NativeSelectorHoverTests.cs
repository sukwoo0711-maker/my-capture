using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorHoverTests
{
    [Fact]
    public void RetainedTabClearsItsActualDrawingBeforeTheNextOwnersFirstRender() => StaTestHost.Run(() =>
    {
        using NativeCaptureOverlay first = HoverOwner(new PointD(80, 80),
            new WindowCandidate(new IntPtr(32), new RectD(40, 30, 200, 150), "Notes"));
        using NativeCaptureOverlay second = HoverOwner(new PointD(500, 100),
            new WindowCandidate(new IntPtr(33), new RectD(400, 40, 180, 140), "Chat"));
        using var chrome = new NativeSelectorChrome(first, false, false);
        chrome.Update(first.EffectivePointer, first.DisplayedRegionForTest);
        chrome.FlushHoverTabForTest();
        var drawing = chrome.HoverTabDrawingForTest;
        var windows = chrome.Windows.ToArray();
        Assert.NotEmpty(drawing.Children);
        string? firstLabel = chrome.TargetLabelForTest;
        Assert.EndsWith("200 × 150", firstLabel, StringComparison.Ordinal);
        Assert.NotNull(chrome.TargetTabForTest);

        chrome.Detach();
        Assert.Same(drawing, chrome.HoverTabDrawingForTest);
        Assert.Empty(drawing.Children);
        Assert.Null(chrome.TargetLabelForTest);
        Assert.Null(chrome.TargetTabForTest);
        Assert.False(chrome.HasPendingHoverTabForTest);

        chrome.Bind(second, requireExclusion: false);
        Assert.Equal(windows, chrome.Windows);
        Assert.All(chrome.Windows, window => { Assert.False(window.IsVisible); Assert.False(window.ShowActivated); });
        Assert.Same(drawing, chrome.HoverTabDrawingForTest);
        Assert.Empty(drawing.Children);
        Assert.Null(chrome.TargetLabelForTest);
        Assert.Null(chrome.TargetTabForTest);
        Assert.True(chrome.HasPendingHoverTabForTest);

        chrome.FlushHoverTabForTest();
        Assert.Same(drawing, chrome.HoverTabDrawingForTest);
        Assert.NotEmpty(drawing.Children);
        Assert.EndsWith("180 × 140", chrome.TargetLabelForTest, StringComparison.Ordinal);
        Assert.NotEqual(firstLabel, chrome.TargetLabelForTest);
        Assert.NotNull(chrome.TargetTabForTest);
        Assert.False(chrome.HasPendingHoverTabForTest);
    });

    [Fact]
    public void MovingWithinTheSameWindowDoesNotScheduleOrRebuildItsVisibleTab() => StaTestHost.Run(() =>
    {
        FrozenFrame frame = WindowDetectOverlayTests.Frame();
        PointD pointer = new(80, 80);
        WindowCandidate target = new(new IntPtr(32), new RectD(40, 30, 200, 150), "Notes");
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        using var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            SourceWindows = [target], DetectWindows = true, ReadClock = () => TimeSpan.FromSeconds(10),
        };
        owner.AttachFrame(frame);
        owner.PreparePointerFeedback();
        owner.UpdatePointer(false, false);
        owner.RefreshHover();
        using var chrome = new NativeSelectorChrome(owner, false, false);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        chrome.FlushHoverTabForTest();
        Assert.True(chrome.IsReadoutVisibleForTest);
        Assert.NotNull(chrome.TargetTabForTest);
        Assert.NotEmpty(chrome.HoverTabDrawingForTest.Children);
        object label = Assert.IsType<System.Windows.Media.FormattedText>(chrome.HoverLabelLayoutForTest);
        int renders = chrome.HoverTabRenderCountForTest;
        int layouts = chrome.HoverTabLayoutCountForTest;
        Assert.True(renders > 0);
        Assert.True(layouts > 0);
        Assert.False(chrome.HasPendingHoverTabForTest);

        for (int index = 1; index <= 20; index++)
        {
            pointer = new PointD(80 + index, 80 + index);
            owner.UpdatePointer(false, false);
            owner.RefreshHover();
            chrome.Update(pointer, owner.DisplayedRegionForTest);
            Assert.False(chrome.HasPendingHoverTabForTest);
            Assert.Equal(renders, chrome.HoverTabRenderCountForTest);
            Assert.Equal(layouts, chrome.HoverTabLayoutCountForTest);
            Assert.Same(label, chrome.HoverLabelLayoutForTest);
        }
        chrome.FlushHoverTabForTest();
        Assert.Equal(renders, chrome.HoverTabRenderCountForTest);
        Assert.Equal(layouts, chrome.HoverTabLayoutCountForTest);
        Assert.Same(label, chrome.HoverLabelLayoutForTest);
    });

    private static NativeCaptureOverlay HoverOwner(PointD pointer, WindowCandidate target)
    {
        FrozenFrame frame = WindowDetectOverlayTests.Frame();
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
        {
            ReadCursor = () => pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            SourceWindows = [target], DetectWindows = true, ReadClock = () => TimeSpan.FromSeconds(10),
        };
        owner.AttachFrame(frame);
        owner.PreparePointerFeedback();
        owner.UpdatePointer(false, false);
        owner.RefreshHover();
        return owner;
    }
}
