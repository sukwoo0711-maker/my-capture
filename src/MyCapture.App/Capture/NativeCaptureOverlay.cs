using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>Diagnostic-only selector. Only the small chrome uses WPF; the desktop is a GDI HWND.</summary>
internal sealed partial class NativeCaptureOverlay : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, NativeCaptureOverlay> Owners = new();
    private static readonly NativeSelectorInterop.WindowProc Procedure = DispatchWindowMessage;
    private static readonly Lazy<ushort> WindowClass = new(RegisterWindowClass);
    private const string ClassName = "MyCapture.Diagnostic.NativeSelector";
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly RectD _bounds;
    private readonly bool _abortOnFocusLoss, _showMagnifier;
    private readonly IReadOnlyDictionary<string, Brush> _palette;
    private NativeSelectorDib? _dib;
    private NativeSelectorChrome? _chrome;
    private NativeSelectorStartup? _startup;
    private Task? _dibPreparation;
    private bool _displayChanged;
    private IntPtr _borderPen;
    private bool _closed, _completing, _showing, _submitted, _dragging, _chromeReady;
    private PointD _anchor;
    private Int32Rect _selection;
    private Task<IReadOnlyList<WindowCandidate>>? _sourceDetails;

    internal NativeCaptureOverlay(RectD bounds, bool abortOnFocusLoss, bool showMagnifier, bool showSelectionGrid = true)
    {
        _bounds = bounds.ToPixelBounds();
        if (_bounds.IsEmpty) throw new ArgumentException("A non-empty desktop is required.", nameof(bounds));
        _abortOnFocusLoss = abortOnFocusLoss;
        _showMagnifier = showMagnifier;
        ShowSelectionGrid = showSelectionGrid;
        _palette = CaptureGuidePalette.Resolve();
        try
        {
            _ = WindowClass.Value;
            Handle = NativeSelectorInterop.CreateWindowEx(0x88, ClassName, "MyCapture", 0x80000000,
                (int)_bounds.Left, (int)_bounds.Top, (int)_bounds.Width, (int)_bounds.Height,
                IntPtr.Zero, IntPtr.Zero, NativeSelectorInterop.GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Owners[Handle] = this;
            Color border = (_palette["CaptureGuide.SelectionBorder"] as SolidColorBrush)?.Color ?? Colors.White;
            _borderPen = NativeSelectorInterop.CreatePen(0, 2, (uint)(border.R | (border.G << 8) | (border.B << 16)));
            if (_borderPen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            int disabled = 1;
            _ = NativeSelectorInterop.DwmSetWindowAttribute(Handle, 3, ref disabled, sizeof(int));
            SelectorTimeline.Mark?.Invoke("native-window-ready");
        }
        catch { Close(); throw; }
    }

    internal IntPtr Handle { get; private set; }
    internal bool IsVisible { get; private set; }
    internal bool IsClosed => _closed;
    internal bool IsReady { get; private set; }
    internal bool IsChromeReady => _chromeReady;
    internal NativeSelectorChromeStrategy ChromeStrategy { get; set; } = NativeSelectorChromeStrategy.Retained;
    internal NativeSelectorChrome? Chrome => _chrome;
    internal RectD ScreenBounds => _bounds;
    internal Int32Rect SelectionRectangle => _selection;
    internal PointD? SelectionAnchor => _dragging && !_closed ? _anchor : null;
    internal CaptureGuideArea ChromeArea
    {
        get
        {
            CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(ReadMonitors(), ReadCursor(), _bounds);
            return area with { WorkArea = new RectD(area.WorkArea.Left + _bounds.Left, area.WorkArea.Top + _bounds.Top,
                area.WorkArea.Width, area.WorkArea.Height) };
        }
    }
    internal bool CaptureExcluded { get; private set; }
    internal bool RequireCaptureExclusion { get; set; }
    internal FrozenFrame? Frame { get; private set; }
    internal NativeSelectorDib? DisplayDib => _dib;
    internal IntPtr SelectionPen => _borderPen;
    internal int DibAllocations { get; private set; }
    internal int LiveOwnedNativeHandles => (Handle != IntPtr.Zero ? 1 : 0) + (_borderPen != IntPtr.Zero ? 1 : 0)
        + (_dib?.Bitmap != IntPtr.Zero && _dib is not null ? 1 : 0) + (_dib?.Dc != IntPtr.Zero && _dib is not null ? 1 : 0)
        + (_visualFeedback?.LiveHandles ?? 0);
    internal IReadOnlyList<WindowCandidate> SourceWindows { get; set; } = [];
    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;
    internal event EventHandler? Closed;
    internal event Action<Exception>? Failed;

    internal void Prepare()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_closed, this);
        CaptureExcluded = CaptureWindowExclusion.TryApply(Handle);
        if (RequireCaptureExclusion && !CaptureExcluded) throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
        _prepared = true;
    }

    internal void AttachFrame(FrozenFrame frame)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_closed, this);
        if (Frame is not null) throw new InvalidOperationException("A native selector owns exactly one frame per session.");
        if (frame.ScreenBounds.ToPixelBounds() != _bounds || frame.PixelWidth != (int)_bounds.Width || frame.PixelHeight != (int)_bounds.Height)
            throw new InvalidOperationException(UiText.Get("Capture.DisplayChanged"));
        if (!frame.Bitmap.IsFrozen) throw new ArgumentException("The source must be frozen.", nameof(frame));
        if (_palette["CaptureGuide.Dimmer"] is not SolidColorBrush dimmer)
            throw new InvalidOperationException("The diagnostic DIB renderer requires a solid dimmer brush.");
        bool parallel = ChromeStrategy == NativeSelectorChromeStrategy.Parallel;
        _dib = new NativeSelectorDib(frame.Bitmap, dimmer, deferPixels: parallel);
        DibAllocations++;
        Frame = frame;
        if (parallel)
        {
            NativeSelectorDib dib = _dib;
            _dibPreparation = Task.Run(dib.InitializePixels);
            try { _chrome = CreateChrome(); }
            finally
            {
                // No paint, pixel mutation, or native resource release can race this worker.
                try { _dibPreparation.GetAwaiter().GetResult(); }
                finally { _dibPreparation = null; }
            }
        }
        SelectorTimeline.Mark?.Invoke("native-display-dib-ready");
    }

    internal void AwaitSourceDetails(Task<IReadOnlyList<WindowCandidate>> pending)
    {
        _sourceDetails = pending;
        if (pending.IsCompletedSuccessfully) SourceWindows = pending.Result;
    }

    internal void Show()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_dib is null) throw new InvalidOperationException("The display DIB is not ready.");
        if (IsVisible) return;
        if (!_prepared) Prepare();
        _showing = true;
        try
        {
            IsVisible = true;
            SelectorTimeline.Mark?.Invoke("native-show-start");
            _ = NativeSelectorInterop.ShowWindow(Handle, 5);
            SelectorTimeline.Mark?.Invoke("native-window-shown");
            _ = NativeSelectorInterop.UpdateWindow(Handle);
            if (!_submitted) throw new InvalidOperationException("The first native frame was not submitted.");
            // Diagnosis only: a compositor synchronization point, not a physical scan-out timestamp.
            if (SelectorTimeline.Mark is { } mark)
            {
                int result = NativeSelectorInterop.DwmFlush();
                mark(result >= 0 ? "native-dwm-flush-complete" : "native-dwm-flush-failed");
            }
            Activate();
        }
        catch { Close(); throw; }
        finally { _showing = false; }
        // This is the first input dispatcher turn after background submission/activation.
        // Chrome construction must not delay either that turn or the background's first paint.
        _startup = new NativeSelectorStartup(_dispatcher, () =>
        {
            if (_closed || !IsVisible) return;
            PreparePointerFeedback();
            IsReady = true;
            SelectorTimeline.Mark?.Invoke("native-input-ready");
            RefreshHover();
        }, ShowChrome);
    }

    internal void ChromeReady()
    {
        if (_closed) return;
        _chromeReady = true;
        SelectorTimeline.Mark?.Invoke("native-chrome-ready");
    }

    private void ShowChrome()
    {
        if (_closed || !IsVisible) return;
        _showing = true;
        try
        {
            _ = ReadEffectivePointer();
            PrepareVisualFeedback();
            var chrome = _chrome ?? CreateChrome();
            if (_closed || !IsVisible) { chrome.Dispose(); return; }
            _chrome = chrome;
            CaptureExcluded &= chrome.AllCaptureExcluded;
            // A drag can already be in progress while the optional chrome is being created.
            chrome.Update(ReadCursor(), _selection);
            chrome.Show();
            if (!_closed) SelectorTimeline.Mark?.Invoke("native-chrome-show-returned");
        }
        catch (Exception error)
        {
            try { if (!_closed) Failed?.Invoke(error); }
            finally { Close(); }
        }
        finally { _showing = false; }
    }

    private NativeSelectorChrome CreateChrome()
    {
        SelectorTimeline.Mark?.Invoke("native-chrome-create-start");
        NativeSelectorChrome chrome = ChromeStrategy == NativeSelectorChromeStrategy.Retained
            ? NativeSelectorChromeCache.Acquire(this, _showMagnifier, RequireCaptureExclusion)
            : new NativeSelectorChrome(this, _showMagnifier, RequireCaptureExclusion);
        SelectorTimeline.Mark?.Invoke("native-chrome-create-complete");
        return chrome;
    }

    internal void SetChromeStamps(IReadOnlyList<NativeSelectorStamp> stamps)
    {
        if (_closed || _dib is null) return;
        foreach (Int32Rect dirty in _dib.SetStamps(stamps))
        {
            var rectangle = new NativeSelectorInterop.Rect(dirty.X, dirty.Y, dirty.Width, dirty.Height);
            _ = NativeSelectorInterop.InvalidateRect(Handle, ref rectangle, false);
        }
    }

    internal void FlushChromeStamps()
    {
        if (!_closed && IsVisible && ChromeStrategy == NativeSelectorChromeStrategy.Dib)
        {
            _ = NativeSelectorInterop.UpdateWindow(Handle);
            SelectorTimeline.Mark?.Invoke("native-static-chrome-submitted");
        }
    }

    internal void SetAnchorChromeStamp(NativeSelectorStamp? stamp)
    {
        if (_closed || _dib is null) return;
        foreach (Int32Rect dirty in _dib.SetAnchorStamp(stamp))
        {
            var rectangle = new NativeSelectorInterop.Rect(dirty.X, dirty.Y, dirty.Width, dirty.Height);
            _ = NativeSelectorInterop.InvalidateRect(Handle, ref rectangle, false);
        }
    }

    internal bool Activate()
    {
        if (!IsVisible || _closed) return false;
        bool activated = NativeSelectorInterop.SetForegroundWindow(Handle);
        _ = NativeSelectorInterop.SetFocus(Handle);
        return activated;
    }

    internal void BeginSelection(PointD bitmapPoint)
    {
        _dispatcher.VerifyAccess();
        if (_closed || Frame is null || _copyingColor) return;
        if (Mode == CaptureSelectorMode.Color) { _ = CopyColorAtPointerAsync(); return; }
        ResetRefitCycle();
        _anchor = Clamp(bitmapPoint);
        RefreshHover();
        _selectedWindow = null;
        _dragBeyondClick = false;
        _dragging = true;
        SetSelection(Int32Rect.Empty);
    }

    internal void MoveSelection(PointD bitmapPoint)
    {
        _dispatcher.VerifyAccess();
        if (_closed || !_dragging) return;
        if (_hoverRegion.HasValue && !_dragBeyondClick)
        {
            if (IsWindowClick(Clamp(bitmapPoint))) return;
            _dragBeyondClick = true;
            ClearHover();
        }
        ResetRefitCycle();
        SetSelection((CaptureOverlayView.ResolvePresetSelection(_anchor, PresetSize, FrameBounds)
            ?? SelectionRatios.FitDrag(_anchor, Clamp(bitmapPoint), FrameBounds, ActiveRatio, RecordingSelection)).ToPixelBounds());
    }

    internal void EndSelection(PointD bitmapPoint)
    {
        if (_closed || !_dragging) return;
        MoveSelection(bitmapPoint);
        _dragging = false;
        UpdateVisualFeedback();
        SetAnchorChromeStamp(null);
        _chrome?.Update(ReadCursor(), _selection);
        if (_hoverRegion is { } hover && !_dragBeyondClick && IsWindowClick(Clamp(bitmapPoint)))
        {
            _selectedWindow = _hoverWindow;
            bool monitor = _hoverWholeMonitor;
            ClearHover();
            SetSelection(hover);
            CompleteSelection(hover, monitor);
            return;
        }
        if ((CaptureOverlayView.ResolvePresetSelection(_anchor, PresetSize, FrameBounds)
            ?? CaptureOverlayView.ResolveCompletedDrag(_anchor, Clamp(bitmapPoint), FrameBounds, ActiveRatio, RecordingSelection)) is { Width: >= 2, Height: >= 2 } region)
            CompleteSelection(region);
    }

    private PointD Clamp(PointD point) => new(Math.Clamp(point.X, 0, _bounds.Width), Math.Clamp(point.Y, 0, _bounds.Height));

    private void SetSelection(Int32Rect region)
    {
        _selection = region;
        UpdateReveal();
    }

    private void UpdateReveal()
    {
        if (_dib is null) return;
        UpdateShownHover();
        Int32Rect region = RevealedRegion;
        foreach (Int32Rect dirty in _dib.SetReveal(region))
        {
            var rectangle = new NativeSelectorInterop.Rect(dirty.X, dirty.Y, dirty.Width, dirty.Height);
            _ = NativeSelectorInterop.InvalidateRect(Handle, ref rectangle, false);
        }
        _chrome?.Update(EffectivePointer, region);
        UpdateVisualFeedback();
    }

    internal void ConfirmCurrent()
    {
        if (_selection.Width >= 2 && _selection.Height >= 2)
            CompleteSelection(new RectD(_selection.X, _selection.Y, _selection.Width, _selection.Height));
    }

    internal void CompleteMonitor()
    {
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(ReadMonitors(), ReadCursor(), _bounds);
        CompleteSelection(area.Bounds, wholeMonitor: true);
    }

    internal void CompleteSelection(RectD region, bool wholeMonitor = false)
    {
        _dispatcher.VerifyAccess();
        if (_closed || _completing || _copyingColor || Frame is not { } frame) return;
        region = region.ClampTo(new RectD(0, 0, frame.PixelWidth, frame.PixelHeight)).ToPixelBounds();
        if (region.IsEmpty) return;
        _completing = true;
        try
        {
            var crop = ScreenCaptureEngine.Crop(frame, region);
            var screen = new RectD(_bounds.Left + region.Left, _bounds.Top + region.Top, region.Width, region.Height);
            IReadOnlyList<WindowCandidate> windows = _sourceDetails is { IsCompletedSuccessfully: true } complete ? complete.Result : SourceWindows;
            CaptureSelectionSource source = CaptureOverlayWindow.ResolveSelectionSource(windows, screen, _selectedWindow, wholeMonitor);
            Task<CaptureSelectionSource>? pending = !TextCaptureMode && _sourceDetails is { IsCompleted: false } task
                ? ResolvePending(task, screen, _selectedWindow, wholeMonitor) : null;
            Hide();
            SelectionCompleted?.Invoke(this, new CaptureSelectionCompletedEventArgs(frame, region, crop, source.Title,
                sourcePageUrl: source.PageUrl) { SourceDetails = source.Details, PendingSource = pending, TextCapture = TextCaptureMode });
        }
        finally { Close(); }
    }

    private static async Task<CaptureSelectionSource> ResolvePending(Task<IReadOnlyList<WindowCandidate>> pending, RectD screen, WindowCandidate? detected, bool monitor)
    {
        try { return CaptureOverlayWindow.ResolveSelectionSource(await pending.ConfigureAwait(false), screen, detected, monitor); }
        catch (Exception error) when (error is not OutOfMemoryException) { return CaptureSelectionSource.Empty; }
    }

    internal void CheckFocusLater()
    {
        if (!_abortOnFocusLoss || _closed || _showing || !IsVisible) return;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closed || _showing || !IsVisible) return;
            IntPtr foreground = NativeSelectorInterop.GetForegroundWindow();
            if (foreground != Handle && NativeSelectorInterop.GetAncestor(foreground, 3) != Handle) Close();
        }));
    }

    private void Hide()
    {
        IsVisible = false;
        _chrome?.Hide();
        if (Handle != IntPtr.Zero) _ = NativeSelectorInterop.ShowWindow(Handle, 0);
    }

    public void Dispose() => Close();

    internal void Close()
    {
        _dispatcher.VerifyAccess();
        if (_closed) return;
        _closed = true;
        _precisionPointer?.Reset();
        _startup?.Dispose();
        _startup = null;
        _toastTimer?.Stop();
        _toastTimer = null;
        StopHoverFrames();
        _dragging = false;
        IsReady = false;
        if (NativeSelectorInterop.GetCapture() == Handle && Handle != IntPtr.Zero) _ = NativeSelectorInterop.ReleaseCapture();
        try { Hide(); }
        finally
        {
            try
            {
                if (_chrome is { } chrome)
                {
                    if (ChromeStrategy == NativeSelectorChromeStrategy.Retained && !_displayChanged)
                        NativeSelectorChromeCache.Release(chrome);
                    else chrome.Dispose();
                }
            }
            finally
            {
                _chrome = null;
                if (Handle != IntPtr.Zero)
                {
                    IntPtr handle = Handle;
                    _ = NativeSelectorInterop.DestroyWindow(handle);
                    Owners.TryRemove(handle, out _);
                    Handle = IntPtr.Zero;
                }
                try { _dibPreparation?.GetAwaiter().GetResult(); }
                finally
                {
                    _dibPreparation = null;
                    _dib?.Dispose();
                    _dib = null;
                    _visualFeedback?.Dispose();
                    _visualFeedback = null;
                    if (_borderPen != IntPtr.Zero) _ = NativeSelectorInterop.DeleteObject(_borderPen);
                    _ = NativeSelectorInterop.GdiFlush();
                    _borderPen = IntPtr.Zero;
                    Frame = null;
                    SourceWindows = [];
                    _sourceDetails = null;
                    _selectedWindow = null;
                    ClearHover();
                    DetachModeBar();
                    CopyColorText = null;
                    ColorCopied = null;
                    SelectorTimeline.Mark?.Invoke("native-resources-released");
                    Closed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private static ushort RegisterWindowClass()
    {
        var info = new NativeSelectorInterop.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeSelectorInterop.WindowClass>(), Procedure = Procedure,
            Instance = NativeSelectorInterop.GetModuleHandle(null), ClassName = ClassName,
            Cursor = NativeSelectorInterop.LoadCursor(IntPtr.Zero, new IntPtr(32515)),
        };
        ushort atom = NativeSelectorInterop.RegisterClassEx(ref info);
        if (atom == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return atom;
    }

    private static IntPtr DispatchWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Owners.TryGetValue(hwnd, out NativeCaptureOverlay? owner)) return NativeSelectorInterop.DefWindowProc(hwnd, message, wParam, lParam);
        try { return owner.WindowMessage(hwnd, message, wParam, lParam); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never unwind managed exceptions through user32. The coordinator receives the failure.
            _ = owner._dispatcher.BeginInvoke(new Action(() => { try { owner.Failed?.Invoke(error); } finally { owner.Close(); } }));
            return IntPtr.Zero;
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case 0x0113 when (nuint)wParam == HoverTimerId: // WM_TIMER: retained hover feedback.
                OnHoverFrame();
                return IntPtr.Zero;
            case 0x0014: return new IntPtr(1); // WM_ERASEBKGND: every invalid pixel is copied in WM_PAINT.
            case 0x000F:
                Paint(hwnd);
                return IntPtr.Zero;
            case 0x0201: // WM_LBUTTONDOWN
                BeginSelection(Pointer());
                _ = NativeSelectorInterop.SetCapture(hwnd);
                return IntPtr.Zero;
            case 0x0200:
                PointD pointer = Pointer();
                if (_dragging) MoveSelection(pointer);
                else RefreshHover();
                return IntPtr.Zero;
            case 0x0204: // WM_RBUTTONDOWN
                Close();
                return IntPtr.Zero;
            case 0x0202:
                EndSelection(Pointer());
                if (NativeSelectorInterop.GetCapture() == hwnd) _ = NativeSelectorInterop.ReleaseCapture();
                return IntPtr.Zero;
            case 0x0215: // WM_CAPTURECHANGED: an interrupted drag must not complete later.
                if (_dragging)
                {
                    _dragging = false;
                    ResetRefitCycle();
                    ClearHover();
                    SetSelection(Int32Rect.Empty);
                }
                return IntPtr.Zero;
            case 0x0100:
                if (wParam.ToInt32() is 0x10 or 0xA0 or 0xA1) { _ = Pointer(); _chrome?.Update(EffectivePointer, _selection); }
                if (HandleKey(System.Windows.Input.KeyInterop.KeyFromVirtualKey(wParam.ToInt32()), System.Windows.Input.Keyboard.Modifiers))
                    return IntPtr.Zero;
                break;
            case 0x0101:
                if (wParam.ToInt32() is 0x10 or 0xA0 or 0xA1) { _ = Pointer(); _chrome?.Update(EffectivePointer, _selection); }
                break;
            case 0x0006:
                if ((wParam.ToInt64() & 0xffff) == 0) CheckFocusLater();
                break;
            case 0x0010: // WM_CLOSE
                Close();
                return IntPtr.Zero;
            case 0x007E: // display layout changed; never return a crop against stale geometry.
            case 0x02E0:
                _displayChanged = true;
                NativeSelectorChromeCache.Trim();
                Close();
                return IntPtr.Zero;
            case 0x0082:
                Owners.TryRemove(hwnd, out _);
                Handle = IntPtr.Zero;
                if (!_closed) Close();
                break;
        }
        return NativeSelectorInterop.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private PointD Pointer()
    {
        return ReadEffectivePointer();
    }

    private void Paint(IntPtr hwnd)
    {
        IntPtr dc = NativeSelectorInterop.BeginPaint(hwnd, out NativeSelectorInterop.Paint paint);
        try
        {
            if (_closed || _dib is null || dc == IntPtr.Zero) return;
            NativeSelectorInterop.Rect r = paint.Rectangle;
            if (!_submitted) SelectorTimeline.Mark?.Invoke("native-first-paint-start");
            PaintTo(dc, r);
            if (!_submitted && r.Left <= 0 && r.Top <= 0 && r.Right >= _dib.Width && r.Bottom >= _dib.Height)
            {
                _submitted = true;
                SelectorTimeline.Mark?.Invoke("native-first-full-frame-submitted");
            }
        }
        finally { _ = NativeSelectorInterop.EndPaint(hwnd, ref paint); }
    }

    internal void PaintTo(IntPtr dc) => PaintTo(dc, new NativeSelectorInterop.Rect(0, 0, (int)_bounds.Width, (int)_bounds.Height));

    internal void PaintTo(IntPtr dc, NativeSelectorInterop.Rect r)
    {
        if (_closed || _dib is null || dc == IntPtr.Zero) return;
        if (!NativeSelectorInterop.BitBlt(dc, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            _dib.Dc, r.Left, r.Top, 0x00CC0020)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Int32Rect revealed = RevealedRegion;
        if (revealed.Width > 0 && revealed.Height > 0)
        {
            IntPtr oldPen = NativeSelectorInterop.SelectObject(dc, _borderPen);
            IntPtr oldBrush = NativeSelectorInterop.SelectObject(dc, NativeSelectorInterop.GetStockObject(5));
            _ = NativeSelectorInterop.Rectangle(dc, revealed.X, revealed.Y, revealed.X + revealed.Width, revealed.Y + revealed.Height);
            _ = NativeSelectorInterop.SelectObject(dc, oldBrush);
            _ = NativeSelectorInterop.SelectObject(dc, oldPen);
        }
        _visualFeedback?.Paint(dc);
        if (!NativeSelectorInterop.GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
