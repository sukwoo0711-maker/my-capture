using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// Result of a paste-to-screen request, so the caller (the app shell) can surface the
/// right tray feedback without knowing anything about the clipboard internals.
/// </summary>
internal enum PasteResult
{
    Pinned,
    NoSupportedContent,
    ClipboardBusy,
}

/// <summary>
/// Owns every live <see cref="PinWindow"/> and exposes the operations the global hotkeys
/// and tray drive: paste-to-screen, hide/show all, toggle click-through under the cursor,
/// close all, and reopening recently closed pins.
/// </summary>
/// <remarks>
/// <para>
/// The manager keeps a list of open pins and prunes it whenever one closes, so
/// <see cref="Count"/> is always accurate and closed windows never leak. It runs entirely
/// on the WPF dispatcher thread (the only thread that may touch the clipboard and windows),
/// which is why it takes plain positions and bitmaps rather than async work.
/// </para>
/// <para>
/// Placement uses the monitor under the cursor: the frozen image's pixel size is converted
/// to DIP with that monitor's scale factor, fitted to ~80% of its working area by
/// <see cref="PinGeometry"/>, and centred near the cursor but clamped fully inside the work
/// area. This keeps a pin from opening off-screen or larger than the display.
/// </para>
/// <para>
/// Every pin that closes (Esc, Delete, double-click, its menu, or close all) is remembered
/// in a <see cref="ClosedPinHistory{T}"/> bounded by
/// <see cref="PinSettings.ClosedWindowRestoreLimit"/> and a byte budget, and
/// <see cref="RestoreLastClosedAsync"/> reopens the newest one with its original content and last
/// placement. Hiding with Shift+F3 is not a close and is never recorded. The history is
/// memory-only and is discarded when the app exits. The separate opt-in session store saves
/// only pins that are still open, to reopen them at the next launch.
/// </para>
/// </remarks>
internal sealed partial class PinManager
{
    private readonly List<PinWindow> _pins = [];
    // Closed pins whose exit is still on the screen; their windows close when it ends.
    private readonly List<PinWindow> _leaving = [];
    private readonly Dictionary<PinWindow, long> _openedOrders = [];
    private readonly Dictionary<PinWindow, long> _restoredOpenedOrders = [];
    private long _nextOpenedOrder;
    private readonly ClosedPinHistory<EncodedClosedPin> _closed;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private int _pendingEncodes;
    private int _pendingRestores;
    private int _restoreGeneration;
    private bool _recordClosedPins = true;
    private readonly Func<PinSettings> _settings;
    private readonly PinImageSaveService? _saveService;
    private readonly Func<BitmapSource, string?, string?, CaptureProvenance?, ClipboardImageService.PngCache?, Task<bool>> _copyImageAsync;
    private readonly Func<string, Task<bool>> _copyTextAsync;
    private readonly ILogger _log;

    internal PinManager(Func<PinSettings> settings, ILogger log)
        : this(
            settings,
            saveService: null,
            static (_, _) => Task.FromResult(true),
            static _ => Task.FromResult(true),
            log)
    {
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        ILogger log)
        : this(
            settings,
            saveService,
            static (image, sourceUrl, text, provenance, cache) => ClipboardImageService.CopyImageWithTextAsync(
                image, sourceUrl, text, writeData: null, provenance, pngCache: cache),
            ClipboardImageService.CopyTextAsync,
            log)
    {
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, string?, Task<bool>> copyImageAsync,
        ILogger log)
        : this(
            settings,
            saveService,
            copyImageAsync,
            ClipboardImageService.CopyTextAsync,
            log)
    {
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, string?, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        ILogger log)
        : this(settings, saveService, (image, sourceUrl, _) => copyImageAsync(image, sourceUrl), copyTextAsync, log)
    {
        ArgumentNullException.ThrowIfNull(copyImageAsync);
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, string?, string?, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        ILogger log)
        : this(settings, saveService, (image, sourceUrl, text, _) => copyImageAsync(image, sourceUrl, text), copyTextAsync, log)
    {
        ArgumentNullException.ThrowIfNull(copyImageAsync);
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, string?, string?, CaptureProvenance?, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        ILogger log)
        : this(settings, saveService, (image, sourceUrl, text, provenance, _) => copyImageAsync(image, sourceUrl, text, provenance), copyTextAsync, log)
    {
        ArgumentNullException.ThrowIfNull(copyImageAsync);
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, string?, string?, CaptureProvenance?, ClipboardImageService.PngCache?, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        ILogger log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveService = saveService;
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _closed = new ClosedPinHistory<EncodedClosedPin>(() => _settings().ClosedWindowRestoreLimit);
    }

