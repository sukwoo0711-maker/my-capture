using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyCapture.App.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Runs only when <c>MYCAPTURE_MEDIA_PROBE_OUT</c> names a folder for the probe's records.</summary>
internal sealed class MediaColdOpenProbeFactAttribute : FactAttribute
{
    public MediaColdOpenProbeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MediaColdOpenProbe.OutVariable)))
            Skip = $"Diagnostic probe; set {MediaColdOpenProbe.OutVariable} to run it.";
    }
}

/// <summary>
/// Diagnostic only, not a regression test. On hosted runners the first WPF media open in a test
/// process has taken 15 to 25 s and twice never presented a frame, while later opens take about a
/// second. This probe opens a clip the way the failing tests do and records, for the first and the
/// following open, when each phase completed, which modules loaded meanwhile, how the audio and
/// media services and other processes behaved, and, when an open stays pending, native dumps of
/// this process. It always passes; the records in <c>MYCAPTURE_MEDIA_PROBE_OUT</c> are the result.
/// </summary>
public sealed class MediaColdOpenProbe
{
    internal const string OutVariable = "MYCAPTURE_MEDIA_PROBE_OUT";

    [MediaColdOpenProbeFact]
    public void RecordFirstAndSecondOpen()
    {
        ProbeSettings settings = ProbeSettings.FromEnvironment();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")))
        {
            // Never let anything this process starts fall back to the real %APPDATA%\MyCapture.
            string dataRoot = Path.Combine(Path.GetTempPath(), $"mycapture-media-probe-data-{Environment.ProcessId}");
            Directory.CreateDirectory(dataRoot);
            Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", dataRoot);
        }

        StaTestHost.Run(() => new ColdOpenRun(settings).Execute());
    }
}

internal sealed record ProbeSettings(string OutDir, string Label, string Variant, string? DumpScript, double LimitMs, double FirstDumpMs, double? ActMs)
{
    internal static readonly string[] Variants = ["plain", "rescue", "reopen", "editor"];

    internal static ProbeSettings FromEnvironment()
    {
        string outDir = Path.GetFullPath(Environment.GetEnvironmentVariable(MediaColdOpenProbe.OutVariable)!);
        Directory.CreateDirectory(outDir);
        string label = Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_LABEL") is { Length: > 0 } l ? l : "run";
        string variant = Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_VARIANT") is { Length: > 0 } v ? v : "plain";
        if (!Variants.Contains(variant)) throw new ArgumentException($"Unknown probe variant '{variant}'.");
        string? dumpScript = Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_DUMP_SCRIPT") is { Length: > 0 } d ? Path.GetFullPath(d) : null;
        double limitS = double.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_LIMIT_S"), out double s) && s > 0 ? s : 300;
        // When the first open is still pending this long, the first dump is written (5 s by default).
        double firstDumpMs = double.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_FIRST_DUMP_MS"), out double ms) && ms >= 0 ? ms : 5000;
        // Overrides when the rescue and reopen variants act, so their paths can be tried locally.
        double? actMs = double.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_MEDIA_PROBE_ACT_MS"), out double act) && act >= 0 ? act : null;
        return new ProbeSettings(outDir, label, variant, dumpScript, limitS * 1000, firstDumpMs, actMs);
    }
}

/// <summary>One probe run on its STA thread: prepare like the failing tests, open, observe, report.</summary>
internal sealed class ColdOpenRun
{
    private const int ClipWidth = 320;
    private const int ClipHeight = 180;
    private const int ClipFps = 15;
    private const int ClipFrames = 45;

    /// <summary>The rescue variant opens a fresh player when the first is still pending this long.</summary>
    private const double RescueAtMs = 3000;

    /// <summary>The reopen variant closes and reopens the first player when it is still pending this long.</summary>
    private const double ReopenAtMs = 6000;

    /// <summary>After the first open settled, or at its limit, a following open gets this long.</summary>
    private const double FollowUpLimitMs = 60000;

    private readonly ProbeSettings _settings;
    private readonly ProbeRecorder _rec;
    private readonly List<ProbeTarget> _targets = [];
    private readonly List<DumpRecord> _dumps = [];
    private readonly Dictionary<string, object?> _machine = [];
    private readonly Dictionary<string, double> _pre = [];
    private ProbeDumper? _dumper;
    private string _outcome = "not-started";

