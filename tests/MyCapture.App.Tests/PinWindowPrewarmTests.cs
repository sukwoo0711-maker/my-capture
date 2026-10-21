using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinWindowPrewarmTests
{
    [Fact]
    public void Prewarm_ShowsOnePinWindowOffEveryMonitorTransparentAndInactive_ThenKeepsItHidden() => StaTestHost.Run(() =>
    {
        PinWindowPrewarm.ResetForTest();
        var shown = new List<PinWindow>();
        bool visible = false, active = true, topmost = true, onAMonitor = true;
        double opacity = -1;
        PinWindowPrewarm.ShownForTest = pin =>
        {
            shown.Add(pin);
            visible = pin.IsVisible;
            active = pin.IsActive;
            topmost = pin.Topmost;
            opacity = pin.Opacity;
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            onAMonitor = right > left && MonitorEnumerator.GetAll().Any(monitor =>
                left < monitor.Bounds.Right && right > monitor.Bounds.Left
                && top < monitor.Bounds.Bottom && bottom > monitor.Bounds.Top);
        };
        try
        {
            PinWindowPrewarm.Run();

            PinWindow window = Assert.Single(shown);
            Assert.True(visible);
            Assert.False(window.ShowActivated);
            Assert.False(active);
            Assert.False(topmost);
            Assert.Equal(0, opacity);
            Assert.False(onAMonitor);
            Assert.Same(window, PinWindowPrewarm.WindowForTest);
            Assert.False(window.IsVisible);
            Assert.False(window.IsClosed);

            // One window per UI thread.
            PinWindowPrewarm.Run();
            Assert.Single(shown);
        }
        finally
        {
            PinWindowPrewarm.ShownForTest = null;
            PinWindowPrewarm.ResetForTest();
        }
    });

    [Fact]
    public void KeptWindow_IsNoPin_AndOutlivesClosingEveryPin() => StaTestHost.Run(() =>
    {
        PinWindowPrewarm.ResetForTest();
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        int changes = 0;
        manager.OpenPinsChanged += () => changes++;
        try
        {
            PinWindowPrewarm.Run();
            PinWindow kept = PinWindowPrewarm.WindowForTest!;
            Assert.Equal(0, manager.Count);
            Assert.Empty(manager.SnapshotOpenPins());
            Assert.Equal(0, changes);

            var image = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
            image.Freeze();
            // Opened without activation, as a restored session pin is.
            manager.RestoreSessionPin(new PinManager.ClosedPin(PinContent.FromImage(image), 24, 16, 1, 1, null));
            Assert.Equal(1, manager.Count);
            manager.CloseAll();
            Assert.Equal(0, manager.Count);

            Assert.False(kept.IsClosed);
            Assert.False(kept.IsVisible);
            Assert.Same(kept, PinWindowPrewarm.WindowForTest);
        }
        finally
        {
            manager.CloseAllForExit();
            PinWindowPrewarm.ResetForTest();
        }
    });
}
