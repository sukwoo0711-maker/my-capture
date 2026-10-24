using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;
using MyCapture.Tests;

namespace MyCapture.App.Tests;

/// <summary>
/// The receipt sections: a still capture confirmed on the selector until its quick access card is on
/// screen and the capture has flown into it, through the App's own completion path.
/// </summary>
/// <remarks>
/// <para>
/// Sections <c>receipt-region</c> (480 x 300 on a 1920 x 1080 frame), <c>receipt-1080</c>,
/// <c>receipt-1440</c> and <c>receipt-4k</c> (whole 1920 x 1080, 3440 x 1440 and 3840 x 2160 frames).
/// A frame larger than the desktop's largest window (SM_CXMAXTRACK/SM_CYMAXTRACK) cannot fly: its
/// lift placement fails, as it would on such a desktop. Each child process takes one cold
/// capture (after the app's launch prewarm) and ten warm ones, motion on. The
/// <c>receipt-first-*</c> sections run the same captures but call the App's own
/// <c>ShowQuickAccess</c> right after the selector has closed (on the dispatcher turn after the lift),
/// suppressing the scheduled show: a simulation of a card shown first, the product unchanged. The
/// <c>receipt-ab-*</c> sections alternate the two in one process after a cold current-path capture
/// (ten warm captures each), so a busy desktop loads both alike.
/// </para>
/// <para>
/// The App is the real class without its shell (as <c>CaptureLibraryFirstTests</c> builds it): the
/// isolated library, persistence, commit service and the real <see cref="CaptureOverlayCoordinator"/>
/// and <see cref="CaptureOverlayWindow"/>. Everything shown lies on a test display at -32000 and is
/// never activated: the selector shows without activation and its first-render activation is
/// detached, the card and the flight windows never activate by design, and the card's activation key
/// goes to a router that registers nothing. The shutter is off (it would sound on a shared desktop);
/// its Play call is the first statement of the handler, timed as "handler-entered". The clipboard
/// runs the real PNG encode and data object and skips only the OLE write.
/// </para>
/// <para>
/// Stage times come from <see cref="ReceiptTimeline"/> marks (default off in the product), window
/// events, the persistence hooks and the dispatcher's own hooks. The heartbeat is off in these
/// sections: its 8 ms Input-priority beat would sit in front of the card's ContextIdle turn.
/// </para>
/// </remarks>
internal sealed partial class AppResponsivenessProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static bool IsReceiptSection(string section) => section.StartsWith("receipt-", StringComparison.Ordinal);

    /// <summary>Stage columns, in path order; times are milliseconds after the selection was confirmed.</summary>
    private static readonly string[] ReceiptStages =
    [
        "overlay-hidden", "handler-entered", "handler-returned", "overlay-closed", "commit-op-end",
        "lift-affinity-start", "lift-affinity-end", "lift-shown", "lift-none", "lift-rendered",
        "original-written", "save-published", "save-resumed", "clipboard-data-ready", "copy-resumed",
        "card-scheduled", "handler-task-done", "card-op-start", "card-constructed", "card-show", "card-hwnd-ready", "card-loaded", "card-shown",
        "card-first-rendering", "card-content-rendered",
        "flight-handover", "flight-start", "flight-arrived", "flight-skipped", "flight-finished",
    ];

    private static readonly (string Name, string From, string To)[] ReceiptIntervals =
    [
        ("save (handler → save-resumed)", "handler-entered", "save-resumed"),
        ("copy (handler → copy-resumed)", "handler-entered", "copy-resumed"),
        ("ContextIdle wait (scheduled → card op)", "card-scheduled", "card-op-start"),
        ("card constructor", "card-op-start", "card-constructed"),
        ("Land + Show call", "card-constructed", "card-shown"),
        ("  Show → Loaded (HWND, layout, Loaded handler)", "card-show", "card-loaded"),
        ("  Loaded → SourceInitialized handlers done (no-activate, exclusion)", "card-loaded", "card-hwnd-ready"),
        ("  → Show returns", "card-hwnd-ready", "card-shown"),
        ("lift: SetWindowDisplayAffinity", "lift-affinity-start", "lift-affinity-end"),
        ("Show → first Rendering", "card-shown", "card-first-rendering"),
        ("Show → ContentRendered", "card-shown", "card-content-rendered"),
        ("ContentRendered → flight start", "card-content-rendered", "flight-start"),
        ("flight (start → arrival)", "flight-start", "flight-arrived"),
    ];

    private volatile ReceiptCapture? _receipt;

    private void RunReceipt()
    {
        bool cardFirst = _section.StartsWith("receipt-first-", StringComparison.Ordinal);
        // receipt-ab-*: after the cold capture (current path), warm captures alternate card first and
        // current, so both see the same load from whatever else this desktop runs meanwhile.
        bool alternate = _section.StartsWith("receipt-ab-", StringComparison.Ordinal);
        string kind = _section[(_section.LastIndexOf('-') + 1)..];
        (int frameWidth, int frameHeight, RectD? part) = kind switch
        {
            "region" => (1920, 1080, new RectD(600, 300, 480, 300)),
            "1080" => (1920, 1080, (RectD?)null),
            "4k" => (3840, 2160, (RectD?)null),
            "1440" => (3440, 1440, (RectD?)null),
            _ => throw new ArgumentException("Unknown receipt section: " + _section),
        };
        RectD region = part ?? new RectD(0, 0, frameWidth, frameHeight);
        bool whole = part is null;
        _size = $"{(int)region.Width}x{(int)region.Height}" + (whole ? " (full screen)" : $" on {frameWidth}x{frameHeight}");
        _variant = alternate ? "alternating: card first odd / current even" : cardFirst ? "card first (simulated)" : "current";

        uint dpi = Ui(() => MonitorEnumerator.GetPrimary().Dpi);
        // A display far off the visible desktop, at the primary display's scale, that the card and the
        // flight treat as real (as CaptureFlightTests does).
        var monitor = new MonitorInfo("PROBE-OFFSCREEN", new RectD(-32000, -32000, frameWidth, frameHeight),
            new RectD(-32000, -32000, frameWidth, frameHeight - 40), dpi, false);
        _environment.Add($"Receipt: {_variant}; frame {frameWidth}x{frameHeight} on a test display at -32000 ({dpi} dpi); selection {region}; whole monitor {whole}");
        int maxTrackWidth = GetSystemMetrics(59), maxTrackHeight = GetSystemMetrics(60);
        _environment.Add($"Largest window this desktop allows: {maxTrackWidth}x{maxTrackHeight} px (SM_CXMAXTRACK/SM_CYMAXTRACK)");
        if (frameWidth > maxTrackWidth || frameHeight > maxTrackHeight)
        {
            // No window here can be as large as this frame: the selector is cut to the largest window,
            // and the lift window cannot be placed (its placement fails after three attempts, the
            // capture goes on without the flight and the card enters normally).
            _environment.Add($"This desktop cannot hold a {frameWidth}x{frameHeight} window: the selector is cut to the largest window, the lift's placement fails and the card enters without the flight.");
        }

        _recorder.Step = "setup: synthetic screen-like frame";
        byte[] pixels = ScreenLikePixels(frameWidth, frameHeight, frameWidth + frameHeight);
        FrozenFrame frame = Ui(() =>
        {
            BitmapSource bitmap = BitmapSource.Create(frameWidth, frameHeight, 96, 96, PixelFormats.Bgr32, null, pixels, frameWidth * 4);
            bitmap.Freeze();
            return new FrozenFrame(bitmap, monitor.Bounds, monitor, 0);
        });
        pixels = [];

        string root = TestRecycleBin.CreateTempSubdirectory("mcap-receipt-").FullName;
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IReadOnlyList<MonitorInfo>> previousMonitors = CaptureFlight.ReadMonitors;
        Func<Window, bool> previousExclusion = CaptureFlightDecoration.ApplyCaptureExclusion;
        ReceiptShell shell = Ui(() => new ReceiptShell(root, this));
        var captures = new List<ReceiptCapture>();
        DispatcherHookEventHandler posted = (_, e) => { if (e.Operation.Priority == DispatcherPriority.ContextIdle) shell.LastContextIdle = e.Operation; };
        DispatcherHookEventHandler started = (_, e) => _receipt?.OperationStarted(e.Operation);
        DispatcherHookEventHandler finished = (_, e) => _receipt?.OperationFinished(e.Operation);
        try
        {
            Ui(() =>
            {
                FluidMotion.AnimationsEnabledOverrideForTest = true;
                CaptureFlight.ReadMonitors = () => [monitor];
                // The same call, timed: the first one in a capture is the lift window's.
                CaptureFlightDecoration.ApplyCaptureExclusion = window =>
                {
                    _receipt?.Mark("lift-affinity-start");
                    try { return previousExclusion(window); }
                    finally { _receipt?.Mark("lift-affinity-end"); }
                };
                ReceiptTimeline.Mark = stage => OnReceiptMark(shell, stage);
                _ui.Hooks.OperationPosted += posted;
                _ui.Hooks.OperationStarted += started;
                _ui.Hooks.OperationCompleted += finished;
                _ui.Hooks.OperationAborted += finished;
                _environment.Add($"Flight eligible here: animations (forced on) {FluidMotion.AnimationsEnabled}, remote {CaptureFlight.ReadRemoteSession()}, layered {CaptureFlight.ReadLayeredSupport()}");
            });

            // As the app does at launch: the selector's and the editor's first-use costs at idle.
            _recorder.Step = "setup: launch prewarm (selector, editor)";
            Ui(() => CaptureOverlayPrewarm.Run(MonitorEnumerator.GetVirtualDesktopBounds()));
            WaitIdle();
            MethodInfo? editorPrewarm = typeof(AnnotationEditorWindow).Assembly.GetType("MyCapture.App.Editing.AnnotationEditorPrewarm")
                ?.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);
            if (editorPrewarm is not null) Ui(() => editorPrewarm.Invoke(null, null));
            WaitIdle();

            for (int index = 0; index <= (alternate ? 20 : 10); index++)
            {
                bool first = alternate ? index % 2 == 1 : cardFirst;
                ReceiptCapture capture = MeasureReceipt(shell, frame, region, whole, monitor, first, index);
                captures.Add(capture);
                _delay.Wait(400);
                // As the next capture command does before its selector opens.
                _recorder.Step = "setup: dismiss the card for the next capture";
                Ui(shell.DismissQuickAccessForCapture);
                _delay.Wait(300);
            }

            try
            {
                ReceiptBreakdown(frame, region, monitor);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _environment.Add("Receipt breakdown stopped: " + ex.Message);
            }
        }
        finally
        {
            Ui(() =>
            {
                ReceiptTimeline.Mark = null;
                _ui.Hooks.OperationPosted -= posted;
                _ui.Hooks.OperationStarted -= started;
                _ui.Hooks.OperationCompleted -= finished;
                _ui.Hooks.OperationAborted -= finished;
                shell.DismissQuickAccessForCapture();
                shell.Dispose();
                FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
                CaptureFlight.ReadMonitors = previousMonitors;
                CaptureFlightDecoration.ApplyCaptureExclusion = previousExclusion;
            });
            DeleteRoot(root);
        }

        WriteReceiptResults(captures);
    }

    private ReceiptCapture MeasureReceipt(ReceiptShell shell, FrozenFrame frame, RectD region, bool whole, MonitorInfo monitor,
        bool cardFirst, int index)
    {
        string kind = index == 0 ? "cold" : "warm";
        var capture = new ReceiptCapture(index, kind, cardFirst);
        var overlayRendered = new ManualResetEventSlim();

        // The selector over its frozen frame, as CaptureOverlayCoordinator.ShowOverlay wires it, shown
        // without activation off the visible desktop.
        _recorder.Step = "setup: selector over the frozen frame";
        CaptureOverlayWindow overlay = Ui(() => shell.ShowSelector(frame, overlayRendered, capture));
        Wait(overlayRendered, "selector first render");
        WaitIdle();
        // The user drags and releases.
        _delay.Wait(600);

        ProbeScenario s = Begin($"capture #{index + 1} ({kind}): confirmed selection → card on screen, capture landed");
        _receipt = capture;
        Op(s, "CaptureOverlayWindow.CompleteSelection (input turn)", () =>
        {
            capture.Begin();
            overlay.CompleteSelection(region, null, false, whole);
            capture.Mark("commit-op-end");
            // The card-first simulation: on the dispatcher turn after the lift (same priority, posted
            // later), the App's own ShowQuickAccess for the pending record, before the save and copy.
            if (cardFirst) _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => shell.ShowCardFirst(capture, monitor)));
        }, idle: false);

        var timeout = Stopwatch.StartNew();
        while (!capture.IsComplete && timeout.Elapsed < TimeSpan.FromSeconds(20)) _delay.Wait(5);
        if (!capture.IsComplete) capture.Notes.Add("timed out after 20 s waiting for the card, the flight and the handler");
        // The flight ends one frame after it lands.
        _delay.Wait(150);
        _receipt = null;
        End(s);

        foreach (string stage in ReceiptStages)
            if (capture.Ms(stage) is double ms) s.Extra[stage] = ms;
        capture.Notes.Add(Ui(shell.Describe));
        s.Notes = string.Join("; ", capture.Notes);
        return capture;
    }

    private void OnReceiptMark(ReceiptShell shell, string stage)
    {
        ReceiptCapture? capture = _receipt;
        if (capture is null) return;
        capture.Mark(stage);
        switch (stage)
        {
            case "card-scheduled":
                if (capture.CardFirst)
                {
                    // The simulated card is already up: the scheduled show finds a newer receipt and does nothing.
                    shell.SuppressScheduledShow();
                }
                else
                {
                    capture.CardOperation = shell.LastContextIdle;
                }
                break;

            case "lift-shown":
                shell.WatchLift(capture);
                break;

            case "card-show":
                shell.WatchCard(capture);
                break;

            case "card-shown":
                EventHandler? rendering = null;
                rendering = (_, _) =>
                {
                    CompositionTarget.Rendering -= rendering;
                    capture.Mark("card-first-rendering");
                };
                CompositionTarget.Rendering += rendering;
                break;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>
    /// The pieces of the lift, the card's Show and the save and copy, timed one at a time, three
    /// times each, on an idle UI thread after the captures (nothing else competes for the CPU).
    /// Window creation is timed with no other probe window open, as after the selector closes, and
    /// again while one plain window stays open off the desktop.
    /// </summary>
    private void ReceiptBreakdown(FrozenFrame frame, RectD region, MonitorInfo monitor)
    {
        _recorder.Step = "breakdown: receipt parts";
        var source = new RectD(frame.ScreenBounds.Left + region.Left, frame.ScreenBounds.Top + region.Top, region.Width, region.Height);
        string folder = TestRecycleBin.CreateTempSubdirectory("mcap-receipt-parts-").FullName;
        using var router = new ReceiptKeyboardRouter(_ => new ReceiptShell.NoHotkeys());
        try
        {
            for (int round = 0; round < 3; round++)
            {
                long begin = Stopwatch.GetTimestamp();
                BitmapSource crop = Ui(() => ScreenCaptureEngine.Crop(frame, region));
                AddReceiptPart("ScreenCaptureEngine.Crop", Ms(begin));
                (RectD bounds, double scale) = CaptureFlight.PlanBounds(new Rect(source.Left, source.Top, source.Width, source.Height), [monitor])!.Value;

                foreach (bool keepAlive in new[] { false, true })
                {
                    string when = keepAlive ? " [another window open]" : " [no other window]";
                    Window? keep = null;
                    if (keepAlive)
                    {
                        Ui(() =>
                        {
                            keep = new Window
                            {
                                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 8, Height = 8,
                            };
                            keep.Show();
                        });
                        WaitIdle();
                        _delay.Wait(100);
                    }

                    try
                    {
                        // The lift window, step by step, as CaptureFlight.TryLift and ShowDecoration build it.
                        using var liftRendered = new ManualResetEventSlim();
                        CaptureFlightDecoration lift = null!;
                        Ui(() =>
                        {
                            long t = Stopwatch.GetTimestamp();
                            lift = new CaptureFlightDecoration(crop, bounds, scale);
                            AddReceiptPart("lift: new CaptureFlightDecoration" + when, Ms(t));
                            lift.ContentRendered += (_, _) => liftRendered.Set();
                            t = Stopwatch.GetTimestamp();
                            IntPtr handle = new WindowInteropHelper(lift).EnsureHandle();
                            AddReceiptPart("lift: EnsureHandle (layered HWND, click-through, DWM transitions off, display affinity)" + when, Ms(t));
                            t = Stopwatch.GetTimestamp();
                            lift.Show();
                            AddReceiptPart("lift: Window.Show after EnsureHandle" + when, Ms(t));
                            t = Stopwatch.GetTimestamp();
                            try
                            {
                                PhysicalWindowPositioner.PlaceTopmostWithoutActivation(handle, bounds);
                                AddReceiptPart("lift: PlaceTopmostWithoutActivation" + when, Ms(t));
                            }
                            catch (InvalidOperationException)
                            {
                                AddReceiptPart("lift: PlaceTopmostWithoutActivation FAILED (window larger than this desktop allows)" + when, Ms(t));
                            }
                        });
                        begin = Stopwatch.GetTimestamp();
                        Wait(liftRendered, "lift window first render");
                        AddReceiptPart("lift: Show → ContentRendered (wall)" + when, Ms(begin));
                        Ui(() =>
                        {
                            long t = Stopwatch.GetTimestamp();
                            lift.Close();
                            AddReceiptPart("lift: Close" + when, Ms(t));
                        });
                        WaitIdle();

                        // A plain window's HWND, and display affinity and DWM transitions alone on it.
                        Ui(() =>
                        {
                            var plain = new Window
                            {
                                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
                                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 100, Height = 100,
                            };
                            long t = Stopwatch.GetTimestamp();
                            IntPtr handle = new WindowInteropHelper(plain).EnsureHandle();
                            AddReceiptPart("plain window: EnsureHandle (not layered)" + when, Ms(t));
                            t = Stopwatch.GetTimestamp();
                            bool applied = CaptureWindowExclusion.TryApply(handle);
                            AddReceiptPart($"SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) ({(applied ? "applied" : "refused")})" + when, Ms(t));
                            t = Stopwatch.GetTimestamp();
                            CaptureWindowExclusion.DisableTransitions(plain);
                            AddReceiptPart("DwmSetWindowAttribute(TRANSITIONS_FORCEDISABLED)" + when, Ms(t));
                            plain.Close();
                        });
                        WaitIdle();

                        // The card, step by step.
                        using var cardRendered = new ManualResetEventSlim();
                        QuickAccessWindow card = null!;
                        Ui(() =>
                        {
                            long t = Stopwatch.GetTimestamp();
                            card = new QuickAccessWindow(crop, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                                () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), capturedRegion: source,
                                targetMonitor: monitor, copyText: null, copiedToClipboard: true, firstCaptureTip: null, keyboardRouter: router);
                            AddReceiptPart("card: new QuickAccessWindow" + when, Ms(t));
                            card.ContentRendered += (_, _) => cardRendered.Set();
                            t = Stopwatch.GetTimestamp();
                            _ = new WindowInteropHelper(card).EnsureHandle();
                            AddReceiptPart("card: EnsureHandle (layered HWND, no-activate, DWM transitions off, display affinity)" + when, Ms(t));
                            t = Stopwatch.GetTimestamp();
                            card.Show();
                            AddReceiptPart("card: Window.Show after EnsureHandle (layout, Loaded, placement)" + when, Ms(t));
                        });
                        begin = Stopwatch.GetTimestamp();
                        Wait(cardRendered, "card first render");
                        AddReceiptPart("card: Show → ContentRendered (wall)" + when, Ms(begin));
                        Ui(() => card.CloseNow());
                        WaitIdle();
                    }
                    finally
                    {
                        if (keep is not null) Ui(keep.Close);
                        WaitIdle();
                    }
                }

                // The save and the copy, each alone.
                begin = Stopwatch.GetTimestamp();
                _ = WaitTask(Task.Run(() => ImageCodec.EncodePng(crop, null)), "encode");
                AddReceiptPart("ImageCodec.EncodePng (the copy's PNG, thread pool)", Ms(begin));
                begin = Stopwatch.GetTimestamp();
                _ = WaitTask(ClipboardImageService.CopyImageAsync(crop, null, static _ => { }), "clipboard data object");
                AddReceiptPart("ClipboardImageService.CopyImageAsync (encode + data object, OLE write skipped)", Ms(begin));
                string path = DiagnosticOutputPaths.Child(folder, $"original-{round}.png");
                begin = Stopwatch.GetTimestamp();
                long bytes = WaitTask(MyCapture.App.Threading.StaThreadTask.RunAsync(() => ImageCodec.SavePng(crop, path), "probe original"), "save png");
                AddReceiptPart($"ImageCodec.SavePng original.png on an STA thread ({bytes / 1024} KiB)", Ms(begin));
                WaitIdle();
            }
        }
        finally
        {
            TestRecycleBin.DeleteDirectory(folder, recursive: true);
        }
    }

    private void AddReceiptPart(string item, double ms) => _recorder.AddBreakdown(_size, _variant, item + " ms", ms);

    // ---- Results -----------------------------------------------------------------------------

    private void WriteReceiptResults(List<ReceiptCapture> captures)
    {
        var csv = new StringBuilder();
        csv.AppendLine("section,size,variant,index,kind,capture_variant," + string.Join(",", ReceiptStages) + ",notes");
        foreach (ReceiptCapture c in captures)
        {
            csv.AppendLine(string.Join(",", _section, Q(_size), Q(_variant), c.Index, c.Kind, c.Variant,
                string.Join(",", ReceiptStages.Select(stage => c.Ms(stage) is double ms ? F(ms) : "")), Q(string.Join("; ", c.Notes))));
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(_output, "receipt-stages.csv"), csv.ToString(), new UTF8Encoding(true));

        var ops = new StringBuilder();
        ops.AppendLine("section,index,kind,operation,priority,start_ms,end_ms,duration_ms");
        foreach (ReceiptCapture c in captures)
        {
            foreach (ReceiptOperation op in c.Operations)
                ops.AppendLine(string.Join(",", _section, c.Index, c.Kind, Q(op.Name), op.Priority, F(c.MsAt(op.Start)), F(c.MsAt(op.End)), F(c.MsAt(op.End) - c.MsAt(op.Start))));
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(_output, "receipt-dispatcher.csv"), ops.ToString(), new UTF8Encoding(true));

        var md = new StringBuilder();
        md.AppendLine($"# 캡처 확정 → 빠른 작업 카드: {_section}");
        md.AppendLine();
        foreach (IGrouping<bool, ReceiptCapture> group in captures.Where(c => c.Index > 0).GroupBy(c => c.CardFirst))
            WriteReceiptGroup(md, group.ToArray(), captures.FirstOrDefault(c => c.Index == 0 && c.CardFirst == group.Key));

        md.AppendLine();
        md.AppendLine("## 따로 잰 부분(유휴 UI 스레드, 3회)");
        md.AppendLine();
        md.AppendLine("| 부분 | 1회 | 2회 | 3회 |");
        md.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var pair in _recorder.Breakdown.Where(pair => pair.Key.Size == _size && pair.Key.Variant == _variant))
            md.AppendLine($"| {pair.Key.Item.Replace("|", "/")} | {string.Join(" | ", pair.Value.Select(F))} |");

        md.AppendLine();
        md.AppendLine("## 메모");
        md.AppendLine();
        foreach (ReceiptCapture c in captures) md.AppendLine($"- #{c.Index + 1} {c.Kind} {c.Variant}: {string.Join("; ", c.Notes)}");
        File.WriteAllText(DiagnosticOutputPaths.Child(_output, "receipt-summary.md"), md.ToString(), new UTF8Encoding(false));
    }

    private void WriteReceiptGroup(StringBuilder md, ReceiptCapture[] warm, ReceiptCapture? cold)
    {
        string variant = warm[0].Variant;
        md.AppendLine($"## {variant}");
        md.AppendLine();
        md.AppendLine($"크기 {_size}, {variant}. 시각은 선택 확정(CompleteSelection 시작) 기준 ms. '처음'은 새 프로세스의 첫 캡처{(cold is null ? "(이 변형에는 없음)" : "")}, 나머지는 같은 프로세스의 따뜻한 캡처 {warm.Length}회의 중앙값과 p95(가장 가까운 순위).");
        md.AppendLine();
        md.AppendLine("| 단계 | 처음 | 중앙값 | p95 | n |");
        md.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (string stage in ReceiptStages)
        {
            double[] values = warm.Select(c => c.Ms(stage)).OfType<double>().ToArray();
            double? first = cold?.Ms(stage);
            if (values.Length == 0 && first is null) continue;
            md.AppendLine($"| {stage} | {(first is double f ? F(f) : "-")} | {Cell(Median(values))} | {Cell(P95Rank(values))} | {values.Length} |");
        }

        md.AppendLine();
        md.AppendLine("| 구간 | 처음 | 중앙값 | p95 |");
        md.AppendLine("| --- | ---: | ---: | ---: |");
        foreach ((string name, string from, string to) in ReceiptIntervals)
        {
            // A simulated card-first show has no scheduled turn of its own.
            if (from == "card-scheduled" && warm[0].CardFirst) continue;
            double[] values = warm.Select(c => c.Between(from, to)).OfType<double>().ToArray();
            double? first = cold?.Between(from, to);
            if (values.Length == 0 && first is null) continue;
            md.AppendLine($"| {name} | {(first is double f ? F(f) : "-")} | {Cell(Median(values))} | {Cell(P95Rank(values))} |");
        }

        // What the UI thread ran while the card waited for its ContextIdle turn, and before it.
        foreach ((string title, Func<ReceiptCapture, (long From, long To)?> window) in new (string, Func<ReceiptCapture, (long, long)?>)[]
        {
            ("ContextIdle 대기 중 UI 스레드 작업(따뜻한 캡처 평균)", c => c.Window("card-scheduled", "card-op-start")),
            ("확정부터 카드 작업 시작까지 UI 스레드 작업(따뜻한 캡처 평균)", c => c.Window("commit", "card-op-start")),
            ("카드 표시부터 날아감 도착까지 UI 스레드 작업(따뜻한 캡처 평균)", c => c.Window("card-shown", "flight-arrived")),
        })
        {
            var totals = new Dictionary<(string Name, string Priority), (double Ms, int Count)>();
            int windows = 0;
            foreach (ReceiptCapture c in warm)
            {
                if (window(c) is not (long from, long to)) continue;
                windows++;
                foreach (ReceiptOperation op in c.Operations.Where(op => op.Start >= from && op.Start < to))
                {
                    totals.TryGetValue((op.Name, op.Priority), out (double Ms, int Count) sum);
                    totals[(op.Name, op.Priority)] = (sum.Ms + c.MsAt(op.End) - c.MsAt(op.Start), sum.Count + 1);
                }
            }

            if (windows == 0) continue;
            md.AppendLine();
            md.AppendLine($"### {title}");
            md.AppendLine();
            md.AppendLine("| 작업 | 우선순위 | 평균 ms(포함) | 평균 횟수 |");
            md.AppendLine("| --- | --- | ---: | ---: |");
            foreach (var pair in totals.OrderByDescending(pair => pair.Value.Ms).Take(12))
                md.AppendLine($"| {pair.Key.Name.Replace("|", "/")} | {pair.Key.Priority} | {F(pair.Value.Ms / windows)} | {F(pair.Value.Count / (double)windows)} |");
        }

        md.AppendLine();

        static string Cell(double? value) => value is double v ? F(v) : "-";
    }

    private static double? Median(double[] values)
    {
        if (values.Length == 0) return null;
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double? P95Rank(double[] values)
    {
        if (values.Length == 0) return null;
        double[] sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// A desktop-like frame: windows with title bars, document, code and image content, text-like
    /// rows of anti-aliased marks, a taskbar. PNG compresses it as it does a real screen, unlike noise.
    /// </summary>
    internal static byte[] ScreenLikePixels(int width, int height, int seed)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        var random = new Random(seed);
        var windows = new List<(int X, int Y, int W, int H, int Kind)>();
        // A large document window behind, then smaller ones: code, image, document, code.
        windows.Add((width / 20, height / 24, width * 7 / 10, height * 4 / 5, 0));
        for (int i = 0; i < 4; i++)
        {
            int w = (int)(width * (0.3 + random.NextDouble() * 0.3));
            int h = (int)(height * (0.35 + random.NextDouble() * 0.35));
            windows.Add((random.Next(0, Math.Max(1, width - w)), random.Next(0, Math.Max(1, height - h - 48)), w, h, new[] { 1, 2, 0, 1 }[i]));
        }

        Parallel.For(0, height, y =>
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                byte b, g, r;
                if (y >= height - 48)
                {
                    bool icon = x % 56 is >= 12 and < 44 && (y - (height - 48)) is >= 8 and < 40 && x < width / 2;
                    (b, g, r) = icon ? ((byte)(120 + x / 56 * 13 % 120), (byte)(90 + x / 56 * 29 % 140), (byte)(60 + x / 56 * 41 % 180)) : ((byte)38, (byte)32, (byte)30);
                }
                else
                {
                    int top = -1;
                    for (int i = windows.Count - 1; i >= 0; i--)
                    {
                        var w = windows[i];
                        if (x >= w.X && x < w.X + w.W && y >= w.Y && y < w.Y + w.H) { top = i; break; }
                    }

                    if (top < 0) (b, g, r) = ((byte)(150 + y * 60 / height), (byte)(95 + y * 40 / height), (byte)(40 + x * 30 / width));
                    else (b, g, r) = WindowPixel(windows[top], x - windows[top].X, y - windows[top].Y, top);
                }

                int offset = row + x * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        });
        return pixels;

        static (byte, byte, byte) WindowPixel((int X, int Y, int W, int H, int Kind) w, int x, int y, int id)
        {
            if (x == 0 || y == 0 || x == w.W - 1 || y == w.H - 1) return (190, 190, 190);
            bool dark = w.Kind == 1;
            if (y < 32)
            {
                // Title bar with its title and three caption glyphs.
                if (y is >= 11 and < 21 && x is >= 12 and < 260 && Mark(x, y, id * 7 + 1)) return dark ? ((byte)200, (byte)200, (byte)200) : ((byte)40, (byte)40, (byte)40);
                if (x > w.W - 140 && y is >= 15 and < 17 && x % 46 is >= 18 and < 28) return dark ? ((byte)200, (byte)200, (byte)200) : ((byte)60, (byte)60, (byte)60);
                return dark ? ((byte)48, (byte)44, (byte)42) : ((byte)243, (byte)243, (byte)243);
            }

            int cy = y - 32;
            if (w.Kind == 2)
            {
                // A photo in a viewer: smooth gradients with fine grain.
                uint grain = Hash(x, y, id) & 15;
                return ((byte)Math.Clamp(60 + 120 * x / w.W + (int)grain, 0, 255), (byte)Math.Clamp(90 + 80 * cy / w.H + (int)grain, 0, 255),
                    (byte)Math.Clamp(180 - 100 * x / w.W + 40 * cy / w.H + (int)grain, 0, 255));
            }

            int gutter = dark ? 48 : 24;
            if (dark && x < gutter)
            {
                bool number = cy % 20 is >= 6 and < 15 && x is >= 14 and < 34 && Mark(x, cy, cy / 20);
                return number ? ((byte)130, (byte)130, (byte)130) : ((byte)36, (byte)32, (byte)30);
            }

            int line = cy / 20, inLine = cy % 20;
            int length = (int)(Hash(line, 999, id) % (uint)Math.Max(1, w.W - gutter - 40));
            bool paragraph = Hash(line, 7, id) % 7 == 0;
            int lx = x - gutter;
            if (!paragraph && inLine is >= 6 and < 16 && lx >= 0 && lx < length)
            {
                int segment = lx / 52, within = lx % 52;
                int word = 16 + (int)(Hash(line, segment, id) % 30);
                if (within < word && Mark(lx, cy, line * 131 + segment))
                {
                    if (!dark) return ((byte)32, (byte)32, (byte)32);
                    uint hue = Hash(line, segment, 5) % 4;
                    return hue switch { 0 => ((byte)220, (byte)156, (byte)86), 1 => ((byte)120, (byte)220, (byte)200), 2 => ((byte)156, (byte)206, (byte)214), _ => ((byte)212, (byte)212, (byte)212) };
                }
            }

            return dark ? ((byte)30, (byte)30, (byte)30) : ((byte)255, (byte)255, (byte)255);
        }

        // Roughly 40% of a word's pixels ink: strokes, not noise, so neighbouring rows repeat.
        static bool Mark(int x, int y, int salt) => ((x * 5 + (y % 10) * 3 + salt) % 7) < 3 || Hash(x / 3, y / 4, salt) % 5 == 0;

        static uint Hash(int a, int b, int c)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                return h;
            }
        }
    }

    // ---- Per-capture record ------------------------------------------------------------------

    private sealed record ReceiptOperation(string Name, string Priority, long Start, long End);

    private sealed class ReceiptCapture(int index, string kind, bool cardFirst)
    {
        private readonly ConcurrentDictionary<string, long> _stages = new();
        private readonly Dictionary<DispatcherOperation, (string Name, long Start)> _running = new(ReferenceEqualityComparer.Instance);

        internal int Index { get; } = index;
        internal string Kind { get; } = kind;
        internal bool CardFirst { get; } = cardFirst;
        internal string Variant => CardFirst ? "card first" : "current";
        internal long Start { get; private set; }
        internal List<ReceiptOperation> Operations { get; } = [];
        internal List<string> Notes { get; } = [];
        internal DispatcherOperation? CardOperation { get; set; }

        internal bool IsComplete =>
            _stages.ContainsKey("card-content-rendered") && _stages.ContainsKey("handler-task-done")
            && (_stages.ContainsKey("flight-finished") || _stages.ContainsKey("flight-skipped") || _stages.ContainsKey("lift-none"));

        internal void Begin()
        {
            Start = Stopwatch.GetTimestamp();
            _stages.TryAdd("commit", Start);
        }

        internal void Mark(string stage) => _stages.TryAdd(stage, Stopwatch.GetTimestamp());

        internal double? Ms(string stage) => _stages.TryGetValue(stage, out long at) ? MsAt(at) : null;

        internal double MsAt(long timestamp) => (timestamp - Start) * 1000.0 / Stopwatch.Frequency;

        internal double? Between(string from, string to) =>
            _stages.TryGetValue(from, out long a) && _stages.TryGetValue(to, out long b) ? (b - a) * 1000.0 / Stopwatch.Frequency : null;

        internal (long From, long To)? Window(string from, string to) =>
            _stages.TryGetValue(from, out long a) && _stages.TryGetValue(to, out long b) ? (a, b) : null;

        // UI thread only.
        internal void OperationStarted(DispatcherOperation operation)
        {
            if (ReferenceEquals(operation, CardOperation)) Mark("card-op-start");
            string name;
            try { name = DispatcherOperationTracker.Describe(operation); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { name = "(unnamed: " + ex.GetType().Name + ")"; }
            _running[operation] = (name, Stopwatch.GetTimestamp());
        }

        internal void OperationFinished(DispatcherOperation operation)
        {
            if (!_running.Remove(operation, out (string Name, long Start) run)) return;
            Operations.Add(new ReceiptOperation(run.Name, operation.Priority.ToString(), run.Start, Stopwatch.GetTimestamp()));
        }
    }

    // ---- The App around the capture ----------------------------------------------------------

    /// <summary>
    /// The real App without its shell, rooted in an isolated library, with the real coordinator.
    /// Fields are set as App.InitializeShell sets them; the tray, hotkeys, OCR, pins, gallery, Store
    /// rating and welcome notice stay absent (each is null-checked on this path).
    /// </summary>
    private sealed class ReceiptShell : IDisposable
    {
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly AppResponsivenessProbe _probe;
        private readonly CaptureOverlayCoordinator _coordinator;
        private readonly ReceiptKeyboardRouter _router = new(_ => new NoHotkeys());
        private readonly CaptureQueue _queue;
        private CaptureSelectionCompletedEventArgs? _selection;
        private string? _liftWindow;
        private string? _overlayWindow;

        internal ReceiptShell(string root, AppResponsivenessProbe probe)
        {
            _probe = probe;
            typeof(DispatcherObject).GetField("_dispatcher", Instance)!.SetValue(_app, Dispatcher.CurrentDispatcher);
            AppPaths paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
            settings.General.FirstCaptureTipShown = true;
            settings.General.PlayCaptureSound = false;
            settings.Queue.MaxItems = 1000;
            settings.Queue.MaxBytes = 1L << 40;
            _queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(_queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance)
            {
                BeforeRecordMetadataCommit = _ => probe._receipt?.Mark("original-written"),
            };
            // App.InitializeShell: library refresh and retention follow a save at ApplicationIdle.
            persistence.ImagePersisted += (_, _) =>
            {
                probe._receipt?.Mark("save-published");
                _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(_queue.RequestAutomaticMaintenance));
            };
            var commit = new CaptureCommitService(persistence, () => settings, () => paths, NullLogger<CaptureCommitService>.Instance,
                (bitmap, url) => ClipboardImageService.CopyImageAsync(bitmap, url, _ => probe._receipt?.Mark("clipboard-data-ready")));
            Set("_queue", _queue);
            Set("_settings", settings);
            Set("_persistence", persistence);
            Set("_commit", commit);
            Set("_capturePaths", paths);
            Set("_services", new ServiceCollection().AddLogging().BuildServiceProvider());
            Set("_receiptKeyboard", _router);

            _coordinator = new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance);
            MethodInfo completed = typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Instance)!;
            _coordinator.SelectionPersistRequested = e =>
            {
                // The shutter's Play call is the handler's first statement (off here).
                probe._receipt?.Mark("handler-entered");
                _selection = e;
                var task = (Task)completed.Invoke(_app, [e])!;
                Set("_capturePersistenceTask", task);
                probe._receipt?.Mark("handler-returned");
                ReceiptCapture? capture = probe._receipt;
                _ = task.ContinueWith(_ => capture?.Mark("handler-task-done"), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return task;
            };
            _coordinator.OpenEditorAfterCapture = () => AfterCaptureBehavior.Resolve(settings.General.AfterCapture, Get("_currentRecord") is not null) == AfterCaptureAction.Editor;
            _coordinator.OriginalNeedsRecovery = () => Get("_currentRecord") is null && Get("_pendingRecord") is not null;
            // App.InitializeShell's OverlayClosed: the tray, welcome notice and library recovery parts
            // do nothing without a shell.
            _coordinator.OverlayClosed += (_, _) =>
            {
                (Get("_currentEditSession") as IDisposable)?.Dispose();
                Set("_currentRecord", null);
                Set("_currentEditSession", null);
                Set("_pendingRecord", null);
            };
            Set("_overlay", _coordinator);
        }

        internal DispatcherOperation? LastContextIdle { get; set; }

        internal CaptureOverlayWindow ShowSelector(FrozenFrame frame, ManualResetEventSlim rendered, ReceiptCapture capture)
        {
            var settings = new CaptureSettings();
            var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, settings.ShowMagnifier, settings.ShowSelectionGrid)
            {
                ShowActivated = false,
                SourceWindows = [],
                ShowModeBar = true,
            };
            overlay.IsForeground = () => true;
            overlay.TakeForeground = () => { };
            // Its first-render handler activates it; the probe never takes the foreground.
            overlay.ContentRendered -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), overlay,
                typeof(CaptureOverlayWindow).GetMethod("OnContentRendered", Instance, [typeof(object), typeof(EventArgs)])!);
            overlay.SelectionCompleted += (EventHandler<CaptureSelectionCompletedEventArgs>)Delegate.CreateDelegate(
                typeof(EventHandler<CaptureSelectionCompletedEventArgs>), _coordinator,
                typeof(CaptureOverlayCoordinator).GetMethod("OnOverlaySelectionCompleted", Instance)!);
            overlay.Closed += (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), _coordinator,
                typeof(CaptureOverlayCoordinator).GetMethod("OnOverlayClosed", Instance)!);
            overlay.ContentRendered += (_, _) => rendered.Set();
            overlay.IsVisibleChanged += (_, e) => { if (e.NewValue is false) capture.Mark("overlay-hidden"); };
            overlay.Closed += (_, _) => capture.Mark("overlay-closed");
            ActiveOverlayField.SetValue(_coordinator, overlay);
            _ = _coordinator.ApplyCaptureExclusion(overlay);
            overlay.Show();
            (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(overlay).Handle);
            _overlayWindow = $"selector window {right - left}x{bottom - top} px";
            return overlay;
        }

        /// <summary>The App's DismissQuickAccessForCapture, as each capture command runs it first.</summary>
        internal void DismissQuickAccessForCapture() =>
            typeof(App).GetMethod("DismissQuickAccessForCapture", Instance)!.Invoke(_app, null);

        internal void ShowCardFirst(ReceiptCapture capture, MonitorInfo monitor)
        {
            capture.Mark("card-op-start");
            if (_selection is not { } e || Get("_pendingRecord") is not CaptureRecord record)
            {
                capture.Notes.Add("card-first: no pending record");
                return;
            }

            Set("_latestReceipt", (Guid?)record.Id);
            var region = new RectD(e.Frame.ScreenBounds.Left + e.BitmapRegion.Left, e.Frame.ScreenBounds.Top + e.BitmapRegion.Top,
                e.BitmapRegion.Width, e.BitmapRegion.Height);
            // Not yet copied: the card says so at once (a card-first design would say 복사 중… here).
            typeof(App).GetMethod("ShowQuickAccess", Instance)!.Invoke(_app, [record, e.SelectedBitmap, region, e.Frame.Monitor ?? monitor, false]);
        }

        internal void SuppressScheduledShow() => Set("_latestReceipt", (Guid?)Guid.NewGuid());

        internal void WatchLift(ReceiptCapture capture)
        {
            if (Get("_captureFlight") is not CaptureFlight { WindowForTest: { } window }) return;
            window.ContentRendered += (_, _) => capture.Mark("lift-rendered");
            IntPtr handle = new WindowInteropHelper(window).Handle;
            (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(handle);
            _liftWindow = $"lift window {right - left}x{bottom - top} px (planned {window.Bounds.Width}x{window.Bounds.Height})";
        }

        internal void WatchCard(ReceiptCapture capture)
        {
            if (Get("_quickAccess") is not QuickAccessWindow card) return;
            // After the card's own handlers, which were added in its constructor.
            card.SourceInitialized += (_, _) => capture.Mark("card-hwnd-ready");
            card.Loaded += (_, _) => capture.Mark("card-loaded");
            card.ContentRendered += (_, _) => capture.Mark("card-content-rendered");
        }

        internal string Describe()
        {
            var card = Get("_quickAccess") as QuickAccessWindow;
            string status = card is null ? "no card" : $"card status {card.StatusForTest.Text}, landing {card.IsLanding}";
            return $"{status}; {_overlayWindow}; {_liftWindow ?? "no lift window"}; library {_queue.Count}";
        }

        public void Dispose()
        {
            _coordinator.Dispose();
            _router.Dispose();
            (Get("_services") as IDisposable)?.Dispose();
        }

        private object? Get(string name) => typeof(App).GetField(name, Instance)!.GetValue(_app);

        private void Set(string name, object? value) => typeof(App).GetField(name, Instance)!.SetValue(_app, value);

        internal sealed class NoHotkeys : IHotkeyRegistrar
        {
            public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
            {
                errorCode = 0;
                errorMessage = string.Empty;
                return true;
            }

            public void Unregister(int id)
            {
            }
        }
    }
}
