using System.Windows.Input;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private CapturePrecisionPointer? _precisionPointer;
    private PointD _effectivePointer;
    private bool _pointerInitialized;
    private Func<PointD, bool>? _placeCursor;

    internal Func<PointD, bool> PlaceCursor
    {
        get => _placeCursor ??= CursorLocator.TrySetPosition;
        set => _placeCursor = value;
    }
    internal bool PrecisionActive => _precisionPointer?.IsPrecision == true;
    internal PointD EffectivePointer => _pointerInitialized ? _effectivePointer : ReadCursor();

    // The startup input turn calls this after the first desktop frame, before accepting input.
    internal void PreparePointerFeedback()
    {
        _precisionPointer ??= new CapturePrecisionPointer();
        _placeCursor ??= CursorLocator.TrySetPosition;
        PrepareHoverFeedback();
    }

    internal void RefreshPrecisionPointer() => _ = ReadEffectivePointer();

    internal PointD UpdatePointer(bool precision, bool active)
    {
        if (_closed) return Clamp(_effectivePointer.Offset(-_bounds.Left, -_bounds.Top));
        PreparePointerFeedback();
        PointD actual = ReadCursor();
        PointD target = _precisionPointer!.Update(actual, active && precision, _bounds);
        if (target != actual && (!active || !PlaceCursor(target)))
        {
            _precisionPointer.Reset();
            target = actual;
        }
        _effectivePointer = target;
        _pointerInitialized = true;
        return Clamp(target.Offset(-_bounds.Left, -_bounds.Top));
    }

    private PointD ReadEffectivePointer()
    {
        // Native messages can arrive while Show is submitting its first frame. The prepared input
        // turn owns initialization; those earlier messages keep their existing unmodified coordinates.
        if (_precisionPointer is null)
        {
            PointD actual = ReadCursor();
            return actual.Offset(-_bounds.Left, -_bounds.Top);
        }
        IntPtr foreground = NativeSelectorInterop.GetForegroundWindow();
        bool active = IsVisible && (foreground == Handle || NativeSelectorInterop.GetAncestor(foreground, 3) == Handle);
        return UpdatePointer((Keyboard.Modifiers & ModifierKeys.Shift) != 0, active);
    }
}
