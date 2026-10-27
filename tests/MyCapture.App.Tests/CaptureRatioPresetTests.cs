using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The selector's aspect-ratio lock (R key and the bar's ratio menu), the fixed sizes, the size readout
/// that names the lock, and the even sides the recording selector needs. The arithmetic itself is in
/// <c>SelectionRatiosTests</c>; these tests check how the selector and its bar use it.
/// </summary>
public sealed class CaptureRatioPresetTests
{
    [Fact]
    public void RCyclesTheLockThroughFreeAndTheFourRatiosAndTheBarAndMessageFollow() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal(SelectionRatio.Free, view.Ratio);
            Assert.False(bar.RatioButton.IsChecked);
            Assert.Equal(Visibility.Visible, bar.RatioButton.Visibility);

            (SelectionRatio Ratio, string Message)[] expected =
            [
                (SelectionRatio.Ratio16x9, "비율: 16:9"), (SelectionRatio.Ratio4x3, "비율: 4:3"), (SelectionRatio.Ratio1x1, "비율: 1:1"),
                (SelectionRatio.Ratio9x16, "비율: 9:16"), (SelectionRatio.Free, "비율: 자유"),
            ];
            foreach ((SelectionRatio ratio, string message) in expected)
            {
                KeyEventArgs key = Overlay.KeyArgs(Key.R);
                view.RaiseEvent(key);
                Assert.True(key.Handled);
                Assert.Equal(ratio, view.Ratio);
                Assert.Equal(ratio != SelectionRatio.Free, bar.RatioButton.IsChecked);
                Assert.Equal(message, view.ToastMessageForTest);
                // A readout of a value being changed: it is gone before the next gesture.
                Assert.Equal(TimeSpan.FromSeconds(1), view.ToastIntervalForTest);
            }

