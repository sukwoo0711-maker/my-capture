using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class CaptureKeyPanelTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ko-KR", 12)]
    [InlineData("en-US", 11)]
    public void LocalizedPointerActionsUseCaptionWhileLatinKeysUseMicro(string language, double pointerSize) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var panel = new CaptureKeyPanel(ThemeFonts.Ui, new FontFamily("Consolas"));
        panel.Configure(new CaptureKeyPanelContext(true, true, true, false, false, false, false, false, false, Hotkey.None));
        var caps = ((StackPanel)panel.Child).Children.OfType<Grid>()
            .Select(row => (TextBlock)((Border)row.Children[0]).Child).ToArray();
        Assert.Equal(pointerSize, caps.Single(cap => cap.Text == UiText.Get("CaptureKeyPanel.Drag")).FontSize);
        Assert.Equal(pointerSize, caps.Single(cap => cap.Text == UiText.Get("CaptureKeyPanel.Click")).FontSize);
        Assert.Equal(TypeScale.Micro, caps.Single(cap => cap.Text == "F1").FontSize);
        panel.ReleaseResources();
    });

    [Theory]
    [InlineData("confirm")]
    [InlineData("cancel")]
    [InlineData("action")]
    [InlineData("release")]
    public void TerminalViewEventsReleasePanelRowsControllerAndPeersBeforeHandoff(string terminal) => StaTestHost.Run(() =>
    {
        var view = NewView();
        try
        {
            view.OffersOtherActions = true;
            var references = BuildPanelReferences(view);
            int callbacks = 0;
            void VerifyHandoff() { AssertPanelReleased(view); callbacks++; }
            view.SelectionConfirmed += (_, _) => VerifyHandoff();
            view.CancelRequested += (_, _) => VerifyHandoff();
            view.ActionRequested += (_, _) => VerifyHandoff();
            switch (terminal)
            {
                case "confirm":
                    view.HandleKeyDown(Overlay.KeyArgs(Key.A), ModifierKeys.Control);
                    view.HandleKeyDown(Overlay.KeyArgs(Key.Enter), ModifierKeys.None);
                    break;
                case "cancel": view.HandleKeyDown(Overlay.KeyArgs(Key.Escape), ModifierKeys.None); break;
                case "action": view.ModeBarForTest!.RecordKindButton.IsChecked = true; break;
                case "release": view.ReleaseResources(); break;
            }
            Assert.Equal(terminal == "release" ? 0 : 1, callbacks);
            AssertPanelReleased(view);
            view.NotifyFirstFramePresented();
            AssertPanelReleased(view);
            AssertCollected(references);
            GC.KeepAlive(view);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("capture")]
    [InlineData("geometry")]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("action")]
    public void WindowTerminalPathsReleasePanelWhileTheWindowIsStillRetained(string terminal) => StaTestHost.Run(() =>
    {
        var window = new CaptureOverlayWindow(Desktop(), false, showMagnifier: false);
        var view = Assert.IsType<CaptureOverlayView>(window.Content);
        if (terminal == "action") { window.ShowModeBar = true; window.OffersOtherActions = true; }
        try
        {
            var references = BuildPanelReferences(view);
            int callbacks = 0;
            void VerifyHandoff() { AssertPanelReleased(view); callbacks++; }
            window.SelectionCompleted += (_, _) => VerifyHandoff();
            window.SelectionCancelled += (_, _) => VerifyHandoff();
            window.ActionRequested += (_, _) => VerifyHandoff();
            if (terminal == "geometry") window.GeometrySelectionCompleted = _ => VerifyHandoff();
            if (terminal is "capture" or "geometry") window.CompleteSelection(new RectD(100, 100, 200, 150));
            else if (terminal == "cancel")
                typeof(CaptureOverlayWindow).GetMethod("Cancel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            else if (terminal == "action") view.ModeBarForTest!.RecordKindButton.IsChecked = true;
            else window.Close();
            Assert.Equal(terminal == "close" ? 0 : 1, callbacks);
            AssertPanelReleased(view);
            AssertCollected(references);
            GC.KeepAlive(window);
            GC.KeepAlive(view);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheNextSelectorRebuildsOnlyAfterItsOwnFirstFrame() => StaTestHost.Run(() =>
    {
        var first = NewView();
        var second = NewView();
        try
        {
            var references = BuildPanelReferences(first);
            first.HandleKeyDown(Overlay.KeyArgs(Key.Escape), ModifierKeys.None);
            AssertPanelReleased(first);
            second.ShowKeyPanel = true;
            Render(second);
            Assert.False(second.FirstFramePresentedForTest);
            Assert.Null(second.KeyPanelForTest);
            second.NotifyFirstFramePresented();
            Assert.NotNull(second.KeyPanelForTest);
            Assert.True(second.FirstFramePresentedForTest);
            AssertCollected(references);
            GC.KeepAlive(first);
        }
        finally { first.ReleaseResources(); second.ReleaseResources(); }
    });

    [Fact]
    public void NoKeyPanelProbeVariantSkipsAutomaticAndExplicitPanelCreation() => StaTestHost.Run(() =>
    {
        bool previous = CaptureOverlayView.SuppressKeyPanelForProbe;
        var view = NewView();
        try
        {
            var variants = MyCapture.App.Diagnostics.ResidentProbeOptions.ReadVariants(["--resident-variant", "no-key-panel"]);
            CaptureOverlayView.SuppressKeyPanelForProbe = variants.Contains("no-key-panel");
            view.ClaimKeyPanel = _ => throw new InvalidOperationException("The no-panel probe must not consume a showing.");
            view.ReadPasteToScreenHotkey = () => throw new InvalidOperationException("The no-panel probe must not prepare panel content.");
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            view.ModeBarForTest!.HelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            view.DismissStartGuide();
            Assert.Null(view.KeyPanelForTest);
            Assert.Null(ReadPanelField(view, "_keyPanelUpdate"));
            Assert.Null(ReadPanelField(view, "_deferredGuideChildren"));
        }
        finally { view.ReleaseResources(); CaptureOverlayView.SuppressKeyPanelForProbe = previous; }
        var normal = NewView();
        try
        {
            normal.ShowKeyPanel = true;
            Render(normal);
            normal.NotifyFirstFramePresented();
            Assert.NotNull(normal.KeyPanelForTest);
        }
        finally { normal.ReleaseResources(); }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Name, WeakReference Reference)[] BuildPanelReferences(CaptureOverlayView view)
    {
        view.ShowKeyPanel = true;
        Render(view);
        view.NotifyFirstFramePresented();
        Assert.NotNull(view.KeyPanelForTest);
        var references = new List<(string, WeakReference)>();
        var update = Assert.IsType<Action>(ReadPanelField(view, "_keyPanelUpdate"));
        references.Add(("controller", new WeakReference(update.Target!)));
        CaptureKeyPanel[] panels = LogicalTreeHelper.GetChildren(view).OfType<CaptureKeyPanel>().ToArray();
        Assert.Equal(2, panels.Length);
        foreach (CaptureKeyPanel panel in panels)
        {
            references.Add(("panel", new WeakReference(panel)));
            references.Add(("row text cache", new WeakReference(panel.RowTexts)));
            foreach (FrameworkElement row in Assert.IsType<StackPanel>(panel.Child).Children)
            {
                references.Add(("row", new WeakReference(row)));
                references.Add(("row peer", new WeakReference(UIElementAutomationPeer.CreatePeerForElement(row)!)));
            }
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(panel)!;
            _ = peer.GetChildren();
            references.Add(("panel peer", new WeakReference(peer)));
        }
        return references.ToArray();
    }

    private static object? ReadPanelField(CaptureOverlayView view, string name) =>
        typeof(CaptureOverlayView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);

    private static void AssertPanelReleased(CaptureOverlayView view)
    {
        Assert.Null(view.KeyPanelForTest);
        Assert.Null(view.KeyPanelBoundsForTest);
        Assert.False(view.ShowKeyPanel);
        Assert.False(view.FirstFramePresentedForTest);
        Assert.Null(view.ClaimKeyPanel);
        Assert.Null(view.ReadPasteToScreenHotkey);
        foreach (string name in new[] { "_keyPanelUpdate", "_keyPanelPaletteChanged", "_keyPanelRelease", "_keyPanelElement", "_deferredGuideChildren", "_pasteToScreenHotkey", "_keyPanelUserOverride" })
            Assert.Null(ReadPanelField(view, name));
        Assert.Empty(LogicalTreeHelper.GetChildren(view).OfType<CaptureKeyPanel>());
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
            Assert.IsNotType<CaptureKeyPanel>(VisualTreeHelper.GetChild(view, i));
    }

    private static void AssertCollected((string Name, WeakReference Reference)[] references)
    {
        for (int i = 0; i < 2; i++)
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(references, item => Assert.False(item.Reference.IsAlive, item.Name + " survived selector teardown."));
    }

    [Theory]
    [InlineData("ko-KR", 1)]
    [InlineData("en-US", 1)]
    [InlineData("ko-KR", 1.5)]
    [InlineData("en-US", 1.5)]
    public void PremeasuredRowSetsAlignLabelsAndRightEdgesIncludingFooter(string culture, double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        var view = NewView();
        try
        {
            view.ShowKeyPanel = true;
            Render(view, scale);
            view.NotifyFirstFramePresented();
            CaptureKeyPanel ready = Panel(view);
            var selected = LogicalTreeHelper.GetChildren(view).OfType<CaptureKeyPanel>().Single(panel => !ReferenceEquals(panel, ready));
            double readyWidth = CheckColumns(ready);
            double selectedWidth = CheckColumns(selected);
            Assert.True(selectedWidth > readyWidth, "The ready panel must not inherit the hidden selection panel's wider key column.");

            static double CheckColumns(CaptureKeyPanel panel)
            {
                var rows = Assert.IsType<StackPanel>(panel.Child).Children.OfType<Grid>().ToArray();
                Assert.Equal(panel.RowTexts.Count, rows.Length);
                Assert.StartsWith("F1 —", panel.RowTexts[^1]);
                var caps = rows.Select(row => Assert.IsType<Border>(row.Children[0])).ToArray();
                var labels = rows.Select(row => Assert.IsType<TextBlock>(row.Children[1])).ToArray();
                double labelX = labels[0].TranslatePoint(new Point(), panel).X;
                double keyRight = caps[0].TranslatePoint(new Point(caps[0].ActualWidth, 0), panel).X;
                foreach (TextBlock label in labels)
                {
                    double x = label.TranslatePoint(new Point(), panel).X;
                    var row = Assert.IsAssignableFrom<Grid>(label.Parent);
                    Assert.True(Math.Abs(x - labelX) <= 0.001,
                        $"{label.Text}: left={x}, expected={labelX}, column={row.ColumnDefinitions[0].ActualWidth}, specified={row.ColumnDefinitions[0].Width.Value}");
                }
                foreach (Border cap in caps)
                {
                    double right = cap.TranslatePoint(new Point(cap.ActualWidth, 0), panel).X;
                    var row = Assert.IsAssignableFrom<Grid>(cap.Parent);
                    Assert.True(Math.Abs(right - keyRight) <= 0.001,
                        $"{((TextBlock)cap.Child).Text}: right={right}, expected={keyRight}, desired={cap.DesiredSize.Width}, actual={cap.ActualWidth}, column={row.ColumnDefinitions[0].ActualWidth}");
                }
                double width = caps.Max(cap => cap.DesiredSize.Width);
                Assert.All(rows, row => Assert.Equal(width, row.ColumnDefinitions[0].Width.Value, 3));
                Assert.All(caps, cap => Assert.True(cap.ActualWidth >= cap.DesiredSize.Width - 0.001));
                return width;
            }
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void HoveredWindowFrameAndTabSwapAt48DipWithoutPingPong()
    {
        Rect work = new(0, 0, 1280, 700);
        Size size = new(350, 240);
        Point pointer = new(700, 100);
        bool right = false;
        Rect left = CaptureKeyPanelLayout.Place(work, size, pointer, null, null, null, null, 1, ref right);
        Rect nearFrame = new(left.Right + 47, left.Top, 100, 100);
        CaptureKeyPanelLayout.Place(work, size, pointer, nearFrame, null, null, null, 1, ref right);
        Assert.True(right);
        right = false;
        Rect fartherFrame = new(left.Right + 49, left.Top, 100, 100);
        CaptureKeyPanelLayout.Place(work, size, pointer, fartherFrame, null, null, null, 1, ref right);
        Assert.False(right);
        Rect nearTab = new(left.Right + 47, left.Top, 20, 20);
        CaptureKeyPanelLayout.Place(work, size, pointer, null, null, null, nearTab, 1, ref right, nearTab);
        Assert.True(right);
        for (int i = 0; i < 20; i++)
        {
            CaptureKeyPanelLayout.Place(work, size, pointer, work, null, null, nearTab, 1, ref right, nearTab);
            Assert.True(right);
        }
        right = false;
        for (int i = 0; i < 20; i++)
        {
            CaptureKeyPanelLayout.Place(work, size, pointer, work, null, null, null, 1, ref right);
            Assert.False(right);
        }
    }

    [Fact]
    public void DisplayedHoverDrivesPlacementThroughGlideAndSelectionTakesPrecedence() => StaTestHost.Run(() =>
    {
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        PointD pointer = new(300, 300);
        TimeSpan now = TimeSpan.FromSeconds(10);
        var view = NewView();
        view.ReadCursor = () => pointer;
        view.ReadClock = () => now;
        view.SimulatedPointerInside = true;
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            Rect right = view.KeyPanelBoundsForTest!.Value;
            Assert.True(right.Left > Notes.ScreenBounds.Right);
            pointer = new PointD(1100, 300);
            view.UpdatePointer(false, false);
            Render(view);
            Assert.True(view.HoverGlidingForTest);
            Assert.NotEqual(view.HoverRegionForTest, view.ShownHoverForTest);
            RectD shown = view.ShownHoverForTest!.Value;
            Assert.Equal(new Rect(shown.Left, shown.Top, shown.Width, shown.Height), view.DeferredGuideHighlight);
            Assert.Equal(right, view.KeyPanelBoundsForTest);
            now += TimeSpan.FromSeconds(1);
            Render(view);
            Rect left = view.KeyPanelBoundsForTest!.Value;
            Assert.True(left.Right < Browser.ScreenBounds.Left);
            pointer = new PointD(1000, 120);
            view.UpdatePointer(false, false);
            Render(view);
            now += TimeSpan.FromSeconds(1);
            for (int i = 0; i < 20; i++)
            {
                Render(view);
                Assert.Equal(left, view.KeyPanelBoundsForTest);
            }
            pointer = new PointD(700, 100);
            view.SimulatePointerDown();
            pointer = new PointD(1000, 200);
            view.SimulatePointerMove();
            Render(view);
            Assert.Equal(new Rect(700, 100, 300, 100), view.DeferredGuideHighlight);
            Assert.Null(view.ShownHoverForTest);
        }
        finally { view.ReleaseResources(); FluidMotion.AnimationsEnabledOverrideForTest = oldMotion; }
    });

    [Fact]
    public void RetainsAlignedHoverAndDraggedSelectionEvidence() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string directory = OwnedTestDirectory.Create("mycapture-selector-key-panel-r3-");
        foreach (string state in new[] { "window-hover", "drag" })
        {
            var view = EvidenceView(Desktop(), AppTheme.Glass, state);
            try
            {
                view.ShowKeyPanel = true;
                Render(view);
                view.NotifyFirstFramePresented();
                RenderTargetBitmap rendered = Render(view);
                Assert.Equal(Visibility.Visible, Panel(view).Visibility);
                if (state == "drag")
                {
                    Assert.Contains(Panel(view).RowTexts, row => row.StartsWith("←"));
                    Assert.Contains(Panel(view).RowTexts, row => row.StartsWith("Enter"));
                }
                string path = Path.Combine(directory, state == "drag" ? "after-selection.png" : "after.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rendered));
                using (var file = File.Create(path)) encoder.Save(file);
                output.WriteLine($"Retained selector image: {path}");
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Fact]
    public void UnrequestedSelectorDoesNotInitializePanelAndClaimsOnlyAfterPresentation() => StaTestHost.Run(() =>
    {
        var view = NewView();
        int claims = 0, keyReads = 0;
        bool? guideAtClaim = null;
        var marks = new List<string>();
        Action<string>? oldMark = MyCapture.App.Diagnostics.SelectorTimeline.Mark;
        MyCapture.App.Diagnostics.SelectorTimeline.Mark = marks.Add;
        view.ClaimKeyPanel = guide => { claims++; guideAtClaim = guide; return false; };
        view.StartGuideClaimedForKeyPanel = true;
        view.ReadPasteToScreenHotkey = () => { keyReads++; return Hotkey.None; };
        try
        {
            Render(view);
            Assert.Equal(0, claims);
            Assert.Equal(0, keyReads);
            Assert.Null(view.KeyPanelForTest);
            view.NotifyFirstFramePresented();
            Assert.Equal(1, claims);
            Assert.True(guideAtClaim);
            Assert.Equal(0, keyReads);
            for (int i = 0; i < 20; i++) Render(view);
            Assert.Null(view.KeyPanelForTest);
            Assert.DoesNotContain(marks, mark => mark.StartsWith("key-panel-", StringComparison.Ordinal));
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            Assert.Equal(1, keyReads);
            Assert.NotNull(view.KeyPanelForTest);
            Assert.Contains("key-panel-controller", marks);
        }
        finally { view.ReleaseResources(); MyCapture.App.Diagnostics.SelectorTimeline.Mark = oldMark; }
    });

    [Fact]
    public void FirstHelpRequestInitializesOnlyAfterTheStartCardLeaves() => StaTestHost.Run(() =>
    {
        var view = NewView();
        try
        {
            Render(view);
            view.NotifyFirstFramePresented();
            Assert.Null(view.KeyPanelForTest);
            view.ModeBarForTest!.HelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(view.ShowStartGuide);
            Assert.Null(view.KeyPanelForTest);
            view.DismissStartGuide();
            Assert.Equal(Visibility.Collapsed, view.StartGuideCardForTest!.Visibility);
            Assert.NotNull(view.KeyPanelForTest);
            Assert.Equal(Visibility.Visible, Panel(view).Visibility);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void EarlyF1RequestsQueueUntilPresentedAndRecordingNeverClaims() => StaTestHost.Run(() =>
    {
        var view = NewView();
        try
        {
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            Render(view);
            Assert.Null(view.KeyPanelForTest);
            view.NotifyFirstFramePresented();
            Assert.NotNull(view.KeyPanelForTest);
        }
        finally { view.ReleaseResources(); }
        var recording = NewView();
        try
        {
            recording.RecordingSelection = true;
            recording.ShowKeyPanel = true;
            recording.ClaimKeyPanel = _ => throw new InvalidOperationException("Recording must not consume a panel showing.");
            Render(recording);
            recording.NotifyFirstFramePresented();
            Assert.Null(recording.KeyPanelForTest);
        }
        finally { recording.ReleaseResources(); }
    });

    [Fact]
    public void ConstructionWaitsForPresentedFrameAndReleasedCallbackDoesNothing() => StaTestHost.Run(() =>
    {
        var view = NewView();
        var marks = new List<string>();
        Action<string>? oldMark = MyCapture.App.Diagnostics.SelectorTimeline.Mark;
        MyCapture.App.Diagnostics.SelectorTimeline.Mark = marks.Add;
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            Assert.Null(view.KeyPanelForTest);
            Assert.False(view.FirstFramePresentedForTest);
            Assert.DoesNotContain(marks, mark => mark.StartsWith("key-panel-", StringComparison.Ordinal));
            view.NotifyFirstFramePresented();
            Assert.NotNull(view.KeyPanelForTest);
            Assert.True(view.FirstFramePresentedForTest);
            Assert.Contains("key-panel-controller", marks);
            Assert.Contains("key-panel-construct", marks);
            view.ReleaseResources();
            view.NotifyFirstFramePresented();
            Assert.Null(view.KeyPanelForTest);
        }
        finally { view.ReleaseResources(); MyCapture.App.Diagnostics.SelectorTimeline.Mark = oldMark; }
    });

    [Fact]
    public void F1TogglesPersistentPanelAndHelpShowsGuideThenPanelWithoutOverlap() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var view = NewView();
        try
        {
            Render(view);
            view.NotifyFirstFramePresented();
            Assert.Null(view.KeyPanelForTest);
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            Assert.True(view.ShowKeyPanel);
            view.HandleKeyDown(Overlay.KeyArgs(Key.Q), ModifierKeys.None);
            Assert.True(view.ShowKeyPanel);
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            Assert.False(view.ShowKeyPanel);
            Assert.Equal(Visibility.Hidden, Panel(view).Visibility);
            view.ModeBarForTest!.HelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(view.ShowStartGuide);
            Assert.Equal(Visibility.Hidden, Panel(view).Visibility);
            view.DismissStartGuide();
            Assert.Equal(Visibility.Collapsed, view.StartGuideCardForTest!.Visibility);
            Assert.True(view.ShowKeyPanel);
            Assert.Equal(Visibility.Visible, Panel(view).Visibility);
            view.ShowStartGuide = true;
            view.HandleKeyDown(Overlay.KeyArgs(Key.F1), ModifierKeys.None);
            Assert.Equal(Visibility.Collapsed, view.StartGuideCardForTest.Visibility);
            Assert.True(view.ShowKeyPanel);
            view.RecordingSelection = true;
            Assert.Equal(Visibility.Hidden, Panel(view).Visibility);
            var f1 = Overlay.KeyArgs(Key.F1);
            view.HandleKeyDown(f1, ModifierKeys.None);
            Assert.False(f1.Handled);
            Assert.Equal(Visibility.Hidden, Panel(view).Visibility);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ConditionalRowsTrackModeSelectionAndActualPinAssignment() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var view = NewView();
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            Assert.Contains(Panel(view).RowTexts, r => r.StartsWith("클릭"));
            Assert.DoesNotContain(Panel(view).RowTexts, r => r.StartsWith("←") || r.StartsWith("Enter"));
            view.HandleKeyDown(Overlay.KeyArgs(Key.A), ModifierKeys.Control);
            Render(view);
            Assert.Contains(Panel(view).RowTexts, r => r.StartsWith("←"));
            Assert.Contains(Panel(view).RowTexts, r => r.StartsWith("Enter"));
            view.DetectWindows = false;
            view.PresetSize = new System.Drawing.Size(300, 200);
            view.CopyColorText = null;
            view.TextCaptureAvailable = false;
            Assert.DoesNotContain(Panel(view).RowTexts, r => r.StartsWith("클릭") || r.StartsWith("R —") || r.StartsWith("C —") || r.StartsWith("T —"));
            view.PasteToScreenHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkF3);
            Assert.DoesNotContain(Panel(view).RowTexts, r => r.Contains("핀"));
            view.PinSelectionAvailable = true;
            Assert.Contains(Panel(view).RowTexts, r => r.StartsWith("Ctrl+Shift+F3 —"));
            view.PasteToScreenHotkey = Hotkey.None;
            Assert.DoesNotContain(Panel(view).RowTexts, r => r.Contains("핀"));
            view.TextCaptureAvailable = true;
            view.TextCaptureMode = true;
            Render(view);
            Assert.Contains(Panel(view).RowTexts, r => r == "T — 이미지 캡처로 전환");
            view.CopyColorText = _ => Task.FromResult(true);
            view.SelectMode(CaptureSelectorMode.Color);
            Render(view);
            Assert.DoesNotContain(Panel(view).RowTexts, r => r.StartsWith("드래그") || r.StartsWith("클릭"));
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void PanelIsTransparentToInputAndExposesOneGroupWithTextRows() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var view = NewView();
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            var panel = Panel(view);
            Assert.False(panel.IsHitTestVisible);
            Assert.False(panel.Focusable);
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(panel)!;
            Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
            Assert.Equal("단축키 안내", peer.GetName());
            var children = peer.GetChildren();
            Assert.Equal(panel.RowTexts.Count, children.Count);
            Assert.All(children, child => Assert.Equal(AutomationControlType.Text, child.GetAutomationControlType()));
            Assert.Equal(panel.RowTexts, children.Select(child => child.GetName()));
            Assert.Single(AccessibleGroups());
            view.HandleKeyDown(Overlay.KeyArgs(Key.A), ModifierKeys.Control);
            Render(view);
            Assert.Single(AccessibleGroups());
            view.RecordingSelection = true;
            Assert.Empty(AccessibleGroups());

            IEnumerable<AutomationPeer> AccessibleGroups() => LogicalTreeHelper.GetChildren(view).OfType<CaptureKeyPanel>()
                .Select(child => UIElementAutomationPeer.CreatePeerForElement(child)!).Where(child => child.IsControlElement());
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void CornersSwapAt48DipAndStayStableWhenBothAreCrowded()
    {
        Rect work = new(0, 0, 1280, 700);
        Size size = new(350, 240);
        bool right = false;
        Rect left = CaptureKeyPanelLayout.Place(work, size, new Point(700, 100), null, null, null, null, 1, ref right);
        Assert.False(right);
        Assert.Equal(12, left.Left);
        Rect moved = CaptureKeyPanelLayout.Place(work, size, new Point(left.Right + 47, left.Top), null, null, null, null, 1, ref right);
        Assert.True(right);
        Assert.Equal(work.Right - 12, moved.Right);
        CaptureKeyPanelLayout.Place(work, size, moved.TopLeft, null, null, null, null, 1, ref right);
        Assert.False(right);
        for (int i = 0; i < 20; i++)
        {
            CaptureKeyPanelLayout.Place(work, size, new Point(700, 100), work, null, null, null, 1, ref right);
            Assert.False(right);
        }
        CaptureKeyPanelLayout.Place(work, size, new Point(700, 100), left, null, null, null, 1, ref right);
        Assert.True(right);
        Rect blocked = CaptureKeyPanelLayout.Place(work, size, new Point(700, 100), null, null, left, moved, 1, ref right);
        Assert.True(blocked.IsEmpty);
    }

    [Fact]
    public void MonitorAndPointerMovesDoNotRebuildRowsOrLoseWorkAreaContainment() => StaTestHost.Run(() =>
    {
        PointD pointer = new(700, 100);
        var view = NewView();
        view.ReadCursor = () => pointer;
        view.ReadMonitors = () => new[]
        {
            new MonitorInfo("one", new RectD(0, 0, 640, 720), new RectD(0, 0, 640, 680), 96, true),
            new MonitorInfo("two", new RectD(640, 0, 640, 720), new RectD(640, 0, 640, 640), 144, false),
        };
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            int builds = Panel(view).ContentBuildCount;
            for (int i = 0; i < 40; i++)
            {
                pointer = new PointD(i % 2 == 0 ? 400 : 900, 100 + i);
                view.UpdatePointer(false, false);
                Render(view);
                Rect bounds = view.KeyPanelBoundsForTest!.Value;
                Assert.True(i % 2 == 0 ? bounds.Right <= 640 && bounds.Left >= 0 : bounds.Left >= 640 && bounds.Right <= 1280);
                Assert.Equal(builds, Panel(view).ContentBuildCount);
            }
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ReadyToDragAndPointerBurstUsePremeasuredRows() => StaTestHost.Run(() =>
    {
        PointD pointer = new(700, 120);
        var view = NewView();
        view.ReadCursor = () => pointer;
        view.SimulatedPointerInside = true;
        try
        {
            view.ShowKeyPanel = true;
            Render(view);
            view.NotifyFirstFramePresented();
            CaptureKeyPanel ready = Panel(view);
            var selected = LogicalTreeHelper.GetChildren(view).OfType<CaptureKeyPanel>().Single(panel => !ReferenceEquals(panel, ready));
            var before = (ready.ContentBuildCount, ready.MeasureCount, ready.ArrangeCount,
                selected.ContentBuildCount, selected.MeasureCount, selected.ArrangeCount);
            view.SimulatePointerDown();
            for (int i = 0; i < 400; i++)
            {
                pointer = new PointD(750 + i % 300, 160 + i % 200);
                view.SimulatePointerMove();
                Render(view);
            }
            Assert.Same(selected, view.KeyPanelForTest);
            Assert.Equal(before, (ready.ContentBuildCount, ready.MeasureCount, ready.ArrangeCount,
                selected.ContentBuildCount, selected.MeasureCount, selected.ArrangeCount));
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void WindowTitleSizeChipNeverOverlapsPanel() => StaTestHost.Run(() =>
    {
        var view = NewView();
        view.ReadCursor = () => new PointD(1100, 600);
        view.WindowCandidates = [new MyCapture.Platform.Capture.WindowCandidate(new IntPtr(55), new RectD(0, 560, 1280, 100), "A window near the lower edge")];
        try
        {
            view.ShowKeyPanel = true;
            view.UpdatePointer(false, false);
            Render(view);
            view.NotifyFirstFramePresented();
            Assert.NotNull(view.TargetTabForTest);
            if (view.KeyPanelBoundsForTest is { } panel) Assert.False(panel.IntersectsWith(view.TargetTabForTest!.Value));
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void CounterWritesAfterWelcomeAndSurvivesDisk()
    {
        string directory = OwnedTestDirectory.Create("mycapture-key-panel-settings-");
        var paths = AppPaths.CreateForRoot(directory);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings { General = { IsFirstRun = true }, Capture = { StartGuideShownCount = 3 } };
        var writes = new List<Action>();
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings, store.Save, writes.Add, NullLogger.Instance);
        Assert.True(bookkeeping.ClaimKeyPanel(false));
        Assert.Single(writes);
        writes[0]();
        Assert.False(File.Exists(paths.SettingsFile));
        store.CompleteFirstRun(settings);
        Assert.Equal(1, store.Load().Capture.KeyPanelShownCount);
        Assert.True(bookkeeping.ClaimKeyPanel(false));
        writes[1]();
        Assert.Equal(2, store.Load().Capture.KeyPanelShownCount);
        settings.Capture.KeyPanelShownCount = 99;
        settings.Capture.AlwaysShowKeyPanel = true;
        store.Save(settings);
        Assert.Equal(10, store.Load().Capture.KeyPanelShownCount);
        Assert.True(store.Load().Capture.AlwaysShowKeyPanel);
    }

    private static CaptureOverlayView NewView() => EvidenceView(Desktop(), AppTheme.Glass, "window-hover");
    private static CaptureKeyPanel Panel(CaptureOverlayView view) => Assert.IsType<CaptureKeyPanel>(view.KeyPanelForTest);
}
