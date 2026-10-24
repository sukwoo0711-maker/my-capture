using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The capture flight (<see cref="CaptureFlight"/>) on video time. When the selector closes, the
/// captured region lifts off the screen where it was taken (its shadow fades in over 167 ms); when
/// the quick access card shows, the image flies into the card's preview over 250 ms while the card
/// fades in, and the card's own preview takes its place on arrival.
/// </summary>
/// <remarks>
/// The product draws the image in its own capture-excluded window on the wall clock. The clip builds
/// the same <see cref="CaptureFlightDecoration"/> over the stage's display, never shows it, and draws
/// its surface for each frame with the product's <see cref="CaptureFlight.LiftFrame"/> and
/// <see cref="CaptureFlight.FlightFrame"/>. Stage units are the product's DIPs, so the shadow is
/// sized at scale 1. The card side is the product's landing: <see cref="QuickAccessWindow.PrepareLanding"/>
/// before the lift, <see cref="QuickAccessWindow.BeginLandingFade"/> as the flight starts (its
/// 100 ms fade set on video time), <see cref="QuickAccessWindow.RevealPreview"/> on arrival and
/// <see cref="QuickAccessWindow.CompleteLanding"/> on the next frame, when the image is gone.
/// </remarks>
internal sealed class CaptureFlightPicture : IDisposable
{
    internal const double FrameMs = 1000.0 / TutorialStoryboard.Fps;

    /// <summary>
    /// Frames the capture stays lifted before the card shows: the shadow fades in and rests while
    /// the capture is saved and copied (320 ms).
    /// </summary>
    internal const int LiftFrames = 8;

    private readonly CaptureFlightDecoration _window;
    private readonly FrameworkElement _surface;
    private readonly Rect _source, _display;

    /// <param name="image">The captured pixels, which cover <paramref name="source"/> exactly.</param>
    /// <param name="source">Where the capture was taken, in stage units.</param>
    /// <param name="display">The stage's display; the flight's window is trimmed to it.</param>
    internal CaptureFlightPicture(BitmapSource image, Rect source, Rect display)
    {
        _source = source;
        _display = display;
        _window = new CaptureFlightDecoration(image, new RectD(display.X, display.Y, display.Width, display.Height), 1);
        // Never shown: the surface is laid out and drawn without its window.
        _surface = (FrameworkElement)_window.Content;
        _window.Content = null;
    }

    /// <summary>Frames from the flight's start to the one it lands on, inclusive.</summary>
    internal static int FlightFrames => (int)Math.Ceiling(FluidMotion.DeliberateDuration.TimeSpan.TotalMilliseconds / FrameMs) + 1;

    /// <summary>The landed flight's frame: the card shows its preview under the image.</summary>
    internal static bool Landed(double flightMs) => flightMs >= FluidMotion.DeliberateDuration.TimeSpan.TotalMilliseconds;

    /// <summary>The landing card's opacity <paramref name="flightMs"/> into the flight (BeginLandingFade).</summary>
    internal static double CardOpacity(double flightMs) => FluidMotion.SoftLandingEasing.Ease(
        Math.Clamp(flightMs / QuickAccessWindow.LandingFadeDuration.TimeSpan.TotalMilliseconds, 0, 1));

    /// <summary>The capture <paramref name="liftMs"/> after the selector closed, on its source.</summary>
    internal void DrawLift(DrawingContext dc, double liftMs) => Draw(dc, CaptureFlight.LiftFrame(_source, liftMs, 1));

    /// <summary>
    /// The capture <paramref name="flightMs"/> into its flight to <paramref name="target"/> (the
    /// card's preview in stage units), after <paramref name="liftedMs"/> on its source.
    /// </summary>
    internal void DrawFlight(DrawingContext dc, Rect target, double flightMs, double liftedMs)
    {
        double progress = Math.Clamp(flightMs / FluidMotion.DeliberateDuration.TimeSpan.TotalMilliseconds, 0, 1);
        Draw(dc, CaptureFlight.FlightFrame(_source, target, progress, 1, CaptureFlight.LiftLevel(liftedMs + flightMs)));
    }

    /// <summary>Where <paramref name="card"/>'s preview image is drawn, with the card's content at <paramref name="origin"/>.</summary>
    internal static Rect PreviewBounds(QuickAccessWindow card, FrameworkElement cardRoot, Point origin)
    {
        System.Windows.Controls.Image preview = card.PreviewForTest;
        Point at = preview.TranslatePoint(new Point(), cardRoot);
        if (preview.ActualWidth <= 0 || preview.ActualHeight <= 0)
            throw new InvalidOperationException("The card's preview is not laid out.");
        return new Rect(origin.X + at.X, origin.Y + at.Y, preview.ActualWidth, preview.ActualHeight);
    }

    private void Draw(DrawingContext dc, CaptureFlightFrame frame)
    {
        _window.Draw(frame);
        _surface.Measure(_display.Size);
        _surface.Arrange(new Rect(_display.Size));
        _surface.UpdateLayout();
        // The surface draws in its window's coordinates whatever its laid-out size, so it is
        // rendered at the display's size rather than through Detached (which sizes by ActualWidth).
        dc.DrawImage(OffscreenWindow.SnapshotElement(_surface, _display.Width, _display.Height, opaque: false), _display);
    }

    public void Dispose() => _window.Close();
}
