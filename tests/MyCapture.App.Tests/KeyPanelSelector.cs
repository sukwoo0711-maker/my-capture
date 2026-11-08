using System.Reflection;
using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;

namespace MyCapture.App.Tests;

/// <summary>The same deferred-guide contract, exercised through either selector renderer.</summary>
internal sealed class KeyPanelSelector : IDisposable
{
    private readonly CaptureOverlayView? _wpf;
    private readonly NativeCaptureOverlay? _native;

    internal KeyPanelSelector(bool native, bool magnifier = false)
    {
        var frame = CaptureCueEvidenceTests.Desktop();
        if (native)
        {
            _native = new NativeCaptureOverlay(frame.ScreenBounds, false, magnifier)
            {
                ReadCursor = () => new PointD(300, 300),
                ReadMonitors = () => CaptureCueEvidenceTests.Monitors,
                DetectWindows = true,
                SourceWindows = [CaptureCueEvidenceTests.Browser, CaptureCueEvidenceTests.Notes,
                    CaptureCueEvidenceTests.Taskbar, CaptureCueEvidenceTests.Shell],
                CopyColorText = _ => Task.FromResult(true),
                TextCaptureAvailable = true,
                OffersOtherActions = true,
            };
            _native.AttachFrame(frame);
        }
        else _wpf = CaptureCueEvidenceTests.EvidenceView(frame, AppTheme.Glass, "window-hover");
    }

    internal NativeCaptureOverlay? Native => _native;
    internal FrameworkElement? KeyPanelForTest => _native is { } native ? native.KeyPanelForTest : _wpf!.KeyPanelForTest;
    internal Rect? KeyPanelBoundsForTest => _native is { } native ? native.KeyPanelBoundsForTest : _wpf!.KeyPanelBoundsForTest;
    internal CaptureStartGuideCard? StartGuideCardForTest => _native is { } native ? native.StartGuideCardForTest : _wpf!.StartGuideCardForTest;
    internal bool FirstFramePresentedForTest => _native?.FirstFramePresentedForTest ?? _wpf!.FirstFramePresentedForTest;
    internal CaptureModeBar? ModeBarForTest => _native?.ModeBar ?? _wpf!.ModeBarForTest;
    internal IEnumerable<CaptureKeyPanel> Panels => _native is { } native ? native.KeyPanelsForTest
        : LogicalTreeHelper.GetChildren(_wpf!).OfType<CaptureKeyPanel>();

    internal bool ShowKeyPanel
    {
        get => _native?.ShowKeyPanel ?? _wpf!.ShowKeyPanel;
        set { if (_native is { } native) native.ShowKeyPanel = value; else _wpf!.ShowKeyPanel = value; }
    }
    internal bool ShowStartGuide
    {
        get => _native?.ShowStartGuide ?? _wpf!.ShowStartGuide;
        set { if (_native is { } native) native.ShowStartGuide = value; else _wpf!.ShowStartGuide = value; }
    }
    internal bool RecordingSelection
    {
        set { if (_native is { } native) native.RecordingSelection = value; else _wpf!.RecordingSelection = value; }
    }
    internal bool DetectWindows
    {
        set { if (_native is { } native) native.DetectWindows = value; else _wpf!.DetectWindows = value; }
    }
    internal bool TextCaptureAvailable
    {
        set { if (_native is { } native) native.TextCaptureAvailable = value; else _wpf!.TextCaptureAvailable = value; }
    }
    internal bool TextCaptureMode
    {
        set { if (_native is { } native) native.TextCaptureMode = value; else _wpf!.TextCaptureMode = value; }
    }
    internal bool PinSelectionAvailable
    {
        set { if (_native is { } native) native.PinSelectionAvailable = value; else _wpf!.PinSelectionAvailable = value; }
    }
    internal Hotkey PasteToScreenHotkey
    {
        set { if (_native is { } native) native.PasteToScreenHotkey = value; else _wpf!.PasteToScreenHotkey = value; }
    }
    internal Func<bool, bool>? ClaimKeyPanel
    {
        set { if (_native is { } native) native.ClaimKeyPanel = value; else _wpf!.ClaimKeyPanel = value; }
    }
    internal bool StartGuideClaimedForKeyPanel
    {
        set { if (_native is { } native) native.StartGuideClaimedForKeyPanel = value; else _wpf!.StartGuideClaimedForKeyPanel = value; }
    }
    internal Func<Hotkey>? ReadPasteToScreenHotkey
    {
        set { if (_native is { } native) native.ReadPasteToScreenHotkey = value; else _wpf!.ReadPasteToScreenHotkey = value; }
    }
    internal Func<string, Task<bool>>? CopyColorText
    {
        set { if (_native is { } native) native.CopyColorText = value; else _wpf!.CopyColorText = value; }
    }
    internal System.Drawing.Size? PresetSize
    {
        set { if (_native is { } native) native.PresetSize = value; else _wpf!.PresetSize = value; }
    }
    internal Func<PointD> ReadCursor
    {
        get => _native?.ReadCursor ?? _wpf!.ReadCursor;
        set { if (_native is { } native) native.ReadCursor = value; else _wpf!.ReadCursor = value; }
    }
    internal Func<IReadOnlyList<MonitorInfo>> ReadMonitors
    {
        set { if (_native is { } native) native.ReadMonitors = value; else _wpf!.ReadMonitors = value; }
    }
    internal bool SimulatedPointerInside
    {
        set { if (_wpf is { } wpf) wpf.SimulatedPointerInside = value; }
    }
    internal void NotifyFirstFramePresented()
    {
        if (_native is { } native) native.NotifyFirstFramePresented(); else _wpf!.NotifyFirstFramePresented();
    }
    internal void DismissStartGuide()
    {
        if (_native is { } native) native.DismissStartGuide(); else _wpf!.DismissStartGuide();
    }
    internal void HandleKeyDown(KeyEventArgs key, ModifierKeys modifiers)
    {
        if (_native is { } native) key.Handled = native.HandleKey(key.Key, modifiers); else _wpf!.HandleKeyDown(key, modifiers);
    }
    internal void SelectMode(CaptureSelectorMode mode)
    {
        if (_native is { } native) native.SelectMode(mode); else _wpf!.SelectMode(mode);
    }
    internal void UpdatePointer(bool force, bool track)
    {
        if (_native is { } native)
            typeof(NativeCaptureOverlay).GetMethod("RefreshHover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(native, null);
        else _wpf!.UpdatePointer(force, track);
    }
    internal void SimulatePointerDown()
    {
        if (_native is { } native) native.BeginSelection(ReadCursor()); else _wpf!.SimulatePointerDown();
    }
    internal void SimulatePointerMove()
    {
        if (_native is { } native) native.MoveSelection(ReadCursor()); else _wpf!.SimulatePointerMove();
    }
    internal void Render(double scale = 1)
    {
        if (_wpf is { } wpf) CaptureCueEvidenceTests.Render(wpf, scale);
        else
        {
            if (scale != 1)
            {
                MonitorInfo[] monitors = _native!.ReadMonitors()
                    .Select(monitor => monitor with { Dpi = (uint)Math.Round(96 * scale) }).ToArray();
                _native.ReadMonitors = () => monitors;
            }
            _native!.UpdateDeferredGuides();
        }
    }
    internal void ReleaseResources() { _native?.Close(); _wpf?.ReleaseResources(); }
    public void Dispose() => ReleaseResources();
}
