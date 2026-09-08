using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

/// <summary>Explicit synthetic pin/input and isolated storage/OCR workload. No user clipboard or queue access.</summary>
internal static class PinStoragePerformanceSelfTest
{
    internal const string CommandLineSwitch = "--selftest-pin-storage-performance";

    internal static int Run(string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var results = new List<object>();
        var failures = new List<string>();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            MeasurePin(results);
            MeasureStorage(outputDirectory, results, failures);
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        File.WriteAllText(Path.Combine(outputDirectory, "pin-storage-performance.json"), JsonSerializer.Serialize(new
        {
            Utc = DateTimeOffset.UtcNow, Os = Environment.OSVersion.ToString(), LogicalProcessors = Environment.ProcessorCount,
            CpuNormalization = "process CPU / measured wall time / logical processors * 100",
            Limits = "Synthetic input invokes actual WPF drag overrides with native HWND movement and per-gesture display cache; excludes OS input queue and physical mouse latency. Idle is the diagnostic host, not fully initialized tray app. Source images and queue are synthetic. No performance improvement is inferred from these current-source measurements.",
            Results = results, Failures = failures,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outputDirectory, "pin-storage-performance-report.txt"),
            (failures.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL") + Environment.NewLine + string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;
    }

    private static void MeasurePin(List<object> results)
    {
        var input = new SyntheticPinDragInput();
        BitmapSource image = SyntheticImage(360, 220, false);
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        double scale = monitor.ScaleFactor;
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(360, 220, 1, 1, 0.1),
            (monitor.Bounds.Left + 100) / scale, (monitor.Bounds.Top + 100) / scale,
            () => new PinSettings { CloseOnDoubleClick = false }, input) { ShowActivated = false };
        try
        {
            Pump(600); // Exclude diagnostic dispatcher startup from hidden-idle samples.
            for (int sample = 0; sample < 3; sample++) results.Add(Idle("hidden-pin", sample));
            pin.Show();
            Pump(600); // Initial reveal finishes before visible-idle or gesture measurement.
            for (int sample = 0; sample < 3; sample++) results.Add(Idle("visible-pin", sample));

            // Warm callbacks/JIT/native movement without retaining these samples.
            input.Down(pin);
            input.CursorPosition = ((int)monitor.Bounds.Left + 200, (int)monitor.Bounds.Top + 200);
            input.Move(pin);
            input.Up(pin);
            Pump(100);
            for (int gesture = 0; gesture < 6; gesture++) results.Add(Gesture(pin, input, monitor, gesture));
        }
        finally { pin.Close(); }
    }

    private static object Idle(string condition, int sample)
    {
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        long allocations = GC.GetTotalAllocatedBytes(true);
        long started = Stopwatch.GetTimestamp();
        Pump(2000);
        double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        return new { Kind = "idle", Condition = condition, Sample = sample, WallMs = wallMs, ProcessCpuMs = cpuMs,
            NormalizedCpuPercent = cpuMs / wallMs / Environment.ProcessorCount * 100,
            AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocations };
    }

    private static object Gesture(PinWindow pin, SyntheticPinDragInput input, MonitorInfo monitor, int gesture)
    {
        const int samples = 60;
        var callbacks = new double[samples];
        var dispatchGaps = new double[samples - 1];
        int index = 0;
        long prior = 0;
        long callbackAllocations = 0;
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        long gestureStart = Stopwatch.GetTimestamp();
        long downStart = Stopwatch.GetTimestamp();
        input.Down(pin);
        double downMs = Stopwatch.GetElapsedTime(downStart).TotalMilliseconds;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            try
            {
                long tick = Stopwatch.GetTimestamp();
                if (prior != 0) dispatchGaps[index - 1] = Stopwatch.GetElapsedTime(prior, tick).TotalMilliseconds;
                prior = tick;
                input.CursorPosition = ((int)monitor.Bounds.Left + 180 + index * 3,
                    (int)monitor.Bounds.Top + 180 + (gesture % 2 == 0 ? index : samples - index));
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                input.Move(pin);
                callbacks[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                callbackAllocations += GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (++index == samples) { timer.Stop(); frame.Continue = false; }
            }
            catch (Exception ex) { failure = ex; timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        bool retainedCapture = input.Captured;
        input.Up(pin);
        if (failure is not null) throw new InvalidOperationException("Synthetic drag callback failed.", failure);
        var bounds = WindowStyleFacade.GetWindowBounds(pin.Handle);
        DpiScale dpi = VisualTreeHelper.GetDpi(pin);
        double expectedLeft = input.CursorPosition.X - input.Anchor.X * dpi.DpiScaleX;
        double expectedTop = input.CursorPosition.Y - input.Anchor.Y * dpi.DpiScaleY;
        if (!retainedCapture || Math.Abs(bounds.Left - expectedLeft) > 1 || Math.Abs(bounds.Top - expectedTop) > 1)
            throw new InvalidOperationException("The synthetic callback sequence did not preserve capture and move the native pin to its expected position.");
        double wallMs = Stopwatch.GetElapsedTime(gestureStart).TotalMilliseconds;
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        return new { Kind = "pin-gesture", Gesture = gesture, Samples = samples, MouseDownMs = downMs,
            Callback = Summary(callbacks), DispatchGap = Summary(dispatchGaps), CallbackAllocatedBytes = callbackAllocations,
            WallMs = wallMs, ProcessCpuMs = cpuMs, NormalizedCpuPercent = cpuMs / wallMs / Environment.ProcessorCount * 100,
            RetainedCaptureThroughGesture = retainedCapture, FinalLeft = bounds.Left, FinalTop = bounds.Top, ExpectedLeft = expectedLeft, ExpectedTop = expectedTop };
    }

    private static void MeasureStorage(string outputDirectory, List<object> results, List<string> failures)
    {
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(outputDirectory, "synthetic-queue-" + Guid.NewGuid().ToString("N")));
        var settings = new QueueSettings { MaxItems = 2000 };
        var queue = new CaptureQueue(paths, settings, NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var persistence = new CapturePersistenceService(queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
        var realRecords = new List<CaptureRecord>();
        foreach ((int width, int height) in new[] { (320, 240), (1280, 720) })
        {
            BitmapSource image = SyntheticImage(width, height, true);
            var total = new double[16];
            var publication = new double[16];
            long publicationStart = 0;
            long published = 0;
            persistence.BeforeRecordMetadataCommit = _ => publicationStart = Stopwatch.GetTimestamp();
            EventHandler<CaptureRecord> onPersisted = (_, _) => published = Stopwatch.GetTimestamp();
            persistence.ImagePersisted += onPersisted;
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(true);
            for (int index = 0; index < total.Length; index++)
            {
                publicationStart = 0;
                published = 0;
                long started = Stopwatch.GetTimestamp();
                CaptureRecord record = Await(persistence.PersistOriginalAsync(image, 1, "Synthetic fixture", "Synthetic"));
                total[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                publication[index] = Stopwatch.GetElapsedTime(publicationStart, published).TotalMilliseconds;
                realRecords.Add(record);
                if (!File.Exists(queue.GetFilePath(record, CaptureFileNames.Original))
                    || File.Exists(queue.GetFilePath(record, CaptureFileNames.OriginalPending)))
                    failures.Add("Original persistence did not publish a durable journal-free capture.");
            }
            persistence.ImagePersisted -= onPersisted;
            persistence.BeforeRecordMetadataCommit = null;
            results.Add(new { Kind = "original-persistence", Width = width, Height = height, Samples = total.Length,
                Total = Summary(total), MetadataIndexPublication = Summary(publication),
                ProcessCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
        }

        var gallery = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        foreach (int targetCount in new[] { 32, 300, 1000 })
        {
            // Only metadata is needed to exercise the actual queue serializer at larger history sizes.
            while (queue.Records.Count < targetCount)
                queue.Add(new CaptureRecord { Width = 320, Height = 240, SourceWindowTitle = "Synthetic scale record", TotalBytes = 0 });
            var save = new double[16];
            var cache = new double[16];
            for (int index = 0; index < save.Length; index++)
            {
                long started = Stopwatch.GetTimestamp();
                queue.Save();
                save[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                CaptureRecord record = realRecords[index];
                _ = gallery.CacheOcr(record.Id, "MyCapture Hello World 12345", "en-US", record.ContentRevision);
                cache[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            results.Add(new { Kind = "index-publication", RecordCount = targetCount, Samples = save.Length,
                QueueSave = Summary(save), OcrCacheMetaAndIndex = Summary(cache), IndexBytes = new FileInfo(paths.IndexFile).Length });
        }
        MeasureOcr(outputDirectory, results, failures);
    }

    private static void MeasureOcr(string outputDirectory, List<object> results, List<string> failures)
    {
        var service = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        if (!service.IsAvailable)
        {
            results.Add(new { Kind = "ocr", Status = "UNAVAILABLE", Samples = 0 });
            return;
        }
        foreach (bool text in new[] { true, false })
        {
            string path = Path.Combine(outputDirectory, text ? "synthetic-text.png" : "synthetic-blank.png");
            ImageCodec.SavePng(SyntheticImage(720, 360, text), path);
            var wall = new double[6];
            var engine = new double[6];
            var statuses = new string[6];
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(true);
            for (int index = 0; index < wall.Length; index++)
            {
                long started = Stopwatch.GetTimestamp();
                OcrResult result = Await(service.RecognizeAsync(OcrRequest.FromFile(path, 2, ["en-US", "en"], searchRotatedOrientations: false)));
                wall[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                engine[index] = result.Elapsed.TotalMilliseconds;
                statuses[index] = result.Status.ToString();
                if (text && (result.Status != OcrStatus.Success || !result.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"Synthetic text OCR did not recognize Hello: {result.Status}.");
                if (!text && result.Status != OcrStatus.NoText) failures.Add($"Blank OCR unexpectedly returned {result.Status}.");
            }
            results.Add(new { Kind = "ocr", Input = text ? "text" : "blank", Samples = wall.Length, Statuses = statuses,
                Wall = Summary(wall), ReportedPipeline = Summary(engine), ProcessCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated });
        }
    }

    private static BitmapSource SyntheticImage(int width, int height, bool text)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (text)
                context.DrawText(new FormattedText("MyCapture\nHello World\n12345", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 32, Brushes.Black, 1), new Point(20, 20));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static object Summary(double[] raw)
    {
        double[] sorted = raw.Order().ToArray();
        double median = sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
        return new { RawMilliseconds = raw, MedianMs = median, P95Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], MaxMs = sorted[^1] };
    }

    private static T Await<T>(Task<T> task)
    {
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
}
