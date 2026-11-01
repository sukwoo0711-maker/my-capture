using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Editing;

/// <summary>
/// Resizable, taskbar-visible editor host used after capture selection and for gallery re-edit.
/// </summary>
internal partial class AnnotationEditorWindow : Window
{
    private readonly AnnotationEditorControl _editor;
    private bool _committed;

    internal AnnotationEditorWindow(
        FrozenFrame sourceFrame,
        RectD sourceRegion,
        BitmapSource selectedBitmap,
        string? title = null,
        AnnotationDocument? initialDocument = null,
        IReadOnlyDictionary<string, BitmapSource>? initialAssets = null,
        IPrivacyRedactionService? privacyRedactionService = null,
        string? sourceWindowTitle = null,
        MonitorInfo? pointerMonitor = null,
        MyCapture.Core.Capture.CaptureProvenance? provenance = null,
        Task<MyCapture.Core.Capture.CaptureProvenance>? pendingProvenance = null)
        : this(false, sourceFrame, sourceRegion, selectedBitmap, title, initialDocument, initialAssets,
            privacyRedactionService, sourceWindowTitle, pointerMonitor, provenance, pendingProvenance)
    {
    }

    private AnnotationEditorWindow(bool prewarm, FrozenFrame sourceFrame, RectD sourceRegion,
        BitmapSource selectedBitmap, string? title, AnnotationDocument? initialDocument,
        IReadOnlyDictionary<string, BitmapSource>? initialAssets, IPrivacyRedactionService? privacyRedactionService,
        string? sourceWindowTitle, MonitorInfo? pointerMonitor, MyCapture.Core.Capture.CaptureProvenance? provenance,
        Task<MyCapture.Core.Capture.CaptureProvenance>? pendingProvenance)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(selectedBitmap);

        StandardWindowTheme.Apply(this);

