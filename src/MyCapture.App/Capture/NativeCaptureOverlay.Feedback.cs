using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private NativePointerFeedback? _visualFeedback;
    private MyCapture.Core.Primitives.RectD? _feedbackModeBarBounds;
    internal bool ShowSelectionGrid { get; set; } = true;
    internal IReadOnlyDictionary<string, Brush> GuidePalette => _palette;
    internal Int32Rect? HorizontalGuideBoundsForTest => _visualFeedback?.Horizontal;
    internal Int32Rect? VerticalGuideBoundsForTest => _visualFeedback?.Vertical;
    internal IReadOnlyList<IntPtr> VisualFeedbackHandlesForTest => _visualFeedback?.Handles ?? [];

    internal void PrepareVisualFeedback()
    {
        if (_closed || Frame is null || _visualFeedback is not null) return;
        _visualFeedback = new NativePointerFeedback(this, _palette, ReadMonitors());
        UpdateVisualFeedback();
    }

    internal void SetModeBarFeedbackBounds(MyCapture.Core.Primitives.RectD? bounds)
    {
        _feedbackModeBarBounds = bounds;
        UpdateVisualFeedback();
    }

    internal void UpdateVisualFeedback()
    {
        if (_closed || _visualFeedback is null) return;
        var pointer = _pointerInitialized ? _effectivePointer.Offset(-_bounds.Left, -_bounds.Top) : (MyCapture.Core.Primitives.PointD?)null;
        bool overBar = _pointerInitialized && _feedbackModeBarBounds?.Contains(_effectivePointer) == true;
        _visualFeedback.Update(pointer, SelectionAnchor, ShowSelectionGrid && !_dragging && Selection is null, overBar);
    }
}
