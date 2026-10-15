using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Explicit synthetic-only workload; all captured pixels are covered by the owned fixture.</summary>
internal static class RecordingPerformanceSelfTest
{
    internal const string CommandLineSwitch = "--selftest-recording-performance";

    /// <summary>Opaque fixture colour every pixel check compares against.</summary>
    internal static readonly Color FixtureColor = Color.FromRgb(18, 90, 166);

    private const int FixtureChannelTolerance = 12;
    private const int FixtureWidth = 1400;
    private const int FixtureHeight = 850;
    private static readonly (int Width, int Height)[] SustainedSizes = [(320, 240), (1280, 720)];

    internal static int Run(string outputDirectory)
    {
        outputDirectory = DiagnosticOutputPaths.Create(outputDirectory);
        var measurements = new List<object>();
        var failures = new List<string>();
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        MonitorInfo monitor = SelectFixtureMonitor(MonitorEnumerator.GetFromCursor(), MonitorEnumerator.GetAll());
        Window fixture = CreateFixture();
        try
        {
            fixture.Show();
            RectD fixtureBounds = FixtureBoundsOn(monitor);
            PhysicalWindowPositioner.PlaceTopmost(new System.Windows.Interop.WindowInteropHelper(fixture).Handle, fixtureBounds);
            fixture.UpdateLayout();
            Pump(250);
            // Overlay checks are only meaningful, and recordings only synthetic, while the
            // owned fixture is what the capture path actually sees.
            if (VerifyFixtureCoverage(engine, fixtureBounds, measurements, failures))
                Measure(engine, fixtureBounds, outputDirectory, measurements, failures);
        }
        catch (Exception ex)
        {
            failures.Add(ex.ToString());
        }
        finally
        {
            fixture.Close();
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, "recording-performance.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow,
            Os = Environment.OSVersion.ToString(),
            Build = typeof(RecordingPerformanceSelfTest).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion,
            LogicalProcessors = Environment.ProcessorCount,
            CpuNormalization = "process CPU milliseconds / wall milliseconds / logical processors * 100",
            Limitations = "Synthetic static source. Other workstation/agent load is uncontrolled. Interleaved capture comparison isolates resource reuse; sustained clips measure current implementation only. No before/after FPS claim.",
            Measurements = measurements,
            Failures = failures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(DiagnosticOutputPaths.Child(outputDirectory, "recording-performance-report.txt"),
            (failures.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL") + Environment.NewLine + string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Plain opaque fixture. The app-wide implicit Window style enables ModernWindowChrome,
    /// which replaces Background with Surface.Base (translucent over the WPF client background on glass themes) once
    /// the HWND exists. The fixture opts out so the capture path sees <see cref="FixtureColor"/>
    /// and not the theme surface the overlay checks cannot tell apart from overlay pixels.
    /// </summary>
    internal static Window CreateFixture() => new()
    {
        Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false, ShowActivated = false, Topmost = true,
        Background = new SolidColorBrush(FixtureColor), Width = FixtureWidth, Height = FixtureHeight,
    };

    internal static RectD FixtureBoundsOn(MonitorInfo monitor) => new(monitor.Bounds.Left + 20, monitor.Bounds.Top + 20,
        Math.Min(FixtureWidth, monitor.Bounds.Width - 40), Math.Min(FixtureHeight, monitor.Bounds.Height - 40));

    internal static bool CanCover(RectD fixtureBounds, int width, int height) =>
        fixtureBounds.Width >= width + 20 && fixtureBounds.Height >= height + 20;

    /// <summary>
    /// The cursor display when it can hold every sustained size, otherwise the primary or the
    /// first other display that can, so a narrow or portrait cursor display does not make the
    /// result depend on where the pointer happens to rest.
    /// </summary>
    internal static MonitorInfo SelectFixtureMonitor(MonitorInfo cursor, IReadOnlyList<MonitorInfo> all)
    {
        static bool Fits(MonitorInfo monitor) =>
            SustainedSizes.All(size => CanCover(FixtureBoundsOn(monitor), size.Width, size.Height));
        return Fits(cursor) ? cursor : all.Where(Fits).OrderByDescending(monitor => monitor.IsPrimary).FirstOrDefault() ?? cursor;
    }

    private static bool VerifyFixtureCoverage(ScreenCaptureEngine engine, RectD fixtureBounds,
        List<object> measurements, List<string> failures)
    {
        var inset = new RectD(fixtureBounds.Left + 10, fixtureBounds.Top + 10, fixtureBounds.Width - 20, fixtureBounds.Height - 20);
        int width = (int)inset.Width, height = (int)inset.Height, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        engine.CaptureRegionInto(inset, false, pixels, stride);
        int mismatched = CountFixtureMismatches(pixels, stride, 0, 0, width, height);
        measurements.Add(new { Kind = "fixture-coverage", Width = width, Height = height, MismatchedPixels = mismatched });
        if (mismatched == 0) return true;
        failures.Add($"Synthetic fixture is not visible as RGB({FixtureColor.R},{FixtureColor.G},{FixtureColor.B}) " +
            $"({mismatched} of {width * height} pixels differ); overlay checks and recordings were skipped.");
        return false;
    }

    private static void Measure(ScreenCaptureEngine engine, RectD fixtureBounds, string outputDirectory,
        List<object> measurements, List<string> failures)
    {
        foreach ((int width, int height) in SustainedSizes)
        {
            if (!CanCover(fixtureBounds, width, height))
            {
                failures.Add($"Synthetic fixture cannot safely cover {width}x{height} on this display.");
                continue;
            }
            var region = new RectD(fixtureBounds.Left + 10, fixtureBounds.Top + 10, width, height);
            measurements.Add(CompareCapturePaths(engine, region));
            measurements.Add(Record(engine, region, DiagnosticOutputPaths.Child(outputDirectory, $"sustained-{width}.mp4"), null, failures));
        }
        var concurrentRegion = new RectD(fixtureBounds.Left + 10, fixtureBounds.Top + 10, 320, 240);
        measurements.Add(Record(engine, concurrentRegion, DiagnosticOutputPaths.Child(outputDirectory, "concurrent.mp4"),
            recorder => ConcurrentWindows(engine, concurrentRegion, recorder, failures), failures));
    }

    private static object CompareCapturePaths(ScreenCaptureEngine engine, RectD region)
    {
        const int count = 64;
        byte[] pixels = new byte[(int)region.Width * (int)region.Height * 4];
        int stride = (int)region.Width * 4;
        using var session = engine.CreateSession(region, false);
        engine.CaptureRegionInto(region, false, pixels, stride);
        session.CaptureInto(pixels, stride);
        var oldSamples = new double[count];
        var newSamples = new double[count];
        long oldAllocations = 0, newAllocations = 0;
        double oldCpu = 0, newCpu = 0;
        using Process process = Process.GetCurrentProcess();
        uint handlesBefore = GetGuiResources(process.Handle, 0);
        for (int index = 0; index < count; index++)
        {
            // Alternate order to reduce thermal/scheduling order bias.
            for (int position = 0; position < 2; position++)
            {
                bool reusable = (index + position) % 2 == 0;
                TimeSpan cpu = process.TotalProcessorTime;
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                if (reusable) session.CaptureInto(pixels, stride);
                else engine.CaptureRegionInto(region, false, pixels, stride);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                if (reusable) { newSamples[index] = elapsed; newAllocations += bytes; newCpu += cpuMs; }
                else { oldSamples[index] = elapsed; oldAllocations += bytes; oldCpu += cpuMs; }
            }
        }
        return new { Kind = "interleaved-capture", region.Width, region.Height, SamplesPerPath = count,
            Old = Summarize(oldSamples, oldAllocations, oldCpu), Reusable = Summarize(newSamples, newAllocations, newCpu),
            GdiHandleDelta = (long)GetGuiResources(process.Handle, 0) - handlesBefore };
    }

    private static object Summarize(double[] raw, long allocations, double cpu)
    {
        double[] sorted = raw.Order().ToArray();
        return new { RawMilliseconds = raw, MedianMs = (sorted[31] + sorted[32]) / 2,
            P95Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], MaxMs = sorted[^1],
            AllocatedBytes = allocations, ProcessCpuMs = cpu,
            NormalizedCpuPercent = cpu / raw.Sum() / Environment.ProcessorCount * 100 };
    }

    private static object Record(ScreenCaptureEngine engine, RectD region, string path,
        Func<RegionRecorder, List<Phase>>? concurrentWork, List<string> failures)
    {
        string metadataPath = DiagnosticOutputPaths.Child(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".json");
        var grabber = new RegionFrameGrabber(engine, false);
        using var recorder = new RegionRecorder(grabber,
            options => new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance), NullLogger.Instance);
        using Process process = Process.GetCurrentProcess();
        recorder.Start(region, path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps30, IncludeCursor = false });
        long startWait = Stopwatch.GetTimestamp();
        while (!recorder.IsReady && recorder.IsRecording && Stopwatch.GetElapsedTime(startWait).TotalSeconds < 15) Pump(20);
        if (!recorder.IsReady)
        {
            RecordingResult startupResult = AwaitWithDispatcher(Task.Run(recorder.Stop));
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(startupResult, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException("Recorder did not become ready within 15 seconds.");
        }
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        List<Phase>? phases = null;
        if (concurrentWork is null) Pump(6000);
        else phases = concurrentWork(recorder);
        double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long allocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        RecordingResult result = AwaitWithDispatcher(Task.Run(recorder.Stop));
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        AwaitWithDispatcher(Task.Run(() =>
        {
        using var reader = new MediaFoundationVideoFrameReader(path);
        DecodedVideoFrame decoded = reader.ReadFrameAt(result.DurationMs / 2);
        if (decoded.Width != (int)region.Width || decoded.Height != (int)region.Height || result.EmittedFrames < 30)
            failures.Add($"Recording did not produce a sustained decodable clip: {path}.");
        if (phases is not null)
        {
            // A fresh reader avoids seeking backwards after the general midpoint probe.
            using var phaseReader = new MediaFoundationVideoFrameReader(path);
            foreach (Phase phase in phases)
            {
                DecodedVideoFrame pixels = phaseReader.ReadFrameAt(phase.AtMs);
                phase.DecodedChangedPixels = CountChangedPixels(pixels.Pixels, pixels.Stride);
                if (phase.ClockExpected ? phase.DecodedChangedPixels < 20 : phase.DecodedChangedPixels > 10)
                    failures.Add($"{phase.Name}: encoded MP4 has unexpected overlay/clock pixels ({phase.DecodedChangedPixels}).");
            }
        }

            return true;
        }));
        return new { Kind = concurrentWork is null ? "sustained-recording" : "concurrent-recording",
            CaptureBackend = new { Requested = grabber.RequestedBackend.ToString(), Active = grabber.ActiveBackend?.ToString(),
                grabber.FallbackReason, Wgc = grabber.WgcDiagnostics },
            Result = result, WallSampleMs = wallMs, ProcessCpuMs = cpuMs,
            NormalizedCpuPercent = cpuMs / wallMs / Environment.ProcessorCount * 100,
            AllocatedBytes = allocations, Phases = phases };
    }

    private sealed record Phase(string Name, double AtMs, int ChangedPixels, bool ClockExpected)
    {
        public int DecodedChangedPixels { get; set; }
    }

    private static List<Phase> ConcurrentWindows(ScreenCaptureEngine engine, RectD region, RegionRecorder recorder, List<string> failures)
    {
        var phases = new List<Phase>();
        void Sample(string name, bool clockExpected)
        {
            Pump(600);
            byte[] pixels = new byte[320 * 240 * 4];
            engine.CaptureRegionInto(region, false, pixels, 1280);
            int changed = CountChangedPixels(pixels, 1280);
            if (clockExpected ? changed < 20 : changed > 10) failures.Add($"{name}: unexpected visible overlay/clock pixels ({changed}).");
            phases.Add(new Phase(name, recorder.RecordedElapsed.TotalMilliseconds, changed, clockExpected));
            Pump(600);
        }
        var magenta = new WriteableBitmap(320, 240, 96, 96, PixelFormats.Bgr32, null);
        byte[] marker = new byte[320 * 240 * 4];
        for (int offset = 0; offset < marker.Length; offset += 4) { marker[offset] = 255; marker[offset + 2] = 255; }
        magenta.WritePixels(new Int32Rect(0, 0, 320, 240), marker, 1280, 0);
        magenta.Freeze();
        var frame = new FrozenFrame(magenta, region, null, 0);
        Sample("plain", false);
        var selector = new CaptureOverlayWindow(frame, false, false);
        try
        {
            if (!CaptureWindowExclusion.TryApply(selector)) throw new InvalidOperationException("Selector exclusion failed.");
            if (selector.IsVisible) throw new InvalidOperationException("Selector became visible before Show.");
            selector.Show();
            Sample("excluded-selector", false);
        }
        finally { selector.Close(); }
        var editor = new AnnotationEditorWindow(frame, new RectD(0, 0, 320, 240), magenta);
        try
        {
            if (!CaptureWindowExclusion.TryApply(editor)) throw new InvalidOperationException("Editor exclusion failed.");
            editor.Show();
            PhysicalWindowPositioner.PlaceTopmost(new System.Windows.Interop.WindowInteropHelper(editor).Handle,
                new RectD(region.Left, region.Top, 700, 500));
            Sample("excluded-editor", false);
        }
        finally { editor.Close(); }
        var clock = new RecordingWallClockWindow(region, 1, 30);
        try { clock.Show(); Sample("included-clock", true); }
        finally { clock.Close(); }
        return phases;
    }

    internal static int CountChangedPixels(byte[] pixels, int stride) => CountFixtureMismatches(pixels, stride, 4, 4, 310, 70);

    /// <summary>Counts BGRA pixels in [left,right)x[top,bottom) outside the fixture colour tolerance.</summary>
    internal static int CountFixtureMismatches(byte[] pixels, int stride, int left, int top, int right, int bottom)
    {
        int changed = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int offset = y * stride + x * 4;
                if (Math.Abs(pixels[offset] - FixtureColor.B) > FixtureChannelTolerance
                    || Math.Abs(pixels[offset + 1] - FixtureColor.G) > FixtureChannelTolerance
                    || Math.Abs(pixels[offset + 2] - FixtureColor.R) > FixtureChannelTolerance) changed++;
            }
        return changed;
    }

    private static T AwaitWithDispatcher<T>(Task<T> task)
    {
        // Native MF calls stay on the worker while the diagnostic fixture remains responsive.
        while (!task.IsCompleted) Pump(10);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
