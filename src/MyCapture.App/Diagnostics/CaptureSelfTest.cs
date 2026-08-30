using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Captures the screen once and writes the result plus a report to disk.
/// </summary>
/// <remarks>
/// <para>
/// Reachable via <c>MyCapture.exe --selftest-capture &lt;directory&gt;</c>.
/// </para>
/// <para>
/// Exists because the parts of a capture tool most likely to be wrong — per-monitor
/// DPI, virtual-desktop coordinates on multi-monitor layouts with negative origins,
/// and whether layered windows appear at all — cannot be verified by a unit test.
/// They only manifest against a real display configuration. This gives a one-command
/// way to confirm them on any machine, including a user's when diagnosing a report.
/// </para>
/// </remarks>
internal static class CaptureSelfTest
{
    public const string CommandLineSwitch = "--selftest-capture";

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);

        Line("MyCapture capture self-test");
        Line($"UTC: {DateTimeOffset.UtcNow:u}");
        Line($"OS: {Environment.OSVersion.VersionString}");
        Line($"64-bit process: {Environment.Is64BitProcess}");
        Line(string.Empty);

        // ----- Monitor topology -----
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();

        Line($"Monitors: {monitors.Count}");
        foreach (MonitorInfo m in monitors)
        {
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"  {m.DeviceName,-16} bounds={m.Bounds} work={m.WorkArea} dpi={m.Dpi} scale={m.ScaleFactor:0.00} primary={m.IsPrimary}"));
        }

        Line($"Virtual desktop: {desktop}");
        Line(string.Empty);

        // A mixed-scaling layout is the configuration most likely to expose a
        // coordinate bug, so it is called out explicitly in the report.
        bool mixedScaling = monitors.Select(m => m.Dpi).Distinct().Count() > 1;
        Line($"Mixed DPI across monitors: {mixedScaling}");

        bool negativeOrigin = monitors.Any(m => m.Bounds.Left < 0 || m.Bounds.Top < 0);
        Line($"Monitor at negative origin: {negativeOrigin}");
        Line(string.Empty);

        // ----- Capture -----
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var failures = new List<string>();

        // Mirrors normal tray startup: initialise GDI/WPF imaging before the user can
        // press the capture hotkey. The reported monitor timings below therefore
        // measure the real steady-state hotkey path rather than one-time JIT cost.
        double prewarmMs = engine.Prewarm();
        Line(string.Create(CultureInfo.InvariantCulture, $"Capture pipeline prewarm: {prewarmMs:0.0}ms"));
        Line("Warmed capture budget: <= 100ms");

        foreach (MonitorInfo monitor in monitors)
        {
            string safeName = monitor.DeviceName
                .Replace("\\", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal);

            try
            {
                FrozenFrame frame = engine.CaptureMonitor(monitor, includeCursor: false);
                string path = Path.Combine(outputDirectory, $"monitor-{safeName}.png");
                long bytes = ImageCodec.SavePng(frame.Bitmap, path);

                bool sizeMatches =
                    frame.PixelWidth == monitor.PixelWidth &&
                    frame.PixelHeight == monitor.PixelHeight;

                Line(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {monitor.DeviceName}: {frame.PixelWidth}x{frame.PixelHeight} " +
                    $"in {frame.ElapsedMilliseconds:0.0}ms, {bytes / 1024.0:0} KB -> {Path.GetFileName(path)}"));

                if (frame.ElapsedMilliseconds > 100.0)
                {
                    string message =
                        $"{monitor.DeviceName}: warmed capture took {frame.ElapsedMilliseconds:0.0}ms " +
                        "(budget is 100ms)";
                    failures.Add(message);
                    Line($"    SLOW: {message}");
                }

                if (!sizeMatches)
                {
                    // The classic DPI-virtualisation symptom: the captured bitmap comes
                    // back at the unscaled size instead of the monitor's real pixels.
                    string message =
                        $"{monitor.DeviceName}: captured {frame.PixelWidth}x{frame.PixelHeight} " +
                        $"but the monitor is {monitor.PixelWidth}x{monitor.PixelHeight}";
                    failures.Add(message);
                    Line($"    MISMATCH: {message}");
                }

                if (IsUniformlyBlank(frame.Bitmap))
                {
                    string message = $"{monitor.DeviceName}: captured image is a single flat colour";
                    failures.Add(message);
                    Line($"    SUSPECT: {message}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{monitor.DeviceName}: {ex.Message}");
                Line($"    FAILED: {ex.Message}");
            }
        }

        Line(string.Empty);

        // ----- Virtual desktop capture -----
        try
        {
            FrozenFrame full = engine.CaptureVirtualDesktop(includeCursor: true);
            string path = Path.Combine(outputDirectory, "virtual-desktop.png");
            long bytes = ImageCodec.SavePng(full.Bitmap, path);

            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"Virtual desktop: {full.PixelWidth}x{full.PixelHeight} " +
                $"in {full.ElapsedMilliseconds:0.0}ms, {bytes / 1024.0:0} KB"));

            if (full.PixelWidth != (int)desktop.Width || full.PixelHeight != (int)desktop.Height)
            {
                string message =
                    $"virtual desktop captured {full.PixelWidth}x{full.PixelHeight} " +
                    $"but bounds are {(int)desktop.Width}x{(int)desktop.Height}";
                failures.Add(message);
                Line($"  MISMATCH: {message}");
            }

            // Exercise the crop path with a region that is deliberately offset, since
            // an origin bug only shows up when the crop is not at 0,0.
            var cropRegion = new RectD(
                full.PixelWidth * 0.25, full.PixelHeight * 0.25,
                Math.Min(400, full.PixelWidth * 0.5), Math.Min(300, full.PixelHeight * 0.5));

            BitmapSource cropped = ScreenCaptureEngine.Crop(full, cropRegion);
            ImageCodec.SavePng(cropped, Path.Combine(outputDirectory, "crop-offset.png"));
            Line($"Offset crop: {cropped.PixelWidth}x{cropped.PixelHeight} -> crop-offset.png");

            BitmapSource thumb = ImageCodec.CreateThumbnail(full.Bitmap, 320);
            ImageCodec.SaveJpeg(
                thumb, Path.Combine(outputDirectory, "thumbnail.jpg"), ImageCodec.ThumbnailJpegQuality);
            Line($"Thumbnail: {thumb.PixelWidth}x{thumb.PixelHeight} -> thumbnail.jpg");
        }
        catch (Exception ex)
        {
            failures.Add($"virtual desktop: {ex.Message}");
            Line($"Virtual desktop FAILED: {ex.Message}");
        }

        Line(string.Empty);
        Line(failures.Count == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} problem(s))");
        foreach (string failure in failures)
        {
            Line($"  - {failure}");
        }

        string reportPath = Path.Combine(outputDirectory, "selftest-report.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Detects a capture that came back as one flat colour.
    /// </summary>
    /// <remarks>
    /// A pure black or pure white frame is the signature of a blocked capture — a
    /// protected-content window, a secure desktop, or a driver refusing the blit. It
    /// is worth flagging because the file itself looks perfectly valid.
    /// </remarks>
    private static bool IsUniformlyBlank(BitmapSource bitmap)
    {
        const int samplesPerAxis = 8;

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        if (width < samplesPerAxis || height < samplesPerAxis)
        {
            return false;
        }

        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        byte[] first = new byte[4];
        bool haveFirst = false;

        for (int iy = 0; iy < samplesPerAxis; iy++)
        {
            for (int ix = 0; ix < samplesPerAxis; ix++)
            {
                int x = (int)((ix + 0.5) / samplesPerAxis * width);
                int y = (int)((iy + 0.5) / samplesPerAxis * height);
                x = Math.Clamp(x, 0, width - 1);
                y = Math.Clamp(y, 0, height - 1);

                byte[] pixel = new byte[4];
                converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);

                if (!haveFirst)
                {
                    Array.Copy(pixel, first, 4);
                    haveFirst = true;
                    continue;
                }

                // Compare only the colour channels; BitBlt leaves alpha undefined.
                if (pixel[0] != first[0] || pixel[1] != first[1] || pixel[2] != first[2])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
