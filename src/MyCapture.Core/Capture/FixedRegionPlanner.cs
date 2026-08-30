using MyCapture.Core.Primitives;

namespace MyCapture.Core.Capture;

/// <summary>
/// Places a fixed-size capture rectangle relative to the cursor and clamps it fully inside a
/// monitor, in pure managed code.
/// </summary>
/// <remarks>
/// <para>
/// Fixed-size capture (알캡처's "고정 크기 캡처": presets plus a directly typed size) needs a
/// deterministic answer to "given a requested WxH and where the pointer is, which screen
/// rectangle do we grab?". That answer must be identical whether it is driven from a hotkey,
/// a tray preset, or a unit test, so the maths lives here in Core with no display, no WPF and
/// no interop — the App layer only supplies the cursor position and the monitor bounds it
/// already has.
/// </para>
/// <para>
/// The rectangle is centred on the cursor because that matches the user's mental model of
/// "capture a WxH box around what I'm pointing at", then nudged wholly inside the monitor so
/// a box requested near an edge still yields the full requested size rather than being cropped
/// by the frame. When the monitor is smaller than the request in either axis, that axis is
/// reduced to the monitor — a fixed size can never exceed the screen it is taken from.
/// </para>
/// </remarks>
public static class FixedRegionPlanner
{
    /// <summary>
    /// The screen rectangle, in virtual-desktop physical pixels, for a
    /// <paramref name="width"/> × <paramref name="height"/> capture centred on
    /// <paramref name="cursor"/> and clamped inside <paramref name="monitorBounds"/>.
    /// </summary>
    /// <param name="width">Requested width in physical pixels; values below one are invalid.</param>
    /// <param name="height">Requested height in physical pixels; values below one are invalid.</param>
    /// <param name="cursor">Cursor position in virtual-desktop physical pixels.</param>
    /// <param name="monitorBounds">
    /// The monitor the capture is taken from, in virtual-desktop physical pixels.
    /// </param>
    /// <returns>
    /// The clamped placement, or <see langword="null"/> when the request is not a positive
    /// size or the monitor has no area.
    /// </returns>
    public static RectD? PlaceAtCursor(int width, int height, PointD cursor, RectD monitorBounds)
    {
        if (width < 1 || height < 1)
        {
            return null;
        }

        RectD monitor = monitorBounds.Normalized();
        if (monitor.IsEmpty)
        {
            return null;
        }

        // A fixed size can never exceed the monitor it is taken from.
        double w = Math.Min(width, monitor.Width);
        double h = Math.Min(height, monitor.Height);

        // Centre on the cursor, then slide wholly inside the monitor so an edge request keeps
        // its full size instead of being cropped.
        double left = cursor.X - (w / 2.0);
        double top = cursor.Y - (h / 2.0);

        left = Math.Clamp(left, monitor.Left, monitor.Right - w);
        top = Math.Clamp(top, monitor.Top, monitor.Bottom - h);

        return new RectD(left, top, w, h);
    }
}
