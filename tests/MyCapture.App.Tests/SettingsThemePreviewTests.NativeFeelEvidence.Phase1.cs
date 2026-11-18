using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string Phase1RenderVariable = "MYCAPTURE_PHASE1_RENDER_EVIDENCE";
    private const string Phase1MotionVariable = "MYCAPTURE_PHASE1_MOTION_EVIDENCE";
    private const string Phase1CardNote = "Synthetic table source and inert actions; real product card. "
        + "The lifetime timer stops in Loaded before its first tick to avoid native cursor polling; timeout interaction is not measured. ";
    private static readonly int[] Phase1FrameTargets = [0, 83, 167, 250, 600];

    [NativeFeelSceneGroup(41)]
    [NativeFeelScene("phase1-selector-table-ko", "selection-screen")]
    [NativeFeelScene("phase1-selector-table-en", "selection-screen")]
    [NativeFeelScene("phase1-card-table-ko", "result-card")]
    [NativeFeelScene("phase1-card-table-en", "result-card")]
    [NativeFeelScene("phase1-card-portrait-ko", "result-card")]
    [NativeFeelScene("phase1-card-portrait-en", "result-card")]
    [NativeFeelScene("phase1-card-wide-ko", "result-card")]
    [NativeFeelScene("phase1-card-wide-en", "result-card")]
    [NativeFeelScene("phase1-card-tall-ko", "result-card")]
    [NativeFeelScene("phase1-card-tall-en", "result-card")]
    [NativeFeelScene("phase1-card-tiny-ko", "result-card")]
    [NativeFeelScene("phase1-card-tiny-en", "result-card")]
    [NativeFeelScene("phase1-card-video-ko", "result-card")]
    [NativeFeelScene("phase1-card-video-en", "result-card")]
    [NativeFeelScene("phase1-card-gif-ko", "result-card")]
    [NativeFeelScene("phase1-card-gif-en", "result-card")]
    [NativeFeelScene("phase1-card-saving-ko", "result-card")]
    [NativeFeelScene("phase1-card-saving-en", "result-card")]
    [NativeFeelScene("phase1-card-failure-ko", "result-card")]
    [NativeFeelScene("phase1-card-failure-en", "result-card")]
    [NativeFeelScene("phase1-card-in-ko", "result-card")]
    [NativeFeelScene("phase1-card-in-en", "result-card")]
    [NativeFeelScene("phase1-pin-table-ko", "image-pin")]
    [NativeFeelScene("phase1-pin-table-en", "image-pin")]
    [NativeFeelScene("phase1-pin-landing-ko", "image-pin")]
    [NativeFeelScene("phase1-pin-landing-en", "image-pin")]
    [NativeFeelScene("phase1-pin-hover-ko", "image-pin")]
    [NativeFeelScene("phase1-pin-hover-en", "image-pin")]
    [NativeFeelScene("phase1-pin-menu-ko", "image-pin")]
    [NativeFeelScene("phase1-pin-menu-en", "image-pin")]
    private static void NativeFeelPhase1(NativeFeelSceneContext context)
    {
        foreach (string language in new[] { "ko", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            BitmapSource table = Phase1Table();
            Phase1DesktopSelector(context.Desktop, table, language);
            using var keyboard = new ReceiptKeyboardRouter(_ => new NativeFeelHotkeyRegistrar());
            foreach ((string variant, BitmapSource source) in Phase1CardSources(table))
            {
                QuickAccessWindow card = Phase1Card(source, variant, context.Desktop.Monitor, keyboard);
                Phase1PauseCardTimer(card);
                try
                {
                    context.Desktop.Place(card);
                    if (variant == "table")
                        Phase1Sequence(context.Desktop, "phase1-card-in-" + language, "phase1-card-motion-" + language,
                            context.Desktop.Monitor.WorkArea, card.Show, Phase1CardNote + "Language=" + language + ". Actual Show trigger.");
                    else card.Show();
                    context.Desktop.Still("phase1-card-" + variant + "-" + language, "phase1-card-" + language, card,
                        Phase1CardNote + Phase1VariantNote(variant));
                }
                finally { card.CloseNow(); }
            }
            Phase1DesktopPin(context.Desktop, table, language);
        }
    }

    private static void Phase1DesktopSelector(NativeFeelDesktopCapture desktop, BitmapSource table, string language)
    {
        // The real WPF selector is the portable, input-free desktop path. The native selector's
        // current startup activates, claims Esc and changes the cursor; its renderer is captured
        // separately below without starting that native input path.
        FrozenFrame frame = Phase1DesktopFrame(table, desktop.Monitor.Bounds, desktop.Monitor.ScaleFactor);
        RectD target = Phase1TableBounds(frame.ScreenBounds, desktop.Scale);
        var view = new CaptureOverlayView(frame, showMagnifier: true)
        {
            DetectWindows = true, WindowCandidates = [new WindowCandidate(new IntPtr(88), target, "Register map", ProcessId: uint.MaxValue)],
            ReadCursor = () => target.Center, PlaceCursor = _ => false, ReadMonitors = () => [desktop.Monitor],
            SimulatedPointerInside = true, ShowModeBar = true, OffersOtherActions = true, TextCaptureAvailable = true,
        };
        var host = Phase1FixtureWindow(view);
        try
        {
            desktop.Place(host);
            host.Left = frame.ScreenBounds.Left / desktop.Scale;
            host.Top = frame.ScreenBounds.Top / desktop.Scale;
            host.Width = frame.ScreenBounds.Width / desktop.Scale;
            host.Height = frame.ScreenBounds.Height / desktop.Scale;
            host.Show();
            view.InitializePointer();
            view.FlushFeedbackForTest();
            desktop.AreaStill("phase1-selector-table-" + language, "phase1-journey-" + language, desktop.Monitor.Bounds,
                "Actual WPF fallback selector over a synthetic desktop and table; injected window candidate, pointer and monitor. "
                + "No native input, real window enumeration or desktop source capture. Native selector rendering is a separate offscreen artifact. Language=" + language + ".");
        }
        finally { view.ReleaseResources(); host.Close(); }
    }

    private static void Phase1DesktopPin(NativeFeelDesktopCapture desktop, BitmapSource table, string language)
    {
        var background = Phase1FixtureWindow(new Image
        {
            Source = Phase1CodeScene(1000, 620, table: null), Stretch = Stretch.Uniform,
        });
        var pin = new PinWindow(PinContent.FromImage(table), new PinViewState(table.PixelWidth, table.PixelHeight, 1, 1, .1),
            0, 0, () => new PinSettings(), new SyntheticPinDragInput());
        Func<Window, bool> hoverExclusion = PinHoverBar.ApplyCaptureExclusion;
        Func<Window, bool> landingExclusion = PinLandingDecoration.ApplyCaptureExclusion;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        PinLandingDecoration.ApplyCaptureExclusion = _ => true;
        try
        {
            desktop.Place(background, 1000, 620);
            background.Show();
            desktop.Place(pin);
            pin.ReadWorkArea = _ => desktop.Monitor.WorkArea;
            double left = desktop.Monitor.WorkArea.Left + Math.Max(32, desktop.Monitor.WorkArea.Width - table.PixelWidth * desktop.Scale - 64 * desktop.Scale);
            double top = desktop.Monitor.WorkArea.Top + 160 * desktop.Scale;
            pin.PlaceAtPhysicalOnShow(new RectD(left, top, table.PixelWidth * desktop.Scale, table.PixelHeight * desktop.Scale));
            Phase1Sequence(desktop, "phase1-pin-landing-" + language, "phase1-pin-motion-" + language,
                desktop.Monitor.WorkArea, () => { pin.Show(); pin.StartLandingEffect(); },
                "Real pin Show and StartLandingEffect; same table as selector/card, synthetic code backdrop. "
                + "Existing drag and exclusion seams; host animation preference preserved. Language=" + language + ".");
            desktop.AreaStill("phase1-pin-table-" + language, "phase1-journey-" + language, desktop.Monitor.WorkArea,
                "Real resting pin beside a synthetic code fixture; no clipboard read or code-editor process.");
            pin.ShowHoverBar();
            desktop.AreaStill("phase1-pin-hover-" + language, "phase1-pin-" + language, desktop.Monitor.WorkArea,
                "Actual external hover bar via its existing ShowHoverBar command; no pointer motion.");
            pin.HideHoverBar();
            ContextMenu menu = pin.ContextMenu!;
            menu.PlacementTarget = pin;
            menu.Placement = PlacementMode.Left;
            try
            {
                menu.IsOpen = true;
                desktop.AreaStill("phase1-pin-menu-" + language, "phase1-pin-" + language, desktop.Monitor.WorkArea,
                    "Actual pin menu; no command invoked.");
            }
            finally { menu.IsOpen = false; }
        }
        finally
        {
            pin.Close(); background.Close();
            PinHoverBar.ApplyCaptureExclusion = hoverExclusion;
            PinLandingDecoration.ApplyCaptureExclusion = landingExclusion;
        }
    }

    private static void Phase1Sequence(NativeFeelDesktopCapture desktop, string id, string group, RectD area, Action change, string note)
    {
        // Reuse the core's exact coverage checks and pixel capture; its shared 500-ms runner stays
        // unchanged. All frames are live samples. Nominal targets are never posed animation states.
        MethodInfo grab = typeof(NativeFeelDesktopCapture).GetMethod("Grab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo save = typeof(NativeFeelDesktopCapture).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var samples = new List<(double Begin, double End, BitmapSource Image, int? Target)>();
        var clock = Stopwatch.StartNew();
        BitmapSource Grab() => (BitmapSource)grab.Invoke(desktop, [area])!;
        double begin = clock.Elapsed.TotalMilliseconds;
        BitmapSource baseline = Grab();
        samples.Add((begin, clock.Elapsed.TotalMilliseconds, baseline, null));
        double trigger = clock.Elapsed.TotalMilliseconds;
        change();
        double triggerEnd = clock.Elapsed.TotalMilliseconds - trigger;
        int[] deadlines = Enumerable.Range(0, 39).Select(index => index * 16).Concat(Phase1FrameTargets).Distinct().Order().ToArray();
        foreach (int deadline in deadlines)
        {
            NativeFeelDesktopCapture.Pump(Math.Max(0, trigger + deadline - clock.Elapsed.TotalMilliseconds));
            begin = clock.Elapsed.TotalMilliseconds;
            BitmapSource bitmap = Grab();
            samples.Add((begin, clock.Elapsed.TotalMilliseconds, bitmap, Phase1FrameTargets.Contains(deadline) ? deadline : null));
        }
        for (int index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            string sampleNote = note + (sample.Target is { } target ? $" Nominal target={target} ms; use measured BeginMs/EndMs." : "");
            save.Invoke(desktop, [id, group, "sequence", sample.Begin - trigger, sample.End - trigger, area, sample.Image, sampleNote, index, triggerEnd]);
        }
        string theme = Phase1DesktopTheme(desktop);
        string output = (string)typeof(NativeFeelDesktopCapture).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(desktop)!;
        TestDiagnosticArtifacts.WriteJson(output, $"{id}-{theme}-{desktop.ScaleTag}-targets.json", new
        {
            id, theme, scale = desktop.Scale, language = UiText.Culture.Name, triggerEndMs = triggerEnd,
            requestedTargetsMs = Phase1FrameTargets, sequenceDurationMs = samples[^1].End - trigger,
            timing = "Live capture at the earliest available composed frame after each deadline; target is nominal, BeginMs/EndMs are measured. Zero follows the Show callback, not an invented first frame.",
            targets = samples.Select((sample, index) => new { sample, index }).Where(item => item.sample.Target.HasValue)
                .Select(item => new { targetMs = item.sample.Target, beginMs = item.sample.Begin - trigger, endMs = item.sample.End - trigger,
                    file = $"{id}-{theme}-{desktop.ScaleTag}-frame{item.index:000}.png" }),
        });
        desktop.Cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, theme, desktop.Scale, "captured", note));
    }

    private static string Phase1DesktopTheme(NativeFeelDesktopCapture desktop) =>
        (string)typeof(NativeFeelDesktopCapture).GetField("_theme", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(desktop)!;

    [Fact]
    public Task NativeFeel_Phase1RenderEvidence() => RunIsolated(nameof(NativeFeel_Phase1RenderEvidence), CapturePhase1RenderEvidence);

    private static void CapturePhase1RenderEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable(Phase1RenderVariable);
        if (string.IsNullOrWhiteSpace(requested)) return;
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Choose a new absolute output directory.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        var application = CreateLibraryApplication();
        var files = new List<object>();
        Exception? failure = null;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in new[] { "ko", "en" })
            foreach (double scale in new[] { 1d, 1.5 })
            {
                ThemeService.Apply(theme);
                using var culture = UiText.UseLanguage(language);
                string suffix = $"{(theme == AppTheme.Glass ? "dark" : "light")}-{language}-scale{scale * 100:0}";
                BitmapSource table = Phase1Table();
                foreach ((string variant, BitmapSource source) in Phase1CardSources(table))
                {
                    var monitor = new MonitorInfo("phase1-fixture", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1040), (uint)(96 * scale), true);
                    using var keyboard = new ReceiptKeyboardRouter(_ => new NativeFeelHotkeyRegistrar());
                    QuickAccessWindow card = Phase1Card(source, variant, monitor, keyboard);
                    try { Save("card-" + variant, Phase1RenderWindowContent(card, scale)); }
                    finally { card.CloseNow(); }
                }
                FrozenFrame frame = Phase1DesktopFrame(table, new RectD(0, 0, 1366, 768), scale);
                RectD target = Phase1TableBounds(frame.ScreenBounds, scale);
                var monitorInfo = new MonitorInfo("phase1-fixture", frame.ScreenBounds, frame.ScreenBounds, (uint)(96 * scale), true);
                using (var owner = new NativeCaptureOverlay(frame.ScreenBounds, false, true)
                {
                    DetectWindows = true, SourceWindows = [new WindowCandidate(new IntPtr(88), target, "Register map", ProcessId: uint.MaxValue)],
                    ReadCursor = () => target.Center, PlaceCursor = _ => false, ReadMonitors = () => [monitorInfo],
                    OffersOtherActions = true, TextCaptureAvailable = true,
                })
                {
                    owner.AttachFrame(frame);
                    using var chrome = new NativeSelectorChrome(owner, true, false);
                    owner.PreparePointerFeedback(); owner.PrepareVisualFeedback(); owner.UpdatePointer(false, false);
                    owner.RefreshHover(); owner.FlushFeedbackForTest();
                    chrome.Update(target.Center, owner.DisplayedRegionForTest); chrome.FlushHoverTabForTest();
                    Save("selector-table", NativeSelectorTestRenderer.Render(owner, chrome));
                }
                var pin = new PinWindow(PinContent.FromImage(table), new PinViewState(table.PixelWidth, table.PixelHeight, 1, 1, .1),
                    0, 0, () => new PinSettings(), new SyntheticPinDragInput());
                try
                {
                    Save("pin-table", Phase1RenderWindowContent(pin, scale));
                    PinHoverBar bar = pin.CreateHoverBarForTest();
                    Save("pin-hover", Phase1RenderWindowContent(bar, scale));
                }
                finally { pin.Close(); }
                void Save(string scene, BitmapSource bitmap)
                {
                    string name = $"{scene}-{suffix}.png";
                    TestDiagnosticArtifacts.SavePng(output, name, bitmap);
                    files.Add(new { scene, language, scale, theme = theme.ToString(), file = name, bitmap.PixelWidth, bitmap.PixelHeight });
                }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(output, "phase1-render-manifest.json", new
            {
                capture = "Offscreen actual product visuals; synthetic source table and monitors. Not desktop, input, motion, or native-DPI verification.",
                files, failure = failure?.ToString(),
                media = "Video and GIF are synthetic poster frames passed to the real result-card surface; no decoder or recording is exercised.",
                retention = "Retained; no fixture deletion.",
            });
            application.Shutdown();
        }
    }

    [Fact]
    public Task NativeFeel_Phase1MotionEvidence() => RunIsolated(nameof(NativeFeel_Phase1MotionEvidence), CapturePhase1MotionEvidence);

    private static void CapturePhase1MotionEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable(Phase1MotionVariable);
        if (string.IsNullOrWhiteSpace(requested)) return;
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Choose a new absolute output directory.");
        string output = TestDiagnosticArtifacts.NewDirectory(requested);
        var application = CreateLibraryApplication();
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();
        MonitorInfo nearest = monitors.OrderBy(item => item.Bounds.Left).First();
        var hiddenArea = new RectD(monitors.Min(item => item.Bounds.Left) - 4096,
            monitors.Min(item => item.Bounds.Top) - 2048, 1920, 1080);
        var monitor = new MonitorInfo("phase1-offscreen-motion", hiddenArea, hiddenArea, nearest.Dpi, true);
        var results = new List<object>();
        var unavailable = new List<string>();
        Exception? failure = null;
        Func<Window, bool> exclusion = PinLandingDecoration.ApplyCaptureExclusion;
        PinLandingDecoration.ApplyCaptureExclusion = _ => true;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in new[] { "ko", "en" })
            {
                ThemeService.Apply(theme);
                using var culture = UiText.UseLanguage(language);
                string suffix = $"{(theme == AppTheme.Glass ? "dark" : "light")}-{language}";
                BitmapSource table = Phase1Table();
                using var keyboard = new ReceiptKeyboardRouter(_ => new NativeFeelHotkeyRegistrar());
                var card = new QuickAccessWindow(table, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                    () => Task.FromResult(true), () => { }, () => Task.FromResult("synthetic.png"), excludeFromCapture: false,
                    targetMonitor: monitor, copiedToClipboard: false, keyboardRouter: keyboard)
                {
                    MonitorOfWindow = _ => monitor, ShowActivated = false, ShowInTaskbar = false,
                    Left = hiddenArea.Left / monitor.ScaleFactor, Top = hiddenArea.Top / monitor.ScaleFactor,
                    WindowStartupLocation = WindowStartupLocation.Manual, IsKept = true,
                };
                Phase1PauseCardTimer(card);
                try
                {
                    card.SourceInitialized += (_, _) => Phase1AssertOffscreen(card, monitors);
                    _ = new WindowInteropHelper(card).EnsureHandle();
                    Capture(card, null, "card-in-" + suffix, card.Show);
                }
                finally { card.CloseNow(); }
                var pin = new PinWindow(PinContent.FromImage(table), new PinViewState(table.PixelWidth, table.PixelHeight, 1, 1, .1),
                    hiddenArea.Left / monitor.ScaleFactor, hiddenArea.Top / monitor.ScaleFactor,
                    () => new PinSettings(), new SyntheticPinDragInput())
                {
                    ShowActivated = false, ShowInTaskbar = false, ReadWorkArea = _ => hiddenArea,
                };
                pin.PlaceAtPhysicalOnShow(new RectD(hiddenArea.Left + 96, hiddenArea.Top + 96,
                    table.PixelWidth * monitor.ScaleFactor, table.PixelHeight * monitor.ScaleFactor));
                try
                {
                    pin.SourceInitialized += (_, _) => Phase1AssertOffscreen(pin, monitors);
                    _ = new WindowInteropHelper(pin).EnsureHandle();
                    Capture(pin, pin, "pin-landing-" + suffix, () => { pin.Show(); pin.StartLandingEffect(); });
                }
                finally { pin.Close(); }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            PinLandingDecoration.ApplyCaptureExclusion = exclusion;
            TestDiagnosticArtifacts.WriteJson(output, "phase1-motion-manifest.json", new
            {
                capture = "Live elapsed WPF animations in offscreen test-owned windows, rendered in place with RenderTargetBitmap. No desktop pixels or input; not a DWM presentation or native display-scale measurement.",
                clock = "Product clocks and host reduced-motion preference unchanged. No Advance, Render(time), animation seek, opacity override, visual detachment or forced layout.",
                card = "IsKept=true prevents native pointer polling and automatic expiry; the kept receipt is identified separately from the default-card stills.",
                motionEnabled = FluidMotion.AnimationsEnabled, systemAnimation = SystemParameters.ClientAreaAnimation,
                requestedTargetsMs = Phase1FrameTargets, results, unavailable, failure = failure?.ToString(),
            });
            application.Shutdown();
        }
        Assert.True(unavailable.Count == 0, string.Join(Environment.NewLine, unavailable));

        void Capture(Window window, PinWindow? pin, string id, Action show)
        {
            var frames = new List<(BitmapSource Image, double Begin, double End, RectD Bounds,
                double Scale, double Opacity, string Transform, double? PinOpacity, double? Accent, object? Decoration)>();
            var renderingTimes = new List<double>();
            var clock = new Stopwatch();
            TimeSpan? lastRenderingTime = null;
            var dispatcherFrame = new DispatcherFrame();
            var stop = new DispatcherTimer(DispatcherPriority.Send);
            stop.Tick += (_, _) => dispatcherFrame.Continue = false;
            double nextSample = 0;
            bool sampling = false;
            Exception? captureError = null;
            double showReturned = 0;
            RectD area = RectD.Empty;
            EventHandler rendered = (_, args) =>
            {
                TimeSpan time = ((RenderingEventArgs)args).RenderingTime;
                if (time == lastRenderingTime) return;
                lastRenderingTime = time;
                renderingTimes.Add(clock.Elapsed.TotalMilliseconds);
                if (sampling || clock.Elapsed.TotalMilliseconds < nextSample) return;
                try { Sample(); }
                catch (Exception error) { captureError = error; dispatcherFrame.Continue = false; }
            };
            try
            {
                clock.Start();
                show();
                showReturned = clock.Elapsed.TotalMilliseconds;
                Phase1AssertOffscreen(window, monitors);
                area = Phase1WindowBounds(window).Inflate(80 * VisualTreeHelper.GetDpi(window).DpiScaleX).ToPixelBounds();
                Sample();
                // Attach after Show/StartLandingEffect so the product's rendering handlers run
                // before this observer. One dispatcher frame avoids starving a fresh background
                // timer on every sample. A missed frame stays missed; no catch-up loop invents it.
                CompositionTarget.Rendering += rendered;
                stop.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 650 - clock.Elapsed.TotalMilliseconds));
                stop.Start();
                Dispatcher.PushFrame(dispatcherFrame);
                if (captureError is not null) throw new InvalidOperationException("Live offscreen frame capture failed.", captureError);
            }
            finally { stop.Stop(); CompositionTarget.Rendering -= rendered; }
            void Sample()
            {
                sampling = true;
                try
                {
                    double begin = clock.Elapsed.TotalMilliseconds;
                    nextSample = begin + 16;
                    Phase1AssertOffscreen(window, monitors);
                    PinLandingDecoration? decoration = pin?.LandingDecoration is { IsClosed: false } active ? active : null;
                    if (decoration is not null) Phase1AssertOffscreen(decoration, monitors);
                    BitmapSource bitmap = Phase1RenderLiveWindows(window, decoration, area);
                    var surface = (FrameworkElement)window.Content;
                    double? pinOpacity = pin is null ? null : ((UIElement)typeof(PinWindow)
                        .GetField("_chrome", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!).Opacity;
                    frames.Add((bitmap, begin, clock.Elapsed.TotalMilliseconds, Phase1WindowBounds(window),
                        VisualTreeHelper.GetDpi(window).DpiScaleX, surface.Opacity, surface.RenderTransform.Value.ToString(CultureInfo.InvariantCulture),
                        pinOpacity, pin?.LandingAccentAmount, decoration?.State));
                }
                finally { sampling = false; }
            }
            var frameRecords = new List<object>();
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                string file = $"{id}-frame{index:000}.png";
                TestDiagnosticArtifacts.SavePng(output, file, frame.Image);
                var pixels = new byte[frame.Image.PixelWidth * frame.Image.PixelHeight * 4];
                frame.Image.CopyPixels(pixels, frame.Image.PixelWidth * 4, 0);
                string hash = Convert.ToHexString(SHA256.HashData(pixels));
                hashes.Add(hash);
                frameRecords.Add(new { file, beginMs = frame.Begin, endMs = frame.End,
                    boundsPhysical = frame.Bounds, observedWindowScale = frame.Scale, surfaceOpacity = frame.Opacity,
                    surfaceTransform = frame.Transform, pinChromeOpacity = frame.PinOpacity, landingAccent = frame.Accent,
                    decoration = frame.Decoration, pixelHash = hash });
            }
            bool activeFrames = pin is null ? frames.Any(frame => frame.Opacity < .999)
                : frames.Any(frame => frame.PinOpacity < .999 || frame.Decoration is not null || frame.Accent > 0);
            bool observedMotion = renderingTimes.Count >= 2 && hashes.Count > 1 && activeFrames;
            bool decorationObserved = frames.Any(frame => frame.Decoration is not null);
            bool openingSampled = Phase1FrameTargets.Zip(Phase1FrameTargets.Skip(1), (start, end) => (start, end))
                .All(range => frames.Any(frame => frame.Begin >= range.start && frame.Begin < range.end))
                && frames.Any(frame => frame.Begin >= Phase1FrameTargets[^1]);
            string status = !FluidMotion.AnimationsEnabled ? "reduced-motion-observed"
                : observedMotion && openingSampled ? "live-motion-observed"
                : observedMotion ? "live-motion-incomplete-coverage" : "motion-not-observed";
            if (FluidMotion.AnimationsEnabled && !observedMotion)
                unavailable.Add(id + ": the offscreen compositor did not supply changing live motion frames; do not use this sequence to judge animation strength.");
            if (pin is not null && FluidMotion.AnimationsEnabled && !decorationObserved)
                unavailable.Add(id + ": the host did not present the optional landing decoration; these frames cannot assess its strength or duration.");
            if (FluidMotion.AnimationsEnabled && !openingSampled)
                unavailable.Add(id + ": at least one requested opening interval has no actual sample; target coverage remains incomplete.");
            object[] targets = Phase1FrameTargets.Select(target =>
            {
                int index = Enumerable.Range(0, frames.Count).MinBy(index => Math.Abs(frames[index].Begin - target));
                var nearestFrame = frames[index];
                return (object)new { targetMs = target, beginMs = nearestFrame.Begin, endMs = nearestFrame.End,
                    offsetMs = nearestFrame.Begin - target, file = $"{id}-frame{index:000}.png" };
            }).ToArray();
            var result = new { id, status, showReturnedMs = showReturned, areaPhysical = area,
                compositionRenderingCallbacksMs = renderingTimes, distinctPixelFrames = hashes.Count, decorationObserved, openingSampled,
                targets, frames = frameRecords };
            TestDiagnosticArtifacts.WriteJson(output, id + ".json", result);
            results.Add(new { id, status, showReturnedMs = showReturned, distinctPixelFrames = hashes.Count,
                compositionCallbacks = renderingTimes.Count, decorationObserved, openingSampled, manifest = id + ".json" });
        }
    }

    private static RectD Phase1WindowBounds(Window window)
    {
        (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(window).Handle);
        return new RectD(left, top, right - left, bottom - top);
    }

    private static void Phase1AssertOffscreen(Window window, IReadOnlyList<MonitorInfo> monitors)
    {
        RectD bounds = Phase1WindowBounds(window);
        Assert.False(bounds.IsEmpty, "The test-owned window has no observable native bounds.");
        Assert.All(monitors, monitor => Assert.False(NativeFeelDesktopCapture.Intersects(bounds, monitor.Bounds),
            "The motion fixture must remain wholly outside every real monitor."));
    }

    private static BitmapSource Phase1RenderLiveWindows(Window window, Window? decoration, RectD area)
    {
        var visual = new DrawingVisual();
        using (DrawingContext draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.Gray, null, new Rect(0, 0, area.Width, area.Height));
            Draw(window);
            if (decoration is { IsVisible: true }) Draw(decoration);
            void Draw(Window layer)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(layer);
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(layer.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(layer.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(layer);
                RectD bounds = Phase1WindowBounds(layer);
                draw.DrawImage(bitmap, new Rect(bounds.Left - area.Left, bounds.Top - area.Top, bitmap.PixelWidth, bitmap.PixelHeight));
            }
        }
        return Phase1Bitmap(visual, (int)area.Width, (int)area.Height);
    }

    private static QuickAccessWindow Phase1Card(BitmapSource source, string variant, MonitorInfo monitor, ReceiptKeyboardRouter keyboard)
    {
        if (variant is "saving" or "failure")
        {
            QuickAccessWindow pending = QuickAccessWindow.ForPendingCapture(source, copiedToClipboard: true,
                targetMonitor: monitor, keyboardRouter: keyboard, excludeFromCapture: false);
            if (variant == "failure") pending.ShowCaptureFailure(UiText.Get("Text_AE94EC51B2C2"),
                UiText.Culture.TwoLetterISOLanguageName == "ko" ? "파일을 저장하지 못했습니다." : "The file could not be saved.");
            return pending;
        }
        return new QuickAccessWindow(source, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("synthetic.png"), excludeFromCapture: false,
            targetMonitor: monitor, copiedToClipboard: false, keyboardRouter: keyboard);
    }

    private static void Phase1PauseCardTimer(QuickAccessWindow card)
    {
        var timer = (DispatcherTimer)typeof(QuickAccessWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
        card.Loaded += (_, _) => { timer.Stop(); Assert.False(timer.IsEnabled); };
    }

    private static IEnumerable<(string Variant, BitmapSource Source)> Phase1CardSources(BitmapSource table)
    {
        yield return ("table", table);
        yield return ("portrait", Phase1Table(280, 480));
        yield return ("wide", Phase1Table(2400, 120));
        yield return ("tall", Phase1Table(120, 2400));
        yield return ("tiny", Phase1Table(24, 16));
        yield return ("video", Phase1Table(640, 360, "00:06 / 00:12"));
        yield return ("gif", Phase1Table(360, 360, "GIF  12 fps"));
        yield return ("saving", table);
        yield return ("failure", table);
    }

    private static string Phase1VariantNote(string variant) => variant switch
    {
        "video" or "gif" => "Synthetic decoded poster frame through the same card surface; no media decode/export or playback claim.",
        "saving" => "Actual pending-capture save-ready gating; no save task or file write.",
        "failure" => "Actual ShowCaptureFailure path with a synthetic failure; no save task or file write.",
        _ => "Original aspect and orientation retained; source variant=" + variant + ".",
    };

    private static BitmapSource Phase1RenderWindowContent(Window window, double scale)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Child = content, Resources = window.Resources, UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetFontSize(host, window.FontSize);
        TextElement.SetForeground(host, window.Foreground);
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
        double width = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight
            ? double.PositiveInfinity : double.IsFinite(window.Width) ? window.Width : 600;
        double height = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight
            ? double.PositiveInfinity : double.IsFinite(window.Height) ? window.Height : double.PositiveInfinity;
        host.Measure(new Size(width, height));
        var size = new Size(double.IsFinite(width) ? width : host.DesiredSize.Width,
            double.IsFinite(height) ? height : host.DesiredSize.Height);
        host.Arrange(new Rect(size)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host); bitmap.Freeze();
        host.Child = null; window.Content = content;
        return bitmap;
    }

    private static Window Phase1FixtureWindow(UIElement content)
    {
        var host = new Window { Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Background = Brushes.Gray, Content = content };
        ModernWindowChrome.SetEnabled(host, false);
        FluidMotion.SetWindowEntrance(host, false);
        return host;
    }

    private static RectD Phase1TableBounds(RectD bounds, double scale)
    {
        double width = Math.Min(480 * scale, bounds.Width * .48), height = width * 280 / 480;
        return new RectD(bounds.Left + bounds.Width - width - 28 * scale, bounds.Top + 170 * scale, width, height).ToPixelBounds();
    }

    private static FrozenFrame Phase1DesktopFrame(BitmapSource table, RectD bounds, double scale)
    {
        RectD target = Phase1TableBounds(bounds, scale);
        BitmapSource source = Phase1CodeScene((int)bounds.Width, (int)bounds.Height, table,
            new Rect(target.Left - bounds.Left, target.Top - bounds.Top, target.Width, target.Height));
        return new FrozenFrame(source, bounds, null, 0);
    }

    private static BitmapSource Phase1CodeScene(int width, int height, BitmapSource? table, Rect? tableBounds = null)
    {
        var visual = new DrawingVisual();
        using (DrawingContext draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, new Rect(0, 0, width, height));
            Phase1Text(draw, "registers.cs", 32, 32, Brushes.White, 18);
            string[] lines = ["public async Task ConfigureAsync()", "{", "    var port = OpenDevice();", "    await port.WriteAsync(0x10, 0x01);", "    await port.WriteAsync(0x18, 0x80);", "", "    // Keep the register map nearby.", "    var status = await port.ReadAsync(0x20);", "    if ((status & 0x01) != 0)", "        await BeginCaptureAsync();", "}"];
            for (int index = 0; index < lines.Length; index++)
                Phase1Text(draw, lines[index], 32, 110 + index * 28, new SolidColorBrush(Color.FromRgb(212, 212, 212)), 15);
            if (table is not null) draw.DrawImage(table, tableBounds ?? new Rect(width - 510, 170, 480, 280));
        }
        return Phase1Bitmap(visual, width, height);
    }

    private static BitmapSource Phase1Table(int width = 480, int height = 280, string? footer = null)
    {
        var visual = new DrawingVisual();
        using (DrawingContext draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (width < 80 || height < 48)
            {
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 100, 190)), null, new Rect(0, 0, width / 2d, height));
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 175, 60)), null, new Rect(width / 2d, 0, width / 2d, height));
            }
            else
            {
                Phase1Text(draw, "REGISTER MAP", 12, 10, Brushes.Black, 16);
                int rowCount = (height - 48) / 30;
                var line = new Pen(new SolidColorBrush(Color.FromRgb(190, 198, 207)), 1);
                for (int row = 0; row < rowCount; row++)
                {
                    double y = 42 + row * 30;
                    draw.DrawRectangle(row % 2 == 0 ? new SolidColorBrush(Color.FromRgb(239, 243, 248)) : Brushes.White,
                        null, new Rect(8, y, width - 16, Math.Min(30, height - y)));
                    draw.DrawLine(line, new Point(8, y), new Point(width - 8, y));
                    Phase1Text(draw, $"0x{16 + row * 8:X2}", 14, y + 5, Brushes.Black, 13);
                    if (width > 180) Phase1Text(draw, row switch { 0 => "CONTROL", 1 => "CONFIG", 2 => "STATUS", _ => "DATA_" + row }, 102, y + 5, Brushes.Black, 13);
                    if (width > 350) Phase1Text(draw, row == 2 ? "0x01  READY" : "0x00  R/W", 310, y + 5, Brushes.Black, 13);
                }
                if (footer is not null)
                {
                    draw.DrawRectangle(Brushes.Black, null, new Rect(0, height - 32, width, 32));
                    Phase1Text(draw, footer, 12, height - 26, Brushes.White, 13);
                }
            }
        }
        return Phase1Bitmap(visual, width, height);
    }

    private static void Phase1Text(DrawingContext draw, string value, double left, double top, Brush brush, double size) =>
        draw.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush, 1), new Point(left, top));

    private static BitmapSource Phase1Bitmap(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
}