    internal ColdOpenRun(ProbeSettings settings)
    {
        _settings = settings;
        _rec = new ProbeRecorder(Path.Combine(settings.OutDir, settings.Label + ".log"));
    }

    internal void Execute()
    {
        _rec.Event($"probe {_settings.Label} variant {_settings.Variant} pid {Environment.ProcessId}");
        ModuleTracker? modules = null;
        SystemSampler? system = null;
        string? dir = null;
        Window? host = null;
        try
        {
            modules = ModuleTracker.Start(_rec);
            system = SystemSampler.Start(_rec);
            if (_settings.DumpScript is not null)
            {
                _dumper = ProbeDumper.TryStart(_settings.DumpScript, _rec, _dumps);
            }

            RecordMachine(before: true);
            dir = OwnedTestDirectory.Create("mc-media-probe-");
            string[] clips = PrepareClips(dir);
            host = _settings.Variant == "editor" ? null : ShowHost();
            Pump(TimeSpan.FromMilliseconds(500));

            ProbeTarget first = CreateTarget("P1-first", clips[0], host, dir);
            _rec.MarkZero();
            system.Mark("t0");
            first.Start();
            _outcome = WatchFirstOpen(first, clips, host, dir);
            system.Mark("first-settled");
            _rec.Event($"first open outcome: {_outcome}");

            if (_outcome == "pending-at-limit")
            {
                RequestDump("p1-at-limit");
                ProbeTarget fresh = CreateTarget("P3-fresh-while-P1-pending", clips[2], host, dir);
                fresh.Start();
                WatchUntilSettled([fresh], FollowUpLimitMs);
                if (first is PlayerTarget player)
                {
                    player.Reopen("P1-late-reopen");
                    WatchUntilSettled([first], FollowUpLimitMs);
                }
            }
            else
            {
                Pump(TimeSpan.FromSeconds(1));
                ProbeTarget second = CreateTarget("P3-second-open", clips[2], host, dir);
                second.Start();
                WatchUntilSettled([second], FollowUpLimitMs);
            }

            system.Mark("end");
            RecordMachine(before: false);
        }
        catch (Exception ex)
        {
            _rec.Event("probe error: " + ex);
            _outcome = "probe-error: " + ex.GetType().Name;
        }
        finally
        {
            foreach (ProbeTarget target in _targets)
            {
                try { target.Close(); } catch (Exception ex) { _rec.Event($"{target.Name} close failed: {ex.Message}"); }
            }

            host?.Close();
            Pump(TimeSpan.FromMilliseconds(300));
            _dumper?.Dispose();
            modules?.Dispose();
            system?.Dispose();
            WriteReport(modules, system);
            _rec.Dispose();
            if (dir is not null)
            {
                try { OwnedTestDirectory.Delete(dir); } catch (IOException) { /* a player may still hold a clip */ }
            }
        }
    }

    private string[] PrepareClips(string dir)
    {
        // The same clip and the same steps as the 15 fps frame-step test, whose first open was the
        // one that never presented a frame: encode with Media Foundation, read the container, open.
        string clip = Path.Combine(dir, "clip-a.mp4");
        double start = _rec.Now;
        FrameIndexClip.Encode(clip, ClipWidth, ClipHeight, ClipFps, FrameIndexClip.Constant(ClipFps, ClipFrames));
        _pre["encodeMs"] = _rec.Now - start;
        start = _rec.Now;
        _pre["containerDurationMs"] = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
        _pre["containerReadMs"] = _rec.Now - start;
        string[] clips = [clip, Path.Combine(dir, "clip-b.mp4"), Path.Combine(dir, "clip-c.mp4")];
        File.Copy(clip, clips[1]);
        File.Copy(clip, clips[2]);
        _rec.Event($"clips ready: encode {_pre["encodeMs"]:F0} ms, container read {_pre["containerReadMs"]:F0} ms");
        return clips;
    }