    /// <summary>
    /// A closed pin as the restore history keeps it: the unchanged content plus the
    /// presentation it had when it closed. No window reference is kept, so the closed
    /// window itself can be collected.
    /// </summary>
    internal sealed record ClosedPin(
        PinContent Content,
        double ImageWidthDip,
        double ImageHeightDip,
        double Zoom,
        double Opacity,
        PinClosedPlacement? Placement,
        long OpenedOrder = 0);

    private sealed record EncodedClosedPin(
        Task<EncodedPinContent> Content,
        double ImageWidthDip,
        double ImageHeightDip,
        double Zoom,
        double Opacity,
        PinClosedPlacement? Placement);

    internal bool IsInteractionActive => _pendingEncodes > 0 || _pendingRestores > 0
        || _pins.Any(pin => pin.IsInteractionActive);

    internal event Action? BackgroundWorkEnded;

    internal Task LastClosedEncodingForTest { get; private set; } = Task.CompletedTask;
    internal Task? LastRestoreOperationForTest { get; private set; }
    internal Func<PinContent, byte[]?, EncodedPinContent>? EncodeClosedForTest { get; set; }
    internal Func<EncodedPinContent, PinContent>? DecodeClosedForTest { get; set; }

    /// <summary>Number of closed pins that can currently be reopened.</summary>
    internal int ClosedCount => _closed.Count;

    /// <summary>Whether <see cref="RestoreLastClosedAsync"/> has anything to reopen.</summary>
    internal bool CanRestoreClosed => _closed.Count > 0;

    /// <summary>Estimated bytes held by the closed-pin history, for tests and diagnostics.</summary>
    internal long ClosedRetainedBytes => _closed.RetainedBytes;

    /// <summary>
    /// Re-applies <see cref="PinSettings.ClosedWindowRestoreLimit"/> after settings change, so
    /// a lowered limit or 0 (off) releases retained screen pixels right away.
    /// </summary>
    internal void TrimClosedHistory() => _closed.Trim();

    /// <summary>Raised when a pin requests OCR of its image (Ctrl+double-click or menu).</summary>
    internal event EventHandler<BitmapSource>? OcrRequested;

    /// <summary>Raised after a new pin is shown; reopening a closed pin does not raise it.</summary>
    internal event Action? PinCreated;

    internal event Action? ClickThroughHintShown;

    /// <summary>Raised after pins open, close, or finish changing their saved presentation.</summary>
    internal event Action? OpenPinsChanged;

    internal Func<IReadOnlyList<IntPtr>, IReadOnlyList<IntPtr>>? ReadPinOrderForTest { get; set; }
    internal Action<PinWindow>? ActivatePinForTest { get; set; }
    internal Func<MonitorInfo>? ReadPlacementMonitorForTest { get; set; }
    internal Func<IReadOnlyList<MonitorInfo>>? ReadRestoreMonitorsForTest { get; set; }

    /// <summary>Open pins in opening order, newest first, including hidden pins.</summary>
    internal IReadOnlyList<PinWindow> SnapshotOpenPinWindows()
    {
        Prune();
        return _pins
            .OrderByDescending(pin => _restoredOpenedOrders.ContainsKey(pin) ? 0 : 1)
            .ThenByDescending(pin => _restoredOpenedOrders.GetValueOrDefault(pin, _openedOrders[pin]))
            .ThenByDescending(pin => _openedOrders[pin])
            .ToArray();
    }

    internal void ActivateOpenPin(PinWindow pin)
    {
        Prune();
        if (!_pins.Contains(pin)) return;
        if (AreHidden) ShowAllPins(clearClickThrough: false);
        if (pin.State.IsClickThrough) pin.ApplyClickThrough(enabled: false);
        if (ActivatePinForTest is { } activatePin) activatePin(pin);
        else pin.Activate();
    }

