using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private RectD? _shownHover, _glideTarget;
    private RectD _glideFrom;
    private TimeSpan _glideStart;
    private bool _gliding;
    private const nuint HoverTimerId = 0x5a01;
    private bool _hoverFeedbackPrepared, _hoverFramePending;
    private IEasingFunction? _hoverEasing;
    private Func<TimeSpan>? _readClock;

    internal Func<TimeSpan> ReadClock { get => _readClock ??= ReadHoverClock; set => _readClock = value; }
    internal RectD? ShownHoverForTest => _shownHover;
    internal bool HoverGlidingForTest => _gliding;
    internal bool HasPendingFeedback => _hoverFramePending;
    internal Int32Rect DisplayedRegionForTest => RevealedRegion;
    internal bool HoverWholeMonitor => _hoverWholeMonitor;
    internal void FlushFeedbackForTest() => UpdateReveal();

    private static TimeSpan ReadHoverClock() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    private void PrepareHoverFeedback()
    {
        if (_hoverFeedbackPrepared) return;
        _readClock ??= ReadHoverClock;
        _hoverEasing = FluidMotion.StandardEasing;
        _hoverFeedbackPrepared = true;
    }

    private void OnHoverFrame()
    {
        if (_closed) { StopHoverFrames(); return; }
        UpdateReveal();
    }

    private void StopHoverFrames()
    {
        if (!_hoverFramePending) return;
        _hoverFramePending = false;
        if (Handle != IntPtr.Zero) _ = NativeSelectorInterop.KillTimer(Handle, HoverTimerId);
    }

    private void UpdateShownHover()
    {
        RectD? target = Selection is null ? _hoverRegion : null;
        if (target is not { } to)
        {
            _shownHover = _glideTarget = null;
            _gliding = false;
            StopHoverFrames();
            return;
        }
        // Startup creates the timer after submitting the desktop. Earlier messages can only snap.
        TimeSpan now = _readClock?.Invoke() ?? TimeSpan.Zero;
        if (_glideTarget != to)
        {
            RectD? from = _gliding ? null : _shownHover;
            _glideTarget = to;
            _gliding = _hoverFeedbackPrepared && from is { } start && CanGlide(start, to);
            if (_gliding) { _glideFrom = from!.Value; _glideStart = now; }
        }
        if (!_gliding) { _shownHover = to; StopHoverFrames(); return; }
        double progress = (now - _glideStart) / CaptureOverlayView.HoverGlideLength;
        if (progress >= 1)
        {
            _shownHover = to;
            _gliding = false;
            StopHoverFrames();
            return;
        }
        double eased = _hoverEasing?.Ease(Math.Max(0, progress)) ?? Math.Max(0, progress);
        double left = Math.Round(_glideFrom.Left + (to.Left - _glideFrom.Left) * eased);
        double top = Math.Round(_glideFrom.Top + (to.Top - _glideFrom.Top) * eased);
        double right = Math.Round(_glideFrom.Right + (to.Right - _glideFrom.Right) * eased);
        double bottom = Math.Round(_glideFrom.Bottom + (to.Bottom - _glideFrom.Bottom) * eased);
        _shownHover = new RectD(left, top, right - left, bottom - top);
        if (!_hoverFramePending)
        {
            _hoverFramePending = NativeSelectorInterop.SetTimer(Handle, HoverTimerId, 8, IntPtr.Zero) != 0;
            if (!_hoverFramePending) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    private bool CanGlide(RectD from, RectD to)
    {
        if (!FluidMotion.AnimationsEnabled) return false;
        double shift = Math.Max(Math.Max(Math.Abs(to.Left - from.Left), Math.Abs(to.Top - from.Top)),
            Math.Max(Math.Abs(to.Right - from.Right), Math.Abs(to.Bottom - from.Bottom)));
        double quarter = _bounds.Width * _bounds.Height / 4;
        return shift >= CaptureOverlayView.HoverGlideMinimumShift
            && from.Width * from.Height <= quarter && to.Width * to.Height <= quarter;
    }
}