    private Window ShowHost()
    {
        double start = _rec.Now;
        var host = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1100, Height = 260,
            ShowActivated = false, ShowInTaskbar = false,
            Background = Brushes.Black,
            Content = new WrapPanel(),
        };
        bool rendered = false;
        host.ContentRendered += (_, _) => rendered = true;
        host.Show();
        PumpUntil(() => rendered, TimeSpan.FromSeconds(30));
        _pre["hostWindowRenderedMs"] = _rec.Now - start;
        _rec.Event($"host window rendered after {_pre["hostWindowRenderedMs"]:F0} ms");
        return host;
    }

    private ProbeTarget CreateTarget(string name, string clip, Window? host, string dir)
    {
        ProbeTarget target = _settings.Variant == "editor"
            ? new EditorTarget(name, clip, _rec, Path.Combine(dir, "data-" + _targets.Count), ClipWidth, ClipHeight, ClipFps, ClipFrames, _pre["containerDurationMs"])
            : new PlayerTarget(name, clip, _rec, (Panel)host!.Content, ClipWidth, ClipHeight);
        _targets.Add(target);
        return target;
    }

    private string WatchFirstOpen(ProbeTarget first, string[] clips, Window? host, string dir)
    {
        ProbeTarget? rescue = null;
        bool reopened = false;
        bool dumpedFirst = false;
        bool dumped60 = false;
        while (true)
        {
            double t = _rec.SinceZero;
            first.Poll();
            rescue?.Poll();
            bool firstPending = first.Current.Opened is null && first.Current.Failed is null;
            if (first.Current.Settled && (rescue is null || rescue.Current.Settled)) break;
            if (first.Current.Settled && rescue is not null && _rec.Now >= first.Current.SettledAt + FollowUpLimitMs) break;

            if (_settings.Variant == "rescue" && rescue is null && firstPending && t >= (_settings.ActMs ?? RescueAtMs))
            {
                rescue = CreateTarget("P2-fresh-while-P1-pending", clips[1], host, dir);
                rescue.Start();
            }

            if (_settings.Variant == "reopen" && !reopened && firstPending && t >= (_settings.ActMs ?? ReopenAtMs))
            {
                reopened = true;
                ((PlayerTarget)first).Reopen("P1-reopen-while-pending");
            }

            if (!dumpedFirst && t >= _settings.FirstDumpMs && !first.Current.Settled)
            {
                dumpedFirst = true;
                RequestDump($"p1-pending-{_settings.FirstDumpMs / 1000:0.#}s");
            }

            if (!dumped60 && t >= 60000 && !first.Current.Settled)
            {
                dumped60 = true;
                RequestDump("p1-pending-60s");
            }

            if (t >= _settings.LimitMs)
            {
                return first.Current.Opened is not null ? "opened-no-frame-at-limit" : "pending-at-limit";
            }

            Pump(TimeSpan.FromMilliseconds(20));
        }

        return first.Current.Failed is not null ? "failed" : "opened-and-presented";
    }

    private void WatchUntilSettled(IReadOnlyList<ProbeTarget> targets, double limitMs)
    {
        double start = _rec.Now;
        while (_rec.Now - start < limitMs)
        {
            foreach (ProbeTarget target in targets) target.Poll();
            if (targets.All(target => target.Current.Settled)) return;
            Pump(TimeSpan.FromMilliseconds(20));
        }

        foreach (ProbeTarget target in targets.Where(target => !target.Current.Settled))
        {
            _rec.Event($"{target.Current.Name}: not settled within {limitMs / 1000:F0} s");
        }
    }

    private void RequestDump(string name)
    {
        if (_dumper is null)
        {
            _rec.Event($"dump {name} skipped: no dump script");
            return;
        }

        _dumper.Request(name, Path.Combine(_settings.OutDir, $"{_settings.Label}-{name}.dmp"));
    }

    private void Pump(TimeSpan duration)
    {
        double start = _rec.Now;
        RangePatternClip.Pump(duration);
        double took = _rec.Now - start;
        if (took > duration.TotalMilliseconds + 250)
        {
            _rec.Event($"UI thread: a {duration.TotalMilliseconds:F0} ms pump took {took:F0} ms");
        }
    }

    private void PumpUntil(Func<bool> done, TimeSpan limit)
    {
        double start = _rec.Now;
        while (!done() && _rec.Now - start < limit.TotalMilliseconds) Pump(TimeSpan.FromMilliseconds(20));
    }

    private void RecordMachine(bool before)
    {
        string suffix = before ? "Before" : "After";
        _machine["mediaPlayerUserKey" + suffix] = DescribeKey(Registry.CurrentUser, @"Software\Microsoft\MediaPlayer");
        if (!before) return;

        _machine["os"] = RuntimeInformation.OSDescription;
        _machine["osVersion"] = Environment.OSVersion.VersionString;
        _machine["runtime"] = RuntimeInformation.FrameworkDescription;
        _machine["processorCount"] = Environment.ProcessorCount;
        _machine["uptimeS"] = Environment.TickCount64 / 1000.0;
        _machine["userInteractive"] = Environment.UserInteractive;
        _machine["sessionId"] = Process.GetCurrentProcess().SessionId;
        _machine["renderTier"] = RenderCapability.Tier >> 16;
        _machine["maxHardwareTextureSize"] = RenderCapability.MaxHardwareTextureSize.ToString();
        _machine["pixelShader2"] = RenderCapability.IsPixelShaderVersionSupported(2, 0);
        _machine["pixelShader3"] = RenderCapability.IsPixelShaderVersionSupported(3, 0);
        _machine["processRenderMode"] = RenderOptions.ProcessRenderMode.ToString();
        _machine["imageOS"] = Environment.GetEnvironmentVariable("ImageOS");
        _machine["imageVersion"] = Environment.GetEnvironmentVariable("ImageVersion");
        _machine["dataRoot"] = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT");
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string wpf = Path.GetDirectoryName(typeof(Visual).Assembly.Location) ?? "";
        foreach (string file in new[]
                 {
                     Path.Combine(system, "wmp.dll"), Path.Combine(system, "mfplat.dll"),
                     Path.Combine(system, "msmpeg2vdec.dll"), Path.Combine(system, "AudioSes.dll"),
                     Path.Combine(wpf, "wpfgfx_cor3.dll"),
                 })
        {
            _machine["version:" + Path.GetFileName(file)] = File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).FileVersion : "missing";
        }
    }

    private static string DescribeKey(RegistryKey root, string path)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key is null) return "absent";
        return $"present; subkeys [{string.Join(", ", key.GetSubKeyNames())}]; values {key.ValueCount}";
    }

    private void WriteReport(ModuleTracker? modules, SystemSampler? system)
    {
        try
        {
            double zero = _rec.ZeroRaw ?? 0;
            var report = new Dictionary<string, object?>
            {
                ["label"] = _settings.Label,
                ["variant"] = _settings.Variant,
                ["limitS"] = _settings.LimitMs / 1000,
                ["outcome"] = _outcome,
                ["timeBase"] = "ms since the first player's construction started (t0); negative is before",
                ["machine"] = _machine,
                ["pre"] = _pre,
                ["opens"] = _targets.SelectMany(target => target.Traces).Select(trace => trace.ToReport(zero)).ToList(),
                ["dumps"] = _dumps.Select(dump => dump.ToReport(zero)).ToList(),
                ["services"] = system?.ServiceTransitions(zero),
                ["processesStarted"] = system?.ProcessStarts(zero),
                ["processesExited"] = system?.ProcessExits(zero),
                ["cpuByProcessT0ToFirstSettled"] = system?.Delta("t0", "first-settled"),
                ["ownProcessSamples"] = system?.OwnSamples(zero),
                ["modulesLoaded"] = modules?.Loaded(zero),
                ["modulesUnloaded"] = modules?.Unloaded(zero),
                ["modulesAtStart"] = modules?.BaselineCount,
                ["events"] = _rec.Snapshot(zero),
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_settings.OutDir, _settings.Label + ".json"), json);
        }
        catch (Exception ex)
        {
            _rec.Event("report write failed: " + ex);
        }
    }
}

