using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Measures a visible 1600×1000 picture pin of 60 blocks while it changes into its translation: the UI thread's frame
/// gaps and drawing cost, when the first and the last block open, when the result is shown, and the private memory 30
/// seconds after the change, with a census of the large private allocations. Run it under the desktop lock in separate
/// processes: MYCAPTURE_TRANSLATION_REVEAL_PERF_MODE=on (the live change), anim (motion without the change, as before it)
/// or off (no motion); MYCAPTURE_TRANSLATION_REVEAL_PERF_ROUNDS translates up to five times in one process.
/// </summary>
public sealed class PinTranslationRevealPerformanceTests(ITestOutputHelper output) : KoreanCaptionTest
{
    internal const string OutputVariable = "MYCAPTURE_TRANSLATION_REVEAL_PERF";
    internal const string ModeVariable = "MYCAPTURE_TRANSLATION_REVEAL_PERF_MODE";

    [PinTranslationRevealPerfFact]
    public Task SixtyBlocksChangeWithoutLongFramesAndLeaveNoMemoryBehind() => PinTranslationRevealTests.RunAsync(async () =>
    {
        string directory = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        // on: motion with the live change; anim: motion without it (main today); off: no motion at all.
        string mode = (Environment.GetEnvironmentVariable(ModeVariable) ?? "on").Trim().ToLowerInvariant();
        bool motion = mode != "off";
        using var _ = new PinTranslationRevealTests.Motion(null, animations: motion);
        bool suppressed = PinWindow.TranslationRevealSuppressedForTest;
        PinWindow.TranslationRevealSuppressedForTest = mode == "anim";
        var lines = new List<OcrLine>();
        BitmapSource picture = Draw(lines);
        var translator = new PinTranslationRevealTests.BatchTranslator(source => "번역된 항목 " + source.Split(' ')[^1].TrimEnd('.'),
            batches: 3, batchOf: (unit, _) => unit * 7 % 3) { BatchDelay = TimeSpan.FromMilliseconds(400) };
        translator.ReleaseAll();
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance) { ActivatePinForTest = static _ => { } };
        manager.ConfigureTranslation(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        var frames = new List<double>();
        var draws = new List<double>();
        bool recording = false;
        void OnRendering(object? sender, EventArgs e)
        {
            if (recording && e is RenderingEventArgs rendering) frames.Add(rendering.RenderingTime.TotalMilliseconds);
        }
        PinTranslationReveal.DrawObservedForTest = draws.Add;
        CompositionTarget.Rendering += OnRendering;
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_TRANSLATION_REVEAL_PERF_ROUNDS"), out int count)
            ? Math.Clamp(count, 1, 5) : 1;
        try
        {
            for (int round = 1; round <= rounds; round++)
            {
                frames.Clear();
                draws.Clear();
                PinWindow pin = manager.PinImage(picture);
                await Task.Delay(1500);
                int blocks = PinTranslationOverlay.BuildBlocks(PinTranslationOverlayTests.Recognition([.. lines]), picture).Count;
                long privateBefore = PrivateBytes();
                ResidentProbeMemoryMap.Snapshot mapBefore = ResidentProbeMemoryMap.Take("before");
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                int threadsBefore;
                using (Process self = Process.GetCurrentProcess()) threadsBefore = self.Threads.Count;
                recording = true;
                long pressed = Stopwatch.GetTimestamp();
                pin.InvokeTranslationAction();
                PinTranslationReveal? reveal = pin.TranslationRevealForTest;
                await pin.TranslationWork;
                double shown = Stopwatch.GetElapsedTime(pressed).TotalMilliseconds;
                if (reveal is not null) await PinTranslationRevealTests.Until(() => !pin.IsTranslationRevealActive, "the change to end");
                else await Task.Delay(500);
                double ended = Stopwatch.GetElapsedTime(pressed).TotalMilliseconds;
                recording = false;
                Assert.True(pin.IsShowingTranslation);
                await Task.Delay(TimeSpan.FromSeconds(30));
                long privateAfter = PrivateBytes();
                ResidentProbeMemoryMap.Snapshot mapAfter = ResidentProbeMemoryMap.Take("after");
                long managedAfter = GC.GetTotalMemory(forceFullCollection: true);
                int threadsAfter;
                using (Process self = Process.GetCurrentProcess()) threadsAfter = self.Threads.Count;

                double[] gaps = [.. frames.Zip(frames.Skip(1), (a, b) => b - a)];
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"mode={mode} round={round} blocks={blocks} placedEarly={reveal?.EarlyPlacedCount} shownMs={shown:F0} "
                    + $"firstBlockMs={reveal?.BlockStartsAfter?.First:F0} lastBlockStartMs={reveal?.BlockStartsAfter?.Last:F0} "
                    + $"allOpenMs={reveal?.FinishedAfter:F0} endMs={ended:F0} frames={frames.Count} "
                    + $"maxGapMs={(gaps.Length == 0 ? 0 : gaps.Max()):F1} gapsOver34={gaps.Count(gap => gap > 34)} "
                    + $"p95GapMs={Percentile(gaps, 0.95):F1} drawMaxMs={(draws.Count == 0 ? 0 : draws.Max()):F2} "
                    + $"drawP95Ms={Percentile([.. draws], 0.95):F2} draws={draws.Count} "
                    + $"privateBeforeMB={privateBefore / 1048576d:F1} privateAfter30sMB={privateAfter / 1048576d:F1} "
                    + $"managedBeforeMB={managedBefore / 1048576d:F1} managedAfter30sMB={managedAfter / 1048576d:F1} "
                    + $"threadsBefore={threadsBefore} threadsAfter30s={threadsAfter} "
                    + $"census[{Census(mapBefore)}]->[{Census(mapAfter)}]");
                output.WriteLine(line);
                File.AppendAllText(DiagnosticOutputPaths.Child(directory, "reveal-perf.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
                Assert.Equal(60, blocks);
                manager.CloseAll();
                await Task.Delay(1000);
            }
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            PinTranslationReveal.DrawObservedForTest = null;
            PinWindow.TranslationRevealSuppressedForTest = suppressed;
            manager.CloseAll();
        }
    });

    /// <summary>
    /// The private committed memory, and the allocations of 1 MB or more: all of them, the write-combined ones (graphics
    /// driver memory) and the direct VirtualAlloc ones, as count/MB.
    /// </summary>
    private static string Census(ResidentProbeMemoryMap.Snapshot map)
    {
        static bool WriteCombined(string protect) =>
            uint.TryParse(protect.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) && (value & 0x400) != 0;
        var large = map.Allocations;
        var combined = large.Where(allocation => WriteCombined(allocation.Protect)).ToArray();
        var direct = large.Where(allocation => allocation.Owner == "direct VirtualAlloc").ToArray();
        return string.Create(CultureInfo.InvariantCulture,
            $"private={map.CommittedPrivateMb:F1} large={large.Count}/{large.Sum(item => item.CommittedMb):F1} "
            + $"wc={combined.Length}/{combined.Sum(item => item.CommittedMb):F1} direct={direct.Length}/{direct.Sum(item => item.CommittedMb):F1} "
            + $"wcSizes={string.Join("+", combined.Select(item => item.CommittedMb.ToString("F2", CultureInfo.InvariantCulture)))}");
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static double Percentile(double[] values, double share)
    {
        if (values.Length == 0) return 0;
        double[] sorted = [.. values.Order()];
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(share * sorted.Length) - 1)];
    }

