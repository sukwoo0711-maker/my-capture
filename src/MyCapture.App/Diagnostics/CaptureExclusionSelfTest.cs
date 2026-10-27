using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Diagnostics;

/// <summary>Checks real desktop exclusion against owned pixels through the product grab path.</summary>
internal static class CaptureExclusionSelfTest
{
    internal const string CommandLineSwitch = "--selftest-capture-exclusion";
    internal const string ReportName = "capture-exclusion-report.txt";
    internal static readonly Color[] PatternColors =
        [Color.FromRgb(36, 92, 168), Color.FromRgb(214, 156, 42), Color.FromRgb(58, 162, 106), Color.FromRgb(146, 68, 182)];
    private static readonly Color CoverColor = Color.FromRgb(241, 31, 137);

    internal static int Run(string outputDirectory)
    {
        string output = DiagnosticOutputPaths.Create(outputDirectory);
        var failures = new List<string>();
        var samples = new List<object>();
        Window? fixture = null, cover = null;
        RectD bounds = RectD.Empty;
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        try
        {
            MonitorInfo monitor = MonitorEnumerator.GetPrimary();
            bounds = new RectD(monitor.WorkArea.Left + 32, monitor.WorkArea.Top + 32,
                Math.Min(360, monitor.WorkArea.Width - 64), Math.Min(240, monitor.WorkArea.Height - 64)).ToPixelBounds();
            if (bounds.Width < 80 || bounds.Height < 80) throw new InvalidOperationException("The primary work area is too small for the fixture.");
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int index = 0; index < PatternColors.Length; index++)
            {
                var cell = new Border { Background = new SolidColorBrush(PatternColors[index]) };
                Grid.SetRow(cell, index / 2); Grid.SetColumn(cell, index % 2);
                grid.Children.Add(cell);
            }
            fixture = CreateWindow(bounds, monitor.ScaleFactor, layered: false, grid);
            Show(fixture, bounds);
            Pump(350);
            Check("fixture-baseline", expectedCover: false);

            // An opaque selector is always used; layered cards exist only on hosts whose
            // real display-affinity probe supports them. Neither mode assumes success.
            bool layeredSupported = CaptureWindowExclusion.SupportsLayeredWindows;
            foreach (bool layered in layeredSupported ? new[] { false, true } : new[] { false })
            {
                string mode = layered ? "layered" : "opaque";
                cover = CreateWindow(bounds, monitor.ScaleFactor, layered,
                    new Border { Background = new SolidColorBrush(CoverColor) });
                Show(cover, bounds);
                Pump(200);
                // Prove the covering window really occupies the sampled pixels before
                // enabling affinity. Otherwise an off-screen/never-shown window could pass.
                Check(mode + "-visible-control", expectedCover: true);
                bool applied = CaptureWindowExclusion.TryApply(cover);
                samples.Add(new { Stage = mode + "-apply", Applied = applied });
                if (!applied) throw new InvalidOperationException(mode + " capture exclusion was rejected.");
                Pump(200);
                Check(mode + "-visible-excluded", expectedCover: false);
                cover.Close();
                cover = null;
                // No sleep, pump or DwmFlush between dismissal and the product grab.
                Check(mode + "-immediate-dismissal", expectedCover: false);
                for (int repeat = 0; repeat < 10; repeat++)
                {
                    cover = CreateWindow(bounds, monitor.ScaleFactor, layered,
                        new Border { Background = new SolidColorBrush(CoverColor) });
                    if (!CaptureWindowExclusion.TryApply(cover))
                        throw new InvalidOperationException(mode + " repeated exclusion was rejected.");
                    Show(cover, bounds);
                    Pump(40);
                    cover.Close();
                    cover = null;
                    Check($"{mode}-repeat-{repeat + 1}", expectedCover: false);
                }
            }
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally { cover?.Close(); fixture?.Close(); }

        File.WriteAllText(DiagnosticOutputPaths.Child(output, "capture-exclusion.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow, Os = Environment.OSVersion.ToString(), Bounds = bounds,
            GrabPath = "ScreenCaptureEngine.CaptureVirtualDesktop(includeCursor: false)",
            Samples = samples, Failures = failures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(DiagnosticOutputPaths.Child(output, ReportName), failures.Count == 0
            ? "PASS: known fixture survived visible exclusion and immediate dismissal through the product desktop grab.\n"
            : "FAIL\n" + string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;

        void Check(string stage, bool expectedCover)
        {
            long started = Stopwatch.GetTimestamp();
            FrozenFrame frame = engine.CaptureVirtualDesktop(includeCursor: false);
            int mismatches = CountMismatches(frame.Bitmap, frame.ScreenBounds, bounds, expectedCover);
            samples.Add(new { Stage = stage, Mismatches = mismatches, GrabMs = frame.ElapsedMilliseconds,
                TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            if (mismatches != 0) throw new InvalidOperationException($"{stage}: {mismatches} sample pixels did not match the owned fixture.");
        }
    }

    internal static int CountMismatches(BitmapSource bitmap, RectD frameBounds, RectD fixture, bool expectedCover)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Bgr32
            ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int mismatch = 0;
        byte[] pixel = new byte[4];
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            int x = (int)(fixture.Left - frameBounds.Left + fixture.Width * (column + 0.5) / 4);
            int y = (int)(fixture.Top - frameBounds.Top + fixture.Height * (row + 0.5) / 4);
            if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight) { mismatch++; continue; }
            source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            Color expected = expectedCover ? CoverColor : PatternColors[(row / 2) * 2 + column / 2];
            if (Math.Abs(pixel[0] - expected.B) > 12 || Math.Abs(pixel[1] - expected.G) > 12 || Math.Abs(pixel[2] - expected.R) > 12)
                mismatch++;
        }
        return mismatch;
    }

    private static Window CreateWindow(RectD bounds, double scale, bool layered, UIElement content)
    {
        var window = new Window
        {
            Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, AllowsTransparency = layered,
            Background = Brushes.Transparent, Content = content,
            Left = bounds.Left / scale, Top = bounds.Top / scale,
            Width = bounds.Width / scale, Height = bounds.Height / scale,
        };
        FluidMotion.SetWindowEntrance(window, false);
        window.SourceInitialized += (_, _) =>
        {
            CaptureWindowExclusion.DisableTransitions(window);
            WindowStyleFacade.SetClickThrough(new WindowInteropHelper(window).Handle, enabled: true);
        };
        return window;
    }

    private static void Show(Window window, RectD bounds)
    {
        window.Show();
        PhysicalWindowPositioner.PlaceTopmostWithoutActivation(new WindowInteropHelper(window).Handle, bounds);
        window.UpdateLayout();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}
