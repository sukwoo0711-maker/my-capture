using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinEdgeResizeTests
{
    [Theory]
    [InlineData(PinResizeEdge.Left, CursorType.SizeWE)]
    [InlineData(PinResizeEdge.Right, CursorType.SizeWE)]
    [InlineData(PinResizeEdge.Top, CursorType.SizeNS)]
    [InlineData(PinResizeEdge.Bottom, CursorType.SizeNS)]
    [InlineData(PinResizeEdge.TopLeft, CursorType.SizeNWSE)]
    [InlineData(PinResizeEdge.BottomRight, CursorType.SizeNWSE)]
    [InlineData(PinResizeEdge.TopRight, CursorType.SizeNESW)]
    [InlineData(PinResizeEdge.BottomLeft, CursorType.SizeNESW)]
    public void CursorMatchesEachResizeHandle(PinResizeEdge edge, CursorType cursor) => StaTestHost.Run(() =>
        Assert.Equal(cursor.ToString(), PinWindow.ResizeCursor(edge)!.ToString()));

    [Theory]
    [InlineData(PinResizeEdge.Left, -1, 0)]
    [InlineData(PinResizeEdge.Right, 1, 0)]
    [InlineData(PinResizeEdge.Top, 0, -1)]
    [InlineData(PinResizeEdge.Bottom, 0, 1)]
    [InlineData(PinResizeEdge.TopLeft, -1, -1)]
    [InlineData(PinResizeEdge.TopRight, 1, -1)]
    [InlineData(PinResizeEdge.BottomLeft, -1, 1)]
    [InlineData(PinResizeEdge.BottomRight, 1, 1)]
    public void ActualMouseCallbacksResizeWithFixedOppositeAnchor(PinResizeEdge edge, int x, int y) => StaTestHost.Run(() =>
    {
        var input = new ResizeInput();
        var pin = NewPin(input);
        try
        {
            pin.Show();
            var before = pin.PhysicalBounds;
            input.Start(pin, x, y);
            Assert.Equal(edge, pin.ResizeEdgeAt(input.Anchor));
            Assert.Same(PinWindow.ResizeCursor(edge), pin.Cursor);
            Assert.True(input.Captured);
            input.Delta(pin, x * (before.Right - before.Left), y * (before.Bottom - before.Top));
            pin.UpdateLayout();
            Assert.InRange(pin.State.Zoom, 1.99, 2.01);
            Assert.Equal(pin.State.WidthDip, pin.Width);
            Assert.Equal(pin.State.HeightDip, pin.Height);
            Assert.True(pin.IsLiveZoomingForTest);
            Assert.Equal($"{pin.State.Zoom * 100:0}%", pin.FeedbackForTest.Text);
            Assert.False(pin.IsHoverBarShown);
            var after = pin.PhysicalBounds;
            double ax = x < 0 ? 1 : x > 0 ? 0 : 0.5;
            double ay = y < 0 ? 1 : y > 0 ? 0 : 0.5;
            Assert.InRange(Math.Abs(before.Left + (before.Right - before.Left) * ax
                - after.Left - (after.Right - after.Left) * ax), 0, 2);
            Assert.InRange(Math.Abs(before.Top + (before.Bottom - before.Top) * ay
                - after.Top - (after.Bottom - after.Top) * ay), 0, 2);
            input.Up(pin);
            Assert.False(input.Captured);
            pin.ForceZoomSettleForTest();
            Assert.False(pin.IsLiveZoomingForTest);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void InteriorDragMovesWithoutChangingZoom() => StaTestHost.Run(() =>
    {
        var input = new ResizeInput();
        var pin = NewPin(input);
        try
        {
            pin.Show();
            var before = pin.PhysicalBounds;
            input.Start(pin, 0, 0);
            Assert.Equal(PinResizeEdge.None, pin.ResizeEdgeAt(input.Anchor));
            Assert.Null(pin.Cursor);
            input.Delta(pin, 35, 24);
            var after = pin.PhysicalBounds;
            Assert.Equal(before.Left + 35, after.Left);
            Assert.Equal(before.Top + 24, after.Top);
            Assert.Equal(1, pin.State.Zoom);
            input.Up(pin);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("click-through")]
    [InlineData("text-selection")]
    [InlineData("control")]
    public void OtherInputModesNeverStartResize(string mode) => StaTestHost.Run(() =>
    {
        var input = new ResizeInput();
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render("reference", false), "reference", false);
        var pin = NewPin(input, content);
        try
        {
            pin.Show();
            if (mode == "click-through") pin.State.SetClickThrough(true);
            if (mode == "text-selection") pin.HandleKeyDown(Key(pin, System.Windows.Input.Key.F2), ModifierKeys.None);
            if (mode == "control") input.Modifiers = ModifierKeys.Control;
            var before = (pin.Width, pin.Height, pin.State.Zoom, pin.PhysicalBounds);
            input.Start(pin, 1, 1);
            Assert.Equal(PinResizeEdge.None, pin.ResizeEdgeAt(input.Anchor));
            input.Delta(pin, 80, 50);
            Assert.False(input.Captured);
            Assert.Equal(before, (pin.Width, pin.Height, pin.State.Zoom, pin.PhysicalBounds));
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("up")]
    [InlineData("capture-loss")]
    [InlineData("button-release")]
    [InlineData("click-through")]
    public void EndingResizeReleasesCaptureAndStopsFurtherChanges(string ending) => StaTestHost.Run(() =>
    {
        var input = new ResizeInput();
        var pin = NewPin(input);
        try
        {
            pin.Show();
            input.Start(pin, 1, 1);
            input.Delta(pin, 80, 50);
            if (ending == "up") input.Up(pin);
            if (ending == "capture-loss") input.LoseCapture(pin);
            if (ending == "button-release") input.Pressed = false;
            if (ending == "click-through") pin.ApplyClickThrough(true);
            double zoom = pin.State.Zoom;
            input.Delta(pin, 160, 100);
            Assert.Equal(zoom, pin.State.Zoom);
            Assert.False(input.Captured);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void FailedCaptureDoesNotStartResize() => StaTestHost.Run(() =>
    {
        var input = new ResizeInput { CaptureSucceeds = false };
        var pin = NewPin(input);
        try
        {
            pin.Show();
            input.Start(pin, 1, 1);
            input.Delta(pin, 160, 100);
            Assert.Equal(1, pin.State.Zoom);
            Assert.False(input.Captured);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("text")]
    [InlineData("table")]
    [InlineData("code")]
    public void ResizedRenderedContentRerasterizesOnSettleAndRetainsSource(string kind) => StaTestHost.Run(() =>
    {
        const string source = "a\tb";
        PinContent content;
        if (kind == "code")
        {
            var document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(source,
                "<pre style=\"background-color:#123456;color:#abcdef\">a\tb</pre>"));
            content = PinContent.FromCode(ClipboardCodeRenderer.Render(document), source, document);
        }
        else content = PinContent.FromText(ClipboardTextRenderer.Render(source, kind == "table"), source, kind == "table");
        var input = new ResizeInput();
        var pin = NewPin(input, content);
        try
        {
            pin.Show();
            BitmapSource original = pin.PinnedContent.Image;
            input.Start(pin, 1, 0);
            input.Delta(pin, pin.PhysicalBounds.Right - pin.PhysicalBounds.Left, 0);
            input.Up(pin);
            Assert.Same(original, pin.PinnedContent.Image);
            var geometry = (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom);
            int settledNotifications = 0;
            pin.ViewStateChanged += () => settledNotifications++;
            pin.ForceZoomSettleForTest();
            Assert.NotSame(original, pin.PinnedContent.Image);
            Assert.True(pin.PinnedContent.Image.PixelWidth > original.PixelWidth);
            Assert.Equal(source, pin.PinnedContent.OriginalText);
            Assert.Equal(content.TextStyle, pin.PinnedContent.TextStyle);
            Assert.Equal(content.CodeDocument, pin.PinnedContent.CodeDocument);
            Assert.Equal(geometry, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom));
            Assert.Equal(1, settledNotifications);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(1, 0.1, 1)]
    [InlineData(1.5, 2, 3)]
    [InlineData(2, 8, 8)]
    [InlineData(3, 8, 8)]
    public void HighZoomDensityUsesSupportedRendererScale(double dpi, double zoom, double expected) => StaTestHost.Run(() =>
    {
        double scale = PinWindow.ZoomRasterScale(dpi, zoom);
        Assert.Equal(expected, scale);
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render("a", false), "a", false);
        Assert.Equal(expected * 96, content.RenderedAt(scale).Image.DpiX);
    });

    [Theory]
    [InlineData("text", 2)]
    [InlineData("table", 2)]
    [InlineData("code", 2)]
    [InlineData("text", 8)]
    [InlineData("table", 8)]
    [InlineData("code", 8)]
    public void SettledHighZoomKeepsRasterDensityAcrossMonitorDpiChanges(string kind, double zoom) => StaTestHost.Run(() =>
    {
        const string source = "a\tb";
        PinContent content;
        if (kind == "code")
        {
            var document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(source,
                "<pre style=\"background-color:#123456;color:#abcdef\">a\tb</pre>"));
            content = PinContent.FromCode(ClipboardCodeRenderer.Render(document), source, document);
        }
        else content = PinContent.FromText(ClipboardTextRenderer.Render(source, kind == "table"), source, kind == "table");
        var pin = NewPin(new ResizeInput(), content);
        try
        {
            pin.Show();
            pin.State.SetZoom(zoom);
            pin.Width = pin.State.WidthDip;
            pin.Height = pin.State.HeightDip;
            pin.RerenderForDisplayScale(PinWindow.ZoomRasterScale(1, zoom));
            pin.UpdateLayout();
            var geometry = (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom);
            double expectedDpi = 96 * Math.Min(8, 2 * zoom);
            int dpiEvents = 0;
            pin.DpiChanged += (_, _) =>
            {
                dpiEvents++;
                Assert.Equal(expectedDpi, pin.PinnedContent.Image.DpiX);
            };
            typeof(PinWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, [new DpiScale(1, 1), new DpiScale(2, 2)]);
            pin.UpdateLayout();
            Assert.Equal(expectedDpi, pin.PinnedContent.Image.DpiX);
            Assert.Equal(1, dpiEvents);
            Assert.False(pin.IsLiveZoomingForTest);
            Assert.Equal(geometry, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom));
            Assert.Equal(source, pin.PinnedContent.OriginalText);
            Assert.Equal(content.CodeDocument, pin.PinnedContent.CodeDocument);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void EdgeResizeZoomIsSavedByTheExistingPinSessionService() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("pin-edge-session-");
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true, CloseOnDoubleClick = false };
        var input = new ResizeInput();
        var manager = new PinManager(() => settings, NullLogger.Instance);
        manager.BeforeShowForTest = pin => typeof(PinWindow).GetField("_dragInput", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(pin, input);
        try
        {
            var store = new PinSessionStore(AppPaths.CreateForRoot(root).PinSessionRoot, NullLogger.Instance);
            using var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance);
            PinWindow pin = manager.PinImage(Image());
            input.Start(pin, 1, 0);
            input.Delta(pin, pin.PhysicalBounds.Right - pin.PhysicalBounds.Left, 0);
            input.Up(pin);
            pin.ForceZoomSettleForTest();
            service.SaveNow();
            PinSessionSnapshot saved = Assert.Single(store.LoadDetailed().Pins);
            Assert.InRange(saved.Pin.Zoom, 1.99, 2.01);
            Assert.Equal(pin.State.Zoom, saved.Pin.Zoom);
            Assert.Equal(pin.State.ImageWidthDip, saved.Pin.ImageWidthDip);
            Assert.Equal(pin.State.ImageHeightDip, saved.Pin.ImageHeightDip);
            Assert.Equal(pin.GetCurrentPlacement(), saved.Pin.Placement);
        }
        finally
        {
            manager.CloseAllForExit();
            OwnedTestDirectory.Delete(root);
        }
    });

    private static KeyEventArgs Key(PinWindow pin, Key key) => new(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(pin), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };

    private static BitmapSource Image()
    {
        var bitmap = new WriteableBitmap(160, 100, 96, 96, PixelFormats.Bgr32, null);
        bitmap.Freeze();
        return bitmap;
    }

    private static PinWindow NewPin(ResizeInput input, PinContent? content = null)
    {
        content ??= PinContent.FromImage(Image());
        return new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
            200, 150, () => new PinSettings { CloseOnDoubleClick = false }, input) { ShowActivated = false };
    }

    private sealed class ResizeInput : IPinDragInput
    {
        public ModifierKeys Modifiers { get; set; }
        public (int X, int Y) CursorPosition { get; set; }
        internal Point Anchor { get; set; }
        internal bool Captured { get; set; }
        internal bool Pressed { get; set; } = true;
        internal bool CaptureSucceeds { get; set; } = true;
        private (int X, int Y) _start;
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => Anchor;
        public bool LeftButtonPressed(MouseEventArgs args) => Pressed;
        public bool TryCapture(PinWindow owner) => Captured = CaptureSucceeds;
        public bool HasCapture(PinWindow owner) => Captured;
        public void Release(PinWindow owner) => Captured = false;
        internal void Start(PinWindow pin, int x, int y)
        {
            Anchor = new Point(x < 0 ? 0 : x > 0 ? pin.Width : pin.Width / 2,
                y < 0 ? 0 : y > 0 ? pin.Height : pin.Height / 2);
            DpiScale dpi = VisualTreeHelper.GetDpi(pin);
            _start = CursorPosition = (pin.PhysicalBounds.Left + (int)Math.Round(Anchor.X * dpi.DpiScaleX),
                pin.PhysicalBounds.Top + (int)Math.Round(Anchor.Y * dpi.DpiScaleY));
            Invoke(pin, "OnMouseLeftButtonDown", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        }
        internal void Delta(PinWindow pin, int x, int y)
        {
            CursorPosition = (_start.X + x, _start.Y + y);
            Invoke(pin, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        }
        internal void Up(PinWindow pin) => Invoke(pin, "OnMouseLeftButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        internal void LoseCapture(PinWindow pin)
        {
            Captured = false;
            Invoke(pin, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        }
        private static void Invoke(PinWindow pin, string name, MouseEventArgs args) =>
            typeof(PinWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, [args]);
    }
}