    /// <summary>A 1600×1000 settings-like picture of 60 separate labels in three columns.</summary>
    private static BitmapSource Draw(ICollection<OcrLine> recognized)
    {
        const int width = 1600, height = 1000;
        var visual = new DrawingVisual();
        string[] words = ["Automatic save", "Show notifications", "Keyboard shortcut", "Display scale", "Keep original", "Colour theme",
            "Window border", "Mouse pointer", "Copy to clipboard", "Image format"];
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 248, 250)), null, new Rect(0, 0, width, height));
            for (int column = 0; column < 3; column++)
                for (int row = 0; row < 20; row++)
                {
                    string text = words[(row + column) % words.Length] + " " + (column * 20 + row + 1).ToString(CultureInfo.InvariantCulture);
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        ClipboardTextRenderer.TextTypeface, 20, new SolidColorBrush(Color.FromRgb(28, 34, 42)), 1);
                    var origin = new Point(40 + column * 530, 30 + row * 48);
                    drawing.DrawText(formatted, origin);
                    Rect bounds = formatted.BuildGeometry(origin).Bounds;
                    var rect = new OcrRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    recognized.Add(new OcrLine(text, rect, [new OcrWord(text, rect)]));
                }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed class PinTranslationRevealPerfFactAttribute : FactAttribute
{
    public PinTranslationRevealPerfFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationRevealPerformanceTests.OutputVariable)))
            Skip = "Set MYCAPTURE_TRANSLATION_REVEAL_PERF to measure a visible pin's live translation.";
    }
}