/// <summary>Phase times of one open of one player, raw milliseconds on the recorder's clock.</summary>
internal sealed class OpenTrace(string name, string file)
{
    internal string Name { get; } = name;
    internal string File { get; } = file;
    internal double? CtorStart { get; set; }
    internal double? CtorEnd { get; set; }
    internal double? OpenCall { get; set; }
    internal double? OpenReturn { get; set; }
    internal double? Opened { get; set; }
    internal double? Failed { get; set; }
    internal double? Frame { get; set; }
    internal double? PositionKick { get; set; }
    internal string? Failure { get; set; }
    internal string? FailureHResult { get; set; }
    internal string? Media { get; set; }
    internal string? Note { get; set; }

    internal bool Settled => Frame is not null || Failed is not null;

    internal double SettledAt => Frame ?? Failed ?? double.MaxValue;

    internal Dictionary<string, object?> ToReport(double zero)
    {
        double? Rel(double? raw) => raw is double value ? Math.Round(value - zero, 1) : null;
        double? Span(double? from, double? to) => from is double a && to is double b ? Math.Round(b - a, 1) : null;
        return new Dictionary<string, object?>
        {
            ["name"] = Name,
            ["file"] = Path.GetFileName(File),
            ["ctorStart"] = Rel(CtorStart),
            ["ctorMs"] = Span(CtorStart, CtorEnd),
            ["openCall"] = Rel(OpenCall),
            ["openReturnMs"] = Span(OpenCall, OpenReturn),
            ["openedMs"] = Span(OpenCall, Opened),
            ["failedMs"] = Span(OpenCall, Failed),
            ["failure"] = Failure,
            ["failureHResult"] = FailureHResult,
            ["firstFrameMs"] = Span(OpenCall, Frame),
            ["frameAfterOpenedMs"] = Span(Opened, Frame),
            ["positionKick"] = Rel(PositionKick),
            ["media"] = Media,
            ["note"] = Note,
        };
    }
}

