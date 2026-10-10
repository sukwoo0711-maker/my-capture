using System.Windows;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// The display the selector's mode bar and start guide belong to, in overlay physical pixels.
/// </summary>
/// <param name="Bounds">Whole monitor, as a desktop click or the full-screen button captures it.</param>
/// <param name="WorkArea">The monitor minus the taskbar: where the bar and the guide are placed.</param>
/// <param name="Scale">Display scale of that monitor, 1.5 at 150%.</param>
internal readonly record struct CaptureGuideArea(RectD Bounds, RectD WorkArea, double Scale);

/// <summary>
/// Pure placement for the selector's mode bar and start guide. The overlay spans the whole virtual
/// desktop, so centring on it would put the bar on a monitor seam or on a display the user is not
/// looking at; both are instead placed on the monitor under the pointer (or the primary one).
/// </summary>
internal static class CaptureModeBarLayout
{
    /// <summary>Gap between the work area's top edge and the bar, in DIPs before display scaling.</summary>
    internal const double TopMargin = 12;

    /// <summary>Smallest gap kept between the bar or the guide and the edge of their monitor.</summary>
    internal const double EdgeMargin = 8;

    /// <summary>
    /// Picks the monitor that shows the bar: the one under <paramref name="pointer"/>, else the
    /// primary one, else the first that overlaps the overlay. Monitors the overlay does not cover are
    /// ignored. The result is in overlay-local physical pixels, clipped to the overlay; without a
    /// usable monitor the whole overlay is the area.
    /// </summary>
    /// <param name="monitors">Displays in virtual-desktop physical pixels.</param>
    /// <param name="pointer">Pointer in virtual-desktop physical pixels.</param>
    /// <param name="overlayScreenBounds">The overlay's virtual-desktop rectangle.</param>
    internal static CaptureGuideArea ChooseArea(IReadOnlyList<MonitorInfo> monitors, PointD pointer, RectD overlayScreenBounds)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        RectD overlay = overlayScreenBounds.Normalized();
        var local = new RectD(0, 0, overlay.Width, overlay.Height);
        MonitorInfo? chosen = null;
        foreach (MonitorInfo monitor in monitors)
        {
            RectD bounds = monitor.Bounds.Normalized();
            if (Clip(bounds, overlay) is null) continue;
            if (pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom)
            {
                chosen = monitor;
                break;
            }

            if (chosen is null || (monitor.IsPrimary && !chosen.IsPrimary)) chosen = monitor;
        }

        if (chosen is null || Clip(chosen.Bounds.Normalized(), overlay) is not { } monitorBounds)
            return new CaptureGuideArea(local, local, 0);

        RectD workArea = Clip(chosen.WorkArea.Normalized(), overlay) is { } work && work.Width >= 1 && work.Height >= 1
            && monitorBounds.Contains(work.Center) ? work : monitorBounds;
        return new CaptureGuideArea(monitorBounds, workArea, chosen.Dpi > 0 ? chosen.ScaleFactor : 0);
    }

    /// <summary>
    /// How much the bar and the guide are scaled inside the overlay so they keep their monitor's
    /// physical size. The overlay renders at one DPI for every monitor it spans; on a 100% display
    /// next to a 150% one the bar would otherwise come out half as large again. 1 when the monitor
    /// scale is unknown, and never outside 0.5..3.
    /// </summary>
    /// <param name="monitorScale">The area's <see cref="CaptureGuideArea.Scale"/>.</param>
    /// <param name="dipPerPixel">Overlay DIPs per physical pixel (1 / the overlay's DPI scale).</param>
    internal static double ContentScale(double monitorScale, double dipPerPixel)
    {
        if (!(monitorScale > 0) || !(dipPerPixel > 0) || double.IsInfinity(dipPerPixel)) return 1;
        double scale = monitorScale * dipPerPixel;
        return Math.Abs(scale - 1) < 0.001 ? 1 : Math.Clamp(scale, 0.5, 3);
    }

    /// <summary>
    /// Centres the bar horizontally at the top of <paramref name="area"/>, keeping it inside the area
    /// (or, when wider, starting at its left edge) and inside the overlay. Edges land on physical pixels.
    /// </summary>
    /// <param name="area">The work area in overlay DIPs.</param>
    /// <param name="bar">The bar's arranged size, display scaling already applied.</param>
    /// <param name="viewport">Overlay size in DIPs.</param>
    /// <param name="scale">The <see cref="ContentScale"/> applied to the bar.</param>
    internal static Rect PlaceBar(Rect area, Size bar, Size viewport, double scale = 1, double pixelX = 1, double pixelY = 1)
    {
        double margin = EdgeMargin * scale;
        double x = area.Left + ((area.Width - bar.Width) / 2);
        x = bar.Width + (margin * 2) <= area.Width
            ? Math.Clamp(x, area.Left + margin, area.Right - margin - bar.Width)
            : area.Left;
        double y = area.Top + (TopMargin * scale);
        return Snap(new Rect(x, y, bar.Width, bar.Height), viewport, pixelX, pixelY);
    }

    /// <summary>Centres the start guide in <paramref name="area"/>, kept inside the overlay.</summary>
    internal static Rect PlaceCard(Rect area, Size card, Size viewport, double pixelX = 1, double pixelY = 1)
    {
        double x = area.Left + ((area.Width - card.Width) / 2);
        double y = area.Top + ((area.Height - card.Height) / 2);
        return Snap(new Rect(x, y, card.Width, card.Height), viewport, pixelX, pixelY);
    }

    /// <summary>Overlay-local physical pixels to DIPs.</summary>
    internal static Rect ToDip(RectD pixels, double dipPerPixelX, double dipPerPixelY) => new(
        pixels.Left * dipPerPixelX, pixels.Top * dipPerPixelY, pixels.Width * dipPerPixelX, pixels.Height * dipPerPixelY);

    private static Rect Snap(Rect rect, Size viewport, double pixelX, double pixelY)
    {
        pixelX = pixelX > 0 ? pixelX : 1;
        pixelY = pixelY > 0 ? pixelY : 1;
        double x = Math.Clamp(rect.X, 0, Math.Max(0, viewport.Width - rect.Width));
        double y = Math.Clamp(rect.Y, 0, Math.Max(0, viewport.Height - rect.Height));
        return new Rect(Math.Round(x / pixelX) * pixelX, Math.Round(y / pixelY) * pixelY, rect.Width, rect.Height);
    }

    /// <summary>The part of <paramref name="screen"/> inside the overlay, in overlay-local pixels.</summary>
    private static RectD? Clip(RectD screen, RectD overlay)
    {
        double left = Math.Max(screen.Left, overlay.Left);
        double top = Math.Max(screen.Top, overlay.Top);
        double right = Math.Min(screen.Right, overlay.Right);
        double bottom = Math.Min(screen.Bottom, overlay.Bottom);
        if (right - left < 1 || bottom - top < 1) return null;
        return new RectD(left - overlay.Left, top - overlay.Top, right - left, bottom - top).ToPixelBounds();
    }
}
