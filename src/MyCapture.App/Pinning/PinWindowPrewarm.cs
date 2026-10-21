using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// Opens one pin window, out of sight, while the resident app is idle after launch, and keeps it
/// hidden, so no F3 pays first-use costs: the first transparent WPF window, the pin window's code,
/// menu and outline, and its first layout and drawing.
/// </summary>
/// <remarks>
/// <para>
/// The window is kept because WPF releases the windows' rendering resources when the last one
/// closes: a pin opened when no other pin is open then rebuilds them, about 50 ms of its Show on
/// this PC. Kept hidden, the window holds them for the first F3 and for every pin that opens after
/// all others were closed, at about half a megabyte.
/// </para>
/// <para>
/// The window is never seen and never active: it is fully transparent, lies beyond the top-left
/// corner of the whole desktop, is not top-most, shows without activation and hides in the same
/// call. It is not registered with the pin manager, so no pin list, session, closed-pin history,
/// tip or clipboard sees it. One window per UI thread; a later call does nothing.
/// </para>
/// </remarks>
internal static class PinWindowPrewarm
{
    private const int ImageSize = 32;

    /// <summary>
    /// Distance past the desktop's top-left corner. Every monitor lies right of and below that
    /// corner, and DIP-to-pixel scaling (at least 1) only moves a negative position further out.
    /// </summary>
    private const double OutOfSightMargin = 256;

    [ThreadStatic]
    private static PinWindow? t_window;

    /// <summary>Test hook: the prewarm window right after it was shown.</summary>
    internal static Action<PinWindow>? ShownForTest { get; set; }

    /// <summary>Test hook: the hidden window this UI thread keeps.</summary>
    internal static PinWindow? WindowForTest => t_window;

    internal static void ResetForTest()
    {
        t_window?.Close();
        t_window = null;
    }

    internal static void Run()
    {
        if (t_window is { IsClosed: false }) return;
        BitmapSource bitmap = BitmapSource.Create(ImageSize, ImageSize, 96, 96, PixelFormats.Bgr32, null,
            new byte[ImageSize * ImageSize * 4], ImageSize * 4);
        bitmap.Freeze();
        // An image is never re-rasterised, so a display change leaves the hidden window idle.
        var state = new PinViewState(ImageSize, ImageSize, 1, 1, new PinSettings().ZoomStep);
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        var pin = new PinWindow(PinContent.FromImage(bitmap), state,
            Math.Min(0, desktop.Left) - OutOfSightMargin, Math.Min(0, desktop.Top) - OutOfSightMargin)
        {
            ShowActivated = false,
            Topmost = false,
            Opacity = 0,
        };
        try
        {
            pin.Show();
            ShownForTest?.Invoke(pin);
            pin.Hide();
            t_window = pin;
        }
        catch
        {
            pin.Close();
            throw;
        }
    }
}
