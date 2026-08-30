namespace MyCapture.Core.Pin;

/// <summary>
/// A single pin's physical-pixel window bounds plus whether it is currently hidden,
/// enough to decide which pin the cursor is over without any WPF dependency.
/// </summary>
public readonly record struct PinBounds(int Left, int Top, int Right, int Bottom, bool IsHidden)
{
    public bool Contains(int x, int y) =>
        !IsHidden && x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Pure selection logic for "which pin is under the cursor", extracted so it is testable
/// without live windows.
/// </summary>
/// <remarks>
/// Ordinary hit testing via <c>WindowFromPoint</c> cannot answer this: a click-through pin
/// carries <c>WS_EX_TRANSPARENT</c> and the OS skips it. Testing the cursor against each
/// pin's own bounds — and picking the last (top-most) match — is what lets a global command
/// still reach a pin that has hidden itself from hit testing.
/// </remarks>
public static class PinHitTesting
{
    /// <summary>
    /// Index of the top-most pin whose bounds contain the point, or -1. Pins later in the
    /// list are treated as sitting above earlier ones (most recently opened on top).
    /// </summary>
    public static int TopmostIndexAt(IReadOnlyList<PinBounds> pins, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(pins);

        for (int i = pins.Count - 1; i >= 0; i--)
        {
            if (pins[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }
}
