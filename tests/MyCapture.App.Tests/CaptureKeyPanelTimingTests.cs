using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class CaptureKeyPanelTimingTests(ITestOutputHelper output)
{
    [SelectorTimingFact]
    public void SelectorFirstFrame_RecordsPresentedFrameAndRetainsComparison() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var frame = CaptureCueEvidenceTests.Desktop();
        PropertyInfo? panelProperty = typeof(CaptureOverlayWindow).GetProperty("ShowKeyPanel", BindingFlags.Instance | BindingFlags.NonPublic);
        var samples = new List<double>();
        Action<string>? previous = SelectorTimeline.Mark;
        try
        {
            for (int i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                double? first = null;
                SelectorTimeline.Mark = mark =>
                {
                    if (mark == "content-rendered") first ??= watch.Elapsed.TotalMilliseconds;
                };
                var window = new CaptureOverlayWindow(frame, false, showMagnifier: false)
                {
                    ShowModeBar = true, DetectWindows = true, TextCaptureAvailable = true,
                    CopyColorText = _ => Task.FromResult(true),
                    IsForeground = () => true, TakeForeground = () => { },
                };
                panelProperty?.SetValue(window, true);
                try
                {
                    window.Show();
                    PumpUntil(() => first.HasValue);
                    samples.Add(first!.Value);
                }
                finally { window.Close(); }
            }
            output.WriteLine($"Selector content-rendered ms (cold then warm): {string.Join(", ", samples.Select(v => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}");
            output.WriteLine($"Warm median ms: {samples.Skip(1).Order().ElementAt(2):F2}");
            Assert.Equal(6, samples.Count);

            var view = CaptureCueEvidenceTests.EvidenceView(frame, AppTheme.Glass, "window-hover");
            try
            {
                var property = typeof(CaptureOverlayView).GetProperty("ShowKeyPanel", BindingFlags.Instance | BindingFlags.NonPublic);
                property?.SetValue(view, true);
                typeof(CaptureOverlayView).GetMethod("NotifyFirstFramePresented", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(view, null);
                RenderTargetBitmap image = CaptureCueEvidenceTests.Render(view);
                string directory = OwnedTestDirectory.Create("mycapture-selector-key-panel-");
                string path = Path.Combine(directory, property is null ? "before.png" : "after.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(path)) encoder.Save(file);
                output.WriteLine($"Retained selector image: {path}");
            }
            finally { view.ReleaseResources(); }
        }
        finally { SelectorTimeline.Mark = previous; }
    });

    private static void PumpUntil(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(ready());
    }
}

/// <summary>
/// Skips unless <c>MYCAPTURE_SELECTOR_TIMING</c> is set. The timing harness shows a real 1280 × 720
/// selector window, which a smaller desktop (the CI runner) cannot place; the first-frame rules
/// themselves are covered by <see cref="CaptureKeyPanelTests"/>.
/// </summary>
internal sealed class SelectorTimingFactAttribute : FactAttribute
{
    public SelectorTimingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_SELECTOR_TIMING")))
            Skip = "Opt-in selector timing harness: set MYCAPTURE_SELECTOR_TIMING=1 on a desktop of at least 1280 x 720.";
    }
}
