using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>Real WPF selector input with synthetic cursor/frame providers and no mouse capture.</summary>
public sealed class FreeformSelectionTests
{
    [Theory]
    [InlineData("ko-KR", "자유형")]
    [InlineData("en-US", "Freeform")]
    public void KeyAndAccessibleModeBarEnterAndLeaveTheSameMode(string language, string name) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        CaptureOverlayView view = View(() => new PointD(200, 150));
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            var items = Assert.IsType<WrapPanel>(bar.Child);
            Assert.Equal(items.Children.IndexOf(bar.WindowButton) + 1, items.Children.IndexOf(bar.FreeformButton));
            Assert.Equal(name, AutomationProperties.GetName(bar.FreeformButton));
            Assert.Equal("F", AutomationProperties.GetAcceleratorKey(bar.FreeformButton));
            Assert.True(bar.FreeformButton.IsEnabled);
            Assert.False(bar.FreeformButton.IsChecked);

            Press(view, Key.F);
            Assert.Equal(CaptureSelectorMode.Freeform, view.Mode);
            Assert.True(bar.FreeformButton.IsChecked);
            Assert.False(bar.WindowButton.IsChecked);
            Assert.False(bar.RatioButton.IsEnabled);
            Press(view, Key.F);
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.False(bar.FreeformButton.IsChecked);

