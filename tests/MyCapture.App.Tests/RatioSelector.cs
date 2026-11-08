using System.Reflection;
using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Tests;

/// <summary>Runs the existing ratio contract, including its bar assertions, through either renderer.</summary>
internal sealed class RatioSelector
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;
    private readonly Func<PointD> _pointer;
    private readonly RectD _bounds;
    private Window? _host;
    internal RatioSelector(bool native, FrozenFrame frame, Func<PointD> pointer, bool recording,
        IReadOnlyList<MonitorInfo> monitors)
    {
        _pointer = pointer;
        _bounds = frame.ScreenBounds;
        if (native)
        {
            _native = new NativeCaptureOverlay(frame.ScreenBounds, false, false)
            {
                ReadCursor = pointer, ReadMonitors = () => monitors, RecordingSelection = recording,
            };
            _native.AttachFrame(frame);
            _native.SelectionCompleted += (_, e) => SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(e.BitmapRegion));
            _native.Closed += (_, e) => CancelRequested?.Invoke(this, e);
        }
        else
        {
            _wpf = new CaptureOverlayView(frame, showMagnifier: false)
            {
                ReadCursor = pointer, PlaceCursor = _ => false, ReadMonitors = () => monitors,
                RecordingSelection = recording, ShowModeBar = true,
            };
            CaptureCueEvidenceTests.UsePalette(_wpf, CaptureGuidePalette.Create(MyCapture.Core.Themes.AppTheme.Glass, null, false));
            _wpf.InitializePointer();
            _wpf.SelectionConfirmed += (_, e) => SelectionConfirmed?.Invoke(this, e);
            _wpf.CancelRequested += (_, e) => CancelRequested?.Invoke(this, e);
        }
    }
    internal event EventHandler<RegionSelectionEventArgs>? SelectionConfirmed;
    internal event EventHandler? CancelRequested;
    internal CaptureModeBar? ModeBarForTest => _native?.ModeBar ?? _wpf!.ModeBarForTest;
    internal SelectionRatio Ratio => _native?.Ratio ?? _wpf!.Ratio;
    internal RectD? Selection => _native is { } native ? native.Selection : _wpf!.Selection;
    internal string? ToastMessageForTest => _native is { } native ? native.ToastMessage : _wpf!.ToastMessageForTest;
    internal TimeSpan? ToastIntervalForTest => _native is { } native ? native.ToastInterval : _wpf!.ToastIntervalForTest;
    internal string? ReadoutForTest => _native?.Readout ?? _wpf!.ReadoutForTest;
    internal System.Drawing.Size? PresetSize
    {
        set { if (_native is { } native) native.PresetSize = value; else _wpf!.PresetSize = value; }
    }
    internal Func<string, Task<bool>>? CopyColorText
    {
        set { if (_native is { } native) native.CopyColorText = value; else _wpf!.CopyColorText = value; }
    }
    internal Task<string?> CopyColorAtPointerAsync() => _native?.CopyColorAtPointerAsync() ?? _wpf!.CopyColorAtPointerAsync();
    internal void Show()
    {
        if (_native is { } native) native.Show();
        else
        {
            _host = new Window
            {
                Content = _wpf, Width = 800, Height = 500,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            };
            _host.Show();
            _host.UpdateLayout();
        }
    }
    internal void RaiseEvent(KeyEventArgs key)
    {
        if (_native is { } native) key.Handled = native.HandleKey(key.Key, ModifierKeys.None);
        else _wpf!.RaiseEvent(key);
    }
    internal void HandleKeyDown(KeyEventArgs key, ModifierKeys modifiers)
    {
        if (_native is { } native) key.Handled = native.HandleKey(key.Key, modifiers);
        else _wpf!.HandleKeyDown(key, modifiers);
    }
    internal void SelectRatio(SelectionRatio ratio)
    {
        if (_native is { } native) native.SelectRatio(ratio); else _wpf!.SelectRatio(ratio);
    }
    internal void SelectMode(CaptureSelectorMode mode)
    {
        if (_native is { } native) native.SelectMode(mode); else _wpf!.SelectMode(mode);
    }
    internal bool PlaceFixedSize(FixedSelectionSize size) => _native?.PlaceFixedSize(size) ?? _wpf!.PlaceFixedSize(size);
    internal void NudgeSelection(Key key, ModifierKeys modifiers)
    {
        if (_native is { } native) native.NudgeSelection(key, modifiers);
        else WindowDetectOverlayTests.Invoke(_wpf!, "NudgeSelection", key, modifiers);
    }
    private PointD LocalPointer() => new(_pointer().X - _bounds.Left, _pointer().Y - _bounds.Top);
    internal void SimulatePointerDown()
    {
        if (_native is { } native) native.BeginSelection(LocalPointer()); else _wpf!.SimulatePointerDown();
    }
    internal void SimulatePointerMove()
    {
        if (_native is { } native) native.MoveSelection(LocalPointer()); else _wpf!.SimulatePointerMove();
    }
    internal void SimulatePointerUp()
    {
        if (_native is { } native) native.EndSelection(LocalPointer()); else _wpf!.SimulatePointerUp();
    }
    internal void Render(double scale)
    {
        if (_wpf is not null) CaptureCueEvidenceTests.Render(_wpf, scale);
        else
        {
            // The native readout uses physical selection coordinates; the bar is still a WPF control.
            var bar = _native!.ModeBar;
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bar.Arrange(new Rect(bar.DesiredSize));
        }
    }
    internal void SetSelection(RectD selection, bool refit)
    {
        if (_native is { } native)
        {
            typeof(NativeCaptureOverlay).GetMethod("SetSelection", BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(RectD)])!.Invoke(native, [selection]);
            if (!refit)
            {
                typeof(NativeCaptureOverlay).GetField("_refitBase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(native, null);
                typeof(NativeCaptureOverlay).GetField("_refitResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(native, null);
            }
        }
        else
        {
            typeof(CaptureOverlayView).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_wpf, (RectD?)selection);
            if (!refit)
            {
                typeof(CaptureOverlayView).GetField("_refitBase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_wpf, null);
                typeof(CaptureOverlayView).GetField("_refitResult", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_wpf, null);
            }
        }
    }
    internal void ReleaseResources()
    {
        if (_wpf?.ModeBarForTest?.RatioMenuForTest is { } menu) menu.IsOpen = false;
        _native?.Close();
        _wpf?.ReleaseResources();
        _host?.Close();
        _host = null;
    }
}
