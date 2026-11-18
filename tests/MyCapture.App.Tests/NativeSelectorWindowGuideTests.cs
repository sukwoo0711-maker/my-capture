using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The native selector's guide chip and magnifier in window selection: the chip names the click as well
/// as the drag, and the magnifier waits until a drag leaves the click.
/// </summary>
public sealed class NativeSelectorWindowGuideTests
{
    private static readonly WindowCandidate Notes = new(new IntPtr(0x3401), new RectD(40, 30, 200, 150), "Notes");

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void WindowSelectionGuideNamesClickDragAndEscInAChipThatFitsThem(string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        PointD pointer = new(80, 80);
        using NativeCaptureOverlay owner = Owner(() => pointer, magnifier: true, detectWindows: true);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Update(pointer, owner.DisplayedRegionForTest);

        string expected = UiText.Get("CaptureGuide2_ClickWindow") + "  ·  " + UiText.Get("CaptureGuide2_DragCapture")
            + "  ·  " + UiText.Get("CaptureGuide2_Esc");
        Assert.True(owner.GuidesWindowClick);
        Assert.Equal(expected, chrome.GuideTextForTest);
        Window guide = Assert.IsType<Window>(chrome.GuideWindowForTest);
        double width = chrome.GuideWidthForTest;
        Assert.Equal(width, chrome.LogicalSizeForTest(guide).Width);
        Assert.Equal(NativeSelectorHint.ChipHeight, chrome.LogicalSizeForTest(guide).Height);
        var panel = Assert.IsType<Border>(guide.Content);
        var hint = Assert.IsType<NativeSelectorHint>(panel.Child);
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double needed = hint.DesiredSize.Width + panel.Padding.Left + panel.Padding.Right
            + panel.BorderThickness.Left + panel.BorderThickness.Right;
        Assert.True(needed <= width, $"{culture}: the guide needs {needed:0.#} DIPs in a {width:0.#} DIP chip.");
        Assert.True(width >= needed);
        if (culture == "en-US") Assert.True(width > MeasureGuideWidth(chrome, DefaultGuide),
            "The English window guide needs more width than the region guide.");

        // A press and a drag keep the same chip, so it is not rebuilt under the first drag samples.
        owner.BeginSelection(pointer);
        pointer = new PointD(180, 150);
        owner.MoveSelection(pointer);
        chrome.Update(pointer, owner.SelectionRectangle);
        Assert.True(owner.Selection.HasValue);
        Assert.Equal(expected, chrome.GuideTextForTest);
        Assert.Equal(width, chrome.GuideWidthForTest);
    });

    // The retained chrome keeps the hint's words between selectors: the next bind sets them before any
    // window is shown, and the same guide again keeps the same key caps instead of rebuilding them.
    [Fact]
    public void ReusedChromeShowsTheNextSelectorsGuideAndKeepsKeyCapsOfTheSameGuide() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        PointD pointer = new(80, 80);
        string windowGuide = UiText.Get("CaptureGuide2_ClickWindow") + "  ·  " + UiText.Get("CaptureGuide2_DragCapture")
            + "  ·  " + UiText.Get("CaptureGuide2_Esc");
        string regionGuide = UiText.Get("CaptureGuide2_DragCapture") + "  ·  " + UiText.Get("CaptureGuide2_Esc");
        using NativeCaptureOverlay first = Owner(() => pointer, magnifier: true, detectWindows: true);
        using NativeCaptureOverlay second = Owner(() => pointer, magnifier: true, detectWindows: true);
        using NativeCaptureOverlay region = Owner(() => pointer, magnifier: true, detectWindows: false);
        using var chrome = new NativeSelectorChrome(first, true, false);
        chrome.Update(pointer, first.DisplayedRegionForTest);
        Assert.Equal(windowGuide, chrome.GuideTextForTest);
        Border[] caps = chrome.HintForTest.KeycapBordersForTest.ToArray();
        Assert.NotEmpty(caps);
        double width = chrome.GuideWidthForTest;

        chrome.Detach();
        chrome.Bind(second, requireExclusion: false);
        Assert.All(chrome.Windows, window => Assert.False(window.IsVisible));
        Assert.Equal(windowGuide, chrome.GuideTextForTest);
        Assert.Equal(caps, chrome.HintForTest.KeycapBordersForTest);
        Assert.Equal(width, chrome.GuideWidthForTest);
        Assert.Equal(width, chrome.LogicalSizeForTest(chrome.GuideWindowForTest!).Width);

        chrome.Detach();
        chrome.Bind(region, requireExclusion: false);
        Assert.All(chrome.Windows, window => Assert.False(window.IsVisible));
        Assert.Equal(regionGuide, chrome.GuideTextForTest);
        Assert.Equal(MeasureGuideWidth(chrome, DefaultGuide), chrome.GuideWidthForTest);
        Assert.Equal(chrome.GuideWidthForTest, chrome.LogicalSizeForTest(chrome.GuideWindowForTest!).Width);
        chrome.Detach();
    });

    [Fact]
    public void RegionAndScrollGuidesKeepTheUsualChip() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        PointD pointer = new(80, 80);
        using NativeCaptureOverlay region = Owner(() => pointer, magnifier: true, detectWindows: false);
        using (var chrome = new NativeSelectorChrome(region, true, false))
        {
            chrome.Update(pointer, region.DisplayedRegionForTest);
            Assert.False(region.GuidesWindowClick);
            Assert.Equal(UiText.Get("CaptureGuide2_DragCapture") + "  ·  " + UiText.Get("CaptureGuide2_Esc"),
                chrome.GuideTextForTest);
            Assert.Equal(MeasureGuideWidth(chrome, DefaultGuide), chrome.GuideWidthForTest);
        }

        using NativeCaptureOverlay scroll = Owner(() => pointer, magnifier: true, detectWindows: true);
        using var scrollChrome = new NativeSelectorChrome(scroll, true, false);
        scrollChrome.Update(pointer, scroll.DisplayedRegionForTest);
        Assert.NotEqual(MeasureGuideWidth(scrollChrome, DefaultGuide), scrollChrome.GuideWidthForTest);
        scroll.SelectMode(CaptureSelectorMode.Scroll);
        Assert.Equal(CaptureSelectorMode.Scroll, scroll.Mode);
        scrollChrome.Update(pointer, scroll.DisplayedRegionForTest);
        Assert.False(scroll.GuidesWindowClick);
        Assert.Equal(UiText.Get("CaptureGuide2_ClickScroll") + "  ·  " + UiText.Get("CaptureGuide2_Esc"),
            scrollChrome.GuideTextForTest);
        Assert.Equal(MeasureGuideWidth(scrollChrome, UiText.Get("CaptureGuide2_ClickScroll") + "  ·  "
            + UiText.Get("CaptureGuide2_Esc")), scrollChrome.GuideWidthForTest);
    });

    [Fact]
    public void WindowSelectionMagnifierWaitsUntilADragLeavesTheClick() => StaTestHost.Run(() =>
    {
        PointD pointer = new(80, 80);
        using NativeCaptureOverlay owner = Owner(() => pointer, magnifier: true, detectWindows: true);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        Assert.Equal(Notes.ScreenBounds, owner.HoverRegion);
        Assert.False(owner.MagnifierShown);
        Assert.True(chrome.IsMagnifierParkedForTest);
        Assert.True(chrome.BoundsForTest(chrome.Windows.Last()).Right <= owner.ScreenBounds.Left);

        // Within the click distance the press still takes the highlighted window.
        owner.BeginSelection(pointer);
        owner.MoveSelection(new PointD(81, 81));
        chrome.Update(pointer, owner.SelectionRectangle);
        Assert.Null(owner.Selection);
        Assert.False(owner.MagnifierShown);
        Assert.True(chrome.IsMagnifierParkedForTest);

        pointer = new PointD(180, 150);
        owner.MoveSelection(pointer);
        chrome.Update(pointer, owner.SelectionRectangle);
        Assert.NotNull(owner.Selection);
        Assert.True(owner.MagnifierShown);
        Assert.False(chrome.IsMagnifierParkedForTest);
        RectD shown = chrome.BoundsForTest(chrome.Windows.Last());
        Assert.True(owner.ScreenBounds.Contains(new PointD(shown.Left, shown.Top)));
    });

    [Fact]
    public void ScrollModeParksTheMagnifierAndTheOtherModesKeepTheirRules() => StaTestHost.Run(() =>
    {
        PointD pointer = new(320, 180);
        using NativeCaptureOverlay owner = Owner(() => pointer, magnifier: true, detectWindows: false);
        owner.CopyColorText = _ => Task.FromResult(true);
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        // Region selection shows it before a drag, as before.
        Assert.True(owner.MagnifierShown);
        Assert.False(chrome.IsMagnifierParkedForTest);

        owner.SelectMode(CaptureSelectorMode.Scroll);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        Assert.False(owner.MagnifierShown);
        Assert.True(chrome.IsMagnifierParkedForTest);

        owner.SelectMode(CaptureSelectorMode.Color);
        chrome.Update(pointer, owner.DisplayedRegionForTest);
        Assert.True(owner.MagnifierShown);
        Assert.False(chrome.IsMagnifierParkedForTest);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheSettingDecidesOutsideTheColourModeAndRecordingSelectionFollowsIt(bool magnifier, bool recording) =>
        StaTestHost.Run(() =>
        {
            PointD pointer = new(80, 80);
            using NativeCaptureOverlay owner = Owner(() => pointer, magnifier, detectWindows: true);
            owner.RecordingSelection = recording;
            owner.RefreshHover();
            // Recording selection never takes a window by click, so it follows the setting from the start.
            Assert.Equal(magnifier && recording, owner.MagnifierShown);
            owner.BeginSelection(pointer);
            owner.MoveSelection(new PointD(180, 150));
            Assert.NotNull(owner.Selection);
            Assert.Equal(magnifier, owner.MagnifierShown);
        });

    private static string DefaultGuide => UiText.Get("CaptureGuide2_DragCapture") + "  ·  " + UiText.Get("CaptureGuide2_Esc");

    private static double MeasureGuideWidth(NativeSelectorChrome chrome, string text)
    {
        double scale = chrome.ScaleForTest(chrome.GuideWindowForTest!);
        var hint = new NativeSelectorHint(CaptureGuidePalette.Resolve()) { Outlined = chrome.HintForTest.Outlined };
        hint.SetScale(scale);
        hint.Show(text);
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(hint.DesiredSize.Width * scale - 0.0001) / scale;
    }

    private static NativeCaptureOverlay Owner(Func<PointD> pointer, bool magnifier, bool detectWindows)
    {
        FrozenFrame frame = WindowDetectOverlayTests.Frame();
        MonitorInfo[] monitors = [new("fixture", frame.ScreenBounds, frame.ScreenBounds, 96, true)];
        var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, magnifier)
        {
            ReadCursor = pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
            SourceWindows = [Notes], DetectWindows = detectWindows, OffersOtherActions = true,
            ReadClock = () => TimeSpan.FromSeconds(10),
        };
        owner.AttachFrame(frame);
        owner.PreparePointerFeedback();
        owner.UpdatePointer(false, false);
        owner.RefreshHover();
        return owner;
    }
}
