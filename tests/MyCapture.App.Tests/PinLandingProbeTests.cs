using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Opt-in, retained synthetic measurements; never reads or writes the clipboard.</summary>
public sealed class PinLandingProbeTests
{
    [Fact]
    public void PinLanding_CostProbe_WhenRequested()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_LANDING_PROBE_ROOT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Assert.True(Path.IsPathFullyQualified(output));
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")));
        Directory.CreateDirectory(output);
        StaTestHost.Run(() => RunProbe(output));
    }

    private static void RunProbe(string output)
    {
        var rows = new List<Sample>();
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        IntPtr previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previousDpi);
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        MonitorInfo monitor = MonitorEnumerator.GetAll().First();
        string environment = $"UTC={DateTime.UtcNow:O}\nOS={Environment.OSVersion}\nRuntime={RuntimeInformation.FrameworkDescription}\nWpfTier={RenderCapability.Tier >> 16}\nRemote={SystemParameters.IsRemoteSession}\nMonitor={monitor.DeviceName}\nScale={monitor.ScaleFactor}\nDataRoot={Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")}\nTemp={Path.GetTempPath()}\n";
        Exception? failure = null;
        try
        {
            foreach ((int width, int height) in new[] { (300, 200), (800, 500), (1600, 1000) })
            {
                BitmapSource bitmap = Fixture(width, height);
                // Prime both paths before ten alternating pairs. Keep the real eligibility and throttle.
                Measure(bitmap, width, height, false, monitor, 0);
                Measure(bitmap, width, height, true, monitor, 0);
                for (int repeat = 1; repeat <= 10; repeat++)
                {
                    foreach (bool decorated in repeat % 2 == 0 ? new[] { true, false } : new[] { false, true })
                        rows.Add(Measure(bitmap, width, height, decorated, monitor, repeat));
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi);
            File.WriteAllText(Path.Combine(output, "environment.txt"), environment + "Failure=" + failure);
            File.WriteAllLines(Path.Combine(output, "samples.csv"),
                new[] { "width_dip,height_dip,decorated,repeat,first_callback_ms,show_return_ms,max_gap_ms,over_25ms,callbacks,ambient,overlay_pixels,foreground_same,foreground_observed,actual_width_dip,actual_height_dip" }
                    .Concat(rows.Select(r => string.Join(',', r.Width, r.Height, r.Decorated, r.Repeat,
                        F(r.First), F(r.Show), F(r.MaxGap), r.Over25, r.Callbacks, r.Ambient, F(r.OverlayPixels), r.ForegroundSame, r.ForegroundObserved, F(r.ActualWidth), F(r.ActualHeight)))), new UTF8Encoding(false));
            var summary = new StringBuilder("First CompositionTarget.Rendering callback after Loaded, timed from Show entry; this is not a DWM-present measurement.\nTen warm alternating pairs per size; wall-clock gaps count distinct RenderingTime callbacks over the landing duration after Show returns. All evidence and synthetic fixtures are retained.\n\n");
            summary.AppendLine("| DIP | none median ms | effect median ms | increase ms | max gap none/effect ms | >25ms none/effect | ambient | overlay pixels |");
            summary.AppendLine("| --- | ---: | ---: | ---: | --- | --- | --- | ---: |");
            foreach (IGrouping<int, Sample> group in rows.GroupBy(r => r.Width))
            {
                Sample[] off = group.Where(r => !r.Decorated).ToArray();
                Sample[] on = group.Where(r => r.Decorated).ToArray();
                if (off.Length == 0 || on.Length == 0) continue;
                summary.AppendLine($"| {group.Key}×{group.First().Height} | {F(Median(off.Select(r => r.First)))} | {F(Median(on.Select(r => r.First)))} | {F(Median(on.Select(r => r.First)) - Median(off.Select(r => r.First)))} | {F(off.Max(r => r.MaxGap))}/{F(on.Max(r => r.MaxGap))} | {off.Sum(r => r.Over25)}/{on.Sum(r => r.Over25)} | {on[0].Ambient} | {F(on[0].OverlayPixels)} |");
            }
            File.WriteAllText(Path.Combine(output, "summary.md"), summary.ToString());
        }
        Assert.Equal(60, rows.Count);
        Assert.All(rows, row => Assert.True(row.Callbacks >= 2, "Insufficient compositor callbacks; cannot assess pacing."));
        Assert.All(rows, row => Assert.True(row.ForegroundSame, "Passive pin/decoration changed the foreground window."));
        foreach (var group in rows.GroupBy(r => r.Width))
            Assert.True(Median(group.Where(r => r.Decorated).Select(r => r.First)) - Median(group.Where(r => !r.Decorated).Select(r => r.First)) < 16.7,
                $"{group.Key} DIP exceeded the requested median first-frame budget; see retained samples.");
        Assert.All(rows, row => Assert.True(row.ForegroundObserved, "Foreground HWND unavailable; measurements retained but foreground safety remains unverified."));
    }

    private static Sample Measure(BitmapSource bitmap, int width, int height, bool decorated, MonitorInfo monitor, int repeat)
    {
        double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        var pin = new PinWindow(PinContent.FromImage(bitmap), new PinViewState(width, height, 1, 1, 0.1),
            monitor.WorkArea.Left / scale + 80, monitor.WorkArea.Top / scale + 80, () => new PinSettings())
        { ShowActivated = false, ShowInTaskbar = false };
        pin.PlaceAtPhysicalOnShow(new RectD(monitor.WorkArea.Left + 80 * scale, monitor.WorkArea.Top + 80 * scale, width * scale, height * scale));
        var clock = new Stopwatch();
        var callbacks = new List<double>();
        TimeSpan? lastFrame = null;
        double show = 0;
        bool loaded = false;
        pin.Loaded += (_, _) => loaded = true;
        EventHandler rendered = (_, args) =>
        {
            TimeSpan time = ((RenderingEventArgs)args).RenderingTime;
            if (!loaded || time == lastFrame) return;
            lastFrame = time;
            if (clock.Elapsed.TotalMilliseconds <= show + PinLandingDecoration.DurationMs) callbacks.Add(clock.Elapsed.TotalMilliseconds);
        };
        IntPtr foreground = GetForegroundWindow();
        CompositionTarget.Rendering += rendered;
        try
        {
            clock.Start();
            pin.Show();
            show = clock.Elapsed.TotalMilliseconds;
            if (decorated) pin.StartLandingEffect();
            PinLandingDecoration? effect = pin.LandingDecoration;
            Assert.True(decorated == (effect is not null), $"Decoration unavailable: requested={decorated}, remote={SystemParameters.IsRemoteSession}, layered={pin.AllowsTransparency}");
            bool ambient = effect?.Layout.HasAmbientEffects ?? false;
            double area = effect is null ? 0 : effect.Layout.PhysicalBounds.Width * effect.Layout.PhysicalBounds.Height;
            Pump(600);
            // A pin larger than this monitor's work area opens reduced to fit (each side clamped);
            // the sample records the size that was actually measured.
            Assert.InRange(pin.ActualWidth, 1, width + 0.5);
            Assert.InRange(pin.ActualHeight, 1, height + 0.5);
            if (pin.ActualWidth >= width - 0.5 && pin.ActualHeight >= height - 0.5)
            {
                Assert.Equal(width, pin.ActualWidth, 2);
                Assert.Equal(height, pin.ActualHeight, 2);
            }
            Assert.Equal(scale, VisualTreeHelper.GetDpi(pin).DpiScaleX, 2);
            double[] gaps = callbacks.Zip(callbacks.Skip(1), (a, b) => b - a).ToArray();
            return new Sample(width, height, decorated, repeat, callbacks.FirstOrDefault(double.NaN), show,
                gaps.Length == 0 ? double.NaN : gaps.Max(), gaps.Count(g => g > 25), callbacks.Count, ambient, area,
                foreground == GetForegroundWindow(), foreground != IntPtr.Zero && GetForegroundWindow() != IntPtr.Zero, pin.ActualWidth, pin.ActualHeight);
        }
        finally
        {
            CompositionTarget.Rendering -= rendered;
            pin.Close();
            Pump(40);
        }
    }

    private static BitmapSource Fixture(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte value = (byte)((y % 24 < 2 || x % 80 < 2) ? 90 : 235);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static double Median(IEnumerable<double> numbers)
    {
        double[] values = numbers.Order().ToArray();
        return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    }
    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    private sealed record Sample(int Width, int Height, bool Decorated, int Repeat, double First, double Show, double MaxGap,
        int Over25, int Callbacks, bool Ambient, double OverlayPixels, bool ForegroundSame, bool ForegroundObserved, double ActualWidth, double ActualHeight);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