            var peer = new RadioButtonAutomationPeer(bar.FreeformButton);
            ((ISelectionItemProvider)peer).Select();
            Assert.Equal(CaptureSelectorMode.Freeform, view.Mode);
            Assert.True(((ISelectionItemProvider)peer).IsSelected);
            bar.CaptureButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            bar.FreeformButton.IsChecked = true;
            bar.WindowButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.True(bar.WindowButton.IsChecked);
            bar.FreeformButton.IsChecked = true;
            bar.TextButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Text, view.Mode);
            bar.FreeformButton.IsChecked = true;
            bar.ColorButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeOrRightButtonDiscardsOnlyTheActiveStroke(bool rightButton) => StaTestHost.Run(() =>
    {
        PointD pointer = new(120, 120);
        CaptureOverlayView view = View(() => pointer);
        int cancelled = 0, confirmed = 0;
        view.CancelRequested += (_, _) => cancelled++;
        view.SelectionConfirmed += (_, _) => confirmed++;
        try
        {
            Render(view);
            Press(view, Key.F);
            view.SimulatePointerDown();
            pointer = new(200, 120); view.SimulatePointerMove();
            pointer = new(120, 200); view.SimulatePointerMove();
            Assert.True(view.FreeformDrawing);
            if (rightButton)
            {
                var right = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonDownEvent };
                Overlay.Invoke(view, "OnMouseRightButtonDown", right);
                Assert.True(right.Handled);
            }
            else Press(view, Key.Escape);
            Assert.False(view.FreeformDrawing);
            Assert.Equal(CaptureSelectorMode.Freeform, view.Mode);
            Assert.Equal(0, cancelled);
            Assert.Equal(0, confirmed);
            view.SimulatePointerUp();
            Assert.Equal(0, confirmed);
            Press(view, Key.Escape);
            Assert.Equal(1, cancelled);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void TooSmallAndEmptyStrokesLeaveTheModeReadyForAValidStroke() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        CaptureOverlayView view = View(() => pointer);
        var confirmed = new List<RegionSelectionEventArgs>();
        view.SelectionConfirmed += (_, e) => confirmed.Add(e);
        try
        {
            Render(view);
            view.SelectMode(CaptureSelectorMode.Freeform);
            Draw([new(100, 100), new(107, 100), new(107, 130)], view, p => pointer = p);
            Assert.Empty(confirmed);
            Assert.False(view.FreeformDrawing);
            Assert.Equal(CaptureSelectorMode.Freeform, view.Mode);
            Draw([new(100, 100), new(150, 150), new(200, 200)], view, p => pointer = p);
            Assert.Empty(confirmed);
            Assert.False(view.FreeformDrawing);
            Draw([new(100, 100), new(108, 100), new(108, 108), new(100, 108)], view, p => pointer = p);
            RegionSelectionEventArgs selected = Assert.Single(confirmed);
            Assert.Equal(new RectD(100, 100, 8, 8), selected.BitmapRegion);
            Assert.NotNull(selected.FreeformMask);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RatioAndFixedSizePresetsDoNotChangeAFreeformStroke() => StaTestHost.Run(() =>
    {
        PointD pointer = new(100, 100);
        CaptureOverlayView view = View(() => pointer);
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            Render(view);
            view.SelectRatio(SelectionRatio.Ratio16x9);
            view.PresetSize = new System.Drawing.Size(320, 180);
            view.SelectMode(CaptureSelectorMode.Freeform);
            KeyEventArgs ratioKey = Overlay.KeyArgs(Key.R);
            view.HandleKeyDown(ratioKey, ModifierKeys.None);
            Assert.False(ratioKey.Handled);
            Draw([new(100, 100), new(135, 100), new(100, 157)], view, p => pointer = p);
            Assert.NotNull(confirmed);
            Assert.Equal(new RectD(100, 100, 35, 57), confirmed.BitmapRegion);
            Assert.NotNull(confirmed.FreeformMask);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("CaptureFreeform_Text")]
    [InlineData("CaptureFreeform_Color")]
    [InlineData("CaptureFreeform_Recording")]
    [InlineData("CaptureFreeform_Scrolling")]
    [InlineData("CaptureFreeform_Delay")]
    [InlineData("CaptureFreeform_Editor")]
    public void AnUnavailableRouteShowsItsReasonAndCannotEnterFromKeyOrBar(string reasonKey) => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = View(() => new PointD(200, 150));
        try
        {
            string reason = UiText.Get(reasonKey);
            view.FreeformUnavailableReason = reason;
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.False(view.FreeformAvailable);
            Assert.False(bar.FreeformButton.IsEnabled);
            Assert.Equal(Visibility.Visible, bar.FreeformButton.Visibility);
            Assert.Equal(reason, bar.FreeformButton.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(bar.FreeformButton));
            KeyEventArgs key = Overlay.KeyArgs(Key.F);
            view.HandleKeyDown(key, ModifierKeys.None);
            Assert.False(key.Handled);
            bar.ActivateButton(bar.FreeformButton);
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            view.SelectMode(CaptureSelectorMode.Freeform);
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RecordingContextDisablesTheModeAndANewSelectorStartsInCapture() => StaTestHost.Run(() =>
    {
        CaptureOverlayView first = View(() => new PointD(200, 150));
        CaptureOverlayView second = View(() => new PointD(200, 150));
        try
        {
            Render(first);
            first.SelectMode(CaptureSelectorMode.Freeform);
            Assert.Equal(CaptureSelectorMode.Freeform, first.Mode);
            Render(second);
            Assert.Equal(CaptureSelectorMode.Capture, second.Mode);
            Assert.False(second.ModeBarForTest!.FreeformButton.IsChecked);
            second.RecordingSelection = true;
            Assert.False(second.FreeformAvailable);
            Assert.False(second.ModeBarForTest.FreeformButton.IsEnabled);
            second.SelectMode(CaptureSelectorMode.Freeform);
            Assert.Equal(CaptureSelectorMode.Capture, second.Mode);
        }
        finally { first.ReleaseResources(); second.ReleaseResources(); }
    });

    [Fact]
    public void MovingTheStrokeRevealsOnlyItsInteriorWithoutRebuildingTheDesktop() => StaTestHost.Run(() =>
    {
        PointD pointer = new(200, 100);
        CaptureOverlayView view = View(() => pointer);
        try
        {
            Render(view);
            view.SelectMode(CaptureSelectorMode.Freeform);
            view.SimulatePointerDown();
            Render(view);
            int desktopRenders = view.StaticRenderCount, dimmerRenders = view.DimmerRenderCount;
            pointer = new(350, 100); view.SimulatePointerMove(); Render(view);
            pointer = new(200, 250); view.SimulatePointerMove();
            RenderTargetBitmap image = Render(view);
            Assert.True(view.FreeformDrawing);
            Assert.Equal(desktopRenders, view.StaticRenderCount);
            Assert.Equal(dimmerRenders, view.DimmerRenderCount);
            // Keep this exact pixel assertion clear of the real start-point chip near (200,100).
            Assert.False(Overlay.Visual(view, "_anchorVisual").ContentBounds.Contains(new Point(240, 170)));
            Assert.Equal(new byte[] { 0x20, 0x40, 0x60, 0xff }, Pixel(image, 240, 170));
            byte[] outside = Pixel(image, 360, 200);
            Assert.True(outside[0] < 0x20 && outside[1] < 0x40 && outside[2] < 0x60);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticDragFlowsThroughTheWindowCompletionWithAlphaAndPinIntent(bool pin) => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Overlay.Frame();
        var window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false);
        var view = Assert.IsType<CaptureOverlayView>(window.Content);
        PointD pointer = new(100, 100);
        Configure(view, () => pointer);
        CaptureSelectionCompletedEventArgs? completed = null;
        window.SelectionCompleted += (_, e) => completed = e;
        try
        {
            Render(view);
            view.SelectMode(CaptureSelectorMode.Freeform);
            pointer = new(100, 100); view.SimulatePointerDown();
            pointer = new(140, 100); view.SimulatePointerMove();
            pointer = new(100, 140); view.SimulatePointerMove();
            if (pin) window.ConfirmSelectionAsPin(); else view.SimulatePointerUp();
            Assert.NotNull(completed);
            Assert.Same(frame, completed.Frame);
            Assert.Equal(pin, completed.PinRequested);
            Assert.Equal(new RectD(100, 100, 40, 40), completed.BitmapRegion);
            Assert.True(completed.SelectedBitmap.IsFrozen);
            Assert.Equal(PixelFormats.Bgra32, completed.SelectedBitmap.Format);
            Assert.Equal(new byte[] { 0x20, 0x40, 0x60, 0xff }, Pixel(completed.SelectedBitmap, 5, 5));
            Assert.Equal(new byte[4], Pixel(completed.SelectedBitmap, 35, 35));
            Assert.Equal(new byte[] { 0x20, 0x40, 0x60, 0x80 }, Pixel(completed.SelectedBitmap, 19, 20));
        }
        finally { view.ReleaseResources(); window.Close(); }
    });

    [Fact]
    public void SmallPointerStepChangesLocalTilesWithoutRenderingTheWholeView() => StaTestHost.Run(() =>
    {
        PointD pointer = new(30, 130);
        CaptureOverlayView view = View(() => pointer);
        try
        {
            Render(view);
            view.SelectMode(CaptureSelectorMode.Freeform);
            view.SimulatePointerDown();
            pointer = new(600, 130); view.SimulatePointerMove();
            pointer = new(600, 330); view.SimulatePointerMove();
            pointer = new(30, 330); view.SimulatePointerMove();
            CaptureFreeformPreview preview = Assert.IsType<CaptureFreeformPreview>(view.FreeformPreviewForTest);
            Assert.True(view.FreeformStrokeBounds!.Value.Width >= 570);

            // Do not call InvalidateVisual, Render(view), or force a whole-view draw here.
            pointer = new(31, 330); view.SimulatePointerMove();

            Assert.Same(preview, view.FreeformPreviewForTest);
            Assert.InRange(preview.LastChangedTileCount, 1, 4);
            Assert.Equal(64, preview.LastChangedTileBounds.Width);
            Assert.Equal(0, preview.LastChangedTileBounds.Left);
            Assert.True(preview.LastChangedTileCount < preview.TileCount);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void InactivePaletteRebuildDropsOldTilesAndReentryUsesNewGuideColor() => StaTestHost.Run(() =>
    {
        PointD pointer = new(200, 100);
        CaptureOverlayView view = View(() => pointer);
        try
        {
            Render(view);
            view.SelectMode(CaptureSelectorMode.Freeform);
            CaptureFreeformPreview original = Assert.IsType<CaptureFreeformPreview>(view.FreeformPreviewForTest);
            view.SelectMode(CaptureSelectorMode.Capture);

            // Supply only this view's palette, then invoke the same pen/rebuild pair used
            // by RenderLayers. Neither the process theme nor a real profile is changed.
            var palette = new Dictionary<string, Brush>(CaptureGuidePalette.Resolve())
            {
                ["CaptureGuide.SelectionBorder"] = Brushes.Red,
            };
            CaptureCueEvidenceTests.UsePalette(view, palette);
            typeof(CaptureOverlayView).GetMethod("BuildGuidePens", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            typeof(CaptureOverlayView).GetMethod("RebuildFreeformPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            Assert.Null(view.FreeformPreviewForTest);
            Assert.True(original.IsDisposed);

            view.SelectMode(CaptureSelectorMode.Freeform);
            CaptureFreeformPreview changed = Assert.IsType<CaptureFreeformPreview>(view.FreeformPreviewForTest);
            Assert.NotSame(original, changed);
            view.SimulatePointerDown();
            pointer = new(350, 100); view.SimulatePointerMove();
            pointer = new(200, 250); view.SimulatePointerMove();
            var tileImage = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
            tileImage.Render(changed.Visual);
            Assert.Equal(new byte[] { 0, 0, 128, 128 }, Pixel(tileImage, 300, 99));
        }
        finally { view.ReleaseResources(); }
    });

    private static CaptureOverlayView View(Func<PointD> cursor)
    {
        var view = new CaptureOverlayView(Overlay.Frame(), showMagnifier: false);
        Configure(view, cursor);
        return view;
    }

    private static void Configure(CaptureOverlayView view, Func<PointD> cursor)
    {
        view.ReadCursor = cursor;
        view.PlaceCursor = _ => throw new InvalidOperationException("This synthetic fixture must not move the real cursor.");
        view.ReadMonitors = () => [new MonitorInfo("fixture", new RectD(0, 0, 640, 360),
            new RectD(0, 0, 640, 360), 96, true)];
        view.DetectWindows = true;
        view.TextCaptureAvailable = true;
        view.CopyColorText = _ => Task.FromResult(true);
        view.ShowModeBar = true;
        view.SimulatedPointerInside = true;
        view.InitializePointer();
    }

    private static void Press(CaptureOverlayView view, Key key) => view.HandleKeyDown(Overlay.KeyArgs(key), ModifierKeys.None);

    private static void Draw(PointD[] points, CaptureOverlayView view, Action<PointD> move)
    {
        move(points[0]);
        view.SimulatePointerDown();
        foreach (PointD point in points.Skip(1)) { move(point); view.SimulatePointerMove(); }
        view.SimulatePointerUp();
    }

    private static RenderTargetBitmap Render(CaptureOverlayView view)
    {
        view.InvalidateVisual();
        view.Measure(new Size(640, 360));
        view.Arrange(new Rect(0, 0, 640, 360));
        view.UpdateLayout();
        var image = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        image.Render(view);
        return image;
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
}
