namespace MyCapture.Core.Pin;

/// <summary>
/// Pure geometry and clamping for a screen-pinned image window.
/// </summary>
/// <remarks>
/// <para>
/// Every calculation a pinned window needs — its initial on-screen size, where a
/// zoom notch lands, how far a keyboard nudge moves it, and how opacity is bounded —
/// is a plain arithmetic function of doubles here. None of it touches WPF input,
/// dispatcher state, or a live <c>Window</c>, so the whole interaction model is
/// unit-testable on an ordinary thread. The WPF <c>PinWindow</c> is a thin shell that
/// feeds pointer/keyboard positions into these functions and applies the results.
/// </para>
/// <para>
/// All sizes and positions here are in device-independent units (DIP), the same units
/// WPF <c>Window.Left/Top/Width/Height</c> use. Because the process is PerMonitorV2,
/// working-area rectangles are converted to DIP by the caller before being passed in.
/// </para>
/// </remarks>
public static class PinGeometry
{
    /// <summary>Smallest zoom factor a pin can reach (10%).</summary>
    public const double MinZoom = 0.1;

    /// <summary>Largest zoom factor a pin can reach (800%).</summary>
    public const double MaxZoom = 8.0;

    /// <summary>Lowest opacity a pin can be dimmed to; never fully invisible.</summary>
    public const double MinOpacity = 0.2;

    /// <summary>Full opacity.</summary>
    public const double MaxOpacity = 1.0;

    /// <summary>
    /// Fraction of the working area an unscaled pin is allowed to occupy before its
    /// initial zoom is reduced to fit.
    /// </summary>
    public const double InitialFitFraction = 0.8;

    /// <summary>
    /// The initial on-screen placement of a pin: its DIP size and top-left position.
    /// </summary>
    /// <param name="imageWidthDip">Natural (1:1) width of the image in DIP.</param>
    /// <param name="imageHeightDip">Natural (1:1) height of the image in DIP.</param>
    /// <param name="zoom">
    /// Zoom applied to the natural size. 1.0 is 1:1; reduced when the image is larger
    /// than <see cref="InitialFitFraction"/> of the working area.
    /// </param>
    public readonly record struct Placement(
        double Left,
        double Top,
        double Width,
        double Height,
        double Zoom);

    /// <summary>
    /// Computes the initial zoom so a 1:1 image never exceeds
    /// <see cref="InitialFitFraction"/> of the working area on either axis. Images that
    /// already fit stay at 1.0 (never enlarged).
    /// </summary>
    public static double InitialZoom(
        double imageWidthDip,
        double imageHeightDip,
        double workWidthDip,
        double workHeightDip)
    {
        if (imageWidthDip <= 0 || imageHeightDip <= 0)
        {
            return 1.0;
        }

        double maxWidth = Math.Max(1.0, workWidthDip * InitialFitFraction);
        double maxHeight = Math.Max(1.0, workHeightDip * InitialFitFraction);

        double widthRatio = maxWidth / imageWidthDip;
        double heightRatio = maxHeight / imageHeightDip;

        // Only shrink; a small clip is shown at its natural pixel size, not blown up.
        double fit = Math.Min(1.0, Math.Min(widthRatio, heightRatio));
        return ClampZoom(fit);
    }

    /// <summary>
    /// Computes a full initial placement: fit-to-work zoom, then centred near
    /// <paramref name="cursorXDip"/>/<paramref name="cursorYDip"/> but nudged so the
    /// whole window lies inside the working area.
    /// </summary>
    public static Placement InitialPlacement(
        double imageWidthDip,
        double imageHeightDip,
        double workLeftDip,
        double workTopDip,
        double workWidthDip,
        double workHeightDip,
        double cursorXDip,
        double cursorYDip)
    {
        double zoom = InitialZoom(imageWidthDip, imageHeightDip, workWidthDip, workHeightDip);
        double width = Math.Max(1.0, imageWidthDip * zoom);
        double height = Math.Max(1.0, imageHeightDip * zoom);

        // Prefer to centre the window on the cursor; the clamp keeps it fully visible.
        double left = cursorXDip - (width / 2.0);
        double top = cursorYDip - (height / 2.0);

        (left, top) = ClampTopLeftIntoWork(
            left, top, width, height, workLeftDip, workTopDip, workWidthDip, workHeightDip);

        return new Placement(left, top, width, height, zoom);
    }

