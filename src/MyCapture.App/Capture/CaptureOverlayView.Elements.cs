using System.Windows.Input;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

internal sealed partial class CaptureOverlayView
{
    private ElementSelectionController? _elements;
    private bool _drawnElementHint, _drawnElement;
    internal Func<IElementGeometrySource>? CreateElementSource { get; set; }
    internal bool ElementPending => _elements?.Pending == true;
    internal bool HoverElement => _elements?.HasElement == true;
    internal int ElementDepth => _elements?.Depth ?? 0;
    internal bool ElementWheelHint => !_hoverWholeMonitor && _hoverWindow is { ProcessId: > 0 } window
        && window.ProcessId != Environment.ProcessId && (_elements?.ShowHint ?? true);

    private RectD? TrackElements(WindowCandidate? window, RectD? root, PointD point)
    {
        if (_elements is null) return root;
        if (window is null || root is not { } rect) { _elements.Reset(); return root; }
        _elements.Track(window, new RectD(rect.X + _screenBounds.X, rect.Y + _screenBounds.Y, rect.Width, rect.Height), _screenBounds, point,
            MonitorWindowPlacement.MonitorFor(_monitors ??= ReadMonitors(), window.ScreenBounds)?.ScaleFactor ?? 1);
        RectD highlight = _elements.Highlight;
        return new RectD(highlight.X - _screenBounds.X, highlight.Y - _screenBounds.Y, highlight.Width, highlight.Height);
    }
    private bool PrepareElements()
    {
        if (!_firstFramePresented || _ended || _released || _interaction != InteractionMode.None || _selection is not null || _copyingColor) return false;
        UpdatePointer();
        if (_pointerOverBar || _hoverWindow is null || _hoverRegion is null || _hoverWholeMonitor
            || _hoverWindow.ProcessId == 0 || _hoverWindow.ProcessId == Environment.ProcessId) return false;
        if (_elements is null)
        {
            _elements = new ElementSelectionController(() =>
            {
                if (_ended || _released || _interaction != InteractionMode.None || _selection is not null) return;
                RefreshHover(ReadCursor());
                _shownHover = _glideTarget = _hoverRegion; _gliding = false;
                QueueFeedback();
            }, Dispatcher, CreateElementSource);
            _hoverRegion = TrackElements(_hoverWindow, _hoverRegion, ReadCursor());
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
        DismissStartGuide(); return _elements!.Wheel(delta);
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!e.Handled && _modeBar?.Holds(e.OriginalSource as System.Windows.DependencyObject) != true)
            e.Handled = HandleElementWheel(e.Delta);
    }
    private void ConfirmHoveredOrSelected()
    {
        if (_selection is null && _interaction == InteractionMode.None)
        {
            UpdatePointer();
            if (_hoverRegion is { } rect)
            {
                _elements?.Stop();
                _selection = rect; _selectedWindow = _hoverWindow;
                ConfirmSelection(_hoverWholeMonitor); return;
            }
        }
        ConfirmSelection();
    }
}