/// <summary>Something that opens the probe clip: a bare MediaPlayer or the real video editor.</summary>
internal abstract class ProbeTarget(string name, ProbeRecorder rec)
{
    private double _nextRender;

    internal string Name { get; } = name;

    internal List<OpenTrace> Traces { get; } = [];

    internal OpenTrace Current => Traces[^1];

    protected ProbeRecorder Rec { get; } = rec;

    internal abstract void Start();

    internal abstract void Close();

    protected abstract BitmapSource? Render();

    /// <summary>Called from the watch loop: checks for the first presented frame once opened.</summary>
    internal virtual void Poll()
    {
        OpenTrace trace = Current;
        if (trace.Opened is null || trace.Settled || Rec.Now < _nextRender) return;
        _nextRender = Rec.Now + 50;
        BitmapSource? frame = Render();
        if (frame is not null && IsPresented(frame))
        {
            trace.Frame = Rec.Now;
            Rec.Event($"{trace.Name}: first frame presented {trace.Frame - trace.OpenCall:F0} ms after the open call");
        }
    }

    protected void RecordOpened(OpenTrace trace, string media)
    {
        trace.Opened = Rec.Now;
        trace.Media = media;
        Rec.Event($"{trace.Name}: MediaOpened {trace.Opened - trace.OpenCall:F0} ms after the open call ({media})");
    }

    protected void RecordFailed(OpenTrace trace, Exception? error)
    {
        trace.Failed = Rec.Now;
        trace.Failure = error is null ? "no exception" : $"{error.GetType().FullName}: {error.Message}";
        trace.FailureHResult = error is null ? null : $"0x{error.HResult:X8}";
        Rec.Event($"{trace.Name}: MediaFailed {trace.Failed - trace.OpenCall:F0} ms after the open call, {trace.FailureHResult} {trace.Failure}");
    }

