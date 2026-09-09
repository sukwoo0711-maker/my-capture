using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>Physical-pixel motion accumulator. The caller places the cursor at each output.</summary>
internal sealed class CapturePrecisionPointer
{
    internal const double Scale = 0.2;
    private PointD _previous;
    private PointD _remainder;
    private bool _initialized;
    internal bool IsPrecision { get; private set; }

    internal void Reset()
    {
        _initialized = false;
        IsPrecision = false;
        _remainder = default;
    }

    internal PointD Update(PointD actual, bool precision, RectD bounds)
    {
        if (!_initialized || precision != IsPrecision || !precision)
        {
            _initialized = true;
            IsPrecision = precision;
            _remainder = default;
            return _previous = actual;
        }

        double dx = _remainder.X + (actual.X - _previous.X) * Scale;
        double dy = _remainder.Y + (actual.Y - _previous.Y) * Scale;
        double wholeX = Math.Truncate(dx + Math.Sign(dx) * 1e-9);
        double wholeY = Math.Truncate(dy + Math.Sign(dy) * 1e-9);
        double x = Math.Clamp(_previous.X + wholeX, bounds.Left, bounds.Right - 1);
        double y = Math.Clamp(_previous.Y + wholeY, bounds.Top, bounds.Bottom - 1);
        _remainder = new PointD(x == _previous.X + wholeX ? dx - wholeX : 0,
            y == _previous.Y + wholeY ? dy - wholeY : 0);
        // A SetCursorPos-generated event has actual == previous, so it adds no motion.
        return _previous = new PointD(x, y);
    }
}