    /// <summary>Snapshots live pins bottom-to-top, keeping opening age separate from stacking.</summary>
    internal IReadOnlyList<ClosedPin> SnapshotOpenPins()
    {
        Prune();
        // Restored pins predate anything opened during asynchronous startup decoding.
        Dictionary<PinWindow, long> openingAge = _pins
            .OrderBy(pin => _restoredOpenedOrders.ContainsKey(pin) ? 0 : 1)
            .ThenBy(pin => _restoredOpenedOrders.GetValueOrDefault(pin, _openedOrders[pin]))
            .ThenBy(pin => _openedOrders[pin])
            .Select((pin, index) => (Pin: pin, Order: (long)index + 1))
            .ToDictionary(item => item.Pin, item => item.Order);
        Dictionary<IntPtr, PinWindow> byHandle = _pins.Where(pin => pin.Handle != IntPtr.Zero)
            .ToDictionary(pin => pin.Handle);
        IntPtr[] handles = byHandle.Keys.ToArray();
        IReadOnlyList<IntPtr> order = ReadPinOrderForTest?.Invoke(handles) ?? PinWindowOrder.BottomToTop(handles);
        var ordered = new List<PinWindow>(_pins.Count);
        foreach (IntPtr handle in order)
            if (byHandle.Remove(handle, out PinWindow? pin)) ordered.Add(pin);
        ordered.InsertRange(0, _pins.Where(pin => pin.Handle == IntPtr.Zero || byHandle.ContainsKey(pin.Handle)));
        return ordered.Select(pin => new ClosedPin(
            pin.PinnedContent,
            pin.State.ImageWidthDip,
            pin.State.ImageHeightDip,
            pin.State.Zoom,
            pin.State.Opacity,
            pin.GetCurrentPlacement(),
            openingAge[pin])).ToArray();
    }

    /// <summary>Number of open pins.</summary>
    internal int Count
    {
        get
        {
            Prune();
            return _pins.Count;
        }
    }

    /// <summary>Whether pins are currently hidden by the global hide-all toggle.</summary>
    internal bool AreHidden { get; private set; }

    /// <summary>Number of open pins, shown or hidden, that let clicks through to the windows behind.</summary>
    internal int ClickThroughCount
    {
        get
        {
            Prune();
            return _pins.Count(pin => pin.State.IsClickThrough);
        }
    }

    /// <summary>
    /// The most recent save request routed from a pin. This awaitable internal seam lets the
    /// WPF boundary remain event-based while integration tests deterministically observe the
    /// complete PinWindow -> PinManager -> PinImageSaveService transaction.
    /// </summary>
    internal Task<PinSaveResult>? LastSaveOperationForTest { get; private set; }

    /// <summary>Most recent original-text copy, exposed for deterministic integration tests.</summary>
    internal Task<bool>? LastTextCopyOperationForTest { get; private set; }

    internal Action<PinWindow>? BeforeShowForTest { get; set; }
    internal Action<PinWindow, Action>? ShowPinForTest { get; set; }

    /// <summary>
    /// Reads supported clipboard content and, if present, opens a new independent pin for it.
    /// </summary>
    internal async Task<PasteResult> PasteFromClipboardAsync(Func<Task<ClipboardImageReader.PinReadAttempt>>? readClipboard = null)
    {
        ClipboardImageReader.PinReadAttempt read = await (readClipboard ?? ClipboardImageReader.ReadPinAsync)();

        switch (read.Outcome.Status)
        {
            case ClipboardImageStatus.Success when read.Content is not null:
                OpenPin(read.Content, activate: false);
                return PasteResult.Pinned;

            case ClipboardImageStatus.Busy:
                _log.LogInformation("Paste-to-screen: clipboard was busy");
                return PasteResult.ClipboardBusy;

            default:
                _log.LogInformation("Paste-to-screen: no supported clipboard content");
                return PasteResult.NoSupportedContent;
        }
    }

    /// <summary>Opens a new pin for an already-decoded, frozen image.</summary>
    internal PinWindow PinImage(BitmapSource image, CaptureProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return OpenPin(PinContent.FromImage(image, provenance: provenance), activate: true);
    }

    /// <summary>
    /// Opens an already-rendered text or table pin while retaining its exact Unicode source.
    /// </summary>
    internal PinWindow PinRenderedText(BitmapSource image, string originalText, bool isTable) =>
        OpenPin(PinContent.FromText(image, originalText, isTable), activate: true);

