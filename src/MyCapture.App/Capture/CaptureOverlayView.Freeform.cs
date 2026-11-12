using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

internal sealed partial class CaptureOverlayView
{
    private bool _freeformMode;
    private string? _freeformUnavailableReason;
    private List<PointD>? _freeformPoints;
    private CaptureFreeformPreview? _freeformPreview;
    private RectD? _freeformStrokeBounds;

    internal string? FreeformUnavailableReason
    {
        get => _freeformUnavailableReason;
        set
        {
            _freeformUnavailableReason = value;
            if (!FreeformAvailable && _freeformMode) LeaveFreeform();
            ConfigureGuides();
        }
    }

    private string? EffectiveFreeformUnavailableReason => _freeformUnavailableReason
        ?? (RecordingSelection ? UiText.Get("CaptureFreeform_Recording") : null);
    internal bool FreeformAvailable => EffectiveFreeformUnavailableReason is null;
    internal bool FreeformDrawing => _freeformMode && _freeformPoints is { Count: > 0 };
    internal CaptureFreeformPreview? FreeformPreviewForTest => _freeformPreview;
    internal RectD? FreeformStrokeBounds => _freeformStrokeBounds;

    private void EnterFreeform()
    {
        if (_freeformMode) return;
        _freeformMode = true;
        _textCaptureMode = _colorPickMode = false;
        _selection = null;
        _selectedWindow = null;
        _interaction = InteractionMode.None;
        ClearHover();
        _elements?.Stop();
        ResetRefitCycle();
        _freeformPoints ??= new List<PointD>(256);
        UpdateRevealedRegion(null);
        EnsureFreeformPreview();
        OnModeChanged();
    }

    private void LeaveFreeform()
    {
        DiscardFreeform();
        _freeformMode = false;
        UpdateRevealedRegion(null);
        OnModeChanged();
    }

    private void BeginFreeform(PointD point)
    {
        DiscardFreeform();
        PointD edge = ClampEdgePoint(point);
        _dragAnchor = edge;
        EnsureFreeformPreview();
        _freeformPoints!.Add(edge);
        _freeformStrokeBounds = new RectD(edge.X, edge.Y, 0, 0);
        _interaction = InteractionMode.Create;
        QueueFeedback();
    }

    private void AppendFreeformPoint(PointD point)
    {
        PointD edge = ClampEdgePoint(point);
        if (_freeformPoints is { Count: > 0 } points && points[^1] != edge)
        {
            _freeformPreview?.Append(points[0], points[^1], edge);
            points.Add(edge);
            RectD bounds = _freeformStrokeBounds ?? new RectD(edge.X, edge.Y, 0, 0);
            double left = Math.Min(bounds.Left, edge.X), top = Math.Min(bounds.Top, edge.Y);
            _freeformStrokeBounds = new RectD(left, top, Math.Max(bounds.Right, edge.X) - left,
                Math.Max(bounds.Bottom, edge.Y) - top);
        }
    }

    private void DrawFreeform(DrawingContext dc)
    {
        // Each fixed-size tile has its own retained image visual. This selection visual stays
        // empty: reopening it must never dirty the accumulated stroke's full bounding box.
        EnsureFreeformPreview();
        _freeformPreview?.UpdateScale(DipPerPixelX, DipPerPixelY);
    }

    private void EnsureFreeformPreview()
    {
        if (!_freeformMode || _freeformPreview is not null || _frame?.Bitmap is not { } bitmap
            || _selectionBorderPen is null || _innerGlowPen is null || DipPerPixelX <= 0 || DipPerPixelY <= 0) return;
        _freeformPreview = new CaptureFreeformPreview(bitmap, DipPerPixelX, DipPerPixelY,
            _selectionBorderPen, _innerGlowPen);
        _visuals.Insert(_visuals.IndexOf(_selectionVisual), _freeformPreview.Visual);
        // Input can precede frozen-frame acquisition. Replay those already recorded points once
        // when AttachFrame supplies the source; regular moves append directly to existing tiles.
        if (_freeformPoints is { } points)
            for (int index = 1; index < points.Count; index++)
                _freeformPreview.Append(points[0], points[index - 1], points[index]);
    }

    private void ReleaseFreeformPreview()
    {
        if (_freeformPreview is { } preview)
        {
            _visuals.Remove(preview.Visual);
            preview.Dispose();
        }
        _freeformPreview = null;
        _freeformPoints = null;
    }

    private void RebuildFreeformPreview()
    {
        if (_freeformPreview is not { } preview) return;
        _visuals.Remove(preview.Visual);
        preview.Dispose();
        _freeformPreview = null;
        // Palette/DPI changes are infrequent explicit rebuilds. Keep the physical points and
        // replay them against the newly resolved pens, without changing selection or input state.
        EnsureFreeformPreview();
    }

    internal void DiscardFreeform()
    {
        _interaction = InteractionMode.None;
        _freeformPoints?.Clear();
        _freeformStrokeBounds = null;
        _freeformPreview?.Clear();
        _selection = null;
        if (IsMouseCaptured) Mouse.Capture(null);
        _precisionPointer.Reset();
        QueueFeedback();
    }

    private void CompleteFreeform(bool pinRequested)
    {
        if (_ended || _copyingColor || _freeformPoints is not { Count: > 0 } points) return;
        FreeformMask? mask = FreeformMask.Create(points, FrameBounds);
        if (mask is null) { DiscardFreeform(); return; }
        EndSelection();
        EndPointerInteraction();
        SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(mask.Bounds)
        {
            FreeformMask = mask,
            PinRequested = pinRequested,
        });
    }

}