            // A key the selector does not own is left alone.
            KeyEventArgs other = Overlay.KeyArgs(Key.Q);
            view.RaiseEvent(other);
            Assert.False(other.Handled);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void EachSelectorStartsFreeAndTheChoiceIsNotKept() => StaTestHost.Run(() =>
    {
        CaptureOverlayView first = RatioView(() => new PointD(300, 300));
        try
        {
            first.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(SelectionRatio.Ratio16x9, first.Ratio);
        }
        finally { first.ReleaseResources(); }

        CaptureOverlayView second = RatioView(() => new PointD(300, 300));
        try
        {
            Assert.Equal(SelectionRatio.Free, second.Ratio);
            Assert.False(second.ModeBarForTest!.RatioButton.IsChecked);
        }
        finally { second.ReleaseResources(); }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void FreeDragIsUnchangedAndALockedDragSnapsTheFreeCornerAndConfirmsTheSameRectangle(double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PointD pointer = new(100, 100);
        CaptureOverlayView view = RatioView(() => pointer);
        RectD? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e.BitmapRegion;
        try
        {
            // Free: the rectangle between the corners, as it always was.
            view.SimulatePointerDown();
            pointer = new PointD(500, 250);
            view.SimulatePointerMove();
            Assert.Equal(new RectD(100, 100, 400, 150), view.Selection);
            Render(view, scale);
            Assert.Equal("400 × 150", view.ReadoutForTest);
            view.SimulatePointerUp();
            Overlay.Pump();
            Assert.Equal(new RectD(100, 100, 400, 150), confirmed);
        }
        finally { view.ReleaseResources(); }

        pointer = new PointD(100, 100);
        CaptureOverlayView locked = RatioView(() => pointer);
        confirmed = null;
        locked.SelectionConfirmed += (_, e) => confirmed = e.BitmapRegion;
        try
        {
            locked.RaiseEvent(Overlay.KeyArgs(Key.R));
            locked.SimulatePointerDown();
            pointer = new PointD(500, 250);
            locked.SimulatePointerMove();
            // 400 wide leads; the height follows 16:9, anchored at the start.
            Assert.Equal(new RectD(100, 100, 400, 225), locked.Selection);
            Render(locked, scale);
            Assert.Equal("400 × 225 · 16:9", locked.ReadoutForTest);

            // Dragging up and to the left keeps the start point on the bottom-right corner.
            pointer = new PointD(0, 20);
            locked.SimulatePointerMove();
            Assert.Equal(new RectD(0, 44, 100, 56), locked.Selection);
            pointer = new PointD(500, 250);
            locked.SimulatePointerMove();
            locked.SimulatePointerUp();
            Overlay.Pump();
            Assert.Equal(new RectD(100, 100, 400, 225), confirmed);
        }
        finally { locked.ReleaseResources(); }
    });

    [Fact]
    public void ALockedDragNearTheEdgeOfTheDesktopShrinksInsteadOfLeavingTheRatio() => StaTestHost.Run(() =>
    {
        PointD pointer = new(1100, 600);
        CaptureOverlayView view = RatioView(() => pointer);
        try
        {
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            view.SimulatePointerDown();
            pointer = new PointD(1279, 719);
            view.SimulatePointerMove();
            RectD selection = view.Selection!.Value;
            Assert.Equal(new RectD(1100, 600, 180, 101), selection);
            Assert.True(selection.Right <= Width && selection.Bottom <= Height);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RWithASelectionRefitsItAroundItsCentreAndAFullRoundDoesNotWhittleItAway() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            var start = new RectD(100, 50, 800, 600);
            SetSelection(view, start);

            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(new RectD(100, 125, 800, 450), view.Selection);
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(start, view.Selection);
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(new RectD(200, 50, 600, 600), view.Selection);
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(new RectD(331, 50, 337, 599), view.Selection);
            // Back to Free: the selection the user made is back.
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(SelectionRatio.Free, view.Ratio);
            Assert.Equal(start, view.Selection);

            // Every refit stayed inside the original selection and the frame.
            foreach (SelectionRatio ratio in SelectionRatios.All.Skip(1))
            {
                view.SelectRatio(ratio);
                RectD refit = view.Selection!.Value;
                Assert.True(refit.Left >= start.Left && refit.Top >= start.Top && refit.Right <= start.Right && refit.Bottom <= start.Bottom);
            }

            // A change of the selection by other means starts afresh from what it is now.
            SetSelection(view, new RectD(10, 10, 300, 300));
            view.SelectRatio(SelectionRatio.Ratio1x1);
            Assert.Equal(new RectD(10, 10, 300, 300), view.Selection);
            view.SelectRatio(SelectionRatio.Ratio16x9);
            Assert.Equal(new RectD(10, 75, 300, 169), view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ShiftArrowResizeKeepsTheLockAndArrowsStillMove() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            SetSelection(view, new RectD(100, 100, 160, 90));

            Overlay.Invoke(view, "NudgeSelection", Key.Right, ModifierKeys.Shift);
            Assert.Equal(new RectD(100, 100, 161, 91), view.Selection);
            Overlay.Invoke(view, "NudgeSelection", Key.Down, ModifierKeys.Shift);
            Assert.Equal(new RectD(100, 100, 164, 92), view.Selection);
            Overlay.Invoke(view, "NudgeSelection", Key.Right, ModifierKeys.Shift | ModifierKeys.Control);
            Assert.Equal(new RectD(100, 100, 174, 98), view.Selection);
            Overlay.Invoke(view, "NudgeSelection", Key.Left, ModifierKeys.Shift);
            Assert.Equal(new RectD(100, 100, 173, 97), view.Selection);

            // Moving is not resizing: the size, and with it the ratio, is untouched.
            view.RaiseEvent(Overlay.KeyArgs(Key.Right));
            Assert.Equal(new RectD(101, 100, 173, 97), view.Selection);

            // Free resizes one side only, as before.
            view.SelectRatio(SelectionRatio.Free);
            Overlay.Invoke(view, "NudgeSelection", Key.Right, ModifierKeys.Shift);
            Assert.Equal(new RectD(101, 100, 174, 97), view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ChoosingTheSameFixedRectangleStartsANewRefitCycle() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(500, 500), frame: BlankFrame(1920, 1080),
            monitors: [TwoMonitors[0]]);
        try
        {
            view.HandleKeyDown(Overlay.KeyArgs(Key.A), ModifierKeys.Control);
            Assert.Equal(new RectD(0, 0, 1920, 1080), view.Selection);
            for (int i = 0; i < 3; i++) view.RaiseEvent(Overlay.KeyArgs(Key.R));
            var square = new RectD(420, 0, 1080, 1080);
            Assert.Equal(square, view.Selection);

            MenuItem fixedSquare = view.ModeBarForTest!.CreateRatioMenu().Items.OfType<MenuItem>()
                .Single(item => (string)item.Header == "1080 × 1080");
            fixedSquare.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(square, view.Selection);
            view.SelectRatio(SelectionRatio.Free);
            Assert.Equal(square, view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingOrResizingBackToTheSameRectangleDoesNotReviveTheOldRefitBase(bool resize) => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            view.PlaceFixedSize(new FixedSelectionSize(1280, 720));
            view.SelectRatio(SelectionRatio.Ratio1x1);
            RectD square = view.Selection!.Value;
            ModifierKeys modifiers = resize ? ModifierKeys.Shift : ModifierKeys.None;
            view.HandleKeyDown(Overlay.KeyArgs(Key.Left), modifiers);
            view.HandleKeyDown(Overlay.KeyArgs(Key.Right), modifiers);
            Assert.Equal(square, view.Selection);
            view.SelectRatio(SelectionRatio.Free);
            Assert.Equal(square, view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void SelectAllAfterARefitStartsFromTheNewWholeFrame() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            view.PlaceFixedSize(new FixedSelectionSize(1080, 1080));
            view.SelectRatio(SelectionRatio.Ratio16x9);
            view.HandleKeyDown(Overlay.KeyArgs(Key.A), ModifierKeys.Control);
            var frame = new RectD(0, 0, 1280, 720);
            view.SelectRatio(SelectionRatio.Ratio1x1);
            view.SelectRatio(SelectionRatio.Free);
            Assert.Equal(frame, view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void FixedSizeIsCentredOnThePointersMonitorMovedByArrowsAndConfirmedByEnter() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PointD pointer = new(500, 500);
        CaptureOverlayView view = RatioView(() => pointer, frame: BlankFrame(2560, 1080), monitors: TwoMonitors);
        RectD? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e.BitmapRegion;
        try
        {
            ContextMenu menu = view.ModeBarForTest!.CreateRatioMenu();
            MenuItem Item(string header) => menu.Items.OfType<MenuItem>().Single(item => (string)item.Header == header);

            Item("1280 × 720").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new RectD(320, 180, 1280, 720), view.Selection);
            Assert.Equal(UiText.Get("CaptureRatio_FixedHint"), view.ToastMessageForTest);
            Assert.Equal(TimeSpan.FromSeconds(6), view.ToastIntervalForTest);
            // The size is placed; the lock is not touched.
            Assert.Equal(SelectionRatio.Free, view.Ratio);

            // The other sizes replace it, each centred on the same monitor.
            Item("1080 × 1080").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new RectD(420, 0, 1080, 1080), view.Selection);
            Item("1920 × 1080").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new RectD(0, 0, 1920, 1080), view.Selection);

            // On the narrow second monitor the same size becomes the largest 16:9 that fits there.
            pointer = new PointD(2200, 500);
            Item("1280 × 720").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new RectD(1920, 360, 640, 360), view.Selection);

            // Arrows move it, Enter confirms exactly that rectangle.
            view.RaiseEvent(Overlay.KeyArgs(Key.Left));
            view.RaiseEvent(Overlay.KeyArgs(Key.Down));
            Assert.Equal(new RectD(1919, 361, 640, 360), view.Selection);
            view.RaiseEvent(Overlay.KeyArgs(Key.Enter));
            Assert.Equal(new RectD(1919, 361, 640, 360), confirmed);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void FixedSizeThatFitsKeepsItsExactSizeAndTheReadoutShowsTheRealSizeWhenItDoesNot() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            // The 1280 x 720 test desktop holds 1280 x 720 exactly and not 1920 x 1080.
            Assert.True(view.PlaceFixedSize(new FixedSelectionSize(1280, 720)));
            Assert.Equal(new RectD(0, 0, 1280, 720), view.Selection);
            Assert.True(view.PlaceFixedSize(new FixedSelectionSize(1920, 1080)));
            Assert.Equal(new RectD(0, 0, 1280, 720), view.Selection);
            Assert.True(view.PlaceFixedSize(new FixedSelectionSize(1080, 1080)));
            Assert.Equal(new RectD(280, 0, 720, 720), view.Selection);
            Render(view);
            Assert.Equal("720 × 720", view.ReadoutForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ReadoutNamesTheLockOnlyWhileTheSelectionCarriesIt() => StaTestHost.Run(() =>
    {
        Assert.Equal("1920 × 1080", CaptureOverlayView.FormatReadout(1920, 1080, SelectionRatio.Free));
        Assert.Equal("1920 × 1080 · 16:9", CaptureOverlayView.FormatReadout(1920, 1080, SelectionRatio.Ratio16x9));
        Assert.Equal("1000 × 563 · 16:9", CaptureOverlayView.FormatReadout(1000, 563, SelectionRatio.Ratio16x9));
        Assert.Equal("1080 × 1080 · 1:1", CaptureOverlayView.FormatReadout(1080, 1080, SelectionRatio.Ratio1x1));
        Assert.Equal("1080 × 1920 · 9:16", CaptureOverlayView.FormatReadout(1080, 1920, SelectionRatio.Ratio9x16));
        // A Ctrl+A selection or a fixed size of another shape is not labelled with a ratio it lacks.
        Assert.Equal("1920 × 1080", CaptureOverlayView.FormatReadout(1920, 1080, SelectionRatio.Ratio4x3));

        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(SelectionRatio.Ratio4x3, view.Ratio);
            SetSelection(view, new RectD(0, 0, 1280, 720), refit: false);
            Render(view);
            Assert.Equal("1280 × 720", view.ReadoutForTest);

            // Pressing R with no change to the selection redraws the label: 1280 x 720 is 16:9.
            view.SelectRatio(SelectionRatio.Free);
            view.SelectRatio(SelectionRatio.Ratio16x9);
            Render(view);
            Assert.Equal("1280 × 720 · 16:9", view.ReadoutForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("ko-KR", "비율", "자유", "크기 고정 1280 × 720", "비율: 16:9")]
    [InlineData("en-US", "Aspect ratio", "Free", "Fixed size 1280 × 720", "Ratio: 16:9")]
    public void RatioMenuListsFreeFourRatiosAndThreeSizesWithTheCurrentChoiceChecked(string culture, string name, string free,
        string fixedName, string changed) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        try
        {
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal(name, AutomationProperties.GetName(bar.RatioButton));
            Assert.Equal("R", AutomationProperties.GetAcceleratorKey(bar.RatioButton));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(bar.RatioButton)));
            Assert.EndsWith("(R)", (string)bar.RatioButton.ToolTip, StringComparison.Ordinal);
            Assert.StartsWith(name, (string)bar.RatioButton.ToolTip, StringComparison.Ordinal);
            Assert.Equal(free, AutomationProperties.GetItemStatus(bar.RatioButton));

            ContextMenu menu = bar.CreateRatioMenu();
            Assert.Same(bar.RatioButton, menu.PlacementTarget);
            Assert.Equal(name, AutomationProperties.GetName(menu));
            object[] entries = menu.Items.Cast<object>().ToArray();
            Assert.Equal(9, entries.Length);
            Assert.IsType<Separator>(entries[5]);
            Assert.Equal([free, "16:9", "4:3", "1:1", "9:16"], entries.Take(5).Cast<MenuItem>().Select(item => (string)item.Header));
            Assert.Equal(["1280 × 720", "1920 × 1080", "1080 × 1080"], entries.Skip(6).Cast<MenuItem>().Select(item => (string)item.Header));
            Assert.Equal(fixedName, AutomationProperties.GetName((MenuItem)entries[6]));
            Assert.Equal(1, entries.OfType<MenuItem>().Count(item => item.IsChecked));
            Assert.True(((MenuItem)entries[0]).IsChecked);
            Assert.All(entries.OfType<MenuItem>(), item => Assert.True(item.MinHeight >= 34));

            // Choosing from the menu locks it: the selector, the button and the next menu agree.
            ((MenuItem)entries[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(SelectionRatio.Ratio16x9, view.Ratio);
            Assert.True(bar.RatioButton.IsChecked);
            Assert.Equal("16:9", AutomationProperties.GetItemStatus(bar.RatioButton));
            Assert.Equal(changed, view.ToastMessageForTest);
            MenuItem[] again = bar.CreateRatioMenu().Items.OfType<MenuItem>().ToArray();
            Assert.Equal([false, true, false, false, false], again.Take(5).Select(item => item.IsChecked));
            Assert.IsType<SymbolIcon>(again[1].Icon);
            Assert.Null(again[0].Icon);

            // A fixed size does not lock a ratio, so the same item stays checked.
            again[6].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(SelectionRatio.Ratio16x9, view.Ratio);
            Assert.True(bar.CreateRatioMenu().Items.OfType<MenuItem>().ElementAt(1).IsChecked);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RatioButtonJoinsTheBarAsAnIconButtonAndStopsWhereItCannotApply() => StaTestHost.Run(() =>
    {
        foreach (bool recording in new[] { false, true })
        {
            var bar = new CaptureModeBar();
            bar.ApplyPalette(CaptureGuidePalette.Create(AppTheme.Glass, null, false));
            bar.Configure(true, true, true, otherActions: true, recording: recording);
            bar.ShowMode(CaptureSelectorMode.Capture);
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bar.Arrange(new Rect(bar.DesiredSize));
            // Offered while capturing and while recording, with the size of its neighbours.
            Assert.Equal(Visibility.Visible, bar.RatioButton.Visibility);
            Assert.Equal(36, bar.RatioButton.ActualWidth);
            Assert.Equal(36, bar.RatioButton.ActualHeight);
            Assert.True(bar.RatioButton.IsEnabled);

            // The colour mode picks a pixel; there is no region to give a ratio.
            bar.ShowMode(CaptureSelectorMode.Color);
            Assert.False(bar.RatioButton.IsEnabled);
            bar.ShowMode(CaptureSelectorMode.Capture);
            Assert.True(bar.RatioButton.IsEnabled);

            // A size fixed by a setting leaves nothing to choose.
            bar.Configure(true, true, true, otherActions: true, recording: recording, ratioAvailable: false);
            Assert.Equal(Visibility.Collapsed, bar.RatioButton.Visibility);
        }
    });

    [Fact]
    public void ARecordingSizeFixedBySettingsHidesTheButtonAndIgnoresR() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300), recording: true);
        try
        {
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal(Visibility.Visible, bar.RatioButton.Visibility);
            view.PresetSize = new System.Drawing.Size(640, 360);
            Assert.Equal(Visibility.Collapsed, bar.RatioButton.Visibility);
            KeyEventArgs key = Overlay.KeyArgs(Key.R);
            view.RaiseEvent(key);
            Assert.False(key.Handled);
            Assert.Equal(SelectionRatio.Free, view.Ratio);
            Assert.False(view.PlaceFixedSize(new FixedSelectionSize(1280, 720)));
            Assert.Null(view.Selection);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RecordingSelectionKeepsEverySideEvenSoTheEncoderNeverTrimsTheRatio() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        CaptureOverlayView view = RatioView(() => pointer, recording: true);
        RectD? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e.BitmapRegion;
        try
        {
            Assert.Equal(Visibility.Visible, view.ModeBarForTest!.RatioButton.Visibility);
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            view.SimulatePointerDown();
            pointer = new PointD(501, 300);
            view.SimulatePointerMove();
            // 401 wide would give an odd 401 x 226 (the recorder trims it to 400 x 226, bending 16:9).
            Assert.Equal(new RectD(100, 100, 402, 226), view.Selection);
            view.SimulatePointerUp();
            Overlay.Pump();
            Assert.Equal(new RectD(100, 100, 402, 226), confirmed);
            Assert.Equal(0, confirmed!.Value.Width % 2);
            Assert.Equal(0, confirmed.Value.Height % 2);
        }
        finally { view.ReleaseResources(); }

        pointer = new PointD(100, 100);
        CaptureOverlayView free = RatioView(() => pointer, recording: true);
        try
        {
            free.SimulatePointerDown();
            pointer = new PointD(501, 300);
            free.SimulatePointerMove();
            // Free recording is as it was: the user's own rectangle, odd or not.
            Assert.Equal(new RectD(100, 100, 401, 200), free.Selection);
        }
        finally { free.ReleaseResources(); }

        CaptureOverlayView refit = RatioView(() => new PointD(300, 300), recording: true);
        try
        {
            SetSelection(refit, new RectD(101, 51, 801, 601));
            refit.SelectRatio(SelectionRatio.Ratio16x9);
            Assert.Equal(new RectD(101, 126, 800, 450), refit.Selection);
            SetSelection(refit, new RectD(10, 10, 1000, 500), refit: false);
            Overlay.Invoke(refit, "NudgeSelection", Key.Right, ModifierKeys.Shift);
            Assert.Equal(0, refit.Selection!.Value.Width % 2);
            Assert.Equal(0, refit.Selection.Value.Height % 2);
            Assert.True(SelectionRatios.Matches((int)refit.Selection.Value.Width, (int)refit.Selection.Value.Height, SelectionRatio.Ratio16x9));
        }
        finally { refit.ReleaseResources(); }
    });

    [Fact]
    public void ColorModeIgnoresRAndHandsTheKeyBackWhenTheModeEnds() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = RatioView(() => new PointD(300, 300));
        view.CopyColorText = _ => Task.FromResult(true);
        try
        {
            view.SelectMode(CaptureSelectorMode.Color);
            KeyEventArgs key = Overlay.KeyArgs(Key.R);
            view.RaiseEvent(key);
            Assert.False(key.Handled);
            Assert.Equal(SelectionRatio.Free, view.Ratio);
            Assert.False(view.ModeBarForTest!.RatioButton.IsEnabled);

            view.SelectMode(CaptureSelectorMode.Capture);
            Assert.True(view.ModeBarForTest.RatioButton.IsEnabled);
            view.RaiseEvent(Overlay.KeyArgs(Key.R));
            Assert.Equal(SelectionRatio.Ratio16x9, view.Ratio);
        }
        finally { view.ReleaseResources(); }
    });

    private static readonly IReadOnlyList<MonitorInfo> TwoMonitors =
    [
        new(@"\\.\DISPLAY1", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), 96, IsPrimary: true),
        new(@"\\.\DISPLAY2", new RectD(1920, 0, 640, 1080), new RectD(1920, 0, 640, 1080), 96, IsPrimary: false),
    ];

    private static CaptureOverlayView RatioView(Func<PointD> pointer, bool recording = false, FrozenFrame? frame = null,
        IReadOnlyList<MonitorInfo>? monitors = null)
    {
        var view = new CaptureOverlayView(frame ?? Desktop(), showMagnifier: false)
        {
            ReadCursor = pointer,
            PlaceCursor = _ => false,
            ReadMonitors = () => monitors ?? Monitors,
            RecordingSelection = recording,
            ShowModeBar = true,
        };
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        view.InitializePointer();
        return view;
    }

    /// <summary>What Ctrl+A or a finished keyboard move leaves behind; with <paramref name="refit"/> off the lock does not touch it.</summary>
    private static void SetSelection(CaptureOverlayView view, RectD selection, bool refit = true)
    {
        typeof(CaptureOverlayView).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, (RectD?)selection);
        if (!refit)
        {
            typeof(CaptureOverlayView).GetField("_refitBase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, null);
            typeof(CaptureOverlayView).GetField("_refitResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, null);
        }
    }

    private static FrozenFrame BlankFrame(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 0);
    }
}