    /// <summary>
    /// Clamps a window's top-left so its whole rectangle sits inside the working area.
    /// When the window is larger than the work area on an axis it is aligned to the
    /// top/left edge rather than pushed off-screen.
    /// </summary>
    public static (double Left, double Top) ClampTopLeftIntoWork(
        double left,
        double top,
        double width,
        double height,
        double workLeftDip,
        double workTopDip,
        double workWidthDip,
        double workHeightDip)
    {
        double maxLeft = workLeftDip + Math.Max(0.0, workWidthDip - width);
        double maxTop = workTopDip + Math.Max(0.0, workHeightDip - height);

        double clampedLeft = width >= workWidthDip
            ? workLeftDip
            : Math.Clamp(left, workLeftDip, maxLeft);
        double clampedTop = height >= workHeightDip
            ? workTopDip
            : Math.Clamp(top, workTopDip, maxTop);

        return (clampedLeft, clampedTop);
    }

    /// <summary>Constrains a zoom factor to the supported range.</summary>
    public static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>Constrains an opacity to the supported range.</summary>
    public static double ClampOpacity(double opacity) => Math.Clamp(opacity, MinOpacity, MaxOpacity);

    /// <summary>
    /// Applies a multiplicative zoom step and returns the clamped result.
    /// </summary>
    /// <param name="currentZoom">The zoom before the step.</param>
    /// <param name="notches">Wheel notches; positive zooms in, negative zooms out.</param>
    /// <param name="step">Per-notch fraction, for example 0.1 for 10%.</param>
    public static double StepZoom(double currentZoom, int notches, double step)
    {
        double factor = Math.Pow(1.0 + step, notches);
        return ClampZoom(currentZoom * factor);
    }

    /// <summary>
    /// New top-left after zooming so that the point under the pointer stays anchored.
    /// </summary>
    /// <remarks>
    /// The pointer position is given in screen DIP. Keeping the pixel under the pointer
    /// fixed while the window grows or shrinks is what makes wheel-zoom feel like the
    /// image is scaling under the cursor rather than jumping.
    /// </remarks>
    /// <param name="left">Current window left in DIP.</param>
    /// <param name="top">Current window top in DIP.</param>
    /// <param name="oldZoom">Zoom before the change.</param>
    /// <param name="newZoom">Zoom after the change.</param>
    /// <param name="pointerXDip">Pointer X in screen DIP.</param>
    /// <param name="pointerYDip">Pointer Y in screen DIP.</param>
    public static (double Left, double Top) AnchorTopLeftForZoom(
        double left,
        double top,
        double oldZoom,
        double newZoom,
        double pointerXDip,
        double pointerYDip)
    {
        if (oldZoom <= 0)
        {
            return (left, top);
        }

        // Fraction of the way across the window the pointer sits, in image space.
        double offsetX = pointerXDip - left;
        double offsetY = pointerYDip - top;
        double ratio = newZoom / oldZoom;

        double newLeft = pointerXDip - (offsetX * ratio);
        double newTop = pointerYDip - (offsetY * ratio);
        return (newLeft, newTop);
    }

    /// <summary>
    /// Clamps a window position so at least <paramref name="grabMarginDip"/> of it
    /// remains on the virtual desktop, guaranteeing the user can always grab it to move
    /// or reach its context menu even after aggressive dragging.
    /// </summary>
    public static (double Left, double Top) KeepGrabbable(
        double left,
        double top,
        double width,
        double height,
        double desktopLeftDip,
        double desktopTopDip,
        double desktopWidthDip,
        double desktopHeightDip,
        double grabMarginDip)
    {
        double margin = Math.Min(grabMarginDip, Math.Min(width, height));
        double desktopRight = desktopLeftDip + desktopWidthDip;
        double desktopBottom = desktopTopDip + desktopHeightDip;

        double minLeft = desktopLeftDip - (width - margin);
        double maxLeft = desktopRight - margin;
        double minTop = desktopTopDip; // never let the title/grab area go above the top
        double maxTop = desktopBottom - margin;

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }
}
