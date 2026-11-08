using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private bool _prepared, _copyingColor;
    private RectD? _refitBase, _refitResult;
    private CaptureModeBar? _modeBar;
    private DispatcherTimer? _toastTimer;
    private System.Drawing.Size? _presetSize;
    private bool _textCaptureMode;
    private CaptureSelectorMode _mode;
    private bool _detectWindows, _windowSelected, _dragBeyondClick, _hoverWholeMonitor;
    private WindowCandidate? _hoverWindow, _selectedWindow;
    private RectD? _hoverRegion;

    internal Func<PointD> ReadCursor { get; set; } = CursorLocator.GetPosition;
    internal Func<IReadOnlyList<MonitorInfo>> ReadMonitors { get; set; } = MonitorEnumerator.GetAll;
    internal Func<string, Task<bool>>? CopyColorText { get; set; }
    internal Func<ColorFormat> ReadColorFormat { get; set; } = () => ColorFormat.Hex;
    internal Action<ColorCopyFeedback>? ColorCopied { get; set; }
    internal bool TextCaptureAvailable { get; set; }
    internal bool TextCaptureMode
    {
        get => _textCaptureMode;
        set { if (_closed) return; _textCaptureMode = value; _mode = value ? CaptureSelectorMode.Text : CaptureSelectorMode.Capture; RefreshModeBar(); RefreshHover(); }
    }
    internal bool RecordingSelection { get; set; }
    internal bool OffersOtherActions { get; set; }
    internal Func<bool> CanRepeatLastRegion { get; set; } = () => true;
    internal event EventHandler<CaptureSelectorRequest>? ActionRequested;
    internal bool DetectWindows
    {
        get => _detectWindows;
        set { _detectWindows = value; _windowSelected = value; RefreshModeBar(); }
    }
    internal RectD? HoverRegion => _hoverRegion;
    internal System.Drawing.Size? PresetSize
    {
        get => _presetSize;
        set { _presetSize = value; RefreshModeBar(); }
    }
    internal CaptureSelectorMode Mode => _mode;
    internal SelectionRatio Ratio { get; private set; }
    internal RectD? Selection => _selection.Width > 0 && _selection.Height > 0
        ? new RectD(_selection.X, _selection.Y, _selection.Width, _selection.Height) : null;
    private RectD FrameBounds => new(0, 0, _bounds.Width, _bounds.Height);
    private bool RatioAvailable => Mode != CaptureSelectorMode.Color && PresetSize is null;
    internal string Readout => CaptureOverlayView.FormatReadout(_selection.Width, _selection.Height, Ratio);
    internal string? ToastMessage { get; private set; }
    internal TimeSpan? ToastInterval => _toastTimer?.IsEnabled == true ? _toastTimer.Interval : null;

    // The reusable chrome binds its bar only while a session owns it.
    internal CaptureModeBar ModeBar
    {
        get
        {
            if (_modeBar is null) AttachModeBar(new CaptureModeBar());
            return _modeBar!;
        }
    }

    internal void AttachModeBar(CaptureModeBar bar)
    {
        DetachModeBar();
        _modeBar = bar;
        bar.ApplyPalette(_palette);
        bar.CancelRequested += Close;
        bar.FullScreenRequested += CompleteMonitor;
        bar.ModeRequested += OnBarMode;
        bar.WindowRequested += OnBarWindow;
        bar.ActionRequested += RequestAction;
        bar.RatioRequested += OnBarRatio;
        bar.FixedSizeRequested += OnBarFixedSize;
        RefreshModeBar();
    }

    internal void DetachModeBar()
    {
        if (_modeBar is not { } bar) return;
        bar.CancelRequested -= Close;
        bar.FullScreenRequested -= CompleteMonitor;
        bar.ModeRequested -= OnBarMode;
        bar.WindowRequested -= OnBarWindow;
        bar.ActionRequested -= RequestAction;
        bar.RatioRequested -= OnBarRatio;
        bar.FixedSizeRequested -= OnBarFixedSize;
        _modeBar = null;
    }

    private void OnBarMode(CaptureSelectorMode mode)
    {
        if (mode == CaptureSelectorMode.Capture) _windowSelected = false;
        SelectMode(mode);
        RefreshHover();
        Activate();
    }

    private void OnBarWindow()
    {
        if (_closed || !DetectWindows || RecordingSelection) return;
        _windowSelected = true;
        ResetRefitCycle();
        _selectedWindow = null;
        SetSelection(Int32Rect.Empty);
        SelectMode(CaptureSelectorMode.Capture);
        RefreshHover();
        Activate();
    }

    private void OnBarRatio(SelectionRatio ratio) { SelectRatio(ratio); Activate(); }

    private void OnBarFixedSize(FixedSelectionSize size)
    {
        if (PlaceFixedSize(size))
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (!_closed) Activate(); }));
    }

    private void RefreshModeBar()
    {
        _modeBar?.Configure(detectWindows: DetectWindows, textAvailable: TextCaptureAvailable, colorAvailable: CopyColorText is not null,
            otherActions: OffersOtherActions, canRepeatLastRegion: CanRepeatLastRegion(), recording: RecordingSelection, ratioAvailable: PresetSize is null);
        _modeBar?.ShowMode(Mode, _windowSelected);
        _modeBar?.ShowRatio(Ratio);
    }

    internal void SelectMode(CaptureSelectorMode mode)
    {
        if (_closed || (mode == CaptureSelectorMode.Text && !TextCaptureAvailable)
            || (mode == CaptureSelectorMode.Color && CopyColorText is null)) return;
        _mode = mode;
        _textCaptureMode = mode == CaptureSelectorMode.Text;
        ClearHover();
        RefreshModeBar();
        RefreshHover();
    }

    internal void SelectRatio(SelectionRatio ratio)
    {
        if (_closed || !RatioAvailable) return;
        Ratio = ratio;
        _selectedWindow = null;
        if (Selection is { } current)
        {
            if (_dragging) SetSelection(SelectionRatios.FitDrag(_anchor, Clamp(Pointer()), FrameBounds, ratio, RecordingSelection));
            else
            {
                RectD source = _refitBase is { } origin && _refitResult == current ? origin : current;
                RectD refit = ratio == SelectionRatio.Free ? source : SelectionRatios.Refit(source, FrameBounds, ratio, RecordingSelection);
                _refitBase = source;
                _refitResult = refit;
                SetSelection(refit);
            }
        }
        RefreshModeBar();
        ShowToast(UiText.Format("CaptureRatio_Changed", CaptureModeBar.RatioName(ratio)), TimeSpan.FromSeconds(1));
    }

    private void ResetRefitCycle() { _refitBase = null; _refitResult = null; }

    internal bool PlaceFixedSize(FixedSelectionSize size)
    {
        if (_closed || _copyingColor || !RatioAvailable) return false;
        CaptureGuideArea monitor = CaptureModeBarLayout.ChooseArea(ReadMonitors(), ReadCursor(), _bounds);
        if (SelectionRatios.PlaceFixed(size, monitor.Bounds, FrameBounds, RecordingSelection) is not { } placed) return false;
        ResetRefitCycle();
        _selectedWindow = null;
        ClearHover();
        SetSelection(placed);
        ShowToast(UiText.Get("CaptureRatio_FixedHint"), TimeSpan.FromSeconds(6));
        return true;
    }

    private void SetSelection(RectD rectangle)
    {
        RectD pixels = rectangle.ClampTo(FrameBounds).ToPixelBounds();
        SetSelection(new Int32Rect((int)pixels.X, (int)pixels.Y, (int)pixels.Width, (int)pixels.Height));
    }

    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (_closed) return false;
        switch (key)
        {
            case Key.Escape: Close(); return true;
            case Key.Enter: ConfirmCurrent(); return true;
            case Key.A when modifiers.HasFlag(ModifierKeys.Control):
                ResetRefitCycle(); _selectedWindow = null; ClearHover(); SetSelection(FrameBounds); return true;
            case Key.R when RatioAvailable && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                SelectRatio(SelectionRatios.Next(Ratio)); return true;
            case Key.T when TextCaptureAvailable && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                TextCaptureMode = !TextCaptureMode; return true;
            case Key.C when CopyColorText is not null && modifiers is ModifierKeys.None or ModifierKeys.Shift:
                _ = CopyColorAtPointerAsync(); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                NudgeSelection(key, modifiers); return true;
            default: return false;
        }
    }

    internal void NudgeSelection(Key key, ModifierKeys modifiers)
    {
        if (_closed || Selection is not { } selection) return;
        double step = modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
        bool resize = modifiers.HasFlag(ModifierKeys.Shift);
        ResetRefitCycle();
        _selectedWindow = null;
        if (resize && Ratio != SelectionRatio.Free)
            SetSelection(SelectionRatios.Resize(selection, key is Key.Left or Key.Right,
                key is Key.Right or Key.Down ? 1 : -1, step, FrameBounds, Ratio, RecordingSelection));
        else if (resize)
        {
            double right = selection.Right, bottom = selection.Bottom;
            switch (key)
            {
                case Key.Left: right = Math.Max(selection.Left + 1, right - step); break;
                case Key.Right: right = Math.Min(FrameBounds.Right, right + step); break;
                case Key.Up: bottom = Math.Max(selection.Top + 1, bottom - step); break;
                case Key.Down: bottom = Math.Min(FrameBounds.Bottom, bottom + step); break;
            }
            SetSelection(new RectD(selection.Left, selection.Top, right - selection.Left, bottom - selection.Top));
        }
        else
        {
            double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            SetSelection(new RectD(selection.Left + dx, selection.Top + dy, selection.Width, selection.Height));
        }
    }

    internal async Task<string?> CopyColorAtPointerAsync()
    {
        if (_closed || _completing || _copyingColor || CopyColorText is not { } copy || Frame is not { } frame) return null;
        PointD pointer = Pointer();
        var rect = new Int32Rect(Math.Clamp((int)Math.Floor(pointer.X), 0, frame.PixelWidth - 1),
            Math.Clamp((int)Math.Floor(pointer.Y), 0, frame.PixelHeight - 1), 1, 1);
        byte[] sample = new byte[4];
        BitmapSource source = frame.Bitmap;
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32)
            source.CopyPixels(rect, sample, 4, 0);
        else
            new FormatConvertedBitmap(new CroppedBitmap(source, rect), PixelFormats.Bgra32, null, 0).CopyPixels(sample, 4, 0);
        byte blue = sample[0], green = sample[1], red = sample[2];
        string value = ColorSampleFormatter.Format(red, green, blue, ReadColorFormat());
        _copyingColor = true;
        bool copied;
        try { copied = await copy(value).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { copied = false; }
        void Complete()
        {
            _copyingColor = false;
            if (_closed || _completing) return;
            if (!copied) { ShowToast(UiText.Get("ColorPick_CopyFailed"), TimeSpan.FromSeconds(6)); return; }
            Action<ColorCopyFeedback>? notify = ColorCopied;
            Close();
            notify?.Invoke(new ColorCopyFeedback(value, red, green, blue));
        }
        if (_dispatcher.CheckAccess()) Complete();
        else await _dispatcher.InvokeAsync(Complete);
        return copied ? value : null;
    }

    private void ShowToast(string message, TimeSpan duration)
    {
        ToastMessage = message;
        _toastTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _toastTimer.Stop();
        _toastTimer.Tick -= ClearToast;
        _toastTimer.Tick += ClearToast;
        _toastTimer.Interval = duration;
        _toastTimer.Start();
        _chrome?.Update(ReadCursor(), _selection);
    }

    private void ClearToast(object? sender, EventArgs args)
    {
        _toastTimer?.Stop();
        ToastMessage = null;
        _chrome?.Update(ReadCursor(), _selection);
    }

    private Int32Rect RevealedRegion => Selection is not null ? _selection : _hoverRegion is { } hover
        ? new Int32Rect((int)hover.Left, (int)hover.Top, (int)hover.Width, (int)hover.Height) : Int32Rect.Empty;

    internal void RefreshHover()
    {
        if (_closed || _dragging) return;
        PointD pointer = ReadCursor();
        _hoverWindow = null;
        _hoverRegion = null;
        _hoverWholeMonitor = false;
        if (DetectWindows && _windowSelected && !RecordingSelection && Mode != CaptureSelectorMode.Color && Selection is null)
        {
            _hoverWindow = WindowDetectHitTest.FindTopmost(SourceWindows, pointer, Handle);
            if (_hoverWindow is { } window)
                _hoverRegion = WindowDetectHitTest.ResolveRegion(window, SourceWindows, pointer, _bounds, ReadMonitors, out _hoverWholeMonitor);
        }
        UpdateReveal();
    }

    private void ClearHover() { _hoverWindow = null; _hoverRegion = null; _hoverWholeMonitor = false; }

    private bool IsWindowClick(PointD point)
    {
        CaptureGuideArea area = CaptureModeBarLayout.ChooseArea(ReadMonitors(), ReadCursor(), _bounds);
        return WindowDetectHitTest.IsClick(_anchor, point, SystemParameters.MinimumHorizontalDragDistance * area.Scale,
            SystemParameters.MinimumVerticalDragDistance * area.Scale);
    }

    private void RequestAction(CaptureSelectorRequest request)
    {
        if (_closed || _copyingColor || !OffersOtherActions || (RecordingSelection && request.Action != CaptureSelectorAction.Capture)) return;
        try { ActionRequested?.Invoke(this, request); }
        finally { Close(); }
    }
}
