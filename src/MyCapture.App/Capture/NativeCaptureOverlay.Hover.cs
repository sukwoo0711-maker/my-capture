using System.Diagnostics;
using System.Windows;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private RectD? _shownHover;
    private readonly HoverGlide _hoverGlide = new();
    private const nuint HoverTimerId = 0x5a01;
    private bool _hoverFeedbackPrepared, _hoverFramePending;
    private Func<TimeSpan>? _readClock;

    internal Func<TimeSpan> ReadClock { get => _readClock ??= ReadHoverClock; set => _readClock = value; }
    internal RectD? ShownHoverForTest => _shownHover;
    internal bool HoverGlidingForTest => _hoverGlide.IsGliding;
    internal bool HasPendingFeedback => _hoverFramePending;
    internal Int32Rect DisplayedRegionForTest => RevealedRegion;
    internal bool HoverWholeMonitor => _hoverWholeMonitor;
    internal void FlushFeedbackForTest() => UpdateReveal();

    private static TimeSpan ReadHoverClock() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

    private void PrepareHoverFeedback()
    {
        if (_hoverFeedbackPrepared) return;
        _readClock ??= ReadHoverClock;
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

    /// <summary>
    /// Moves the drawn hover frame one step of its <see cref="HoverGlide"/> and keeps the frame timer
    /// running until it lands. Only the reveal's edge strips are repainted per frame, so windows of
    /// any size glide. A drag selection follows the pointer and never glides.
    /// </summary>
    private void UpdateShownHover()
    {
        RectD? target = Selection is null ? _hoverRegion : null;
        // Startup creates the timer after submitting the desktop. Earlier messages can only snap.
        TimeSpan now = target is null ? TimeSpan.Zero : _readClock?.Invoke() ?? TimeSpan.Zero;
        _shownHover = _hoverGlide.Update(target, now, _hoverFeedbackPrepared && FluidMotion.AnimationsEnabled);
        if (!_hoverGlide.IsGliding)
        {
            StopHoverFrames();
            return;
        }
        if (!_hoverFramePending)
        {
            _hoverFramePending = NativeSelectorInterop.SetTimer(Handle, HoverTimerId, 8, IntPtr.Zero) != 0;
            if (!_hoverFramePending) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }
}
