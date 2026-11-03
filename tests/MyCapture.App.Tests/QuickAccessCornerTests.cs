using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The capture card waits in the bottom-right corner of the work area, above the taskbar's
/// notification area, wherever the capture was taken, and follows that corner when the work area
/// changes; a notification shown before or meanwhile stacks above it instead of being covered.
/// </summary>
public sealed class QuickAccessCornerTests
{
    [Theory]
    [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)]
    public void CardWaitsInTheNotificationCornerOfANegativeCoordinateMonitor(double scale)
    {
        var work = new RectD(-1920, 0, 1920, 1040);
        foreach (double margin in new[] { 0, QuickAccessWindow.ShadowMargin })
        {
            Rect position = QuickAccessWindow.Position(work, scale, 280 + 2 * margin, 110 + 2 * margin, margin);
            // Its visible card sits where the shell's notifications put theirs.
            Assert.Equal(work.Right / scale - ThemedShellPresenter.EdgeGap, position.Right - margin, 6);
            Assert.Equal(work.Bottom / scale - ThemedShellPresenter.EdgeGap, position.Bottom - margin, 6);
            Assert.Equal(ThemedShellPresenter.NotificationBounds(work, scale, position.Width, position.Height, margin), position);
        }

        // A card as large as the work area stays inside it.
        Rect full = QuickAccessWindow.Position(work, scale, 4000, 4000, QuickAccessWindow.ShadowMargin);
        Assert.Equal(new Rect(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale), full);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(1.5)] [InlineData(2.0)]
    public void ANoticeStacksAboveTheCardWaitingInTheCorner(double scale)
    {
        var work = new RectD(-1920, 0, 1920, 1040);
        double cardMargin = QuickAccessWindow.ShadowMargin, noticeMargin = ThemedShellPresenter.ShadowMargin;
        Rect card = QuickAccessWindow.Position(work, scale, 300 + 2 * cardMargin, 260 + 2 * cardMargin, cardMargin);
        // The visible card in physical pixels, as the open card reports it.
        var occupant = new RectD((card.Left + cardMargin) * scale, (card.Top + cardMargin) * scale,
            (card.Width - 2 * cardMargin) * scale, (card.Height - 2 * cardMargin) * scale);

        Rect alone = ThemedShellPresenter.NotificationBounds(work, scale, 384, 180);
        Rect stacked = ThemedShellPresenter.NotificationBounds(work, scale, 384, 180, noticeMargin, occupant);
        Assert.Equal(alone.Left, stacked.Left);
        Assert.Equal(occupant.Top / scale - ThemedShellPresenter.StackGap, stacked.Bottom - noticeMargin, 6);

        // A card on another monitor leaves this corner to the notice.
        var elsewhere = new RectD(0, 0, 400, 300);
        Assert.Equal(alone, ThemedShellPresenter.NotificationBounds(work, scale, 384, 180, noticeMargin, elsewhere));
    }

    [Theory]
    [InlineData(1366, 696, 1.5)]
    [InlineData(1920, 984, 2.0)]
    public void ANoticeWaitsBesideTheCardOnAWorkAreaTooShortToStackThem(double width, double height, double scale)
    {
        // A card with its first-capture tip and a notice with two lines of text, above a 48-DIP taskbar.
        var work = new RectD(0, 0, width, height);
        Rect alone = ThemedShellPresenter.NotificationBounds(work, scale, 384, 174);
        RectD occupant = TippedCardIn(work, scale);
        Rect beside = ThemedShellPresenter.NotificationBounds(work, scale, 384, 174, ThemedShellPresenter.ShadowMargin, occupant);
        // On the card's bottom line, StackGap to its left.
        Assert.Equal(alone.Top, beside.Top, 6);
        Assert.Equal(occupant.Left / scale - ThemedShellPresenter.StackGap, beside.Right - ThemedShellPresenter.ShadowMargin, 6);

        // With no room beside it either, the notice stays inside the work area.
        var narrow = new RectD(0, 0, 800, height);
        Rect cramped = ThemedShellPresenter.NotificationBounds(narrow, scale, 384, 174, ThemedShellPresenter.ShadowMargin,
            TippedCardIn(narrow, scale));
        Assert.Equal(-ThemedShellPresenter.ShadowMargin, cramped.Top, 6);
        Assert.Equal(ThemedShellPresenter.NotificationBounds(narrow, scale, 384, 174).Left, cramped.Left, 6);
    }

    // The visible card, in physical pixels, of a card waiting in the corner with its first-capture tip.
    private static RectD TippedCardIn(RectD work, double scale)
    {
        double margin = QuickAccessWindow.ShadowMargin;
        Rect card = QuickAccessWindow.Position(work, scale, 328 + 2 * margin, 334 + 2 * margin, margin);
        return new RectD((card.Left + margin) * scale, (card.Top + margin) * scale,
            (card.Width - 2 * margin) * scale, (card.Height - 2 * margin) * scale);
    }

    [Fact]
    public void TheShownCardWaitsAboveTheNotificationAreaAndANoticeStacksOverIt() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        MonitorInfo monitor = Offscreen();
        double scale = monitor.ScaleFactor;
        QuickAccessWindow card = KeptCard(monitor, router);
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            card.Show();
            card.UpdateLayout();
            Pump();
            RectD surface = card.OpenSurfaceBounds!.Value;
            Assert.Equal(monitor.WorkArea.Right - ThemedShellPresenter.EdgeGap * scale, surface.Right, 1.0);
            Assert.Equal(monitor.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, surface.Bottom, 1.0);
            // The entrance settles the visible card with a render transform; where it waits stays the same.
            ((FrameworkElement)card.Content).RenderTransform = new TranslateTransform(0, 8);
            card.UpdateLayout();
            Assert.Equal(surface, card.OpenSurfaceBounds!.Value);

            using var presenter = new ThemedShellPresenter(() => false)
            {
                NotificationMonitor = () => monitor,
                CornerOccupant = () => card.OpenSurfaceBounds,
            };
            presenter.ShowNotification("제목", "본문", TrayBalloonKind.Information);
            Window notice = Assert.IsAssignableFrom<Window>(presenter.NotificationWindowForTest);
            notice.UpdateLayout();
            Pump();
            Point origin = notice.PointToScreen(new Point(0, 0));
            double margin = notice.AllowsTransparency ? ThemedShellPresenter.ShadowMargin : 0;
            Assert.Equal(surface.Top - ThemedShellPresenter.StackGap * scale, origin.Y + (notice.ActualHeight - margin) * scale, 1.0);
            Assert.Equal(surface.Right, origin.X + (notice.ActualWidth - margin) * scale, 1.0);
        }
        finally
        {
            card.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Fact]
    public void ANoticeShownBeforeTheCardMovesAboveItWhenTheCardTakesTheCorner() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        MonitorInfo monitor = Offscreen();
        double scale = monitor.ScaleFactor;
        QuickAccessWindow? card = null;
        using var presenter = new ThemedShellPresenter(() => false)
        {
            NotificationMonitor = () => monitor,
            MonitorOfWindow = _ => monitor,
            CornerOccupant = () => card?.OpenSurfaceBounds,
        };
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            // A scroll capture's start notice, say, still in the corner when the capture's card shows.
            presenter.ShowNotification("제목", "본문", TrayBalloonKind.Information);
            Window notice = Assert.IsAssignableFrom<Window>(presenter.NotificationWindowForTest);
            notice.UpdateLayout();
            Pump();
            Assert.Equal(monitor.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, VisibleEdges(notice, scale).Bottom, 1.0);

            card = KeptCard(monitor, router);
            // Wired as App wires every card.
            card.Placed += presenter.RefitNotification;
            card.Show();
            card.UpdateLayout();
            Pump();
            RectD surface = card.OpenSurfaceBounds!.Value;
            Assert.Equal(surface.Top - ThemedShellPresenter.StackGap * scale, VisibleEdges(notice, scale).Bottom, 1.0);
            Assert.Equal(surface.Right, VisibleEdges(notice, scale).Right, 1.0);
        }
        finally
        {
            card?.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Fact]
    public void TheCardFollowsItsCornerWhenTheWorkAreaOrTheDisplayChanges() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        MonitorInfo monitor = Offscreen();
        double scale = monitor.ScaleFactor;
        // The taskbar grows by 48 DIPs while the card is open.
        MonitorInfo shorter = monitor with { WorkArea = monitor.WorkArea with { Height = monitor.WorkArea.Height - Math.Round(48 * scale) } };
        MonitorInfo current = monitor;
        QuickAccessWindow card = KeptCard(monitor, router, _ => current);
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            card.Show();
            card.UpdateLayout();
            Pump();
            IntPtr hwnd = new WindowInteropHelper(card).Handle;
            Assert.Equal(monitor.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, card.OpenSurfaceBounds!.Value.Bottom, 1.0);

            current = shorter;
            SendMessage(hwnd, 0x001A, new IntPtr(0x002F), IntPtr.Zero); // WM_SETTINGCHANGE, SPI_SETWORKAREA
            Pump();
            Assert.Equal(shorter.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, card.OpenSurfaceBounds!.Value.Bottom, 1.0);
            Assert.Equal(monitor.WorkArea.Right - ThemedShellPresenter.EdgeGap * scale, card.OpenSurfaceBounds!.Value.Right, 1.0);

            // Another setting leaves the card where it is; a display change puts it back in its corner.
            current = monitor;
            SendMessage(hwnd, 0x001A, new IntPtr(0x0014), IntPtr.Zero); // WM_SETTINGCHANGE, SPI_SETDESKWALLPAPER
            Pump();
            Assert.Equal(shorter.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, card.OpenSurfaceBounds!.Value.Bottom, 1.0);
            SendMessage(hwnd, 0x007E, IntPtr.Zero, IntPtr.Zero); // WM_DISPLAYCHANGE
            Pump();
            Assert.Equal(monitor.WorkArea.Bottom - ThemedShellPresenter.EdgeGap * scale, card.OpenSurfaceBounds!.Value.Bottom, 1.0);
        }
        finally
        {
            card.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Fact]
    public void ANoticeAboveTheCardMovesWithItWhenTheWorkAreaChanges() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        MonitorInfo monitor = Offscreen();
        double scale = monitor.ScaleFactor;
        // The taskbar moves to the right edge, 60 DIPs wide, while both are open.
        MonitorInfo narrower = monitor with { WorkArea = monitor.WorkArea with { Width = monitor.WorkArea.Width - Math.Round(60 * scale) } };
        MonitorInfo current = monitor;
        QuickAccessWindow? card = null;
        using var presenter = new ThemedShellPresenter(() => false)
        {
            NotificationMonitor = () => monitor,
            MonitorOfWindow = _ => current,
            CornerOccupant = () => card?.OpenSurfaceBounds,
        };
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            presenter.ShowNotification("제목", "본문", TrayBalloonKind.Information);
            Window notice = Assert.IsAssignableFrom<Window>(presenter.NotificationWindowForTest);
            notice.UpdateLayout();
            Pump();
            card = KeptCard(monitor, router, _ => current);
            card.Placed += presenter.RefitNotification;
            card.Show();
            card.UpdateLayout();
            Pump();

            current = narrower;
            SendMessage(new WindowInteropHelper(card).Handle, 0x001A, new IntPtr(0x002F), IntPtr.Zero); // WM_SETTINGCHANGE, SPI_SETWORKAREA
            Pump();
            RectD surface = card.OpenSurfaceBounds!.Value;
            Assert.Equal(narrower.WorkArea.Right - ThemedShellPresenter.EdgeGap * scale, surface.Right, 1.0);
            // The notice is placed on its monitor as it is now, off the strip the taskbar took.
            Assert.Equal(surface.Right, VisibleEdges(notice, scale).Right, 1.0);
            Assert.Equal(surface.Top - ThemedShellPresenter.StackGap * scale, VisibleEdges(notice, scale).Bottom, 1.0);
        }
        finally
        {
            card?.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    // A card that stays until closed. Its monitor is read from the test, never from the real desktop.
    private static QuickAccessWindow KeptCard(MonitorInfo monitor, ReceiptKeyboardRouter router,
        Func<IntPtr, MonitorInfo>? monitorOfWindow = null) =>
        new(Image(), () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), targetMonitor: monitor,
            keyboardRouter: router) { IsKept = true, MonitorOfWindow = monitorOfWindow ?? (_ => monitor) };

    // The notice's visible card, inside its shadow band, in physical pixels.
    private static (double Bottom, double Right) VisibleEdges(Window notice, double scale)
    {
        Point origin = notice.PointToScreen(new Point(0, 0));
        double margin = notice.AllowsTransparency ? ThemedShellPresenter.ShadowMargin : 0;
        return (origin.Y + (notice.ActualHeight - margin) * scale, origin.X + (notice.ActualWidth - margin) * scale);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Off the visible desktop, 1600 x 1000 DIPs at the primary display's scale, above a 40-DIP taskbar.
    private static MonitorInfo Offscreen()
    {
        uint dpi = MonitorEnumerator.GetPrimary().Dpi;
        double scale = dpi / 96.0;
        var bounds = new RectD(-32000, -32000, Math.Round(1600 * scale), Math.Round(1000 * scale));
        return new MonitorInfo("TEST-CARD-CORNER", bounds, bounds with { Height = Math.Round(960 * scale) }, dpi, false);
    }

    private static BitmapSource Image()
    {
        byte[] pixels = Enumerable.Repeat((byte)200, 64 * 48 * 4).ToArray();
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        image.Freeze();
        return image;
    }

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id)
        {
        }
    }
}
