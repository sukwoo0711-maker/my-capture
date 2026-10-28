using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

// Only primitive state and framework-typed callback slots live on the selector. The deferred
// controller and its control types are absent from every field and property signature here.
internal sealed partial class CaptureOverlayView
{
    private bool _firstFramePresented;
    private bool _keyPanelRequested;
    private bool? _keyPanelUserOverride;
    private bool _pinSelectionAvailable;
    private Hotkey? _pasteToScreenHotkey;
    private Rect? _dimensionLabelBounds;
    private Action? _keyPanelUpdate;
    private Action? _keyPanelPaletteChanged;
    private Action? _keyPanelRelease;
    private Func<FrameworkElement?>? _keyPanelElement;
    private List<FrameworkElement>? _deferredGuideChildren;

    internal Func<bool, bool>? ClaimKeyPanel { get; set; }
    internal bool StartGuideClaimedForKeyPanel { get; set; }
    internal Func<Hotkey>? ReadPasteToScreenHotkey { get; set; }
    internal FrameworkElement? KeyPanelForTest => _keyPanelElement?.Invoke();
    internal Rect? KeyPanelBoundsForTest { get; set; }
    internal bool FirstFramePresentedForTest => _firstFramePresented;
    internal bool ShowKeyPanel
    {
        get => _keyPanelRequested;
        set
        {
            _keyPanelRequested = value;
            if (_firstFramePresented && value && _keyPanelUpdate is null) CreateKeyPanelAfterFrame();
            else _keyPanelUpdate?.Invoke();
        }
    }

    internal bool PinSelectionAvailable
    {
        get => _pinSelectionAvailable;
        set { _pinSelectionAvailable = value; _keyPanelUpdate?.Invoke(); }
    }

    internal Hotkey PasteToScreenHotkey
    {
        get => _pasteToScreenHotkey ?? Hotkey.None;
        set { _pasteToScreenHotkey = value; _keyPanelUpdate?.Invoke(); }
    }

    // Invoked only by the callback queued by Window.ContentRendered, or the equivalent test seam.
    internal void NotifyFirstFramePresented()
    {
        if (_released || _ended || _firstFramePresented) return;
        _firstFramePresented = true;
        if (RecordingSelection) return;
        bool automatic = ClaimKeyPanel?.Invoke(StartGuideClaimedForKeyPanel) == true;
        ClaimKeyPanel = null;
        _keyPanelRequested = _keyPanelUserOverride ?? (_keyPanelRequested || automatic);
        if (_keyPanelRequested) CreateKeyPanelAfterFrame();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateKeyPanelAfterFrame()
    {
        if (!_firstFramePresented || _released || _ended || RecordingSelection || _keyPanelUpdate is not null) return;
        if (ReadPasteToScreenHotkey is { } read) _pasteToScreenHotkey = read();
        CaptureKeyPanelController.Attach(this);
    }

    private void ToggleKeyPanel()
    {
        _keyPanelRequested = !_keyPanelRequested || _startGuideShown;
        _keyPanelUserOverride = _keyPanelRequested;
        DismissStartGuide();
        if (_startGuide is { } card)
        {
            card.BeginAnimation(OpacityProperty, null);
            card.Visibility = Visibility.Collapsed;
        }
        if (_firstFramePresented && _keyPanelRequested && _keyPanelUpdate is null) CreateKeyPanelAfterFrame();
        else _keyPanelUpdate?.Invoke();
    }

    // Framework-only accessors are read exclusively by the deferred controller after presentation.
    internal bool DeferredGuideSuppressed => _released || _ended || RecordingSelection
        || _startGuide is { Visibility: Visibility.Visible };
    internal bool DeferredGuideHasFrame => _frame is not null;
    internal bool DeferredGuideCanDrag => !_colorPickMode;
    internal bool DeferredGuideRatioAvailable => RatioAvailable;
    internal bool DeferredGuideWindowDetection => UsesWindowDetection && !_colorPickMode;
    internal bool DeferredGuideHasSelection => _selection is { Width: >= MinimumSelectionPixels, Height: >= MinimumSelectionPixels };
    internal FontFamily DeferredGuideUiFont => _uiTypeface.FontFamily;
    internal FontFamily DeferredGuideMonoFont => _monoTypeface.FontFamily;
    internal RectD DeferredGuideScreenBounds => _screenBounds;
    internal PointD DeferredGuidePointer => _cursorPixel;
    internal IReadOnlyList<MonitorInfo> DeferredGuideMonitors => _monitors ??= ReadMonitors();
    internal double DeferredGuideDipPerPixelX => DipPerPixelX;
    internal double DeferredGuideDipPerPixelY => DipPerPixelY;
    internal Rect? DeferredGuideHighlight => (_selection ?? _shownHover) is { } highlighted ? ToDipRect(highlighted) : null;
    internal Rect? DeferredGuideHoverTab => _targetTab;
    internal Rect DeferredGuideMagnifier => _magnifierVisual.ContentBounds;
    internal Rect? DeferredGuideReadout => _dimensionLabelBounds ?? _targetTab;

    internal void RegisterDeferredGuide(Action update, Action palette, Action release, Func<FrameworkElement?> element)
    {
        _keyPanelUpdate = update;
        _keyPanelPaletteChanged = palette;
        _keyPanelRelease = release;
        _keyPanelElement = element;
    }

    internal void AddDeferredGuide(FrameworkElement element)
    {
        (_deferredGuideChildren ??= []).Add(element);
        AddGuideChild(element);
    }

    internal void RemoveDeferredGuide(FrameworkElement element)
    {
        _deferredGuideChildren?.Remove(element);
        RemoveGuideChild(element);
    }
}
