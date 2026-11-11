using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ElementSelectorBehaviorTests
{
    private static readonly RectD Root = new(40, 30, 500, 280), Panel = new(60, 50, 300, 230), Leaf = new(80, 70, 80, 60);
    private static readonly RectD SiblingPanel = new(380, 50, 130, 230), SiblingLeaf = new(400, 70, 80, 60);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinestFixedDepthPendingOrderAndWindowResetUseActualSelectorInputs(bool native) => StaTestHost.Run(() =>
    {
        using var h = new Harness(native);
        h.Key(1); h.Key(1); h.Key(-1); h.Complete(Root, Panel, Leaf);
        Assert.Equal(Panel, h.Highlight); Assert.Equal(1, h.Depth);
        h.Move(Leaf.Center.Offset(1, 1)); h.Key(1); Assert.Equal(Leaf, h.Highlight);
        h.Key(1); Assert.Equal(Leaf, h.Highlight); Assert.Equal(1, h.Source.Calls);
        h.Move(SiblingLeaf.Center); h.WaitCalls(2); h.Complete(Root, SiblingPanel, SiblingLeaf);
        Assert.Equal(SiblingLeaf, h.Highlight); // Finest follows the new leaf.
        h.Key(-1); Assert.Equal(SiblingPanel, h.Highlight);
        h.Move(Leaf.Center); h.WaitCalls(3); h.Complete(Root, Panel, Leaf);
        Assert.Equal(Panel, h.Highlight); Assert.Equal(1, h.Depth); // Fixed depth stays fixed.
        h.Move(SiblingLeaf.Center); h.WaitCalls(4); h.Complete(Root, SiblingLeaf);
        Assert.Equal(SiblingLeaf, h.Highlight); Assert.Equal(1, h.Depth); // Shorter chain clamps.
        h.Key(-1); Assert.Equal(Root, h.Highlight); h.Key(-1); Assert.Equal(0, h.Depth);
        h.Move(Leaf.Center); Assert.Equal(4, h.Source.Calls);
        h.Key(1); h.WaitCalls(5); h.Complete(Root, Panel, Leaf); Assert.Equal(Leaf, h.Highlight);
        h.OtherWindow(); Assert.Equal(Root, h.Highlight); Assert.Equal(0, h.Depth);
        Assert.Equal(5, h.Source.Calls);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JitterReusesFirstResultAndDifferentTargetRequiresAnotherLookup(bool native) => StaTestHost.Run(() =>
    {
        using var h = new Harness(native); h.Key(1); h.Move(Leaf.Center.Offset(1, 0));
        Assert.False(h.Source.Cancellation.IsCancellationRequested);
        h.Complete(Root, Panel, Leaf); Assert.Equal(Leaf, h.Highlight); Assert.Equal(1, h.Source.Calls);
        h.Key(-1); Assert.Equal(Panel, h.Highlight); Assert.Equal(1, h.Source.Calls);
        h.Move(SiblingLeaf.Center); h.WaitCalls(2); h.Move(Leaf.Center);
        h.CompleteResult(new(ElementGeometryStatus.Success, [new(Root, 1), new(SiblingPanel, 1), new(SiblingLeaf, 1)]), wait: false);
        h.WaitCalls(3); Assert.Equal(Panel, h.Highlight);
        h.Complete(Root, Panel, Leaf); Assert.Equal(Panel, h.Highlight);
    });

    [Theory]
    [InlineData(false, "ko-KR", false)]
    [InlineData(true, "ko-KR", false)]
    [InlineData(false, "en-US", true)]
    [InlineData(true, "en-US", true)]
    public void LabelsReflectEligibilityEmptyResultsAndTwoTimeouts(bool native, string language, bool text) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var h = new Harness(native, text);
        string window = UiText.Get(text ? "CaptureGuide2_ClickWindowText" : "CaptureGuide2_ClickWindow");
        string hint = UiText.Get("CaptureGuide2_ElementWheelHint");
        Assert.Equal(window + hint + "  500 × 280", h.Label());
        h.Key(1); h.Complete(Root); Assert.Equal(window + "  500 × 280", h.Label());
        h.OtherWindow(); Assert.Equal(window + hint + "  500 × 280", h.Label());
        h.Key(1); h.CompleteResult(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut));
        Assert.Equal(window + hint + "  500 × 280", h.Label());
        h.Key(1); h.CompleteResult(ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut));
        Assert.Equal(window + "  500 × 280", h.Label()); h.Key(1); Assert.Equal(3, h.Source.Calls);
        h.OtherWindow(); h.Key(1); h.Complete(Root, Panel, Leaf);
        Assert.Equal(UiText.Get(text ? "CaptureGuide2_ClickElementText" : "CaptureGuide2_ClickElement") + "  80 × 60", h.Label());
        h.OwnWindow(); Assert.Equal(window + "  500 × 280", h.Label()); h.Key(1); Assert.Equal(4, h.Source.Calls);
        h.Move(new PointD(620, 330)); Assert.DoesNotContain(hint, h.Label());
    });

    [Theory]
    [InlineData("ko-KR", "휠", "창 안 요소 선택 (PgDn · PgUp)")]
    [InlineData("en-US", "Wheel", "Select an element in the window (PgDn · PgUp)")]
    public void HelpRowNamesWheelAndBothPageKeys(string language, string key, string label) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var panel = new CaptureKeyPanel(new FontFamily("Segoe UI"), new FontFamily("Consolas"));
        panel.Configure(new CaptureKeyPanelContext(true, true, true, false, false, false, false, false, false, Hotkey.None));
        var row = ((StackPanel)panel.Child).Children.OfType<Grid>().Single(r => ((TextBlock)((Border)r.Children[0]).Child).Text == key);
        Assert.Equal(label, ((TextBlock)row.Children[1]).Text);
        panel.ReleaseResources();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinestFindsButtonInsideCardAndReturnsToPaddingAfterRest(bool native) => StaTestHost.Run(() =>
    {
        using var h = new Harness(native); h.Move(Panel.Center); h.Key(1); h.Complete(Root, Panel);
        h.Move(Leaf.Center); Assert.Equal(Panel, h.Highlight); Assert.Equal(1, h.Source.Calls);
        h.WaitCalls(2); h.Complete(Root, Panel, Leaf); Assert.Equal(Leaf, h.Highlight);
        h.Move(Panel.Center); Assert.Equal(Leaf, h.Highlight); h.WaitCalls(3); h.Complete(Root, Panel);
        Assert.Equal(Panel, h.Highlight); Assert.Equal(1, h.Depth);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedInsideMovementStaysCachedAndForwardIntoFinestRefreshes(bool native) => StaTestHost.Run(() =>
    {
        using var h = new Harness(native); h.Key(1); h.Complete(Root, Panel, Leaf); h.Key(-1);
        h.Move(Leaf.Center.Offset(1, 0)); Harness.Rest();
        Assert.Equal(1, h.Source.Calls); Assert.Equal(Panel, h.Highlight);
        h.Key(1); Assert.Equal(Leaf, h.Highlight); h.WaitCalls(2);
        h.Complete(Root, Panel, Leaf, Leaf.Inflate(-10)); Assert.Equal(Leaf.Inflate(-10), h.Highlight);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultWithSameRectangleAndLabelDoesNotCallSelectorRefresh(bool native) => StaTestHost.Run(() =>
    {
        using var h = new Harness(native); h.Key(1); int beforeFirst = h.CursorReads;
        h.Complete(Root, Panel); Assert.True(h.CursorReads > beforeFirst);
        string label = h.Label(); h.Move(Leaf.Center.Offset(1, 0)); h.WaitCalls(2);
        int beforeSame = h.CursorReads; h.Complete(Root, Panel);
        Assert.Equal(beforeSame, h.CursorReads); Assert.Equal(Panel, h.Highlight); Assert.Equal(label, h.Label());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StepAfterEmptyResultAtAnotherPointQueriesAndRestoresWindowHint(bool native) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR"); using var h = new Harness(native);
        string hint = UiText.Get("CaptureGuide2_ElementWheelHint");
        h.Key(1); h.Complete(Root); Assert.DoesNotContain(hint, h.Label());
        h.Key(1); Assert.Equal(1, h.Source.Calls);
        h.Move(Panel.Center); Harness.Rest(); Assert.Equal(1, h.Source.Calls);
        h.Key(1); h.WaitCalls(2); h.Complete(Root, Panel); Assert.Equal(Panel, h.Highlight);
        h.Key(-1); Assert.Contains(hint, h.Label());
        h.Move(new PointD(50, 40)); h.Key(1); h.WaitCalls(3); h.Complete(Root);
        Assert.DoesNotContain(hint, h.Label()); Assert.Equal(Root, h.Highlight);
    });

    private sealed class Harness : IDisposable
    {
        internal readonly ElementSelectionControllerTests.FakeSource Source = new();
        private readonly NativeCaptureOverlay? _native;
        private readonly CaptureOverlayView? _view;
        private NativeSelectorChrome? _chrome;
        private PointD _point = Leaf.Center;
        internal int CursorReads { get; private set; }
        private PointD ReadPointer() { CursorReads++; return _point; }
        private WindowCandidate _window = new(new IntPtr(88), Root, "", ProcessId: uint.MaxValue);
        internal Harness(bool native, bool text = false)
        {
            if (native)
            {
                _native = ElementSelectorInputTests.Native(Source); _native.ReadCursor = ReadPointer;
                _native.SourceWindows = [_window]; _native.TextCaptureMode = text;
                ElementSelectorInputTests.Ready(_native); _native.RefreshHover();
            }
            else
            {
                _view = new CaptureOverlayView(WindowDetectOverlayTests.Frame(), false)
                { DetectWindows = true, WindowCandidates = [_window], ReadCursor = ReadPointer, PlaceCursor = _ => false,
                    CreateElementSource = () => Source, SimulatedPointerInside = true, TextCaptureMode = text };
                CaptureCueEvidenceTests.UsePalette(_view, CaptureGuidePalette.Resolve());
                _view.InitializePointer(); _view.NotifyFirstFramePresented();
            }
        }
        internal RectD? Highlight => _native?.HoverRegion ?? _view!.HoverRegionForTest;
        internal int Depth => _native?.ElementDepth ?? _view!.ElementDepth;
        internal void Key(int direction)
        {
            Key key = direction > 0 ? System.Windows.Input.Key.PageDown : System.Windows.Input.Key.PageUp;
            if (_native is not null) _native.HandleKey(key, ModifierKeys.None);
            else _view!.HandleKeyDown(WindowDetectOverlayTests.KeyArgs(key), ModifierKeys.None);
        }
        internal void Move(PointD point)
        { _point = point; if (_native is not null) _native.RefreshHover(); else _view!.UpdatePointer(false, false); }
        internal void OtherWindow() { _window = _window with { Handle = _window.Handle + 1 }; SetWindow(); }
        internal void OwnWindow() { _window = _window with { ProcessId = (uint)Environment.ProcessId }; SetWindow(); }
        private void SetWindow()
        { if (_native is not null) _native.SourceWindows = [_window]; else _view!.WindowCandidates = [_window]; Move(_point); }
        internal void WaitCalls(int calls) => WindowDetectOverlayTests.PumpUntil(() => Source.Calls == calls);
        internal void Complete(params RectD[] rects) => CompleteResult(new(ElementGeometryStatus.Success, rects.Select(r => new ElementGeometry(r, 1)).ToArray()));
        internal void CompleteResult(ElementGeometryResult result, bool wait = true)
        {
            bool received = false; var previous = SelectorTimeline.Mark;
            SelectorTimeline.Mark = stage => { previous?.Invoke(stage); if (stage == "element-lookup-result") received = true; };
            try
            {
                Source.Complete(result);
                // Observe this result even if finest mode has queued a newer point's lookup.
                if (wait) WindowDetectOverlayTests.PumpUntil(() => received);
            }
            finally { SelectorTimeline.Mark = previous; }
        }
        internal static void Rest()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            WindowDetectOverlayTests.PumpUntil(() => elapsed.ElapsedMilliseconds >= 100);
        }
        internal string Label()
        {
            if (_native is not null)
            {
                _chrome ??= new NativeSelectorChrome(_native, false, false);
                _chrome.Update(_point, _native.DisplayedRegionForTest); _chrome.FlushHoverTabForTest();
                return _chrome.TargetLabelForTest!;
            }
            _ = CaptureCueEvidenceTests.Render(_view!); _view!.FlushFeedbackForTest(); _ = CaptureCueEvidenceTests.Render(_view);
            return _view.TargetLabelForTest!;
        }
        public void Dispose() { _chrome?.Dispose(); _native?.Close(); _view?.ReleaseResources(); }
    }
}
