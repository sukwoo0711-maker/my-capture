using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private string? _freeformUnavailableReason;
    private List<PointD>? _freeformPoints;
    private RectD _freeformBounds;
    private DispatcherTimer? _freeformFeedbackTimer;
    private bool _freeformFeedbackPending;

    internal string? FreeformUnavailableReason
    {
        get => _freeformUnavailableReason;
        set
        {
            if (_freeformUnavailableReason == value) return;
            _freeformUnavailableReason = value;
            if (!FreeformAvailable && Mode == CaptureSelectorMode.Freeform) SelectMode(CaptureSelectorMode.Capture);
            RefreshModeBar();
        }
    }

    internal bool FreeformAvailable => _freeformUnavailableReason is null && !RecordingSelection;
    internal bool FreeformStrokeActive => Mode == CaptureSelectorMode.Freeform && _dragging;
    internal IReadOnlyList<PointD> FreeformPoints => _freeformPoints is { } points ? points : Array.Empty<PointD>();

    private string? ResolvedFreeformUnavailableReason => _freeformUnavailableReason
        ?? (RecordingSelection ? UiText.Get("CaptureFreeform_Recording") : null);

    private void BeginFreeform(PointD point)
    {
        _freeformPoints ??= new List<PointD>(256);
        _freeformPoints.Clear();
        _freeformPoints.Add(point);
        _freeformBounds = new RectD(point.X, point.Y, 0, 0);
        _anchor = point;
        _selectedWindow = null;
        ClearHover();
        StopHoverFrames();
        _shownHover = _glideTarget = null;
        _dragging = true;
        SetSelection(Int32Rect.Empty);
        _dib?.BeginFreeform();
        _freeformFeedbackTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            OnFreeformFeedback, _dispatcher);
        _freeformFeedbackTimer.Start();
        _freeformFeedbackPending = true;
    }

    private void AppendFreeform(PointD point)
    {
        if (_freeformPoints is not { Count: > 0 } points || points[^1] == point) return;
        SelectorTimeline.Mark?.Invoke("freeform-pointer-move");
        PointD previous = points[^1];
        _dib?.AppendFreeformTriangle(points[0], previous, point, Handle);
        points.Add(point);
        double left = Math.Min(_freeformBounds.Left, point.X), top = Math.Min(_freeformBounds.Top, point.Y);
        double right = Math.Max(_freeformBounds.Right, point.X), bottom = Math.Max(_freeformBounds.Bottom, point.Y);
        _freeformBounds = new RectD(left, top, right - left, bottom - top);
        RectD bounds = _freeformBounds.ToPixelBounds();
        _selection = new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height);
        InvalidateFreeformStroke(previous, point);
        _freeformFeedbackPending = true;
    }

    private void InvalidateFreeformStroke(PointD previous, PointD next)
    {
        if (Handle == IntPtr.Zero) return;
        int left = (int)Math.Floor(Math.Min(previous.X, next.X)) - 2;
        int top = (int)Math.Floor(Math.Min(previous.Y, next.Y)) - 2;
        int right = (int)Math.Ceiling(Math.Max(previous.X, next.X)) + 3;
        int bottom = (int)Math.Ceiling(Math.Max(previous.Y, next.Y)) + 3;
        var rectangle = new NativeSelectorInterop.Rect(left, top, right - left, bottom - top);
        _ = NativeSelectorInterop.InvalidateRect(Handle, ref rectangle, false);
    }

    private void OnFreeformFeedback(object? sender, EventArgs args)
    {
        if (_closed || !_freeformFeedbackPending) return;
        _freeformFeedbackPending = false;
        // Stroke pixels are already invalidated synchronously. Reuse the existing bounded
        // magnifier/readout feedback on a render tick instead of rebuilding it on every move.
        _chrome?.Update(EffectivePointer, _selection);
        UpdateVisualFeedback();
    }

    private void CompleteFreeform(bool pinRequested = false)
    {
        if (!FreeformStrokeActive || _freeformPoints is not { } points) return;
        FreeformMask? mask = FreeformMask.Create(points, FrameBounds);
        if (mask is null)
        {
            DiscardFreeformStroke();
            return;
        }
        _dragging = false;
        _freeformFeedbackTimer?.Stop();
        _freeformFeedbackPending = false;
        CompleteSelection(mask.Bounds, pinRequested: pinRequested, freeform: mask);
    }

    internal bool DiscardFreeformStroke()
    {
        if (!FreeformStrokeActive) return false;
        _dragging = false;
        _freeformFeedbackTimer?.Stop();
        _freeformFeedbackPending = false;
        int left = Math.Max(0, (int)Math.Floor(_freeformBounds.Left) - 2);
        int top = Math.Max(0, (int)Math.Floor(_freeformBounds.Top) - 2);
        int right = Math.Min((int)_bounds.Width, (int)Math.Ceiling(_freeformBounds.Right) + 3);
        int bottom = Math.Min((int)_bounds.Height, (int)Math.Ceiling(_freeformBounds.Bottom) + 3);
        var dirty = new Int32Rect(left, top, right - left, bottom - top);
        _dib?.EndFreeform(dirty);
        _freeformPoints?.Clear();
        _freeformBounds = RectD.Empty;
        _selection = Int32Rect.Empty;
        var rectangle = new NativeSelectorInterop.Rect(dirty.X, dirty.Y, dirty.Width, dirty.Height);
        if (Handle != IntPtr.Zero) _ = NativeSelectorInterop.InvalidateRect(Handle, ref rectangle, false);
        if (Handle != IntPtr.Zero && NativeSelectorInterop.GetCapture() == Handle) _ = NativeSelectorInterop.ReleaseCapture();
        _chrome?.Update(EffectivePointer, _selection);
        UpdateVisualFeedback();
        return true;
    }

    private void PaintFreeform(IntPtr dc)
    {
        if (_freeformPoints is not { Count: > 1 } points) return;
        IntPtr oldPen = NativeSelectorInterop.SelectObject(dc, _borderPen);
        _ = NativeSelectorInterop.MoveToEx(dc, (int)Math.Round(points[0].X), (int)Math.Round(points[0].Y), IntPtr.Zero);
        for (int index = 1; index < points.Count; index++)
            _ = NativeSelectorInterop.LineTo(dc, (int)Math.Round(points[index].X), (int)Math.Round(points[index].Y));
        _ = NativeSelectorInterop.SelectObject(dc, oldPen);
        SelectorTimeline.Mark?.Invoke("freeform-stroke-submitted");
    }
}