    /// <summary>A player draws nothing until it presents a frame; a video frame is opaque.</summary>
    private static bool IsPresented(BitmapSource rendering)
    {
        var bgra = new FormatConvertedBitmap(rendering, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(bgra.PixelWidth / 2, bgra.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[3] == 255;
    }
}

/// <summary>A bare MediaPlayer, drawn in a shown window, opened the way the editor opens its MediaElement.</summary>
internal sealed class PlayerTarget(string name, string clip, ProbeRecorder rec, Panel host, int width, int height)
    : ProbeTarget(name, rec)
{
    private readonly Uri _uri = new(Path.GetFullPath(clip), UriKind.Absolute);
    private MediaPlayer? _player;

    internal override void Start()
    {
        var trace = new OpenTrace(Name, clip) { CtorStart = Rec.Now };
        Traces.Add(trace);
        _player = new MediaPlayer { ScrubbingEnabled = true };
        trace.CtorEnd = Rec.Now;
        Rec.Event($"{Name}: MediaPlayer constructed in {trace.CtorEnd - trace.CtorStart:F0} ms");
        _player.MediaOpened += (_, _) =>
        {
            if (Current.Opened is not null) return;
            RecordOpened(Current, $"video {_player.HasVideo} {_player.NaturalVideoWidth}x{_player.NaturalVideoHeight}, audio {_player.HasAudio}, duration {(_player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalMilliseconds.ToString("F0") + " ms" : "unknown")}");
            // Park on the first frame: a paused, scrubbing player presents it after a seek.
            _player.Position = TimeSpan.Zero;
            Current.PositionKick = Rec.Now;
        };
        _player.MediaFailed += (_, e) => RecordFailed(Current, e.ErrorException);
        host.Children.Add(new Image
        {
            Width = width, Height = height, Margin = new Thickness(4),
            Source = new DrawingImage(new VideoDrawing { Player = _player, Rect = new Rect(0, 0, width, height) }),
        });
        Open(trace);
    }

    /// <summary>The product-side recovery under test: close the source and open it again.</summary>
    internal void Reopen(string traceName)
    {
        Rec.Event($"{Current.Name}: closing to reopen as {traceName}");
        _player!.Close();
        var trace = new OpenTrace(traceName, clip) { Note = "same MediaPlayer, Close() then Open()" };
        Traces.Add(trace);
        Open(trace);
    }

    private void Open(OpenTrace trace)
    {
        trace.OpenCall = Rec.Now;
        _player!.Open(_uri);
        trace.OpenReturn = Rec.Now;
        _player.Play();   // as the editor: Play kicks decoding so MediaOpened fires…
        _player.Pause();  // …and Pause sits on the first frame
        Rec.Event($"{trace.Name}: Open() returned after {trace.OpenReturn - trace.OpenCall:F0} ms");
    }

    protected override BitmapSource? Render()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawVideo(_player, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    internal override void Close() => _player?.Close();
}

/// <summary>The real video editor window, as the frame-step tests open it.</summary>
internal sealed class EditorTarget(
    string name, string clip, ProbeRecorder rec, string dataRoot, int width, int height, int fps, int frames, double containerMs)
    : ProbeTarget(name, rec)
{
    private VideoEditorWindow? _editor;

    internal override void Start()
    {
        var trace = new OpenTrace(Name, clip)
        {
            CtorStart = Rec.Now,
            Note = "editor: the Source is set, then Play and Pause, in its Loaded handler; the open call is that handler",
        };
        Traces.Add(trace);
        Directory.CreateDirectory(dataRoot);
        _editor = new VideoEditorWindow(
            new RecordingResult(clip, containerMs, fps, frames, width, height),
            AppPaths.CreateForRoot(dataRoot),
            NullLoggerFactory.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1000, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
            // Long enough that the fallback never makes the editor ready before MediaOpened here.
            OpenTimeoutForTest = TimeSpan.FromMinutes(30),
        };
        trace.CtorEnd = Rec.Now;
        MediaElement media = _editor.MediaForTest;
        media.MediaOpened += (_, _) =>
        {
            if (Current.Opened is null)
            {
                RecordOpened(Current, $"video {media.HasVideo} {media.NaturalVideoWidth}x{media.NaturalVideoHeight}, audio {media.HasAudio}");
            }
        };
        media.MediaFailed += (_, e) => RecordFailed(Current, e.ErrorException);
        // The editor subscribed its own Loaded handler first, so this one runs right after the
        // editor set the source.
        _editor.Loaded += (_, _) =>
        {
            trace.OpenCall = trace.OpenReturn = Rec.Now;
            Rec.Event($"{Name}: editor Loaded, source set");
        };
        Rec.Event($"{Name}: editor constructed in {trace.CtorEnd - trace.CtorStart:F0} ms");
        _editor.Show();
    }

    internal override void Poll()
    {
        if (Current.OpenCall is not null) base.Poll();
    }

    protected override BitmapSource? Render() => _editor?.RenderCurrentFrameForTest();

    internal override void Close() => _editor?.Close();
}

/// <summary>Timestamped events, written to a log as they happen so a killed run still leaves them.</summary>
internal sealed class ProbeRecorder : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<(double At, string Text)> _events = [];
    private readonly StreamWriter _log;
    private long _zeroTicks = -1;

    internal ProbeRecorder(string logPath)
    {
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
    }

    internal double Now => _clock.Elapsed.TotalMilliseconds;

    internal double? ZeroRaw
    {
        get
        {
            long ticks = Volatile.Read(ref _zeroTicks);
            return ticks >= 0 ? TimeSpan.FromTicks(ticks).TotalMilliseconds : null;
        }
    }

    /// <summary>Milliseconds since t0; before t0, since the probe started.</summary>
    internal double SinceZero => Now - (ZeroRaw ?? 0);

    internal void MarkZero()
    {
        Volatile.Write(ref _zeroTicks, _clock.Elapsed.Ticks);
        Event("t0: the first player is created and opened now");
    }

    internal void Event(string text)
    {
        double at = Now;
        lock (_gate)
        {
            _events.Add((at, text));
            string stamp = ZeroRaw is double zero ? $"t0{at - zero,8:+0;-0}" : $"start+{at,7:F0}";
            _log.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {stamp} ms  {text}");
        }
    }

    internal List<string> Snapshot(double zero)
    {
        lock (_gate)
        {
            return [.. _events.Select(e => $"{e.At - zero,9:F0}  {e.Text}")];
        }
    }

    public void Dispose()
    {
        lock (_gate) _log.Dispose();
    }
}

internal sealed class DumpRecord(string name, string path, double requested)
{
    internal string Name { get; } = name;
    internal string Path { get; } = path;
    internal double Requested { get; } = requested;
    internal double? Completed { get; set; }
    internal string? Error { get; set; }

    internal Dictionary<string, object?> ToReport(double zero) => new()
    {
        ["name"] = Name,
        ["file"] = System.IO.Path.GetFileName(Path),
        ["requested"] = Math.Round(Requested - zero, 1),
        ["completed"] = Completed is double done ? Math.Round(done - zero, 1) : null,
        ["error"] = Error,
    };
}

/// <summary>
/// Writes native minidumps of this process from a separate PowerShell process, started and
/// compiled before t0 so a request costs no start-up during the open being observed.
/// </summary>
internal sealed class ProbeDumper : IDisposable
{
    private readonly Process _process;
    private readonly ProbeRecorder _rec;
    private readonly List<DumpRecord> _records;
    private readonly Thread _reader;

    private ProbeDumper(Process process, ProbeRecorder rec, List<DumpRecord> records)
    {
        _process = process;
        _rec = rec;
        _records = records;
        _reader = new Thread(ReadResults) { IsBackground = true, Name = "probe dump results" };
        _reader.Start();
    }

    internal static ProbeDumper? TryStart(string script, ProbeRecorder rec, List<DumpRecord> records)
    {
        try
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                info.ArgumentList.Add(arg);
            Process process = Process.Start(info) ?? throw new InvalidOperationException("powershell.exe did not start");
            Task<string?> ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(120)) || ready.Result != "ready")
            {
                rec.Event($"dumper not ready: {(ready.IsCompleted ? ready.Result : "timeout")}");
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            rec.Event($"dumper ready, pid {process.Id}");
            return new ProbeDumper(process, rec, records);
        }
        catch (Exception ex)
        {
            rec.Event("dumper failed to start: " + ex.Message);
            return null;
        }
    }

    internal void Request(string name, string path)
    {
        lock (_records) _records.Add(new DumpRecord(name, path, _rec.Now));
        _rec.Event($"dump requested: {name}");
        _process.StandardInput.WriteLine($"{Environment.ProcessId}|{path}");
        _process.StandardInput.Flush();
    }

    private void ReadResults()
    {
        while (_process.StandardOutput.ReadLine() is { } line)
        {
            // "done|<path>|<ms>" or "failed|<path>|<message>"
            string[] parts = line.Split('|', 3);
            if (parts.Length < 3) continue;
            lock (_records)
            {
                DumpRecord? record = _records.LastOrDefault(r => string.Equals(r.Path, parts[1], StringComparison.OrdinalIgnoreCase));
                if (record is null) continue;
                if (parts[0] == "done") record.Completed = _rec.Now;
                else record.Error = parts[2];
            }

            _rec.Event($"dump {parts[0]}: {Path.GetFileName(parts[1])} ({parts[2]})");
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();   // the dumper finishes queued dumps, then exits
            if (!_process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                _rec.Event("dumper did not exit; killing it");
                _process.Kill();
            }

            _reader.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _rec.Event("dumper shutdown failed: " + ex.Message);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
