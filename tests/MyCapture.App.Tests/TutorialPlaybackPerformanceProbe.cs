using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Evidence probe for the feature tutorial player with the real MediaElement and the shipped
/// MP4 files. Runs only when MYCAPTURE_TUTORIAL_PERF names an output JSON path, alone in its
/// process (it creates the process Application).
/// </summary>
public sealed class TutorialPlaybackPerformanceProbe
{
    private static readonly string[] Features = ["text-copy", "capture-region", "image-crop", "video-trim", "pin-click-through", "library-move"];

    [Fact]
    public void MeasureOpenSwitchAndPlayback()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_TUTORIAL_PERF");
        if (string.IsNullOrWhiteSpace(output)) return;
        StaTestHost.Run(() =>
        {
            // A plain Application with the app's theme, never App itself: WPF runs an Application's
            // OnStartup on the first dispatcher pump even without Run(), and App's startup claims the
            // single-instance mutex or, when another MyCapture holds it, signals that instance to
            // open its library and then shuts this one down.
            if (Application.Current is null)
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                    application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                    });
            }
            ThemeService.Apply(AppTheme.Glass);
            using var language = UiText.UseLanguage("ko-KR");
            bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = true;
            var open = new List<double>();
            var switching = new List<double>();
            var workingSet = new List<double>();
            var privateBytes = new List<double>();
            var cpu = new List<double>();
            using var process = Process.GetCurrentProcess();
            try
            {
                // Warm the WPF render path and Media Foundation once; every measured run starts cold for its asset.
                Run(Features[0], measure: false);
                for (int round = 0; round < 3; round++)
                    foreach (string feature in Features) Run(feature, measure: true);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = motion;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }

            void Run(string feature, bool measure)
            {
                Settle();
                process.Refresh();
                long baseWorking = process.WorkingSet64, basePrivate = process.PrivateMemorySize64;
                var window = new FeatureTutorialWindow(new HotkeySettings(), feature)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                };
                try
                {
                    var clock = Stopwatch.StartNew();
                    window.Show();
                    WaitFirstFrame(window);
                    if (measure) open.Add(clock.Elapsed.TotalMilliseconds);
                    if (!window.IsPlaying) window.TogglePlayback();
                    Pump(TimeSpan.FromMilliseconds(1000));
                    process.Refresh();
                    TimeSpan cpuStart = process.TotalProcessorTime;
                    var wall = Stopwatch.StartNew();
                    var ws = new List<long>(); var pb = new List<long>();
                    while (wall.Elapsed < TimeSpan.FromSeconds(5))
                    {
                        Pump(TimeSpan.FromMilliseconds(250));
                        process.Refresh();
                        ws.Add(process.WorkingSet64 - baseWorking); pb.Add(process.PrivateMemorySize64 - basePrivate);
                    }
                    process.Refresh();
                    double cpuPercent = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
                    if (measure)
                    {
                        cpu.Add(cpuPercent);
                        workingSet.Add(Median(ws.Select(value => value / 1048576.0)));
                        privateBytes.Add(Median(pb.Select(value => value / 1048576.0)));
                    }
                    string next = Features[(Array.IndexOf(Features, feature) + 1) % Features.Length];
                    var switchClock = Stopwatch.StartNew();
                    window.SelectFeature(next);
                    WaitFirstFrame(window);
                    if (measure) switching.Add(switchClock.Elapsed.TotalMilliseconds);
                }
                finally { window.Close(); }
            }

            string tutorials = Path.Combine(Path.GetDirectoryName(typeof(FeatureTutorialWindow).Assembly.Location)!, "Assets", "Tutorials");
            FileInfo[] assets = new DirectoryInfo(tutorials).GetFiles("*.mp4");
            var result = new
            {
                Player = typeof(FeatureTutorialWindow).Assembly.GetName().Version?.ToString(),
                Media = "MediaElement",
                Features, Rounds = 3,
                OpenToFirstFrameMs = Summary(open),
                SwitchToFirstFrameMs = Summary(switching),
                PlayingWorkingSetDeltaMiB = Summary(workingSet),
                PlayingPrivateBytesDeltaMiB = Summary(privateBytes),
                PlayingCpuPercentOfOneCore = Summary(cpu),
                AssetCount = assets.Length,
                AssetBytes = assets.Sum(file => file.Length),
            };
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        });
    }

    private static object Summary(List<double> values) => new { Count = values.Count, Median = Median(values), Min = values.Min(), Max = values.Max() };

    private static double Median(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        return values.Length == 0 ? double.NaN : values.Length % 2 == 1 ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    private static void Settle()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Pump(TimeSpan.FromMilliseconds(300));
    }

    private static void WaitFirstFrame(FeatureTutorialWindow window)
    {
        var timeout = Stopwatch.StartNew();
        while (!window.HasFirstFrame)
        {
            if (window.IsShowingFallback) throw new InvalidOperationException($"Tutorial {window.SelectedFeatureId} fell back to its steps: {window.AssetPath}");
            if (timeout.Elapsed > TimeSpan.FromSeconds(20))
                throw new TimeoutException($"First tutorial frame of {window.SelectedFeatureId}: media {window.HasMedia}, "
                    + $"pending {window.IsOpenPending}, loaded {window.IsLoaded}, visible {window.IsVisible}");
            Pump(TimeSpan.FromMilliseconds(1));
        }
        // The frame is visible once the next composition pass has run.
        bool rendered = false;
        void OnRendering(object? sender, EventArgs e) => rendered = true;
        CompositionTarget.Rendering += OnRendering;
        try { while (!rendered) Pump(TimeSpan.FromMilliseconds(1)); }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
