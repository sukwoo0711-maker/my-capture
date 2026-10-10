using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in frame-cadence measurement of the real recorder and GDI grabber, with and without a
/// running WPF render loop. A render loop raises this process's timer resolution, so an idle
/// scene is the condition where a millisecond-granular sleep shows its true granularity. Timing
/// depends on the machine and its load, so it never runs in CI; set
/// <c>MYCAPTURE_RECORDING_PACING_PERF=1</c> (and optionally <c>MYCAPTURE_RECORDING_PACING_OUT</c>
/// to an absolute JSON path) on the validation host.
/// </summary>
public sealed class RecordingPacingPerformanceTests(ITestOutputHelper output)
{
    private const int MeasuredSeconds = 8;
    private const double WarmUpMs = 1000;

    [RecordingPacingPerformanceFact]
    public void RealRecorder_KeepsTheTargetCadence_WithAndWithoutARenderLoop() => StaTestHost.Run(() =>
    {
        var results = new List<PacingSample>();
        using var fixture = new SyntheticCaptureFixture();
        foreach (bool renderLoop in new[] { false, true })
        {
            foreach (RecordingFrameRate rate in new[] { RecordingFrameRate.Fps30, RecordingFrameRate.Fps60 })
            {
                results.Add(Measure(fixture, rate, renderLoop));
            }
        }

        var report = new StringBuilder();
        foreach (PacingSample sample in results)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{(sample.RenderLoop ? "render-loop" : "idle"),-11} {sample.TargetFps}fps: effective {sample.EffectiveFps:0.0} fps, " +
                $"gap p50 {sample.GapP50Ms:0.0} / p95 {sample.GapP95Ms:0.0} / max {sample.GapMaxMs:0.0} ms, " +
                $"capture mean {sample.CaptureMeanMs:0.0} / max {sample.CaptureMaxMs:0.0} ms, process CPU {sample.ProcessCpuPercent:0.00}%, high-resolution timer {sample.HighResolutionTimer}");
        }

        output.WriteLine(report.ToString());
        string? path = Environment.GetEnvironmentVariable("MYCAPTURE_RECORDING_PACING_OUT");
        if (!string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathFullyQualified(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }

        // A drift-free schedule claims every frame slot whenever capture fits in the interval.
        foreach (PacingSample sample in results.Where(sample => sample.CaptureMeanMs < 1000.0 / sample.TargetFps * 0.5))
        {
            Assert.True(sample.EffectiveFps >= sample.TargetFps * 0.95,
                $"{(sample.RenderLoop ? "render-loop" : "idle")} {sample.TargetFps}fps reached only {sample.EffectiveFps:0.0} fps.");
        }
    });

    private static PacingSample Measure(SyntheticCaptureFixture fixture, RecordingFrameRate rate, bool renderLoop)
    {
        var sink = new TimestampEncoder();
        var captureMs = new List<double>();
        var grabber = new RegionFrameGrabber(fixture.Engine, includeCursor: false);
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance);
        var marker = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White };
        var canvas = new Canvas();
        canvas.Children.Add(marker);
        var host = new Window
        {
            Width = 120,
            Height = 80,
            Left = 900,
            Top = 100,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = canvas,
        };
        int ticks = 0;
        void OnRendering(object? sender, EventArgs e) => Canvas.SetLeft(marker, ++ticks % 100);
        host.Show();
        if (renderLoop)
        {
            CompositionTarget.Rendering += OnRendering;
        }

        try
        {
            SyntheticCaptureFixture.Pump(300);
            using Process process = Process.GetCurrentProcess();
            recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings { FrameRate = rate, IncludeCursor = false });
            var wall = Stopwatch.StartNew();
            while (sink.Count == 0 && wall.Elapsed < TimeSpan.FromSeconds(10)) SyntheticCaptureFixture.Pump(10);
            SyntheticCaptureFixture.Pump((int)WarmUpMs);
            TimeSpan cpuBefore = process.TotalProcessorTime;
            long started = Stopwatch.GetTimestamp();
            SyntheticCaptureFixture.Pump(MeasuredSeconds * 1000);
            double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            RecordingResult result = recorder.Stop();

            double[] timestamps = sink.Timestamps.Where(timestamp => timestamp >= WarmUpMs).ToArray();
            double[] gaps = timestamps.Zip(timestamps.Skip(1), (a, b) => b - a).Order().ToArray();
            double spanMs = timestamps.Length > 1 ? timestamps[^1] - timestamps[0] : 1;
            return new PacingSample(
                renderLoop,
                (int)rate,
                (timestamps.Length - 1) * 1000.0 / spanMs,
                Quantile(gaps, 0.5),
                Quantile(gaps, 0.95),
                gaps.Length > 0 ? gaps[^1] : 0,
                result.Performance is { } performance ? performance.CaptureTotalMs / Math.Max(1, result.EmittedFrames) : 0,
                result.Performance is { } p ? p.CaptureMaxMs : 0,
                cpuMs / wallMs / Environment.ProcessorCount * 100,
                result.Performance?.HighResolutionTimer ?? false);
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            host.Close();
        }
    }

    private static double Quantile(double[] sorted, double quantile) =>
        sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * quantile) - 1, 0, sorted.Length - 1)];

    internal sealed record PacingSample(
        bool RenderLoop,
        int TargetFps,
        double EffectiveFps,
        double GapP50Ms,
        double GapP95Ms,
        double GapMaxMs,
        double CaptureMeanMs,
        double CaptureMaxMs,
        double ProcessCpuPercent,
        bool HighResolutionTimer);

    private sealed class TimestampEncoder : IVideoEncoder
    {
        private readonly List<double> _timestamps = [];
        private int _count;

        public int Width => 0;

        public int Height => 0;

        internal int Count => Volatile.Read(ref _count);

        /// <summary>Written on the capture thread; read after <see cref="RegionRecorder.Stop"/> joined it.</summary>
        internal IReadOnlyList<double> Timestamps => _timestamps;

        public void WriteFrame(in EncoderFrame frame)
        {
            _timestamps.Add(frame.TimestampMs);
            Interlocked.Increment(ref _count);
        }

        public void Complete()
        {
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class RecordingPacingPerformanceFactAttribute : FactAttribute
{
    public RecordingPacingPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_RECORDING_PACING_PERF") != "1")
            Skip = "Opt-in recording cadence timing: set MYCAPTURE_RECORDING_PACING_PERF=1 on the validation host.";
    }
}
