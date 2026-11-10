using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private ElementSelectionController? _elements;
    private bool _snapElementHover;
    private WindowCandidate? _elementScaleWindow;
    private double _elementScale = 1;
    internal Func<IElementGeometrySource>? CreateElementSource { get; set; }
    internal bool ElementPending => _elements?.Pending == true;
    internal bool HoverElement => _elements?.HasElement == true;
    internal int ElementDepth => _elements?.Depth ?? 0;
    internal bool ElementWheelHint => !_hoverWholeMonitor && _hoverWindow is { ProcessId: > 0 } window
        && window.ProcessId != Environment.ProcessId && (_elements?.ShowHint ?? true);

    private void TrackElements(PointD pointer)
    {
        if (_elements is null) return;
        if (_hoverWindow is null || _hoverRegion is not { } root) { _elements.Reset(); return; }
        if (_elementScaleWindow != _hoverWindow)
        {
            _elementScaleWindow = _hoverWindow;
            _elementScale = MonitorWindowPlacement.MonitorFor(ReadMonitors(), _hoverWindow.ScreenBounds)?.ScaleFactor ?? 1;
        }
        _elements.Track(_hoverWindow, new RectD(root.X + _bounds.X, root.Y + _bounds.Y, root.Width, root.Height), _bounds, pointer, _elementScale);
        SetElementHover();
    }
    private void SetElementHover()
    {
        if (_elements is not { } elements || _hoverWindow is null) return;
        RectD rect = elements.Highlight;
        _hoverRegion = new RectD(rect.X - _bounds.X, rect.Y - _bounds.Y, rect.Width, rect.Height);
        if (elements.HasElement) _hoverWholeMonitor = false;
        // Element steps are exact: a click must match the visible rectangle, including during siblings.
        if (elements.Depth > 0 || _snapElementHover)
        { _shownHover = _glideTarget = _hoverRegion; _gliding = false; StopHoverFrames(); }
    }
    private bool PrepareElements()
    {
        if (_closed || !IsReady || _dragging || Selection is not null || _copyingColor) return false;
        RefreshHover();
        if (_hoverWindow is null || _hoverRegion is null || _hoverWholeMonitor
            || _hoverWindow.ProcessId == 0 || _hoverWindow.ProcessId == Environment.ProcessId) return false;
        if (_elements is null)
        {
            _elements = new ElementSelectionController(() =>
            {
                if (_closed || _dragging || Selection is not null) return;
                _snapElementHover = true;
                try { RefreshHover(); }
                finally { _snapElementHover = false; }
            }, _dispatcher, CreateElementSource);
            TrackElements(ReadCursor());
        }
        return _elements.CanStep;
    }
    internal bool StepElement(int direction)
    {
        if (!PrepareElements()) return false;
        _elements!.Step(direction); return true;
    }
    internal bool HandleElementWheel(int delta)
    {
        if (!PrepareElements()) return false;
        DismissStartGuide();
        return _elements!.Wheel(delta);
    }
}
