using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>How the converted picture of a display differs from its GDI picture, channel by channel.</summary>
/// <param name="StablePixels">Pixels the two GDI pictures taken around the read agree on; only these are compared.</param>
/// <param name="UnstablePixels">Pixels that changed between the two GDI pictures (a clock, a caret, a video).</param>
/// <param name="Equal">Channel samples with no difference.</param>
/// <param name="OffByOne">Channel samples one code value apart.</param>
/// <param name="OffByTwo">Channel samples two code values apart.</param>
/// <param name="OffBy3To5">Channel samples three to five code values apart.</param>
/// <param name="OffBy6To15">Channel samples six to fifteen code values apart.</param>
/// <param name="OffBy16OrMore">Channel samples sixteen or more code values apart.</param>
/// <param name="LargestDifference">Largest difference of any compared channel sample.</param>
/// <param name="ShareWithinOne">Share of compared channel samples at most one code value apart; 1 when nothing was compared.</param>
/// <param name="OutlierBounds">Bounding box, in picture pixels, of samples more than one code value apart.</param>
internal sealed record HdrPixelComparison(
    long StablePixels,
    long UnstablePixels,
    long Equal,
    long OffByOne,
    long OffByTwo,
    long OffBy3To5,
    long OffBy6To15,
    long OffBy16OrMore,
    int LargestDifference,
    double ShareWithinOne,
    string? OutlierBounds);

/// <summary>
/// Shows, for every display, what the HDR correction detects, reads and produces, next to the
/// GDI picture it replaces.
/// </summary>
/// <remarks>
/// <para>
/// Reachable via <c>MyCapture.exe --selftest-hdr-capture &lt;directory&gt;</c>. It shows no window,
/// reads no settings and touches neither the clipboard nor the pointer; its only output is the
/// directory it is given.
/// </para>
/// <para>
/// A display in HDR mode is captured the way the app captures it, and the report says how its
/// converted picture differs from the GDI picture. A display without HDR is read in scRGB anyway
/// (<see cref="HdrCaptureMode.ForceFloatRead"/>): the converted picture must then equal the GDI
/// picture, which checks the read, the rotation and the conversion on a PC that has no HDR on.
/// </para>
/// </remarks>
internal static class HdrCaptureSelfTest
{
    internal const string CommandLineSwitch = "--selftest-hdr-capture";
    internal const string ReportName = "hdr-capture-report.txt";
    internal const string SummaryName = "hdr-capture.json";

    /// <summary>
    /// Share of channel samples of an SDR desktop that must be within one code value of the GDI
    /// picture. Not all of them: the two pictures are taken a few milliseconds apart, and a
    /// handful of anti-aliased taskbar edges differ between the two readback paths.
    /// </summary>
    internal const double RequiredShareWithinOne = 0.999;

    private const int DetectionSamples = 1000;
    private const int OverheadSamples = 200;
    private const int TimingRepeats = 5;
    private const double PatternWhiteNits = 200, PatternPeakNits = 1000;
    private const int PatternWidth = 1280, PatternHeight = 600;

