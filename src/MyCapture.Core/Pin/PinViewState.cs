namespace MyCapture.Core.Pin;

/// <summary>
/// The mutable presentation state of a single pinned window — zoom, opacity, and
/// click-through — kept independent of WPF so the interaction rules are testable.
/// </summary>
/// <remarks>
/// The WPF <c>PinWindow</c> owns one of these and mirrors its values onto the live
/// window (size, <c>Opacity</c>, extended window styles). Keeping the arithmetic here
/// means "wheel zooms 10% and clamps", "Ctrl+wheel dims within bounds", and "0 resets
/// to 100%" are all verified without a message pump.
/// </remarks>
public sealed class PinViewState
{
    private readonly double _zoomStep;

    /// <summary>
    /// Creates state for an image of the given natural DIP size.
    /// </summary>
    /// <param name="imageWidthDip">1:1 image width in DIP.</param>
    /// <param name="imageHeightDip">1:1 image height in DIP.</param>
    /// <param name="initialZoom">Fitted initial zoom (see <see cref="PinGeometry.InitialZoom"/>).</param>
    /// <param name="initialOpacity">Initial opacity, clamped into the supported range.</param>
    /// <param name="zoomStep">Per-notch/keystroke zoom fraction, for example 0.1.</param>
    public PinViewState(
        double imageWidthDip,
        double imageHeightDip,
        double initialZoom,
        double initialOpacity,
        double zoomStep)
    {
        ImageWidthDip = Math.Max(1.0, imageWidthDip);
        ImageHeightDip = Math.Max(1.0, imageHeightDip);
        Zoom = PinGeometry.ClampZoom(initialZoom);
        Opacity = PinGeometry.ClampOpacity(initialOpacity);
        _zoomStep = zoomStep <= 0 ? 0.1 : zoomStep;
    }

    public double ImageWidthDip { get; }

    public double ImageHeightDip { get; }

    public double Zoom { get; private set; }

    public double Opacity { get; private set; }

    /// <summary>Whether the pin currently passes mouse input through to windows below.</summary>
    public bool IsClickThrough { get; private set; }

    /// <summary>Whether the pin is currently hidden by a global hide-all toggle.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Current window width in DIP at the active zoom.</summary>
    public double WidthDip => Math.Max(1.0, ImageWidthDip * Zoom);

    /// <summary>Current window height in DIP at the active zoom.</summary>
    public double HeightDip => Math.Max(1.0, ImageHeightDip * Zoom);

    /// <summary>Applies a wheel/keyboard zoom step and returns the resulting zoom.</summary>
    public double ApplyZoomStep(int notches)
    {
        Zoom = PinGeometry.StepZoom(Zoom, notches, _zoomStep);
        return Zoom;
    }

    /// <summary>Sets an absolute zoom, clamped.</summary>
    public double SetZoom(double zoom)
    {
        Zoom = PinGeometry.ClampZoom(zoom);
        return Zoom;
    }

    /// <summary>Resets to 1:1 (100%).</summary>
    public double ResetZoom()
    {
        Zoom = 1.0;
        return Zoom;
    }

    /// <summary>Adjusts opacity by a delta, clamped to the supported range.</summary>
    public double AdjustOpacity(double delta)
    {
        Opacity = PinGeometry.ClampOpacity(Opacity + delta);
        return Opacity;
    }

    /// <summary>Sets an absolute opacity, clamped.</summary>
    public double SetOpacity(double opacity)
    {
        Opacity = PinGeometry.ClampOpacity(opacity);
        return Opacity;
    }

    /// <summary>Toggles click-through and returns the new state.</summary>
    public bool ToggleClickThrough()
    {
        IsClickThrough = !IsClickThrough;
        return IsClickThrough;
    }

    /// <summary>Sets click-through to an explicit value.</summary>
    public void SetClickThrough(bool value) => IsClickThrough = value;
}
