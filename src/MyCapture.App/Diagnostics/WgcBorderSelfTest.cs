using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Records an owned monitor-work-area fixture with the WGC border forced on, then checks its edges.</summary>
internal static class WgcBorderSelfTest
{
    internal const string CommandLineSwitch = "--selftest-wgc-border";
    internal const string ReportName = "wgc-border-report.txt";
    private const int EdgeThickness = 10;
    private static readonly Color FixtureColor = Color.FromRgb(18, 90, 166);

    internal static int Run(string outputDirectory)
    {
        Window? fixture = null;
        DispatcherTimer? pulse = null;
        MonitorInfo? monitor = null;
        RectD region = RectD.Empty;
        return RunDiagnostic(outputDirectory, (root, cancellationToken) =>
        {
            monitor = MonitorEnumerator.GetPrimary();
            region = ComputeFixtureRegion(monitor);
            var marker = new Border
            {
                Width = 100, Height = 100, Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            fixture = new Window
            {
                Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Background = new SolidColorBrush(FixtureColor), Content = marker,
            };
            pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            pulse.Tick += (_, _) => marker.Background = ReferenceEquals(marker.Background, Brushes.White) ? Brushes.Black : Brushes.White;
            fixture.Show();
            PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(fixture).Handle, region);
            fixture.UpdateLayout();
            pulse.Start();
            Pump(500);
            string videoPath = DiagnosticOutputPaths.Child(root, "wgc-border.mp4");
            return Task.Run(() => Record(region, videoPath, cancellationToken), cancellationToken);
        }, () =>
        {
            pulse?.Stop();
            fixture?.Close();
        }, (root, result) =>
        {
            var failures = new List<string>();
            if (result.RawYellowPixels != 0 || result.DecodedYellowPixels != 0)
                failures.Add("Yellow capture-border pixels reached the raw or decoded frame edges.");
            if (!result.RawFrames.IsValid)
                failures.Add("Raw frames do not match the animated fixture or its centre marker did not change.");
            if (!result.DecodedFrames.IsValid)
                failures.Add("Decoded MP4 frames do not match the animated fixture or its centre marker did not change.");
            if (result.Diagnostics.FramesCopied < 2)
                failures.Add("The animated fixture did not produce multiple captured frames.");
            if (WindowsFeatureSupport.HasBorderlessCapture(Environment.OSVersion.Version.Build)
                && (result.Diagnostics.BorderRequired != true || result.Diagnostics.BorderSetError is not null))
                failures.Add("The border could not be forced on and read back on this host.");

            File.WriteAllText(DiagnosticOutputPaths.Child(root, "wgc-border.json"), JsonSerializer.Serialize(new
            {
                Os = Environment.OSVersion.ToString(), Monitor = monitor?.Bounds, WorkArea = monitor?.WorkArea, Region = region, Result = result, Failures = failures,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (failures.Count != 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }, TimeSpan.FromSeconds(30));
    }

    /// <summary>Uses Win32 monitor work-area pixels without any WPF/DPI conversion.</summary>
    internal static RectD ComputeFixtureRegion(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        // Round inward and keep H.264 dimensions even without extending into an appbar.
        double left = Math.Ceiling(Math.Max(monitor.Bounds.Left, monitor.WorkArea.Left));
        double top = Math.Ceiling(Math.Max(monitor.Bounds.Top, monitor.WorkArea.Top));
        double right = Math.Floor(Math.Min(monitor.Bounds.Right, monitor.WorkArea.Right));
        double bottom = Math.Floor(Math.Min(monitor.Bounds.Bottom, monitor.WorkArea.Bottom));
        double width = Math.Floor((right - left) / 2) * 2;
        double height = Math.Floor((bottom - top) / 2) * 2;
        if (width <= 2 * EdgeThickness || height <= 2 * EdgeThickness)
            throw new InvalidOperationException("The monitor work area is too small for the border fixture.");
        return new RectD(left, top, width, height);
    }

    // All capture, encoding, decoding and native disposal stay on the worker. After a timeout
    // the UI closes its fixture and reports failure without joining that worker or its cleanup.
    internal static int RunDiagnostic<T>(string outputDirectory,
        Func<string, CancellationToken, Task<T>> diagnostic, Action closeFixture,
        Action<string, T> complete, TimeSpan timeLimit)
    {
        string root = DiagnosticOutputPaths.Create(outputDirectory);
        var elapsed = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        Task<T>? task = null;
        string? failure = null;
        try
        {
            task = diagnostic(root, cancellation.Token);
            while (!task.IsCompleted && elapsed.Elapsed < timeLimit) Pump(20);
            if (elapsed.Elapsed >= timeLimit)
                throw new TimeoutException($"WGC border diagnostic timed out after {timeLimit.TotalSeconds:0.###} seconds.");
            complete(root, task.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            // Cancellation callbacks and native disposal must not delay the failure report.
            _ = cancellation.CancelAsync();
            if (task is not null)
                _ = task.ContinueWith(faulted => { _ = faulted.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        finally
        {
            closeFixture();
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(root, ReportName),
            (failure is null ? "RESULT: PASS" : "RESULT: FAIL") + Environment.NewLine + failure);
        return failure is null ? 0 : 1;
    }

    private static BorderResult Record(RectD bounds, string videoPath, CancellationToken cancellationToken)
    {
        const int Fps = 20;
        const int Frames = 40;
        cancellationToken.ThrowIfCancellationRequested();
        using var capture = WgcRegionCaptureSession.Create(bounds, includeCursor: false, requestBorderless: false);
        int width = capture.Width, height = capture.Height, stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var options = new VideoEncoderOptions(videoPath, width, height, Fps,
            VideoEncoderOptions.DeriveBitrate(width, height, Fps));
        var raw = new FixtureFrameValidator(compressionTolerance: 24);
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            // Let DWM compose the border before examining any frames.
            Thread.Sleep(500);
            for (int index = 0; index < Frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.CaptureInto(pixels, stride);
                raw.Observe(pixels, width, height, stride);
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, index * 1000.0 / Fps));
                Thread.Sleep(1000 / Fps);
            }
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Complete();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = new FixtureFrameValidator(compressionTolerance: 36);
        using (var reader = new MediaFoundationVideoFrameReader(videoPath))
        {
            // Sequential samples avoid aliasing the 100 ms marker pulse at a fixed 1 s interval.
            for (int index = 0; index < Frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DecodedVideoFrame frame = reader.ReadFrameAt(index * 1000.0 / Fps, cancellationToken);
                if (frame.Width != width || frame.Height != height)
                    throw new InvalidOperationException("Decoded frame does not cover the fixture region.");
                decoded.Observe(frame.Pixels, frame.Width, frame.Height, frame.Stride);
            }
        }
        return new BorderResult(Frames, raw.YellowPixels, decoded.YellowPixels, raw.UnexpectedEdgePixels,
            raw, decoded, capture.GetDiagnostics());
    }

    internal sealed class FixtureFrameValidator(int compressionTolerance)
    {
        private bool? _previousWhite;
        public int Frames { get; private set; }
        public int YellowPixels { get; private set; }
        public int UnexpectedEdgePixels { get; private set; }
        public int InvalidMarkerFrames { get; private set; }
        public int MarkerTransitions { get; private set; }
        public bool IsValid => Frames >= 2 && YellowPixels == 0 && UnexpectedEdgePixels == 0
            && InvalidMarkerFrames == 0 && MarkerTransitions > 0;

        internal void Observe(byte[] pixels, int width, int height, int stride)
        {
            YellowPixels += CountYellowEdgePixels(pixels, width, height, stride);
            UnexpectedEdgePixels += CountEdges(pixels, width, height, stride, (b, g, r) =>
                Math.Abs(b - FixtureColor.B) > compressionTolerance
                || Math.Abs(g - FixtureColor.G) > compressionTolerance
                || Math.Abs(r - FixtureColor.R) > compressionTolerance);
            Frames++;
            // A small patch well inside the 100 DIP marker avoids antialiasing at its boundary.
            bool white = true, black = true;
            for (int y = Math.Max(0, height / 2 - 2); y <= Math.Min(height - 1, height / 2 + 2); y++)
            for (int x = Math.Max(0, width / 2 - 2); x <= Math.Min(width - 1, width / 2 + 2); x++)
            for (int channel = 0; channel < 3; channel++)
            {
                byte value = pixels[y * stride + x * 4 + channel];
                white &= value >= 255 - compressionTolerance;
                black &= value <= compressionTolerance;
            }
            if (!white && !black)
            {
                InvalidMarkerFrames++;
                return;
            }
            if (_previousWhite.HasValue && _previousWhite != white) MarkerTransitions++;
            _previousWhite = white;
        }
    }

    internal static int CountYellowEdgePixels(byte[] pixels, int width, int height, int stride) =>
        CountEdges(pixels, width, height, stride, (b, g, r) => r >= 200 && g >= 150 && b <= 100);

    private static int CountEdges(byte[] pixels, int width, int height, int stride, Func<byte, byte, byte, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || stride < checked(width * 4) || pixels.Length < checked(stride * height))
            throw new ArgumentException("Invalid BGRA frame dimensions.");
        int count = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (x >= EdgeThickness && x < width - EdgeThickness && y >= EdgeThickness && y < height - EdgeThickness) continue;
            int offset = y * stride + x * 4;
            if (matches(pixels[offset], pixels[offset + 1], pixels[offset + 2])) count++;
        }
        return count;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private sealed record BorderResult(int Frames, int RawYellowPixels, int DecodedYellowPixels,
        int UnexpectedEdgePixels, FixtureFrameValidator RawFrames, FixtureFrameValidator DecodedFrames,
        WgcCaptureDiagnostics Diagnostics);
}