    internal static int Run(string outputDirectory)
    {
        string output = DiagnosticOutputPaths.Create(outputDirectory);
        var report = new StringBuilder();
        var failures = new List<string>();
        var monitorSummaries = new List<object>();
        object? spanningSummary = null;
        void Line(string text) => report.AppendLine(text);

        Line("MyCapture HDR capture self-test");
        Line($"UTC: {DateTimeOffset.UtcNow:u}");
        Line($"OS: {Environment.OSVersion.VersionString}, 64-bit process: {Environment.Is64BitProcess}, processors: {Environment.ProcessorCount}");
        Line($"{HdrCaptureModeSelection.EnvironmentVariable}: {Environment.GetEnvironmentVariable(HdrCaptureModeSelection.EnvironmentVariable) ?? "(not set)"}");
        Line(string.Empty);

        // ----- Detection -----
        long firstDetection = Stopwatch.GetTimestamp();
        IReadOnlyList<HdrDisplayState> displays = HdrDisplayDetector.Detect();
        double firstDetectionMs = Stopwatch.GetElapsedTime(firstDetection).TotalMilliseconds;
        double[] detection = new double[DetectionSamples];
        for (int sample = 0; sample < detection.Length; sample++)
        {
            long started = Stopwatch.GetTimestamp();
            _ = HdrDisplayDetector.Detect();
            detection[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(detection);
        Line($"Displays reported by the display configuration: {displays.Count}");
        foreach (HdrDisplayState display in displays)
        {
            Line(string.Create(CultureInfo.InvariantCulture,
                $"  {display.DeviceName}: HDR active={display.IsHdrActive}, advanced colour supported={display.AdvancedColorSupported} enabled={display.AdvancedColorEnabled}, " +
                $"wide colour enforced={display.WideColorEnforced}, force-disabled={display.AdvancedColorForceDisabled}, active colour mode={ColorModeName(display.ActiveColorMode)}, " +
                $"{display.BitsPerColorChannel} bits, encoding {display.ColorEncoding}, SDR white {display.SdrWhiteNits:0.#} nits (raw {display.SdrWhiteLevel?.ToString(CultureInfo.InvariantCulture) ?? "n/a"})"));
        }

        Line(string.Create(CultureInfo.InvariantCulture,
            $"Detection pass: first {firstDetectionMs:0.000}ms, then median {detection[detection.Length / 2]:0.0000}ms, p95 {detection[(int)(detection.Length * 0.95)]:0.0000}ms, max {detection[^1]:0.0000}ms (n={detection.Length})"));
        object dxgiOutputs = DescribeOutputs();
        if (dxgiOutputs is IReadOnlyList<DisplayColorInfo> outputs)
        {
            foreach (DisplayColorInfo info in outputs)
            {
                Line(string.Create(CultureInfo.InvariantCulture,
                    $"  DXGI {info.DeviceName}: bounds={info.Bounds}, {info.BitsPerColor} bits, colour space {info.ColorSpace} ({(info.AdvancedColor ? "HDR10" : "not HDR")}), peak {info.MaxLuminance:0} nits"));
            }
        }
        else
        {
            Line($"  DXGI outputs unavailable: {dxgiOutputs}");
        }

        Line(string.Empty);

        var gdiEngine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = HdrCaptureMode.Off };
        var autoEngine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = HdrCaptureMode.Auto };
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();

        // ----- What the correction costs a capture that needs none -----
        object overheadSummary = MeasureOverhead(gdiEngine, autoEngine, monitors, displays, Line);
        Line(string.Empty);

        // ----- Every display -----
        using Process process = Process.GetCurrentProcess();
        double privateBeforeReads = PrivateMegabytes(process);
        foreach (MonitorInfo monitor in monitors)
        {
            try
            {
                monitorSummaries.Add(CheckMonitor(output, monitor, displays, gdiEngine, failures, Line));
            }
            catch (Exception ex)
            {
                failures.Add($"{monitor.DeviceName}: {ex.GetType().Name}: {ex.Message}");
                Line($"  FAILED: {ex}");
            }

            Line(string.Empty);
        }

        // ----- A rectangle on two displays -----
        try
        {
            spanningSummary = CheckSpanningRectangle(output, monitors, displays, gdiEngine, failures, Line);
        }
        catch (Exception ex)
        {
            failures.Add($"spanning rectangle: {ex.GetType().Name}: {ex.Message}");
            Line($"  FAILED: {ex}");
        }

        Line(string.Empty);

        // ----- The kept D3D device -----
        bool deviceKept = ScreenCaptureEngine.HoldsHdrDevice;
        double privateWithDevice = PrivateMegabytes(process);
        bool released = ScreenCaptureEngine.ReleaseHdrDevice();
        double privateAfterRelease = PrivateMegabytes(process);
        Line(string.Create(CultureInfo.InvariantCulture,
            $"D3D device kept after the reads: {deviceKept}. Private memory {privateBeforeReads:0.0} MB before the reads, {privateWithDevice:0.0} MB with the device, {privateAfterRelease:0.0} MB after releasing it (released={released})."));
        Line(string.Empty);

        // ----- Tone operators on a synthetic HDR picture -----
        object patternSummary = RenderPattern(output, Line);
        Line(string.Empty);

        object conversionSummary = MeasureConversion(Line);
        Line(string.Empty);

        Line(failures.Count == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures.Count} problem(s))");
        foreach (string failure in failures)
        {
            Line($"  - {failure}");
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(output, SummaryName), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow,
            Os = Environment.OSVersion.VersionString,
            Environment.Is64BitProcess,
            Environment.ProcessorCount,
            Displays = displays,
            DxgiOutputs = dxgiOutputs,
            Detection = new
            {
                FirstMs = firstDetectionMs,
                MedianMs = detection[detection.Length / 2],
                P95Ms = detection[(int)(detection.Length * 0.95)],
                MaxMs = detection[^1],
                Samples = detection.Length,
            },
            CaptureOverhead = overheadSummary,
            Monitors = monitorSummaries,
            Spanning = spanningSummary,
            Device = new { KeptAfterReads = deviceKept, Released = released, PrivateBeforeReadsMb = privateBeforeReads, PrivateWithDeviceMb = privateWithDevice, PrivateAfterReleaseMb = privateAfterRelease },
            Pattern = patternSummary,
            Conversion = conversionSummary,
            RequiredShareWithinOne,
            Failures = failures,
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            // A display may report an unbounded luminance.
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }), new UTF8Encoding(false));
        File.WriteAllText(DiagnosticOutputPaths.Child(output, ReportName), report.ToString(), new UTF8Encoding(false));
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// One-pixel captures with the correction off and on, alternating, so the difference is the
    /// correction's own cost rather than drift. On a system without HDR that cost is the detection pass.
    /// </summary>
    private static object MeasureOverhead(ScreenCaptureEngine gdiEngine, ScreenCaptureEngine autoEngine,
        IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<HdrDisplayState> displays, Action<string> line)
    {
        MonitorInfo? sdrMonitor = monitors.FirstOrDefault(monitor => displays.All(display =>
            !display.IsHdrActive || !string.Equals(display.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)));
        if (sdrMonitor is null)
        {
            line("Capture without HDR: not measured, every display is in HDR mode.");
            return new { Measured = false };
        }

        var pixel = new RectD(sdrMonitor.Bounds.Left, sdrMonitor.Bounds.Top, 1, 1);
        _ = gdiEngine.CaptureRegion(pixel, includeCursor: false);
        _ = autoEngine.CaptureRegion(pixel, includeCursor: false);
        double[] off = new double[OverheadSamples], auto = new double[OverheadSamples];
        for (int sample = 0; sample < OverheadSamples; sample++)
        {
            // Alternate which goes first, so neither always runs on the warmer cache.
            bool offFirst = sample % 2 == 0;
            double first = Time(() => (offFirst ? gdiEngine : autoEngine).CaptureRegion(pixel, includeCursor: false));
            double second = Time(() => (offFirst ? autoEngine : gdiEngine).CaptureRegion(pixel, includeCursor: false));
            (off[sample], auto[sample]) = offFirst ? (first, second) : (second, first);
        }

        Array.Sort(off);
        Array.Sort(auto);
        double offMedian = off[OverheadSamples / 2], autoMedian = auto[OverheadSamples / 2];
        line(string.Create(CultureInfo.InvariantCulture,
            $"One-pixel capture on {sdrMonitor.DeviceName} (no HDR): correction off median {offMedian:0.0000}ms, automatic median {autoMedian:0.0000}ms, " +
            $"difference {autoMedian - offMedian:+0.0000;-0.0000}ms (n={OverheadSamples} each, alternating)"));
        return new { Measured = true, Monitor = sdrMonitor.DeviceName, OffMedianMs = offMedian, AutoMedianMs = autoMedian, DifferenceMs = autoMedian - offMedian, Samples = OverheadSamples };
    }

    private static object CheckMonitor(string output, MonitorInfo monitor, IReadOnlyList<HdrDisplayState> displays,
        ScreenCaptureEngine gdiEngine, List<string> failures, Action<string> line)
    {
        HdrDisplayState? state = displays.FirstOrDefault(display =>
            string.Equals(display.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
        bool hdr = state?.IsHdrActive == true;
        HdrCaptureMode mode = hdr ? HdrCaptureMode.Auto : HdrCaptureMode.ForceFloatRead;
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = mode };
        string name = SafeName(monitor.DeviceName);
        int width = monitor.PixelWidth, height = monitor.PixelHeight;
        line($"{monitor.DeviceName} {width}x{height} at {monitor.Bounds.Left:0},{monitor.Bounds.Top:0}, {monitor.Dpi} dpi: " +
            (hdr ? $"HDR on, captured as the app captures it (SDR white {state!.SdrWhiteNits:0.#} nits)" : "no HDR, scRGB read forced for the check"));

        BitmapSource before = null!, converted = null!;
        HdrCaptureReport? hdrReport = null;
        double gdiMs = Time(() => before = gdiEngine.CaptureRegion(monitor.Bounds, includeCursor: false));
        double convertedMs = Time(() => converted = engine.CaptureRegion(monitor.Bounds, includeCursor: false, out hdrReport));
        HdrOutputRead? read = hdrReport?.Outputs.FirstOrDefault();
        IReadOnlyList<HdrOutputRead>? lateRead = null;
        double? lateCaptureMs = null;
        if (read?.Outcome == HdrReadOutcome.TimedOut)
        {
            // The capture kept the GDI copy, as it must. The read itself went on: its timings name
            // the slow step, and the capture is taken again with what that read left ready.
            lateCaptureMs = convertedMs;
            lateRead = WaitForLateRead();
            line(string.Create(CultureInfo.InvariantCulture,
                $"  WARNING: the first read missed its budget and the capture kept the GDI copy after {convertedMs:0.0}ms ({read.Detail}). " +
                $"The read ended later with {(lateRead is { Count: > 0 } ? $"{lateRead[0].Outcome}; {Steps(lateRead[0])}" : "no report within five seconds")}."));
            convertedMs = Time(() => converted = engine.CaptureRegion(monitor.Bounds, includeCursor: false, out hdrReport));
            read = hdrReport?.Outputs.FirstOrDefault();
        }

        BitmapSource after = gdiEngine.CaptureRegion(monitor.Bounds, includeCursor: false);

        string gdiFile = $"{name}-gdi.png", convertedFile = $"{name}-converted.png";
        ImageCodec.SavePng(before, DiagnosticOutputPaths.Child(output, gdiFile));
        ImageCodec.SavePng(converted, DiagnosticOutputPaths.Child(output, convertedFile));
        line(string.Create(CultureInfo.InvariantCulture,
            $"  GDI capture {gdiMs:0.0}ms -> {gdiFile}; capture with the correction {convertedMs:0.0}ms -> {convertedFile}"));
        line("  " + Describe(hdrReport, read));
        if (converted.PixelWidth != before.PixelWidth || converted.PixelHeight != before.PixelHeight)
        {
            failures.Add($"{monitor.DeviceName}: the corrected capture is {converted.PixelWidth}x{converted.PixelHeight}, the GDI capture {before.PixelWidth}x{before.PixelHeight}");
        }

        if (read?.Outcome == HdrReadOutcome.NotNeeded)
        {
            // Advanced colour without HDR (a wide-gamut SDR display on a system that does not report
            // the active colour mode): the capture rightly kept the GDI copy.
            line($"  The display needed no correction: {read.Detail}.");
        }
        else if (read?.Applied != true)
        {
            failures.Add($"{monitor.DeviceName}: the scRGB read was not applied ({read?.Outcome.ToString() ?? "no read"}: {read?.Detail ?? "the capture reported no HDR work"})");
        }

        byte[] beforePixels = Pixels(before), convertedPixels = Pixels(converted), afterPixels = Pixels(after);
        HdrPixelComparison comparison = Compare(beforePixels, afterPixels, convertedPixels, before.PixelWidth, before.PixelHeight);
        line("  " + Describe(comparison));
        string? softKneeFile = null;
        object? brightness = null;
        if (hdr && read?.Applied == true)
        {
            // The GDI picture is the thing being corrected here, so equality is not expected.
            (double gdiMean, double convertedMean) = (MeanCode(beforePixels), MeanCode(convertedPixels));
            long pixels = (long)before.PixelWidth * before.PixelHeight;
            double aboveWhite = read is null || pixels == 0 ? 0 : read.PixelsAboveSdrWhite / (double)pixels;
            line(string.Create(CultureInfo.InvariantCulture,
                $"  Mean code value: GDI {gdiMean:0.0}, corrected {convertedMean:0.0}; {aboveWhite:P2} of the pixels were brighter than SDR white."));
            line("  Expected with HDR on: the corrected picture is darker than the GDI picture in mid-tones and equals the same content captured with HDR off.");
            var softKnee = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = mode, HdrToneOperator = HdrToneOperator.SoftKnee };
            softKneeFile = $"{name}-converted-soft-knee.png";
            ImageCodec.SavePng(softKnee.CaptureRegion(monitor.Bounds, includeCursor: false), DiagnosticOutputPaths.Child(output, softKneeFile));
            line($"  The same display with the soft-knee operator -> {softKneeFile}");
            brightness = new { GdiMeanCode = gdiMean, CorrectedMeanCode = convertedMean, ShareAboveSdrWhite = aboveWhite };
        }
        else if (read?.Applied == true && comparison.ShareWithinOne < RequiredShareWithinOne)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"{monitor.DeviceName}: only {comparison.ShareWithinOne:P4} of the stable channel samples are within one code value of the GDI picture (required {RequiredShareWithinOne:P1})"));
        }

        // Later captures: the device is kept now, so these show the steady cost next to GDI.
        var laterReads = new List<object>();
        double[] laterTotal = new double[TimingRepeats], laterGdi = new double[TimingRepeats];
        for (int repeat = 0; repeat < TimingRepeats; repeat++)
        {
            HdrCaptureReport? later = null;
            // This loop makes and drops full-display bitmaps back to back, which makes the collector
            // run. Collecting first keeps most of that out of the timed capture, and the pause that
            // still falls inside it is reported so that it is not read as a cost of the capture.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            laterGdi[repeat] = Time(() => gdiEngine.CaptureRegion(monitor.Bounds, includeCursor: false));
            TimeSpan paused = GC.GetTotalPauseDuration();
            laterTotal[repeat] = Time(() => engine.CaptureRegion(monitor.Bounds, includeCursor: false, out later));
            double pausedMs = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds;
            HdrOutputRead? laterRead = later?.Outputs.FirstOrDefault();
            laterReads.Add(new
            {
                CaptureMs = laterTotal[repeat], GdiCaptureMs = laterGdi[repeat], CorrectionMs = later?.TotalMs, Read = laterRead,
                CollectorPauseMs = pausedMs,
            });
            line(string.Create(CultureInfo.InvariantCulture,
                $"  later capture {repeat + 1}: {laterTotal[repeat]:0.0}ms with the correction ({later?.TotalMs ?? 0:0.0}ms of it the correction), GDI alone {laterGdi[repeat]:0.0}ms; {Steps(laterRead)}; " +
                $"collector pauses during it {pausedMs:0.0}ms"));
        }

        Array.Sort(laterTotal);
        Array.Sort(laterGdi);
        line(string.Create(CultureInfo.InvariantCulture,
            $"  Median of the later captures: {laterTotal[TimingRepeats / 2]:0.0}ms with the correction, {laterGdi[TimingRepeats / 2]:0.0}ms GDI alone."));
        return new
        {
            monitor.DeviceName,
            monitor.Bounds,
            monitor.Dpi,
            State = state,
            HdrActive = hdr,
            Mode = mode.ToString(),
            GdiFile = gdiFile,
            ConvertedFile = convertedFile,
            SoftKneeFile = softKneeFile,
            FirstCapture = new { GdiCaptureMs = gdiMs, CaptureMs = convertedMs, Report = hdrReport },
            FirstReadMissedBudget = lateCaptureMs is null ? null : new { CaptureMs = lateCaptureMs, LateRead = lateRead },
            Comparison = comparison,
            Brightness = brightness,
            LaterCaptures = laterReads,
            LaterMedianMs = laterTotal[TimingRepeats / 2],
            LaterGdiMedianMs = laterGdi[TimingRepeats / 2],
        };
    }

    /// <summary>A rectangle across the edge two displays share: each part must come from its own display.</summary>
    private static object? CheckSpanningRectangle(string output, IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<HdrDisplayState> displays, ScreenCaptureEngine gdiEngine, List<string> failures, Action<string> line)
    {
        RectD? found = FindSpanningRectangle(monitors);
        if (found is not { } rectangle)
        {
            line("Rectangle on two displays: not checked, no two displays share an edge.");
            return null;
        }

        // Displays in HDR mode are read as the app reads them; the forced read adds the others.
        bool anyHdr = displays.Any(display => display.IsHdrActive);
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance) { HdrMode = HdrCaptureMode.ForceFloatRead };
        BitmapSource before = gdiEngine.CaptureRegion(rectangle, includeCursor: false);
        BitmapSource converted = engine.CaptureRegion(rectangle, includeCursor: false, out HdrCaptureReport? report);
        BitmapSource after = gdiEngine.CaptureRegion(rectangle, includeCursor: false);
        ImageCodec.SavePng(before, DiagnosticOutputPaths.Child(output, "spanning-gdi.png"));
        ImageCodec.SavePng(converted, DiagnosticOutputPaths.Child(output, "spanning-converted.png"));
        HdrPixelComparison comparison = Compare(Pixels(before), Pixels(after), Pixels(converted), before.PixelWidth, before.PixelHeight);
        line($"Rectangle on two displays {rectangle}: spanning-gdi.png, spanning-converted.png");
        foreach (HdrOutputRead read in report?.Outputs ?? [])
        {
            line("  " + Describe(report, read));
        }

        line("  " + Describe(comparison));
        int applied = report?.Outputs.Count(read => read.Applied) ?? 0;
        if (applied < 2)
        {
            failures.Add($"spanning rectangle: {applied} of its two displays were read in scRGB");
        }
        else if (!anyHdr && comparison.ShareWithinOne < RequiredShareWithinOne)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"spanning rectangle: only {comparison.ShareWithinOne:P4} of the stable channel samples are within one code value of the GDI picture"));
        }

        return new { Rectangle = rectangle, Report = report, Comparison = comparison };
    }

    /// <summary>Up to 320 by 240 pixels centred on the first edge two displays share.</summary>
    internal static RectD? FindSpanningRectangle(IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (MonitorInfo first in monitors)
        foreach (MonitorInfo second in monitors)
        {
            RectD a = first.Bounds, b = second.Bounds;
            if (a.Right == b.Left)
            {
                double top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);
                double half = Math.Min(160, Math.Min(a.Width, b.Width));
                if (bottom - top >= 16)
                {
                    double height = Math.Min(240, bottom - top);
                    return new RectD(a.Right - half, Math.Floor(top + ((bottom - top - height) / 2)), half * 2, height);
                }
            }

            if (a.Bottom == b.Top)
            {
                double left = Math.Max(a.Left, b.Left), right = Math.Min(a.Right, b.Right);
                double half = Math.Min(120, Math.Min(a.Height, b.Height));
                if (right - left >= 16)
                {
                    double width = Math.Min(320, right - left);
                    return new RectD(Math.Floor(left + ((right - left - width) / 2)), a.Bottom - half, width, half * 2);
                }
            }
        }

        return null;
    }

    /// <summary>The synthetic HDR picture through both tone operators, for choosing between them by eye.</summary>
    private static object RenderPattern(string output, Action<string> line)
    {
        byte[] surface = HdrTestPattern.Render(PatternWidth, PatternHeight, PatternWhiteNits, PatternPeakNits);
        var files = new List<object>();
        foreach ((HdrToneOperator toneOperator, string file) in new[]
        {
            (HdrToneOperator.MaxChannel, "synthetic-hdr-default.png"),
            (HdrToneOperator.SoftKnee, "synthetic-hdr-soft-knee.png"),
        })
        {
            HdrToneMap map = HdrToneMap.Create(PatternWhiteNits, toneOperator, PatternPeakNits);
            var bgra = new byte[PatternWidth * PatternHeight * 4];
            long started = Stopwatch.GetTimestamp();
            long scaled = map.ConvertRows(surface, PatternWidth * 8, PatternWidth, PatternHeight, HdrSurfaceRotation.Identity,
                bgra, PatternWidth * 4, 0, PatternHeight);
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            BitmapSource bitmap = BitmapSource.Create(PatternWidth, PatternHeight, 96, 96, PixelFormats.Bgr32, null, bgra, PatternWidth * 4);
            bitmap.Freeze();
            ImageCodec.SavePng(bitmap, DiagnosticOutputPaths.Child(output, file));
            files.Add(new { Operator = toneOperator.ToString(), File = file, PixelsScaled = scaled, ConvertMs = milliseconds });
            line(string.Create(CultureInfo.InvariantCulture,
                $"Synthetic HDR picture ({PatternWidth}x{PatternHeight}, SDR white {PatternWhiteNits:0} nits, peak {PatternPeakNits:0} nits), {toneOperator}: {file} ({scaled} pixels scaled, {milliseconds:0.0}ms on one thread)"));
        }

        line("  Top to bottom: grey, red, green, blue, orange, sky blue and skin ramps (left half SDR, right half SDR white to peak), full-brightness SDR patches, a sky with a sun at the peak.");
        return new { WhiteNits = PatternWhiteNits, PeakNits = PatternPeakNits, Width = PatternWidth, Height = PatternHeight, Files = files };
    }

    /// <summary>
    /// The conversion alone at 3840x2160, which no display of the PC may have: an SDR picture
    /// (every pixel a table read) and the synthetic HDR picture (its highlights scaled), on one
    /// thread and in the row bands a capture uses.
    /// </summary>
    private static object MeasureConversion(Action<string> line)
    {
        const int width = 3840, height = 2160, runs = 9;
        var bgra = new byte[width * height * 4];
        var results = new List<object>();
        foreach ((string content, double peakNits) in new[] { ("SDR content", PatternWhiteNits), ("HDR content", PatternPeakNits) })
        {
            // A peak equal to SDR white renders the pattern without anything above white.
            byte[] surface = HdrTestPattern.Render(width, height, PatternWhiteNits, peakNits);
            HdrToneMap map = HdrToneMap.Create(PatternWhiteNits);
            long scaled = 0;
            double[] single = new double[runs], banded = new double[runs];
            int bands = Math.Max(1, Math.Min(Environment.ProcessorCount, height / 64));
            for (int run = 0; run < runs; run++)
            {
                single[run] = Time(() => scaled = map.ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, bgra, width * 4, 0, height));
                banded[run] = Time(() => Parallel.For(0, bands, band =>
                {
                    int first = height * band / bands, end = height * (band + 1) / bands;
                    _ = map.ConvertRows(surface, width * 8, width, height, HdrSurfaceRotation.Identity, bgra, width * 4, first, end - first);
                }));
            }

            Array.Sort(single);
            Array.Sort(banded);
            line(string.Create(CultureInfo.InvariantCulture,
                $"Conversion of {width}x{height} {content}: one thread median {single[runs / 2]:0.0}ms (best {single[0]:0.0}ms), {bands} bands median {banded[runs / 2]:0.0}ms (best {banded[0]:0.0}ms), n={runs}; {scaled} pixels above SDR white"));
            results.Add(new
            {
                Content = content, Width = width, Height = height, Bands = bands, PixelsAboveSdrWhite = scaled, Runs = runs,
                SingleThreadMedianMs = single[runs / 2], SingleThreadBestMs = single[0], BandedMedianMs = banded[runs / 2], BandedBestMs = banded[0],
            });
        }

        return results;
    }

    /// <summary>
    /// Compares the converted picture with the first GDI picture wherever the two GDI pictures agree.
    /// All three are top-down BGRX rows of <paramref name="width"/> by <paramref name="height"/> pixels.
    /// </summary>
    internal static HdrPixelComparison Compare(
        ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, ReadOnlySpan<byte> converted, int width, int height)
    {
        int length = checked(width * height * 4);
        if (before.Length < length || after.Length < length || converted.Length < length)
        {
            throw new ArgumentException("A picture is smaller than its dimensions.");
        }

        long stable = 0, unstable = 0, equal = 0, one = 0, two = 0, upToFive = 0, upToFifteen = 0, more = 0;
        int largest = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                if (before[offset] != after[offset] || before[offset + 1] != after[offset + 1] || before[offset + 2] != after[offset + 2])
                {
                    unstable++;
                    continue;
                }

                stable++;
                for (int channel = 0; channel < 3; channel++)
                {
                    int difference = Math.Abs(before[offset + channel] - converted[offset + channel]);
                    largest = Math.Max(largest, difference);
                    switch (difference)
                    {
                        case 0: equal++; break;
                        case 1: one++; break;
                        case 2: two++; break;
                        case <= 5: upToFive++; break;
                        case <= 15: upToFifteen++; break;
                        default: more++; break;
                    }

                    if (difference > 1)
                    {
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
        }

        long samples = stable * 3;
        return new HdrPixelComparison(stable, unstable, equal, one, two, upToFive, upToFifteen, more, largest,
            samples == 0 ? 1.0 : (equal + one) / (double)samples,
            right < 0 ? null : string.Create(CultureInfo.InvariantCulture, $"({left},{top})-({right},{bottom})"));
    }

    private static string Describe(HdrPixelComparison comparison) => string.Create(CultureInfo.InvariantCulture,
        $"Against the GDI picture: {comparison.StablePixels} stable pixels compared, {comparison.UnstablePixels} that changed between the two GDI pictures left out. " +
        $"Channel differences 0: {comparison.Equal}, 1: {comparison.OffByOne}, 2: {comparison.OffByTwo}, 3-5: {comparison.OffBy3To5}, 6-15: {comparison.OffBy6To15}, 16+: {comparison.OffBy16OrMore}; " +
        $"largest {comparison.LargestDifference}; within one: {comparison.ShareWithinOne:P4}; differences above one lie in {comparison.OutlierBounds ?? "no pixel"}.");

    private static string Describe(HdrCaptureReport? report, HdrOutputRead? read)
    {
        if (report is null || read is null)
        {
            return "The capture reported no HDR work.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{read.DeviceName}: {read.Outcome}{(read.Detail is null ? string.Empty : $" ({read.Detail})")}; surface format {read.SurfaceFormat} ({FormatName(read.SurfaceFormat)}), " +
            $"rotation {read.Rotation}, DXGI colour space {read.ColorSpace}, peak {read.MaxLuminance:0} nits, divided by SDR white {read.SdrWhiteNits:0.#} nits, " +
            $"{read.PixelsAboveSdrWhite} pixels above SDR white; detection {report.DetectMs:0.000}ms, read {report.ReadMs:0.0}ms, patch {report.PatchMs:0.0}ms; {Steps(read)}");
    }

    private static string Steps(HdrOutputRead? read) => read is null
        ? "no read"
        : string.Create(CultureInfo.InvariantCulture,
            $"steps: find output {read.FindOutputMs:0.0}, device {read.DeviceMs:0.0}{(read.DeviceReused ? " (kept)" : " (created)")}, duplicate {read.DuplicateMs:0.0}, " +
            $"frame {read.AcquireMs:0.0} in {read.AcquireCalls} call(s), copy {read.CopyMs:0.0}, convert {read.ConvertMs:0.0} ms");

    private static string FormatName(uint format) => format switch
    {
        10 => "R16G16B16A16_FLOAT",
        87 => "B8G8R8A8_UNORM",
        0 => "not read",
        _ => "other",
    };

    private static string ColorModeName(int? mode) => mode switch
    {
        null => "not reported",
        0 => "SDR",
        1 => "wide colour gamut",
        2 => "HDR",
        _ => mode.Value.ToString(CultureInfo.InvariantCulture),
    };

    private static object DescribeOutputs()
    {
        try
        {
            return DisplayColorProbe.Describe();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgr32 || bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static double MeanCode(byte[] pixels)
    {
        long sum = 0;
        for (int offset = 0; offset + 3 < pixels.Length; offset += 4)
        {
            sum += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
        }

        return pixels.Length == 0 ? 0 : sum / (pixels.Length / 4 * 3.0);
    }

    /// <summary>The report of a read that ended after its capture stopped waiting, or null after five seconds.</summary>
    private static IReadOnlyList<HdrOutputRead>? WaitForLateRead()
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            if (ScreenCaptureEngine.TakeLateHdrRead() is { } late)
            {
                return late;
            }

            Thread.Sleep(20);
        }

        return null;
    }

    private static double Time(Action action)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double PrivateMegabytes(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64 / (1024.0 * 1024.0);
    }

    private static string SafeName(string deviceName)
    {
        var name = new StringBuilder(deviceName.Length);
        foreach (char character in deviceName)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                name.Append(character);
            }
        }

        return name.Length == 0 ? "display" : name.ToString();
    }
}
