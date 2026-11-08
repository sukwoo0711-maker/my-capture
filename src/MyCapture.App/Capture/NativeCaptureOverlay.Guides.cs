using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private bool _firstFramePresented, _keyPanelRequested, _startGuideShown, _pinSelectionAvailable;
    private bool? _keyPanelUserOverride;
    private Hotkey? _pasteToScreenHotkey;
    private Func<bool, bool>? _claimKeyPanel;
    private PointD? _startGuideOrigin;
    private DispatcherTimer? _startGuideTimer;
    private DispatcherOperation? _deferredGuideOperation;
    private bool _updatingDeferredGuides;
    private bool _dispatchingDeferredGuides;

    internal Func<bool, bool>? ClaimKeyPanel
    {
        get => _claimKeyPanel;
        set { StartGuideClaimedForKeyPanel = ShowStartGuide; _claimKeyPanel = value; }
    }
    internal bool StartGuideClaimedForKeyPanel { get; set; }
    internal Func<Hotkey>? ReadPasteToScreenHotkey { get; set; }
    internal bool FirstFramePresentedForTest => _firstFramePresented;
    internal FrameworkElement? KeyPanelForTest => _chrome?.KeyPanel;
    internal IEnumerable<CaptureKeyPanel> KeyPanelsForTest => _chrome?.KeyPanels ?? [];
    internal Rect? KeyPanelBoundsForTest => _chrome?.KeyPanelBounds;
    internal CaptureStartGuideCard? StartGuideCardForTest => _chrome?.StartGuideCard;
    internal bool StartGuideTimerRunningForTest => _startGuideTimer?.IsEnabled == true;
    internal bool ShowKeyPanel
    {
        get => _keyPanelRequested;
        set { _keyPanelRequested = value; UpdateDeferredGuides(); }
    }
    internal bool PinSelectionAvailable
    {
        get => _pinSelectionAvailable;
        set { _pinSelectionAvailable = value; UpdateDeferredGuides(); }
    }
    internal Hotkey PasteToScreenHotkey
    {
        get => _pasteToScreenHotkey ?? Hotkey.None;
        set { _pasteToScreenHotkey = value; UpdateDeferredGuides(); }
    }
    internal bool ShowStartGuide
    {
        get => _startGuideShown;
        set
        {
            if (_closed) return;
            if (!value) { DismissStartGuide(); return; }
            _startGuideShown = true;
            _startGuideOrigin = ReadCursor();
            if (_firstFramePresented) StartGuideTimer();
            UpdateDeferredGuides();
        }
    }

    // Scheduled only after the native background has submitted its first complete frame. Kept
    // separate from chrome readiness: neither counting nor constructing these guides gates input.
    private void QueueDeferredGuides()
    {
        _deferredGuideOperation = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _deferredGuideOperation = null;
            if (_closed || !IsVisible) return;
            try { NotifyFirstFramePresented(); }
            catch (Exception error) { try { Failed?.Invoke(error); } finally { Close(); } }
        }));
    }

    internal void NotifyFirstFramePresented()
    {
        if (_closed || _firstFramePresented) return;
        _firstFramePresented = true;
        if (ShowStartGuide) StartGuideTimer();
        if (!RecordingSelection && !CaptureOverlayView.SuppressKeyPanelForProbe)
        {
            bool automatic = _claimKeyPanel?.Invoke(StartGuideClaimedForKeyPanel) == true;
            _keyPanelRequested = _keyPanelUserOverride ?? (_keyPanelRequested || automatic);
        }
        _claimKeyPanel = null;
        UpdateDeferredGuides();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void UpdateDeferredGuides()
    {
        if (_closed || !_firstFramePresented || Frame is null || _updatingDeferredGuides) return;
        if (IsVisible && !_dispatchingDeferredGuides)
        {
            if (_deferredGuideOperation is null)
                _deferredGuideOperation = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _deferredGuideOperation = null;
                    _dispatchingDeferredGuides = true;
                    try { UpdateDeferredGuides(); }
                    catch (Exception error) { try { Failed?.Invoke(error); } finally { Close(); } }
                    finally { _dispatchingDeferredGuides = false; }
                }));
            return;
        }
        _updatingDeferredGuides = true;
        try
        {
            if (_chrome is null && (ShowStartGuide || (ShowKeyPanel && !RecordingSelection && !CaptureOverlayView.SuppressKeyPanelForProbe)))
                _chrome = CreateChrome();
            _chrome?.UpdateGuides();
            if (_chrome is { } chrome) CaptureExcluded &= chrome.AllCaptureExcluded;
        }
        finally { _updatingDeferredGuides = false; }
    }

    internal void PrepareKeyPanelContent()
    {
        if (ReadPasteToScreenHotkey is { } read)
        {
            _pasteToScreenHotkey = read();
            ReadPasteToScreenHotkey = null;
        }
    }

    private void ToggleKeyPanel()
    {
        _keyPanelRequested = !_keyPanelRequested || ShowStartGuide;
        _keyPanelUserOverride = _keyPanelRequested;
        DismissStartGuide();
        UpdateDeferredGuides();
    }

    internal void ToggleStartGuide()
    {
        if (_closed) return;
        if (!RecordingSelection) _keyPanelUserOverride = _keyPanelRequested = true;
        ShowStartGuide = !ShowStartGuide;
        ReturnFocusAfterPointer();
    }

    internal void DismissStartGuide()
    {
        if (!_startGuideShown) return;
        _startGuideShown = false;
        _startGuideOrigin = null;
        _startGuideTimer?.Stop();
        UpdateDeferredGuides();
    }

    private void StartGuideTimer()
    {
        if (_startGuideTimer is null)
        {
            _startGuideTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
                { Interval = CaptureOverlayView.StartGuideDuration };
            _startGuideTimer.Tick += (_, _) => DismissStartGuide();
        }
        _startGuideTimer.Stop();
        _startGuideTimer.Start();
    }

    internal void UpdateGuidePointer()
    {
        if (_startGuideOrigin is { } origin && origin.DistanceTo(ReadCursor())
            > CaptureOverlayView.StartGuideMoveDismissDip * Math.Max(ChromeArea.Scale, 0.01)) DismissStartGuide();
    }

    private void ReleaseDeferredGuides()
    {
        _deferredGuideOperation?.Abort();
        _deferredGuideOperation = null;
        _startGuideTimer?.Stop();
        _startGuideTimer = null;
        _claimKeyPanel = null;
        ReadPasteToScreenHotkey = null;
        _pasteToScreenHotkey = null;
        _keyPanelUserOverride = null;
        _startGuideOrigin = null;
        _firstFramePresented = _keyPanelRequested = _startGuideShown = _pinSelectionAvailable = false;
        StartGuideClaimedForKeyPanel = false;
    }
}
