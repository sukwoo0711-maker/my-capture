using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePresentationLifetimeTests
{
    [Theory]
    [InlineData("finish")]
    [InlineData("cancel")]
    [InlineData("land")]
    [InlineData("close-card")]
    public void FinishedFlightReleasesCaptureAndClosedWindowsWhileStillOwnedByApp(string ending) => Run((monitor, router) =>
    {
        (CaptureFlight flight, WeakReference[] references) = FinishCapture(ending, monitor, router);
        Assert.True(flight.IsDone);
        Assert.Null(flight.Image);
        Assert.Null(flight.WindowForTest);
        AssertCollected(references);
        // App keeps its latest flight until the next capture, even after it has ended.
        GC.KeepAlive(flight);
    });

    [Fact]
    public void ClosedCardReleasesItsPreviewSourceAndActionOwnerWhileStillReferenced() => Run((monitor, router) =>
    {
        (QuickAccessWindow card, WeakReference owner) = CloseCard(monitor, router);
        Assert.False(card.IsOpen);
        Assert.False(card.IsPending);
        Assert.Null(card.PreviewForTest.Source);
        AssertCollected([owner]);
        GC.KeepAlive(card);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (QuickAccessWindow, WeakReference) CloseCard(MonitorInfo monitor, ReceiptKeyboardRouter router)
    {
        var owner = new CaptureOwner(Image());
        var card = new QuickAccessWindow(owner.Image, owner.Copy, owner.Edit, owner.Pin, owner.Copy,
            owner.Edit, owner.Export, capturedRegion: Source, targetMonitor: monitor, keyboardRouter: router);
        card.Show();
        card.UpdateLayout();
        card.CloseNow();
        return (card, new WeakReference(owner));
    }

    private sealed class CaptureOwner(BitmapSource image)
    {
        internal BitmapSource Image { get; } = image;
        internal Task<bool> Copy() => Task.FromResult(true);
        internal void Edit() { }
        internal Task Pin() => Task.CompletedTask;
        internal Task<string> Export() => Task.FromResult("unused.png");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CaptureFlight, WeakReference[]) FinishCapture(string ending, MonitorInfo monitor, ReceiptKeyboardRouter router)
    {
        BitmapSource image = Image();
        double now = 0;
        var flight = Assert.IsType<CaptureFlight>(CaptureFlight.TryLift(image, Source, () => now));
        var references = new List<WeakReference> { new(image), new(flight.WindowForTest!) };
        QuickAccessWindow? card = null;
        try
        {
            if (ending is "land" or "close-card")
            {
                card = Card(image, monitor, router);
                references.Add(new(card));
                Assert.True(flight.Land(card));
                card.Show();
                PumpUntil(() => flight.IsFlying || flight.IsDone);
                Assert.True(flight.IsFlying);
                references.Add(new(flight.WindowForTest!));
                if (ending == "close-card") card.CloseNow();
                else
                {
                    now += FluidMotion.DeliberateDuration.TimeSpan.TotalMilliseconds;
                    flight.Tick();
                    flight.Tick();
                    Assert.True(flight.IsDone);
                }
            }
            else if (ending == "cancel")
            {
                flight.Cancel();
                now += FluidMotion.FastDuration.TimeSpan.TotalMilliseconds;
                flight.Tick();
            }
            else flight.Finish();
            Assert.True(flight.IsDone);
        }
        finally
        {
            card?.CloseNow();
            flight.Finish();
        }
        return (flight, references.ToArray());
    }

    private static void AssertCollected(IEnumerable<WeakReference> references)
    {
        WeakReference[] retained = references.ToArray();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (retained.All(reference => !reference.IsAlive)) return;
        }
        Assert.All(retained, reference => Assert.False(reference.IsAlive));
    }

    private static readonly RectD Source = new(-31800, -31850, 480, 300);

    private static QuickAccessWindow Card(BitmapSource image, MonitorInfo monitor, ReceiptKeyboardRouter router) =>
        new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"),
            capturedRegion: Source, targetMonitor: monitor, keyboardRouter: router);

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(480, 300, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 300 * 4], 480 * 4);
        image.Freeze();
        return image;
    }

    private static void PumpUntil(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) Pump();
        Assert.True(done());
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void Run(Action<MonitorInfo, ReceiptKeyboardRouter> body) => StaTestHost.Run(() =>
    {
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<bool> previousRemote = CaptureFlight.ReadRemoteSession;
        Func<bool> previousLayered = CaptureFlight.ReadLayeredSupport;
        Func<IReadOnlyList<MonitorInfo>> previousMonitors = CaptureFlight.ReadMonitors;
        Func<Window, bool> previousExclusion = CaptureFlightDecoration.ApplyCaptureExclusion;
        var monitor = new MonitorInfo("LIFETIME", new RectD(-32000, -32000, 2400, 1600),
            new RectD(-32000, -32000, 2400, 1560), MonitorEnumerator.GetPrimary().Dpi, false);
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            CaptureFlight.ReadRemoteSession = () => false;
            CaptureFlight.ReadLayeredSupport = () => true;
            CaptureFlight.ReadMonitors = () => [monitor];
            CaptureFlightDecoration.ApplyCaptureExclusion = _ => true;
            body(monitor, router);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            CaptureFlight.ReadRemoteSession = previousRemote;
            CaptureFlight.ReadLayeredSupport = previousLayered;
            CaptureFlight.ReadMonitors = previousMonitors;
            CaptureFlightDecoration.ApplyCaptureExclusion = previousExclusion;
        }
    });

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }
        public void Unregister(int id) { }
    }
}