        Title = title ?? UiText.Get("Text_199EB510DA51");
        Background = Application.Current?.TryFindResource("Surface.Base") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17));
        Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        FontFamily = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _imagePixels = new Size(selectedBitmap.PixelWidth, selectedBitmap.PixelHeight);
        // CenterScreen opens an ownerless window on the pointer's monitor, so it is sized for that
        // monitor, not the primary one. A capture moves it to its own monitor (PlaceOnMonitor).
        // Tests pass the pointer's monitor so the size does not depend on where the pointer is.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        pointerMonitor ??= MonitorEnumerator.GetFromCursor();
        _targetMonitor = pointerMonitor;
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(pointerMonitor.WorkArea,
            pointerMonitor.ScaleFactor, new Size(980, 620), MinimumSize, WorkAreaMargin);
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Size.Width;
        Height = fit.Size.Height;

        AutomationProperties.SetName(this, UiText.Get("Text_53DCBAF22F42"));
        // The window itself never takes text; while it briefly holds focus a Hangul-mode IME
        // must not start a hidden composition either (the editor's text boxes opt back in).
        InputMethod.SetIsInputMethodEnabled(this, false);

        _editor = new AnnotationEditorControl(
            sourceFrame,
            sourceRegion,
            selectedBitmap,
            initialDocument,
            initialAssets,
            privacyRedactionService,
            sourceWindowTitle,
            provenance,
            pendingProvenance,
            initializeHost: editor => Content = editor,
            prewarm: prewarm);
        _editor.EditingCompleted += OnEditingCompleted;
        _editor.CloseRequested += OnCloseRequested;
        ConfirmClose = () => EditorCloseDialog.Ask(this);
        ConstructorSizingMeasurement = SizeForMonitor(pointerMonitor, center: false, measureIfMissing: prewarm);

        // Startup warms the same sizing path without creating an HWND, registering an open
        // capture, or starting a draft. Its owner closes this unshown host after drawing.
        if (prewarm) return;

        ContentRendered += OnContentRendered;
        InitializeDraftRecovery();
        MyCapture.App.Capture.CaptureEditorRegistry.Track(this, static editor => new MyCapture.App.Capture.CaptureGuardState
        {
            EditorOpen = true,
            EditorAsking = editor.IsAskingToClose,
            EditorSaving = editor.IsSavingToClose || editor.Editor.IsCommitInProgress,
            EditorBusy = editor.IsCloseInProgress || editor.Editor.IsEditingOperationInProgress || editor.HasOpenDialog,
        });
    }

    private readonly Size _imagePixels;
    private MonitorInfo _targetMonitor;
    private bool _placeOnTargetMonitor;
    private Size _lastAutomaticSize;
    private Size _lastDesiredSize;
    private double _lastWorkAreaMargin;
    private SizingEnvironment? _lastSizingEnvironment;
    private FrameworkElement? _lastMeasuredContent;
    private (SizingEnvironment Environment, ChromeStamp Stamp)? _lastMeasuredInputs;
    private Size? _lastClientChrome;
    private FrameworkElement? _lastSizedContent;
    private Size? _lastSizedClientChrome;

    internal readonly record struct SizingMeasurement(int Passes, TimeSpan Elapsed, bool Skipped);
    internal SizingMeasurement ConstructorSizingMeasurement { get; private set; }
    internal SizingMeasurement SourceSizingMeasurement { get; private set; }

    private readonly record struct SizingEnvironment(Size NativeChrome, DpiScale Dpi,
        FrameworkElement Content, ResourceDictionary WindowResources, ResourceDictionary ContentResources,
        ResourceDictionary? ApplicationResources, Style? WindowStyle, Style? ContentStyle,
        FontFamily FontFamily, double FontSize, FontWeight FontWeight, FontStyle FontStyle,
        FontStretch FontStretch, string Language, FlowDirection FlowDirection);

    // Client chrome (client area minus drawing surface) does not include the native frame, so
    // the same entry serves the estimate before the HWND exists and its actual frame afterwards.
    // The inspector wraps by width and caps its rows by height: the client size is in the key.
    // Window and content dictionaries are per window and normally empty; only filled ones count.
    private readonly record struct ChromeEnvironment(DpiScale Dpi, Type WindowType, Type ContentType,
        bool CaptureSource, ResourceDictionary? WindowResources, ResourceDictionary? ContentResources,
        Style? WindowStyle, Style? ContentStyle, FontFamily FontFamily, double FontSize, FontWeight FontWeight,
        FontStyle FontStyle, FontStretch FontStretch, TextFormattingMode TextFormatting, string Language,
        FlowDirection FlowDirection);

    // Theme and culture changes clear every entry; DPI and content type are part of each key.
    // Keys hold dispatcher-owned styles, so each UI thread keeps its own few entries.
    private readonly record struct ChromeStamp(ResourceDictionary? ApplicationResources, int ThemeVersion, string Culture);
    private static readonly ConditionalWeakTable<Dispatcher, ChromeCache> ChromeCaches = new();
    // Host banners can differ in height even when their root has the same type. Keep their
    // measurements with that root, without retaining closed windows in the shared cache.
    private static readonly ConditionalWeakTable<FrameworkElement, ChromeCache> HostedChromeCaches = new();
    private sealed class ChromeCache
    {
        private const int Capacity = 64;
        private readonly List<(Size Client, ChromeEnvironment Environment, Size Chrome)> _entries = [];
        private ChromeStamp _stamp;

        internal void Invalidate() => _entries.Clear();

        internal void Validate(ChromeStamp stamp)
        {
            if (stamp == _stamp) return;
            _entries.Clear();
            _stamp = stamp;
        }

        // Client sizes come from whole device pixels; drop floating error from their DIP arithmetic.
        internal static Size Normalize(Size client) => new(Math.Round(client.Width, 4), Math.Round(client.Height, 4));

        internal bool TryGet(Size client, ChromeEnvironment environment, out Size chrome)
        {
            client = Normalize(client);
            int index = _entries.FindIndex(entry => entry.Client == client && entry.Environment == environment);
            chrome = index < 0 ? default : _entries[index].Chrome;
            if (index < 0) return false;
            // Least recently used entries leave first; a converged size outlives its candidates.
            var used = _entries[index];
            _entries.RemoveAt(index);
            _entries.Add(used);
            return true;
        }

        /// <summary>Entries for <paramref name="environment"/>, newest first.</summary>
        internal IEnumerable<(Size Client, Size Chrome)> Entries(ChromeEnvironment environment)
        {
            for (int index = _entries.Count - 1; index >= 0; index--)
                if (_entries[index].Environment == environment) yield return (_entries[index].Client, _entries[index].Chrome);
        }

        internal void Add(Size client, ChromeEnvironment environment, Size chrome)
        {
            client = Normalize(client);
            _entries.RemoveAll(entry => entry.Client == client && entry.Environment == environment);
            if (_entries.Count >= Capacity) _entries.RemoveAt(0);
            _entries.Add((client, environment, chrome));
        }
    }

    [ThreadStatic]
    private static DpiScale? s_sizingDpiOverrideForTest;

    /// <summary>Simulates the content DPI that sizing reads, on the current test thread only.</summary>
    internal static DpiScale? SizingDpiOverrideForTest
    {
        get => s_sizingDpiOverrideForTest;
        set => s_sizingDpiOverrideForTest = value;
    }

    /// <summary>Smallest editor that still shows its tool rows and a usable canvas (DIPs).</summary>
    internal static readonly Size MinimumSize = new(680, 460);

    /// <summary>Space kept free around the editor across each dimension of the work area (DIPs).</summary>
    internal const double WorkAreaMargin = 64;

    /// <summary>
    /// Opens the editor centred in <paramref name="monitor"/>'s work area, sized for that monitor at its
    /// DPI, landscape or portrait. Call before <see cref="Window.Show"/>.
    /// </summary>
    internal void PlaceOnMonitor(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _targetMonitor = monitor;
        _placeOnTargetMonitor = true;
        SizeForMonitor(monitor, center: true);
    }

    internal static AnnotationEditorWindow CreatePrewarmHost(FrozenFrame frame, RectD region,
        BitmapSource bitmap, IReadOnlyList<MonitorInfo> monitors, MyCapture.Core.Capture.CaptureProvenance? provenance)
    {
        MonitorInfo primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var window = new AnnotationEditorWindow(true, frame, region, bitmap, null, null, null,
            null, null, primary, provenance, null);
        try
        {
            window.PrewarmMonitorSizes(monitors);
            return window;
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    internal void PrewarmCaptureSource(IReadOnlyList<MonitorInfo> monitors,
        MyCapture.Core.Capture.CaptureProvenance provenance)
    {
        _editor.SetPrewarmCaptureSource(provenance);
        // This intentional synthetic variant has its own chrome key. Do not treat its
        // dirty status bar as an edit that invalidates the already measured source variant.
        _lastMeasuredContent = null;
        _lastSizedContent = null;
        _lastClientChrome = null;
        PrewarmMonitorSizes(monitors);
    }

    private void PrewarmMonitorSizes(IReadOnlyList<MonitorInfo> monitors)
    {
        // Keep exact width/height keys: wrapped rows and the height cap prevent safely
        // interpolating chrome between arbitrary widths. Primary seeds go last so they
        // survive the bounded cache even on a machine with many displays.
        foreach (MonitorInfo monitor in monitors.OrderBy(monitor => monitor.IsPrimary))
        {
            _targetMonitor = monitor;
            double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
            VisualTreeHelper.SetRootDpi(this, new DpiScale(scale, scale));
            foreach (Size image in new[] { new Size(64, 36), new Size(640, 360), new Size(900, 520),
                new Size(1920, 1080), new Size(monitor.Bounds.Width, monitor.Bounds.Height) })
                SizeForMonitor(monitor, center: false, imagePixels: image);
        }
    }

    private SizingMeasurement SizeForMonitor(MonitorInfo monitor, bool center, bool allowReuse = false, Size? imagePixels = null,
        bool measureIfMissing = true)
    {
        long started = Stopwatch.GetTimestamp();
        double scale = double.IsFinite(monitor.ScaleFactor) && monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1;
        Size nativeChrome = MeasureNativeChrome();
        FrameworkElement content = Content as FrameworkElement ?? _editor;
        DpiScale dpi = SizingDpiOverrideForTest ?? VisualTreeHelper.GetDpi(content);
        var environment = new SizingEnvironment(nativeChrome, dpi, content,
            Resources, content.Resources, Application.Current?.Resources, Style, content.Style,
            FontFamily, FontSize, FontWeight, FontStyle, FontStretch, Language.IetfLanguageTag, FlowDirection);
        bool layoutValid = content.IsMeasureValid && content.IsArrangeValid;
        bool chromeChanged = false;
        if (layoutValid && ReferenceEquals(content, _lastSizedContent) && _lastSizedClientChrome is { } lastChrome)
        {
            // A caller can change a banner and finish layout before Show. WPF's valid flags
            // then describe that new layout, not the chrome used for our previous size.
            Size client = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(content).Size;
            Size surface = _editor.DisplaySurfaceSize;
            var currentChrome = new Size(Math.Max(0, client.Width - surface.Width), Math.Max(0, client.Height - surface.Height));
            chromeChanged = ChromeCache.Normalize(currentChrome) != ChromeCache.Normalize(lastChrome);
        }
        // WPF invalidates measurement when a size-affecting resource or descendant changes.
        // Keep the identity check as well: the gallery can replace Content after construction,
        // and a caller can replace a resource dictionary before the first HWND is created.
        if (allowReuse && environment == _lastSizingEnvironment && layoutValid && !chromeChanged)
        {
            if (center)
                MonitorWindowPlacement.CenterOn(this, monitor, _lastDesiredSize, MinimumSize, _lastWorkAreaMargin);
            _lastAutomaticSize = new Size(Width, Height);
            return new SizingMeasurement(0, Stopwatch.GetElapsedTime(started), true);
        }
        ChromeCache cache = ReferenceEquals(content, _editor)
            ? ChromeCaches.GetValue(Dispatcher, static _ => new ChromeCache())
            : HostedChromeCaches.GetValue(content, static _ => new ChromeCache());
        var stamp = new ChromeStamp(Application.Current?.Resources, ThemeService.ResourceVersion, UiText.Culture.Name);
        cache.Validate(stamp);
        var chromeEnvironment = new ChromeEnvironment(dpi, GetType(), content.GetType(), _editor.ShowsCaptureSource,
            Filled(Resources), Filled(content.Resources), Style, content.Style, FontFamily, FontSize, FontWeight,
            FontStyle, FontStretch, TextOptions.GetTextFormattingMode(this), Language.IetfLanguageTag, FlowDirection);
        // Content this window laid out and that WPF has since invalidated changed in a way the
        // key may not describe (a host edited it before Show): measure it instead of reusing.
        bool reuse = !chromeChanged && (!ReferenceEquals(content, _lastMeasuredContent) || layoutValid);
        // A changed host invalidates its other widths and DPIs too. Otherwise measuring the
        // new banner at one size could leave a self-consistent but stale entry at another.
        if (!reuse) cache.Invalidate();
        Size pixels = imagePixels ?? _imagePixels;
        Size image = new(pixels.Width / scale, pixels.Height / scale);
        // One device pixel of slack absorbs layout rounding at fractional monitor scales.
        double slack = 1 / scale;
        Size DesiredFor(Size clientChrome, double extra) => new(
            Math.Max(980, image.Width + 2 * AnnotationEditorSurface.CanvasInset + clientChrome.Width + nativeChrome.Width + extra),
            Math.Max(620, image.Height + 2 * AnnotationEditorSurface.CanvasInset + clientChrome.Height + nativeChrome.Height + extra));
        MonitorWindowPlacement.WindowFit FitFor(Size wanted, out double wantedMargin)
        {
            wantedMargin = MarginForDesiredSize(monitor, wanted, scale);
            return MonitorWindowPlacement.Fit(monitor.WorkArea, scale, wanted, MinimumSize, wantedMargin);
        }
        Size ClientOf(MonitorWindowPlacement.WindowFit candidate) => new(Math.Max(1, candidate.Size.Width - nativeChrome.Width),
            Math.Max(1, candidate.Size.Height - nativeChrome.Height));

        // Prefer cached chrome that reproduces its own client size for this capture: an identical
        // open, or SourceInitialized when only the native frame differs from the estimate (the
        // client size is kept and the outer size takes the actual frame). Otherwise start from
        // the newest chrome for these inputs, or this window's own, and converge as before.
        Size? estimate = null;
        if (reuse)
        {
            foreach ((Size client, Size chrome) in cache.Entries(chromeEnvironment))
            {
                estimate ??= chrome;
                if (ChromeCache.Normalize(ClientOf(FitFor(DesiredFor(chrome, slack), out _))) != client) continue;
                estimate = chrome;
                break;
            }
        }
        estimate ??= _lastClientChrome;
        Size desired = estimate is { } known ? DesiredFor(known, slack) : DesiredFor(default, 0);
        MonitorWindowPlacement.WindowFit fit = FitFor(desired, out double margin);
        int passes = 0;
        Size? arranged = null;
        Size? resolvedChrome = null;
        Size MeasureChrome(Size client)
        {
            // Replaced or re-themed resources dirty descendants, not always this root, and an
            // unchanged constraint would return the earlier chrome. Content laid out under other
            // inputs is measured afresh; fresh content has no earlier measurement to reuse.
            var inputs = (environment with { NativeChrome = default }, stamp);
            if (passes == 0 && _lastMeasuredContent is not null && _lastMeasuredInputs != inputs)
                InvalidateMeasureTree(content);
            _lastMeasuredInputs = inputs;
            // GalleryEditorWindow can wrap the editor in additional content (a retention
            // banner). Its chrome participates in the same measurement as the toolbars.
            content.Measure(client);
            content.Arrange(new Rect(client));
            passes++;
            arranged = client;
            Size surface = _editor.DisplaySurfaceSize;
            var clientChrome = new Size(Math.Max(0, client.Width - surface.Width),
                Math.Max(0, client.Height - surface.Height));
            cache.Add(client, chromeEnvironment, clientChrome);
            _lastMeasuredContent = content;
            _lastClientChrome = clientChrome;
            return clientChrome;
        }

        // The inspector reserves wrapped rows based on the available width and height.
        // Lay out the actual content at each candidate, including the native non-client area,
        // until the outer size and its measured surface budget agree. This is all before Show
        // or synchronously during SourceInitialized, never after the first visible frame.
        // A candidate measured earlier with the same inputs reuses that client chrome.
        Size? previous = null;
        for (int pass = 0; pass < 8; pass++)
        {
            Size client = ClientOf(fit);
            if (!reuse || !cache.TryGet(client, chromeEnvironment, out Size clientChrome))
            {
                if (!measureIfMissing)
                {
                    // A cold editor has no HWND/DPI yet. Its first source initialization
                    // measures once with the actual frame, before any content is displayed.
                    // Warm editors still take the exact cached size here.
                    _lastAutomaticSize = new Size(Width, Height);
                    return new SizingMeasurement(0, Stopwatch.GetElapsedTime(started), true);
                }
                clientChrome = MeasureChrome(client);
            }
            resolvedChrome = clientChrome;
            Size wanted = DesiredFor(clientChrome, slack);
            MonitorWindowPlacement.WindowFit next = FitFor(wanted, out double wantedMargin);
            bool alternating = next.Size == previous;
            // Layout rounding at a DPI other than the target monitor's can alternate between two
            // sizes. Keep the larger: its surface holds the image where the smaller one falls short.
            if (next.Size == fit.Size || (alternating && fit.Size.Width >= next.Size.Width && fit.Size.Height >= next.Size.Height))
            {
                if (next.Size == fit.Size) (desired, margin) = (wanted, wantedMargin);
                break;
            }
            previous = fit.Size;
            (fit, desired, margin) = (next, wanted, wantedMargin);
            if (alternating) break;
        }
        // Leave content laid out at the window's own size, as the measured passes always did.
        if (arranged is { } last && ChromeCache.Normalize(last) != ChromeCache.Normalize(ClientOf(fit)))
            resolvedChrome = MeasureChrome(ClientOf(fit));

        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        ApplyNativeSize(fit.Size);
        if (center)
            MonitorWindowPlacement.CenterOn(this, monitor, desired, MinimumSize, margin);
        else
        {
            MinWidth = fit.Minimum.Width;
            MinHeight = fit.Minimum.Height;
            Width = fit.Size.Width;
            Height = fit.Size.Height;
        }
        _lastAutomaticSize = new Size(Width, Height);
        _lastDesiredSize = desired;
        _lastWorkAreaMargin = margin;
        _lastSizingEnvironment = environment;
        _lastSizedContent = content;
        _lastSizedClientChrome = resolvedChrome;
        return new SizingMeasurement(passes, Stopwatch.GetElapsedTime(started), false);
    }

    private void ApplyNativeSize(Size size)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState != WindowState.Normal
            || (Width == size.Width && Height == size.Height)) return;
        // Once an HWND exists, separate Width/Height setters each synchronously lay out and
        // paint the content. Resize both dimensions together; WPF's WM_SIZE handling updates
        // its properties. The ordinary setters below remain the fallback if this call fails.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0,
            (int)Math.Round(size.Width * dpi.DpiScaleX),
            (int)Math.Round(size.Height * dpi.DpiScaleY),
            0x0002 | 0x0004 | 0x0010); // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
    }

    private static void InvalidateMeasureTree(DependencyObject element)
    {
        if (element is UIElement child) child.InvalidateMeasure();
        for (int index = 0, count = VisualTreeHelper.GetChildrenCount(element); index < count; index++)
            InvalidateMeasureTree(VisualTreeHelper.GetChild(element, index));
    }

    private static ResourceDictionary? Filled(ResourceDictionary resources) =>
        resources.Count == 0 && resources.MergedDictionaries.Count == 0 ? null : resources;

    private static double MarginForDesiredSize(MonitorInfo monitor, Size desired, double scale)
    {
        RectD workArea = monitor.WorkArea.Normalized();
        double availableWidth = Math.Max(1, Math.Floor(workArea.Width) / scale);
        double availableHeight = Math.Max(1, Math.Floor(workArea.Height) / scale);
        // A small reduction of the surrounding space is preferable to resampling every pixel.
        // A capture that still cannot fit the full work area keeps the normal 64-DIP margin.
        if (desired.Width > availableWidth || desired.Height > availableHeight) return WorkAreaMargin;
        return Math.Min(WorkAreaMargin, Math.Min(availableWidth - desired.Width, availableHeight - desired.Height));
    }

    private Size MeasureNativeChrome()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out NativeRect outer) && GetClientRect(handle, out NativeRect client))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            return new Size(Math.Max(0, (outer.Right - outer.Left - client.Right + client.Left) / dpi.DpiScaleX),
                Math.Max(0, (outer.Bottom - outer.Top - client.Bottom + client.Top) / dpi.DpiScaleY));
        }

        // ModernWindowChrome changes DWM colours/corners, not the native client rectangle.
        // Match WPF's style bits before CreateWindowEx, at the destination monitor's DPI.
        uint style = 0x00080000; // WS_SYSMENU
        uint extended = ShowInTaskbar ? 0x00040000u : 0; // WS_EX_APPWINDOW
        if (WindowStyle != WindowStyle.None) style |= 0x00C00000; // WS_CAPTION
        if (WindowStyle == WindowStyle.ToolWindow) extended |= 0x00000080; // WS_EX_TOOLWINDOW
        if (WindowStyle == WindowStyle.ThreeDBorderWindow) extended |= 0x00000200; // WS_EX_CLIENTEDGE
        if (ResizeMode != ResizeMode.NoResize) style |= 0x00020000; // WS_MINIMIZEBOX
        if (ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            style |= 0x00050000; // WS_THICKFRAME | WS_MAXIMIZEBOX
        uint targetDpi = _targetMonitor.Dpi > 0 ? _targetMonitor.Dpi : 96;
        var rect = new NativeRect();
        if (AdjustWindowRectExForDpi(ref rect, style, false, extended, targetDpi))
        {
            double scale = targetDpi / 96.0;
            return new Size((rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
        }

        // If the platform cannot predict the frame, SourceInitialized still measures and
        // corrects it before rendering. It also handles callers changing the style after new.
        if (WindowStyle == WindowStyle.None) return new Size();
        Thickness frame = SystemParameters.WindowNonClientFrameThickness;
        return new Size(frame.Left + frame.Right, frame.Top + frame.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref NativeRect rect, uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested
    {
        get => _editor.CommitRequested;
        set => _editor.CommitRequested = value is null ? null : async result =>
        {
            // From here a capture command waits for this Done instead of being refused, also
            // before the window has left the screen.
            bool outOfSight = FinishesOutOfSight && result.Action == EditorCommitAction.Done;
            if (outOfSight) _doneFinishing ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closing = false;
            try
            {
                AnnotationEditingResult still = FrameStillImage is null ? result : await FrameStillImage.CreateResultAsync(result);
                if (outOfSight) still.CopiedToClipboard = LeaveScreenWhileFinishing;
                bool saved;
                try { saved = await value(still); }
                finally
                {
                    // A video-frame export may use a derived result. Preserve the outcome for
                    // the original editor even when only the clipboard operation throws.
                    result.SaveCompleted = still.SaveCompleted;
                    result.ClipboardCopyFailed = still.ClipboardCopyFailed;
                }
                if (!saved || result.Action == EditorCommitAction.CopyToClipboard) return saved;
                closing = await RetireDraftBeforeCloseAsync();
                return closing;
            }
            finally
            {
                // A save that failed after the window left the screen shows it again, with the
                // failure in its status line and the draft kept, as when it never left.
                if (!closing) EndDoneWithoutClosing();
            }
        };
    }

    /// <summary>
    /// Lets Done take the window off the screen as soon as the clipboard holds the result, while
    /// the library save finishes. The window stays open (hidden) until the save completes, so the
    /// commit, its events and the draft keep their order; a failed save shows it again. Only for a
    /// modeless window: hiding a modal one would end its <see cref="Window.ShowDialog"/> early.
    /// </summary>
    internal bool FinishesOutOfSight { get; set; }

    private TaskCompletionSource? _doneFinishing;
    private bool _leftScreen;

    /// <summary>True from the start of a Done that may finish out of sight until it has closed the window or ended without closing.</summary>
    internal bool IsFinishingDone => _doneFinishing is not null;

    /// <summary>
    /// Completes when that Done has closed the window, or has ended without closing (the editor is
    /// usable again, on screen); completed when no such Done is running.
    /// </summary>
    internal Task DoneFinished => _doneFinishing?.Task ?? Task.CompletedTask;

    /// <summary>True while Done's save finishes after the window has left the screen.</summary>
    internal bool IsFinishingOutOfSight => _leftScreen;

    private void LeaveScreenWhileFinishing()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(LeaveScreenWhileFinishing);
            return;
        }

        if (_doneFinishing is null || _leftScreen || _windowClosed || !IsVisible) return;
        _leftScreen = true;
        Hide();
    }

    private void EndDoneWithoutClosing()
    {
        TaskCompletionSource? finishing = _doneFinishing;
        _doneFinishing = null;
        bool returning = _leftScreen;
        _leftScreen = false;
        try
        {
            if (returning && !_windowClosed)
            {
                Show();
                WindowForeground.BringForward(this);
            }
        }
        finally
        {
            // Released after the editor has ended its commit and is editable again: the editor
            // resumes on this turn or on a normal-priority one, both ahead of input priority, so a
            // capture command that waited finds an idle editor (asked to save, keep or discard).
            if (finishing is not null)
            {
                if (Dispatcher.HasShutdownStarted) finishing.TrySetResult();
                else _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => finishing.TrySetResult()));
            }
        }
    }

    // Only the persistence/export consumer sees timed text; Committed retains the text-free video layer input.
    internal MyCapture.App.Recording.VideoFrameStillImage? FrameStillImage
    {
        get;
        set { field = value; _editor.AllowsPin = value is null; }
    }

    internal event EventHandler<AnnotationEditingResult>? Committed;

    /// <summary>Raised once when the window closes without a successful commit.</summary>
    internal event EventHandler? Cancelled;

    internal bool WasCommitted => _committed;

    internal AnnotationEditorControl Editor => _editor;

    /// <summary>
    /// Asks what to do with unsaved edits when the user closes the window. Tests replace it;
    /// the default shows <see cref="EditorCloseDialog"/> over this window.
    /// </summary>
    internal Func<EditorCloseChoice> ConfirmClose { get; set; }

    /// <summary>
    /// Closes the way the title bar's close button does: unsaved edits are confirmed first.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="Window.Close"/> from code (a retake, the owner closing, application
    /// exit) does not ask; WPF ignores a cancelled close during exit, so a question there could
    /// not be honoured.
    /// </remarks>
    internal void RequestClose()
    {
        // While the unsaved-edits question is open the window is already closing: a second Close
        // would throw and leave the request flag set for a later close from code.
        if (IsAskingToClose) return;
        _closeAsked = true;
        Close();
    }

    /// <summary>
    /// Closes the way <see cref="RequestClose"/> does and completes once the outcome is known: true
    /// when the window has closed, false when it stays open (the user kept it, or saving failed).
    /// </summary>
    internal Task<bool> RequestCloseAsync()
    {
        if (IsCloseInProgress || IsAskingToClose || IsSavingToClose) return Task.FromResult(false);
        _discardCloseTask = null;
        _saveCloseTask = null;
        LastCloseChoice = null;
        RequestClose();
        return _discardCloseTask ?? _saveCloseTask ?? CompleteRequestedCloseAsync();
    }

    /// <summary>True while the unsaved-edits question is on screen; the window is closing meanwhile.</summary>
    internal bool IsAskingToClose { get; private set; }

    /// <summary>The answer to the last requested close's unsaved-edits question; null when none was asked.</summary>
    internal EditorCloseChoice? LastCloseChoice { get; private set; }

    /// <summary>
    /// True while a modal dialog keeps this window from taking input: one of its own, a file picker
    /// over it, or another window's (a modal dialog disables every window of the app).
    /// </summary>
    internal bool HasOpenDialog => !WindowForeground.IsEnabled(this);

    /// <summary>
    /// Asks what to do with unsaved edits. A capture shortcut can ask while the editor is minimized
    /// or behind another app, so the editor comes forward first and the question opens in front of it.
    /// </summary>
    private EditorCloseChoice AskBeforeClosing()
    {
        WindowForeground.BringForward(this);
        IsAskingToClose = true;
        try
        {
            EditorCloseChoice choice = ConfirmClose();
            LastCloseChoice = choice;
            return choice;
        }
        finally
        {
            IsAskingToClose = false;
        }
    }

    private bool _closeAsked;
    private bool _cancelRaised;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // A host may deliberately choose its own dimensions after construction. Only correct
        // our automatic budget; honour those explicit sizes (including diagnostic hosts).
        if (Width == _lastAutomaticSize.Width && Height == _lastAutomaticSize.Height)
        {
            bool center = _placeOnTargetMonitor || (WindowStartupLocation == WindowStartupLocation.CenterScreen && Owner is null);
            SourceSizingMeasurement = SizeForMonitor(_targetMonitor, center, allowReuse: true);
        }
        else SourceSizingMeasurement = new SizingMeasurement(0, TimeSpan.Zero, true);
        // The caption close button, Alt+F4, the system menu and the taskbar all arrive as
        // SC_CLOSE before WM_CLOSE; only those user closes are confirmed.
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
        MyCapture.App.Diagnostics.ResidentProbeWindowHook.SourceInitialized?.Invoke(this);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int SystemCommand = 0x0112, CloseCommand = 0xF060;
        if (message == SystemCommand && ((int)(wParam.ToInt64() & 0xFFF0)) == CloseCommand)
        {
            _closeAsked = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_editor.IsEditingOperationInProgress || (IsCloseInProgress && !_discardedDraft))
        {
            e.Cancel = true;
            // A picked picture still being read is added first and the close then goes ahead,
            // as it did while the picture was decoded on the UI thread; other work drops it.
            if (_editor.IsOnlyReadingPictures && !IsCloseInProgress) _ = CloseAfterPicturesAsync(_closeAsked);
            _closeAsked = false;
            return;
        }

        bool ask = _closeAsked && !_committed && NeedsUnsavedProtection;
        _closeAsked = false;
        if (ask)
        {
            switch (AskBeforeClosing())
            {
                case EditorCloseChoice.SaveAndClose:
                    // The Done commit closes the window itself once the edit is saved, and keeps
                    // it open (with the failure in the status line) when saving fails.
                    e.Cancel = true;
                    _saveCloseTask = SaveAndCloseAsync();
                    return;
                case EditorCloseChoice.DontSave:
                    if (_draftSession is null)
                    {
                        _discardedDraft = true;
                        break;
                    }
                    e.Cancel = true;
                    _discardCloseTask = DiscardAndCloseAsync();
                    return;
                default:
                    e.Cancel = true;
                    _ = _editor.Focus();
                    return;
            }
        }

        base.OnClosing(e);
    }

    private bool _closeWaitsForPictures;
    private bool _deferredCloseAsked;
    private bool _windowClosed;

    /// <summary>Repeats a close deferred by <see cref="OnClosing"/> once picked pictures are in.</summary>
    private async Task CloseAfterPicturesAsync(bool asked)
    {
        _deferredCloseAsked |= asked;
        if (_closeWaitsForPictures) return;
        _closeWaitsForPictures = true;
        try { await _editor.ImageInsertsSettledAsync(); }
        finally { _closeWaitsForPictures = false; }
        bool ask = _deferredCloseAsked;
        _deferredCloseAsked = false;
        if (_windowClosed || IsAskingToClose) return;
        _closeAsked = ask;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_editor.HandleKeyDown(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (DraftRecordSource?.Invoke() is { } record) PracticeSignals.Ready(record.Id, _editor);
        _ = Activate();
        _ = _editor.Focus();
        _ = Keyboard.Focus(_editor);
    }

    private void OnEditingCompleted(object? sender, AnnotationEditingResult e)
    {
        _committed = true;
        Committed?.Invoke(this, e);
        Close();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => RequestClose();

    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        _editor.CancelPendingMosaics(closing: true);
        FinishDraftRecovery();
        _editor.EditingCompleted -= OnEditingCompleted;
        _editor.CloseRequested -= OnCloseRequested;
        ContentRendered -= OnContentRendered;
        if (!_committed && !_cancelRaised)
        {
            _cancelRaised = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosed(e);
        if (_editor.CompletedPracticeCopy is { } copied)
            PracticeSignals.Publish(new(PracticeEventKind.EditorCopiedAndClosed, copied.SessionId, RecordId: copied.RecordId, EditorId: copied.EditorId));
        else _editor.NotifyPractice(PracticeEventKind.EditorClosed);
        // After Closed: whoever waits for this Done sees the window gone.
        _leftScreen = false;
        if (_doneFinishing is { } finishing)
        {
            _doneFinishing = null;
            finishing.TrySetResult();
        }
    }
}