    private PinWindow OpenPin(PinContent content, bool activate)
    {
        PinSettings settings = _settings();
        MonitorInfo monitor = ReadPlacementMonitorForTest?.Invoke() ?? MonitorEnumerator.GetFromCursor();
        double scale = monitor.ScaleFactor <= 0 ? 1.0 : monitor.ScaleFactor;

        // Captured images and pictures published by other applications (including a
        // spreadsheet's picture of its cells) contain display pixels. Locally rendered
        // previews keep their original DIP layout while their raster follows the display.
        double imageWidthDip = content.UsesDeviceIndependentPixels ? content.LayoutWidthDip : content.Image.PixelWidth / scale;
        double imageHeightDip = content.UsesDeviceIndependentPixels ? content.LayoutHeightDip : content.Image.PixelHeight / scale;
        content = content.RenderedAt(scale);
        BitmapSource image = content.Image;

        RectD work = monitor.WorkArea;
        double workLeftDip = work.Left / scale;
        double workTopDip = work.Top / scale;
        double workWidthDip = work.Width / scale;
        double workHeightDip = work.Height / scale;

        (int cursorX, int cursorY) = WindowStyleFacade.GetCursorPosition();
        double cursorXDip = cursorX / scale;
        double cursorYDip = cursorY / scale;

        Prune();
        PinGeometry.Placement placement = PinGeometry.CascadedPlacement(
            imageWidthDip,
            imageHeightDip,
            workLeftDip,
            workTopDip,
            workWidthDip,
            workHeightDip,
            cursorXDip,
            cursorYDip,
            _pins.Count);

        var state = new PinViewState(
            imageWidthDip,
            imageHeightDip,
            placement.Zoom,
            settings.InitialOpacity,
            settings.ZoomStep);

        var pin = new PinWindow(content, state, placement.Left, placement.Top, _settings);
        AttachAndShow(pin, activate);

        _log.LogInformation(
            "Pinned {Width}x{Height} {Kind} at zoom {Zoom:0.##} ({Count} pin(s) open)",
            image.PixelWidth,
            image.PixelHeight,
            content.Kind,
            placement.Zoom,
            _pins.Count);

        if (PinCreated is { } handlers)
        {
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex)
                {
                    // Optional guidance must not turn an already-open pin into a failed action.
                    _log.LogWarning(ex, "Could not notify a new-pin observer");
                }
            }
        }

        return pin;
    }

    /// <summary>
    /// Reopens the most recently closed pin with its original content, zoom and opacity, at
    /// its last position, moved into a visible work area only when the display layout no
    /// longer shows that position.
    /// </summary>
    /// <returns>The reopened pin, or <see langword="null"/> when nothing can be restored.</returns>
    internal async Task<PinWindow?> RestoreLastClosedAsync()
    {
        _dispatcher.VerifyAccess();
        if (!_closed.TryPop(out EncodedClosedPin? closed))
        {
            _log.LogInformation("Restore closed pin: nothing to restore");
            return null;
        }

        int generation = _restoreGeneration;
        _pendingRestores++;
        var total = Stopwatch.StartNew();
        await _restoreGate.WaitAsync().ConfigureAwait(false);
        try
        {
            EncodedPinContent encoded = await closed.Content.ConfigureAwait(false);
            var decode = Stopwatch.StartNew();
            PinContent content = await Task.Run(() => DecodeClosedForTest?.Invoke(encoded) ?? encoded.Decode()).ConfigureAwait(false);
            decode.Stop();
            return await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _restoreGeneration) return null;
                var snapshot = new ClosedPin(content, closed.ImageWidthDip, closed.ImageHeightDip,
                    closed.Zoom, closed.Opacity, closed.Placement);
                PinWindow pin = RestorePin(snapshot, activate: true);
                _log.LogInformation(
                    "Restored a closed {Kind} pin: decode {DecodeMs:0.##} ms, reopen {ReopenMs:0.##} ms, encoded {Bytes} bytes ({Remaining} more restorable)",
                    encoded.Kind, decode.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds,
                    encoded.Png.LongLength, _closed.Count);
                return pin;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not reopen a closed pin");
            throw;
        }
        finally
        {
            _restoreGate.Release();
            await _dispatcher.InvokeAsync(() =>
            {
                _pendingRestores--;
                if (_pendingRestores == 0 && _pendingEncodes == 0) BackgroundWorkEnded?.Invoke();
            });
        }
    }

    /// <summary>Reopens a saved open pin without activation, guidance, or closed-history changes.</summary>
    internal PinWindow RestoreSessionPin(ClosedPin snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RestorePin(snapshot, activate: false);
    }

    private PinWindow RestorePin(ClosedPin closed, bool activate)
    {
        // Click-through is deliberately not carried over: the new state starts interactive.
        // A click-through pin carries WS_EX_TRANSPARENT, so the mouse cannot reach it to move,
        // zoom or open its menu, and the user is reopening this pin precisely to work with it.
        // Hidden (Shift+F3) state is not carried over either; a restored pin is shown.
        var state = new PinViewState(
            closed.ImageWidthDip,
            closed.ImageHeightDip,
            closed.Zoom,
            closed.Opacity,
            _settings().ZoomStep);

        (double leftDip, double topDip, RectD? physical, double scale) = ResolveRestorePosition(closed, state);
        var pin = new PinWindow(closed.Content.RenderedAt(scale), state, leftDip, topDip, _settings);
        if (!activate)
        {
            _restoredOpenedOrders[pin] = closed.OpenedOrder > 0 ? closed.OpenedOrder : _nextOpenedOrder + 1;
        }
        if (physical is { } bounds)
        {
            pin.PlaceAtPhysicalOnShow(bounds);
        }

        AttachAndShow(pin, activate, landing: activate);
        if (!pin.ApplyRestorePlacement())
        {
            _log.LogWarning("Restored pin could not reach its recorded position; left where Windows placed it");
        }

        pin.CompleteRestorePlacement();
        return pin;
    }

    /// <summary>
    /// Closes every pin at app exit without recording them and discards the restore history,
    /// after the optional open-pin session has been saved by the app.
    /// </summary>
    /// <remarks>
    /// Exit only. It neither checks the session save nor leaves a way to reopen the pins, so a
    /// switch between pin boards must not reuse it; such a switch keeps the live pins open until a
    /// complete save passes <see cref="PinSessionStore.VerifyReadBack"/>.
    /// </remarks>
    internal void CloseAllForExit()
    {
        _recordClosedPins = false;
        try
        {
            CloseAll();
        }
        finally
        {
            _recordClosedPins = true;
            _closed.Clear();
        }
    }

    private (double LeftDip, double TopDip, RectD? Physical, double Scale) ResolveRestorePosition(
        ClosedPin closed,
        PinViewState state)
    {
        if (closed.Placement is { } placement)
        {
            PinDisplayArea[] displays = (ReadRestoreMonitorsForTest?.Invoke() ?? MonitorEnumerator.GetAll())
                .Select(m => new PinDisplayArea(m.DeviceName, m.Bounds, m.WorkArea, m.ScaleFactor))
                .ToArray();
            PinRestorePlacement.Result result = PinRestorePlacement.Resolve(
                placement,
                state.WidthDip,
                state.HeightDip,
                displays);
            if (result.Adjusted)
            {
                _log.LogInformation("Restored pin moved into the visible work area after a display change");
            }

            double displayScale = result.DisplayIndex >= 0 ? displays[result.DisplayIndex].ScaleFactor : 1.0;
            displayScale = displayScale <= 0 ? 1.0 : displayScale;

            // Same DIP convention as a new pin (physical / target display scale) for the
            // initial WPF position; the physical move then makes it exact.
            return (
                result.Left / displayScale,
                result.Top / displayScale,
                new RectD(result.Left, result.Top, result.Width, result.Height),
                displayScale);
        }

        // The pin closed before it had a native window: reopen it centred on the cursor.
        MonitorInfo monitor = ReadPlacementMonitorForTest?.Invoke() ?? MonitorEnumerator.GetFromCursor();
        double scale = monitor.ScaleFactor <= 0 ? 1.0 : monitor.ScaleFactor;
        (int cursorX, int cursorY) = WindowStyleFacade.GetCursorPosition();
        RectD work = monitor.WorkArea;
        (double left, double top) = PinGeometry.ClampTopLeftIntoWork(
            (cursorX / scale) - (state.WidthDip / 2.0),
            (cursorY / scale) - (state.HeightDip / 2.0),
            state.WidthDip,
            state.HeightDip,
            work.Left / scale,
            work.Top / scale,
            work.Width / scale,
            work.Height / scale);
        return (left, top, null, scale);
    }

    private void AttachAndShow(PinWindow pin, bool activate, bool landing = true)
    {
        pin.CloseAllRequested += (_, _) => CloseAll();
        pin.CopyRequested += OnPinCopyRequested;
        pin.OriginalTextCopyRequested += OnPinOriginalTextCopyRequested;
        pin.OcrRequested += (_, source) => OcrRequested?.Invoke(pin, source);
        pin.SaveRequested += OnPinSaveRequested;
        pin.RestoreClosedRequested += (_, _) => LastRestoreOperationForTest = RestoreClosedFromMenuAsync();
        pin.CanRestoreClosedPin = () => CanRestoreClosed;
        // The history, the session and the lists change when a close starts; a user close only
        // leaves its pixels on the screen for the exit, and the window itself goes later.
        pin.CloseStarted += (_, _) => OnPinClosed(pin);
        pin.Closed += (_, _) => _leaving.Remove(pin);
        pin.ViewStateChanged += NotifyOpenPinsChanged;
        pin.ClickThroughHintShown += () => ClickThroughHintShown?.Invoke();
        pin.ContentChanged += NotifyOpenPinsChanged;
        AttachTranslation(pin);

        _pins.Add(pin);
        _openedOrders.Add(pin, ++_nextOpenedOrder);
        pin.ShowActivated = activate;
        BeforeShowForTest?.Invoke(pin);
        ShowPinWindow(pin, pin.Show);
        AttachMermaid(pin);
        if (landing) pin.StartLandingEffect();
        if (activate)
        {
            if (ActivatePinForTest is { } activatePin) activatePin(pin);
            else pin.Activate();
        }

        NotifyOpenPinsChanged();
    }

    private void OnPinClosed(PinWindow pin)
    {
        _pins.Remove(pin);
        _openedOrders.Remove(pin);
        _restoredOpenedOrders.Remove(pin);
        _leaving.Add(pin);
        NotifyOpenPinsChanged();
        if (!_recordClosedPins || _closed.Limit == 0)
        {
            return;
        }

        PinViewState state = pin.State;
        PinContent content = pin.PinnedContent;
        byte[]? cached = content.EncodedPng ?? pin.ClipboardCache.TryGetEncoded(content.Image, content.SourcePageUrl);
        // The task owns the frozen source only until PNG encoding finishes. Its result has no
        // bitmap/window references; closing order is reserved before any background work completes.
        Func<PinContent, byte[]?, EncodedPinContent> encode = EncodeClosedForTest ?? EncodedPinContent.Encode;
        Task<EncodedPinContent> encoding = cached is not null
            ? Task.FromResult(EncodedPinContent.Encode(content, cached))
            : Task.Run(() => encode(content, null));
        var entry = new EncodedClosedPin(
            encoding,
            state.ImageWidthDip,
            state.ImageHeightDip,
            state.Zoom,
            state.Opacity,
            pin.ClosedPlacement);
        long initialBytes = cached is not null ? encoding.Result.RetainedBytes : content.EstimatedRetainedBytes;
        if (_closed.Push(entry, initialBytes))
        {
            _log.LogInformation("Remembered a closed {Kind} pin ({Count} restorable)", pin.PinnedContent.Kind, _closed.Count);
        }
        else
        {
            _log.LogInformation("Closed {Kind} pin not kept for restore (disabled or over the memory budget)", pin.PinnedContent.Kind);
        }
        _pendingEncodes++;
        LastClosedEncodingForTest = ObserveEncodingAsync(entry);
    }

    private async Task RestoreClosedFromMenuAsync()
    {
        // A completed Task<PinWindow> retained by the menu/test seam would keep that window
        // and its decoded image alive even after it closed again. Retain completion only.
        try { await RestoreLastClosedAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Closed-pin menu restore failed"); }
    }

    private async Task ObserveEncodingAsync(EncodedClosedPin entry)
    {
        try
        {
            EncodedPinContent encoded = await entry.Content.ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => _closed.UpdateRetainedBytes(entry, encoded.RetainedBytes));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not encode a closed pin for restore");
            await _dispatcher.InvokeAsync(() => _closed.Remove(entry));
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _pendingEncodes--;
                if (_pendingRestores == 0 && _pendingEncodes == 0) BackgroundWorkEnded?.Invoke();
            });
        }
    }

    private void NotifyOpenPinsChanged()
    {
        if (OpenPinsChanged is not { } handlers)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not notify an open-pin observer");
            }
        }
    }

    /// <summary>
    /// Turns click-through off on every open pin that has it, shown or hidden, and returns how
    /// many changed. The tray menu's way back from click-through.
    /// </summary>
    /// <remarks>
    /// A click-through pin carries <c>WS_EX_TRANSPARENT</c>, so the mouse can never land on it to
    /// reverse the setting, and a new installation leaves the
    /// <see cref="MyCapture.Platform.Shell.GlobalHotkeyCommand.ToggleClickThrough"/> key
    /// unassigned. Hidden pins stay hidden.
    /// </remarks>
    internal int ClearClickThroughAll()
    {
        Prune();
        int cleared = 0;
        foreach (PinWindow pin in _pins.Where(pin => pin.State.IsClickThrough))
        {
            pin.ApplyClickThrough(enabled: false);
            cleared++;
        }

        _log.LogInformation("Turned click-through off on {Count} pin(s)", cleared);
        return cleared;
    }

    /// <summary>Hides every pin if any are visible, otherwise reveals them all.</summary>
    /// <remarks>
    /// Revealing also turns click-through off, a second way back for a pin that turned it on
    /// from its context menu while the global
    /// <see cref="MyCapture.Platform.Shell.GlobalHotkeyCommand.ToggleClickThrough"/> hotkey
    /// is unassigned by default. A click-through pin carries <c>WS_EX_TRANSPARENT</c>, so the
    /// mouse can never land on it to reverse the setting. By turning click-through off on
    /// every pin just before <see cref="PinWindow.ShowPin"/>, two presses of the hide-all key
    /// (hide, then show) always restore mouse interaction on every pin.
    /// </remarks>
    internal void HideOrShowAll()
    {
        Prune();
        if (_pins.Count == 0)
        {
            return;
        }

        if (AreHidden)
        {
            ShowAllPins(clearClickThrough: true);
        }
        else
        {
            foreach (PinWindow pin in _pins)
            {
                pin.HidePin();
            }

            AreHidden = true;
            _log.LogInformation("Hid {Count} pin(s)", _pins.Count);
        }
    }

    private void ShowAllPins(bool clearClickThrough)
    {
        foreach (PinWindow pin in _pins)
        {
            if (clearClickThrough && pin.State.IsClickThrough) pin.ApplyClickThrough(enabled: false);
            ShowPinWindow(pin, pin.ShowPin);
        }

        AreHidden = false;
        _log.LogInformation("Revealed {Count} pin(s)", _pins.Count);
    }

    private void ShowPinWindow(PinWindow pin, Action show)
    {
        if (ShowPinForTest is { } showPin) showPin(pin, show);
        else show();
    }

    /// <summary>
    /// Toggles click-through on the top-most pin whose bounds contain the cursor.
    /// </summary>
    /// <remarks>
    /// A click-through pin carries <c>WS_EX_TRANSPARENT</c>, so <c>WindowFromPoint</c> skips
    /// it entirely — the OS cannot report it as "the window under the cursor". The manager
    /// therefore tests the physical cursor against each pin's own window bounds and picks the
    /// last (top-most, most recently opened) match, which is the only way a global command
    /// can turn click-through back off on a pin that made itself invisible to hit testing.
    /// </remarks>
    internal bool ToggleClickThroughUnderCursor()
    {
        Prune();
        (int x, int y) = WindowStyleFacade.GetCursorPosition();

        PinWindow? target = FindTopmostPinAt(x, y);
        if (target is null)
        {
            _log.LogInformation("Toggle click-through: no pin under the cursor");
            return false;
        }

        bool state = target.ToggleClickThrough();
        _log.LogInformation("Toggled click-through to {State} on a pin", state);
        return true;
    }

    /// <summary>
    /// The top-most pin containing the physical point, or <see langword="null"/>. Later pins
    /// in the list were opened more recently and sit above earlier ones.
    /// </summary>
    internal PinWindow? FindTopmostPinAt(int physicalX, int physicalY)
    {
        var bounds = new List<PinBounds>(_pins.Count);
        foreach (PinWindow pin in _pins)
        {
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            bounds.Add(new PinBounds(left, top, right, bottom, pin.State.IsHidden));
        }

        int index = PinHitTesting.TopmostIndexAt(bounds, physicalX, physicalY);
        return index >= 0 ? _pins[index] : null;
    }

    /// <summary>Closes every open pin at once, and ends any exit a closed pin is still playing.</summary>
    internal void CloseAll()
    {
        _restoreGeneration++;
        foreach (PinWindow leaving in _leaving.ToArray())
        {
            leaving.CompleteExit();
        }

        // Copy first: closing raises CloseStarted, which mutates _pins.
        foreach (PinWindow pin in _pins.ToArray())
        {
            pin.Close();
        }

        _pins.Clear();
        AreHidden = false;
    }

    private async void OnPinCopyRequested(object? sender, PinImageCopyRequest request)
    {
        bool copied;
        try
        {
            copied = await _copyImageAsync(request.Image, request.SourcePageUrl, request.OriginalText,
                (sender as PinWindow)?.Provenance, (sender as PinWindow)?.ClipboardCache);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pinned-image clipboard copy failed unexpectedly");
            copied = false;
        }

        if (copied)
        {
            _log.LogInformation("Copied a pinned image to the clipboard");
        }
        else
        {
            _log.LogWarning("Could not copy a pinned image to the clipboard");
        }

        if (sender is PinWindow pin && !pin.IsClosed)
        {
            pin.ReportCopyResult(copied);
        }
    }

    private void OnPinOriginalTextCopyRequested(object? sender, string originalText)
    {
        LastTextCopyOperationForTest = CopyOriginalTextAsync(sender, originalText);
    }

    private async Task<bool> CopyOriginalTextAsync(object? sender, string originalText)
    {
        bool copied;
        try
        {
            copied = await PinTextCopyService.CopyOriginalAsync(originalText, _copyTextAsync);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pinned original-text clipboard copy failed unexpectedly");
            copied = false;
        }

        if (copied)
        {
            _log.LogInformation("Copied a pinned item's original text to the clipboard");
        }
        else
        {
            _log.LogWarning("Could not copy a pinned item's original text to the clipboard");
        }

        if (sender is PinWindow pin && !pin.IsClosed)
        {
            pin.ReportOriginalTextCopyResult(copied);
        }

        return copied;
    }

    private void OnPinSaveRequested(object? sender, PinSaveRequestedEventArgs e)
    {
        LastSaveOperationForTest = SavePinAsync(sender, e);
    }

    private async Task<PinSaveResult> SavePinAsync(object? sender, PinSaveRequestedEventArgs e)
    {
        if (sender is not PinWindow pin || _saveService is null)
        {
            var unavailable = new PinSaveResult(
                PinSaveStatus.Failed,
                ErrorMessage: UiText.Get("Text_15A28DE50DBB"));
            if (sender is PinWindow unavailablePin && !unavailablePin.IsClosed)
            {
                unavailablePin.ReportSaveResult(unavailable);
            }

            return unavailable;
        }

        PinSaveResult result;
        try
        {
            result = e.Mode == PinSaveMode.SaveAs
                ? await _saveService.SaveAsAsync(e.Image, pin, e.SourcePageUrl)
                : await _saveService.QuickSaveAsync(e.Image, e.SourcePageUrl);
        }
        catch (Exception ex)
        {
            // Catch every unexpected failure inside the task retained at the WPF event
            // boundary, so a bad path or encoder failure can never terminate the tray.
            _log.LogError(ex, "Pinned-image save failed unexpectedly");
            result = new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: ex.Message, Error: ex);
        }

        if (pin.IsClosed)
        {
            return result;
        }

        pin.ReportSaveResult(result);
        if (result.Status != PinSaveStatus.Failed)
        {
            return result;
        }

        string detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? UiText.Get("Text_B651C2B6BDA9")
            : result.ErrorMessage;
        // A known cause (full disk, file in use) says what to do; [Open save location] shows the
        // folder so space can be freed or the other program found.
        string? folder = result.Error is { } error ? MyCapture.App.Themes.ThemedDialog.SaveLocationFor(error, result.Path) : null;
        _ = MyCapture.App.Themes.ThemedDialog.Show(
            pin,
            MyCapture.App.Themes.ThemedDialog.SaveFailureMessage(result.Error, result.Path),
            UiText.Get("Text_A97A3B71F038"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning,
            details: detail,
            recovery: () => LastSaveOperationForTest = SavePinAsync(pin, new PinSaveRequestedEventArgs(PinSaveMode.SaveAs, e.Image, e.SourcePageUrl)),
            recoveryLabel: UiText.Get("Dialog.ChooseLocation"),
            commands: folder is null ? null : [MyCapture.App.Themes.ThemedDialog.OpenSaveLocationCommand(folder)]);
        return result;
    }

    /// <summary>
    /// Drops any pin that has already closed. The <c>CloseStarted</c> handler normally removes a
    /// pin immediately; this is a belt-and-braces sweep in case a close raced a query.
    /// </summary>
    private void Prune()
    {
        foreach (PinWindow pin in _pins.Where(pin => pin.IsClosed).ToArray())
        {
            _pins.Remove(pin);
            _openedOrders.Remove(pin);
            _restoredOpenedOrders.Remove(pin);
        }
    }
}
