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
/// and close all.
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
/// </remarks>
internal sealed class PinManager
{
    private readonly List<PinWindow> _pins = [];
    private readonly Func<PinSettings> _settings;
    private readonly PinImageSaveService? _saveService;
    private readonly Func<BitmapSource, Task<bool>> _copyImageAsync;
    private readonly Func<string, Task<bool>> _copyTextAsync;
    private readonly ILogger _log;

    internal PinManager(Func<PinSettings> settings, ILogger log)
        : this(
            settings,
            saveService: null,
            static _ => Task.FromResult(true),
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
            ClipboardImageService.CopyImageAsync,
            ClipboardImageService.CopyTextAsync,
            log)
    {
    }

    internal PinManager(
        Func<PinSettings> settings,
        PinImageSaveService? saveService,
        Func<BitmapSource, Task<bool>> copyImageAsync,
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
        Func<BitmapSource, Task<bool>> copyImageAsync,
        Func<string, Task<bool>> copyTextAsync,
        ILogger log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveService = saveService;
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Raised when a pin requests OCR of its image (Ctrl+double-click or menu).</summary>
    internal event EventHandler<BitmapSource>? OcrRequested;

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

    /// <summary>
    /// The most recent save request routed from a pin. This awaitable internal seam lets the
    /// WPF boundary remain event-based while integration tests deterministically observe the
    /// complete PinWindow -> PinManager -> PinImageSaveService transaction.
    /// </summary>
    internal Task<PinSaveResult>? LastSaveOperationForTest { get; private set; }

    /// <summary>Most recent original-text copy, exposed for deterministic integration tests.</summary>
    internal Task<bool>? LastTextCopyOperationForTest { get; private set; }

    /// <summary>
    /// Reads supported clipboard content and, if present, opens a new independent pin for it.
    /// </summary>
    internal async Task<PasteResult> PasteFromClipboardAsync()
    {
        ClipboardImageReader.PinReadAttempt read = await ClipboardImageReader.ReadPinAsync();

        switch (read.Outcome.Status)
        {
            case ClipboardImageStatus.Success when read.Content is not null:
                OpenPin(read.Content);
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
        return OpenPin(PinContent.FromImage(image));
    }

    /// <summary>
    /// Opens an already-rendered text or table pin while retaining its exact Unicode source.
    /// </summary>
    internal PinWindow PinRenderedText(BitmapSource image, string originalText, bool isTable) =>
        OpenPin(PinContent.FromText(image, originalText, isTable));

    private PinWindow OpenPin(PinContent content)
    {
        BitmapSource image = content.Image;

        PinSettings settings = _settings();
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        double scale = monitor.ScaleFactor <= 0 ? 1.0 : monitor.ScaleFactor;
        double sourcePixelsPerDip = content.UsesDeviceIndependentPixels ? 1.0 : scale;

        // Captured images contain physical screen pixels. Text/table previews are deliberately
        // rendered at 96 DPI, so keeping them at one pixel per DIP prevents tiny cards on a
        // 150% or 200% monitor.
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

        PinGeometry.Placement placement = PinGeometry.InitialPlacement(
            imageWidthDip,
            imageHeightDip,
            workLeftDip,
            workTopDip,
            workWidthDip,
            workHeightDip,
            cursorXDip,
            cursorYDip);

        var state = new PinViewState(
            imageWidthDip,
            imageHeightDip,
            placement.Zoom,
            settings.InitialOpacity,
            settings.ZoomStep);

        var pin = new PinWindow(content, state, placement.Left, placement.Top, _settings);
        pin.CloseAllRequested += (_, _) => CloseAll();
        pin.CopyRequested += OnPinCopyRequested;
        pin.OriginalTextCopyRequested += OnPinOriginalTextCopyRequested;
        pin.OcrRequested += (_, source) => OcrRequested?.Invoke(this, source);
        pin.SaveRequested += OnPinSaveRequested;
        pin.Closed += (_, _) => _pins.Remove(pin);

        _pins.Add(pin);
        pin.Show();
        pin.Activate();

        _log.LogInformation(
            "Pinned {Width}x{Height} {Kind} at zoom {Zoom:0.##} ({Count} pin(s) open)",
            image.PixelWidth,
            image.PixelHeight,
            content.Kind,
            placement.Zoom,
            _pins.Count);

        return pin;
    }

    /// <summary>Hides every pin if any are visible, otherwise reveals them all.</summary>
    /// <remarks>
    /// Revealing doubles as the documented safety escape for a pin that turned on
    /// click-through from its context menu while the global
    /// <see cref="MyCapture.Platform.Shell.GlobalHotkeyCommand.ToggleClickThrough"/> hotkey
    /// is unassigned by default. A click-through pin carries <c>WS_EX_TRANSPARENT</c>, so the
    /// mouse can never land on it to reverse the setting. By turning click-through off on
    /// every pin just before <see cref="PinWindow.ShowPin"/>, two presses of the default
    /// Shift+F3 (hide, then show) always restore mouse interaction on every pin.
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

    private async void OnPinCopyRequested(object? sender, BitmapSource image)
    {
        bool copied;
        try
        {
            copied = await _copyImageAsync(image);
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
                ErrorMessage: "저장 서비스를 사용할 수 없습니다.");
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
                ? await _saveService.SaveAsAsync(e.Image, pin)
                : await _saveService.QuickSaveAsync(e.Image);
        }
        catch (Exception ex)
        {
            // Catch every unexpected failure inside the task retained at the WPF event
            // boundary, so a bad path or encoder failure can never terminate the tray.
            _log.LogError(ex, "Pinned-image save failed unexpectedly");
            result = new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: ex.Message);
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
            ? "파일을 저장할 수 없습니다. 저장 위치와 권한을 확인해 주세요."
            : result.ErrorMessage;
        _ = MessageBox.Show(
            pin,
            $"고정 이미지를 저장하지 못했습니다.\n\n{detail}",
            "고정 이미지 저장",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return result;
    }

    /// <summary>
    /// Drops any pin that has already closed. The <c>Closed</c> handler normally removes a
    /// pin immediately; this is a belt-and-braces sweep in case a close raced a query.
    /// </summary>
    private void Prune() => _pins.RemoveAll(p => p.IsClosed);
}
