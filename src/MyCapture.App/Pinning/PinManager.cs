using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
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
/// <see cref="RestoreLastClosed"/> reopens the newest one with its original content and last
/// placement. Hiding with Shift+F3 is not a close and is never recorded. The history is
/// memory-only and is discarded when the app exits, so closed screens never reach disk.
/// </para>
/// </remarks>
internal sealed class PinManager
{
    private readonly List<PinWindow> _pins = [];
    private readonly ClosedPinHistory<ClosedPin> _closed;
    private bool _recordClosedPins = true;
    private readonly Func<PinSettings> _settings;
    private readonly PinImageSaveService? _saveService;
    private readonly Func<BitmapSource, string?, string?, Task<bool>> _copyImageAsync;
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
            ClipboardImageService.CopyImageWithTextAsync,
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
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveService = saveService;
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _closed = new ClosedPinHistory<ClosedPin>(() => _settings().ClosedWindowRestoreLimit);
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
        PinClosedPlacement? Placement);

    /// <summary>Number of closed pins that can currently be reopened.</summary>
    internal int ClosedCount => _closed.Count;

    /// <summary>Whether <see cref="RestoreLastClosed"/> has anything to reopen.</summary>
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

    internal Func<PinWindow, bool> ActivatePin { get; set; } = static pin => pin.Activate();

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
    internal PinWindow PinImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return OpenPin(PinContent.FromImage(image), activate: true);
    }

    /// <summary>
    /// Opens an already-rendered text or table pin while retaining its exact Unicode source.
    /// </summary>
    internal PinWindow PinRenderedText(BitmapSource image, string originalText, bool isTable) =>
        OpenPin(PinContent.FromText(image, originalText, isTable), activate: true);

    private PinWindow OpenPin(PinContent content, bool activate)
    {
        BitmapSource image = content.Image;

        PinSettings settings = _settings();
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        double scale = monitor.ScaleFactor <= 0 ? 1.0 : monitor.ScaleFactor;
        double sourcePixelsPerDip = content.UsesDeviceIndependentPixels ? 1.0 : scale;

        // Captured images and pictures published by other applications (including a
        // spreadsheet's picture of its cells) contain display pixels. Text/table previews that
        // MyCapture renders itself are deliberately rendered at 96 DPI, so keeping them at one
        // pixel per DIP prevents tiny cards on a 150% or 200% monitor.
        double imageWidthDip = image.PixelWidth / sourcePixelsPerDip;
        double imageHeightDip = image.PixelHeight / sourcePixelsPerDip;

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
    internal PinWindow? RestoreLastClosed()
    {
        if (!_closed.TryPop(out ClosedPin? closed))
        {
            _log.LogInformation("Restore closed pin: nothing to restore");
            return null;
        }

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

        (double leftDip, double topDip, RectD? physical) = ResolveRestorePosition(closed, state);
        var pin = new PinWindow(closed.Content, state, leftDip, topDip, _settings);
        if (physical is { } bounds)
        {
            pin.PlaceAtPhysicalOnShow(bounds);
        }

        AttachAndShow(pin, activate: true);
        if (!pin.ApplyRestorePlacement())
        {
            _log.LogWarning("Restored pin could not reach its recorded position; left where Windows placed it");
        }

        pin.CompleteRestorePlacement();
        _log.LogInformation(
            "Restored a closed {Kind} pin at zoom {Zoom:0.##} ({Remaining} more restorable)",
            closed.Content.Kind,
            state.Zoom,
            _closed.Count);
        return pin;
    }

    /// <summary>
    /// Closes every pin at app exit without recording them and discards the restore history,
    /// so no closed screen outlives the process in memory or on disk.
    /// </summary>
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

    private (double LeftDip, double TopDip, RectD? Physical) ResolveRestorePosition(
        ClosedPin closed,
        PinViewState state)
    {
        if (closed.Placement is { } placement)
        {
            PinDisplayArea[] displays = MonitorEnumerator.GetAll()
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
                new RectD(result.Left, result.Top, result.Width, result.Height));
        }

        // The pin closed before it had a native window: reopen it centred on the cursor.
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
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
        return (left, top, null);
    }

    private void AttachAndShow(PinWindow pin, bool activate)
    {
        pin.CloseAllRequested += (_, _) => CloseAll();
        pin.CopyRequested += OnPinCopyRequested;
        pin.OriginalTextCopyRequested += OnPinOriginalTextCopyRequested;
        pin.OcrRequested += (_, source) => OcrRequested?.Invoke(pin, source);
        pin.SaveRequested += OnPinSaveRequested;
        pin.RestoreClosedRequested += (_, _) => RestoreLastClosed();
        pin.CanRestoreClosedPin = () => CanRestoreClosed;
        pin.Closed += (_, _) => OnPinClosed(pin);

        _pins.Add(pin);
        pin.ShowActivated = activate;
        BeforeShowForTest?.Invoke(pin);
        pin.Show();
        if (activate) _ = ActivatePin(pin);
    }

    private void OnPinClosed(PinWindow pin)
    {
        _pins.Remove(pin);
        if (!_recordClosedPins)
        {
            return;
        }

        PinViewState state = pin.State;
        var entry = new ClosedPin(
            pin.PinnedContent,
            state.ImageWidthDip,
            state.ImageHeightDip,
            state.Zoom,
            state.Opacity,
            pin.ClosedPlacement);
        if (_closed.Push(entry, pin.PinnedContent.EstimatedRetainedBytes))
        {
            _log.LogInformation("Remembered a closed {Kind} pin ({Count} restorable)", pin.PinnedContent.Kind, _closed.Count);
        }
        else
        {
            _log.LogInformation("Closed {Kind} pin not kept for restore (disabled or over the memory budget)", pin.PinnedContent.Kind);
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
            foreach (PinWindow pin in _pins)
            {
                // Safety escape: clear click-through before revealing so the mouse can
                // always reach the pin again, even when the toggle hotkey is unassigned.
                // Only touch pins that actually have it on, to avoid spurious feedback.
                if (pin.State.IsClickThrough)
                {
                    pin.ApplyClickThrough(enabled: false);
                }

                pin.ShowPin();
            }

            AreHidden = false;
            _log.LogInformation("Revealed {Count} pin(s) with click-through cleared", _pins.Count);
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

    /// <summary>Closes every open pin.</summary>
    internal void CloseAll()
    {
        // Copy first: closing raises Closed, which mutates _pins.
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
            copied = await _copyImageAsync(request.Image, request.SourcePageUrl, request.OriginalText);
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
            copied = await _copyTextAsync(originalText);
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
    /// Drops any pin that has already closed. The <c>Closed</c> handler normally removes a
    /// pin immediately; this is a belt-and-braces sweep in case a close raced a query.
    /// </summary>
    private void Prune() => _pins.RemoveAll(p => p.IsClosed);
}
