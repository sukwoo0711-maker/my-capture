using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// The captured region flying into the quick access card (feel bench 1-C). When the selector
/// closes, the capture lifts off the screen where it was taken: its image covers exactly the
/// pixels it was cut from and a soft shadow fades in under it. When the card shows, the image
/// flies to the card's preview in 250 ms (cubic ease-out, uniform scale, the shadow deepest
/// mid-flight) while the card fades in; on arrival the card's own preview takes its place.
/// </summary>
/// <remarks>
/// <para>
/// The image is drawn by <see cref="CaptureFlightDecoration"/> windows that never take input or
/// the foreground and never appear in a capture. While the capture is saved and copied the lift
/// window is still, so it costs nothing; frames are drawn only while the shadow fades in, the
/// image flies, or the image fades away. The flight is time-based: a slow frame skips positions
/// but never lengthens it.
/// </para>
/// <para>
/// The lift window covers the source rectangle and its shadow margin. A flight needs the bounding
/// box of the source and the card's preview, known only once the card is laid out: when that box
/// fits the lift window (a full-screen capture), the image flies there; otherwise a second window
/// the size of that box takes over once it has drawn its first frame, so no frame lacks the image.
/// </para>
/// <para>
/// Nothing waits for the flight: the card shows, takes input and counts down as before, and a
/// new capture or the card closing ends the flight at once (<see cref="Finish"/>). There is no
/// flight when Windows animation effects are off, in a remote session, without layered windows
/// or capture exclusion, when the path crosses displays of different scale, or when the
/// window would exceed <see cref="MaxSurfacePixels"/>.
/// </para>
/// </remarks>
internal sealed class CaptureFlight
{
    /// <summary>How long the card may take to show before the lifted capture fades away.</summary>
    internal static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a shown card waits for its flight before it shows its preview anyway.</summary>
    internal static readonly TimeSpan FlightTimeout = TimeSpan.FromSeconds(1.5);

    // Shadow at rest under the lifted image and at its deepest, mid-flight; DIPs at the display's scale.
    internal const double LiftBlurDip = 12;
    internal const double LiftOffsetDip = 3;
    internal const double LiftShadowOpacity = 0.3;
    internal const double PeakBlurDip = 32;
    internal const double PeakOffsetDip = 14;
    internal const double PeakShadowOpacity = 0.48;

    /// <summary>Room around the image for the deepest shadow.</summary>
    internal const double MarginDip = 48;

    /// <summary>
    /// The largest decoration surface, in physical pixels: one 3840 x 2160 display. Each frame of a
    /// layered window is drawn into system memory and handed to the compositor, so its cost grows
    /// with its area. A full-screen flight on one 4K display is the most this allows; a larger box
    /// (a region spanning several large displays) does not fly.
    /// </summary>
    internal const double MaxSurfacePixels = 3840d * 2160d;

    // The shadow leaves over the last 40% of the flight, while the image settles into the card.
    private const double ShadowLeaveFrom = 0.6;

    internal static Func<bool> ReadRemoteSession { get; set; } = () => SystemParameters.IsRemoteSession;
    internal static Func<bool> ReadLayeredSupport { get; set; } = () => CaptureWindowExclusion.SupportsLayeredWindows;
    internal static Func<IReadOnlyList<MonitorInfo>> ReadMonitors { get; set; } = MonitorEnumerator.GetAll;

    private enum Phase { Lifted, Landing, HandingOver, Flying, Arrived, Leaving, Done }

    private readonly Func<double> _clock;
    private readonly double _scale;
    private readonly double _liftedAt;
    private readonly DispatcherTimer _deadline;
    private CaptureFlightDecoration _window;
    private CaptureFlightDecoration? _successor;
    private QuickAccessWindow? _card;
    private Phase _phase = Phase.Lifted;
    private double _phaseStart;
    private bool _rendering;

    private CaptureFlight(BitmapSource image, RectD source, CaptureFlightDecoration window, double scale, Func<double> clock)
    {
        Image = image;
        Source = source;
        _window = window;
        _scale = scale;
        _clock = clock;
        _liftedAt = clock();
        _deadline = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval = LandingTimeout };
        _deadline.Tick += OnDeadline;
    }

    /// <summary>The captured image; the card that receives the flight shows this same image.</summary>
    internal BitmapSource Image { get; }

    /// <summary>Where the image was taken, in virtual-desktop physical pixels.</summary>
    internal RectD Source { get; }

    internal bool IsDone => _phase == Phase.Done;

    internal bool IsFlying => _phase == Phase.Flying;

    /// <summary>The window drawing the image now; null once the flight has ended.</summary>
    internal CaptureFlightDecoration? WindowForTest => _phase == Phase.Done ? null : _window;

    internal static bool IsEligible(bool animations, bool remote, bool layered) => animations && !remote && layered;

    /// <summary>
    /// Lifts <paramref name="image"/>, taken from <paramref name="source"/> (physical pixels), off
    /// the screen. Null when the capture cannot fly; nothing is shown then.
    /// </summary>
    internal static CaptureFlight? TryLift(BitmapSource image, RectD source, Func<double>? clock = null)
    {
        if (!IsEligible(FluidMotion.AnimationsEnabled, ReadRemoteSession(), ReadLayeredSupport())) return null;
        RectD pixels = source.ToPixelBounds();
        // The image must be the source pixels one to one, or it would not look like the screen.
        if (pixels.IsEmpty || pixels.Width != image.PixelWidth || pixels.Height != image.PixelHeight) return null;
        if (PlanBounds(ToRect(pixels), ReadMonitors()) is not { } plan) return null;

        CaptureFlightDecoration? window = null;
        try
        {
            window = new CaptureFlightDecoration(image, plan.Bounds, plan.Scale);
            Stopwatch? watch = clock is null ? Stopwatch.StartNew() : null;
            var flight = new CaptureFlight(image, pixels, window, plan.Scale, clock ?? (() => watch!.Elapsed.TotalMilliseconds));
            flight.Draw(flight.LiftFrame(0, 1));
            if (!window.ShowDecoration()) return null;
            window = null;
            flight._deadline.Start();
            // A capture that fills its window, such as a full screen, has no shadow in sight to fade
            // in, so its large surface is drawn once and then left alone until the card shows.
            if (plan.Bounds != pixels) flight.StartRendering();
            return flight;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A decoration failure never affects the capture itself.
            return null;
        }
        finally
        {
            if (window is { IsClosed: false }) window.Close();
        }
    }

    /// <summary>
    /// Hands the flight to <paramref name="card"/>, before it is shown. The card stays clear
    /// until the image starts flying and shows its preview when the image lands. False when the
    /// flight has already ended; the card then shows with its usual entrance.
    /// </summary>
    internal bool Land(QuickAccessWindow card)
    {
        if (_phase is not Phase.Lifted || !FluidMotion.AnimationsEnabled) return false;
        _card = card;
        card.PrepareLanding();
        card.ContentRendered += OnCardRendered;
        card.Closed += OnCardClosed;
        _phase = Phase.Landing;
        _deadline.Stop();
        _deadline.Interval = FlightTimeout;
        _deadline.Start();
        return true;
    }

    /// <summary>
    /// No card will receive the lifted image: it fades away over 83 ms. A flight a card has
    /// already taken (<see cref="Land"/>) goes on.
    /// </summary>
    internal void Cancel()
    {
        if (_phase == Phase.Lifted) Leave();
    }

    /// <summary>Ends the flight at once: the card, if any, shows its preview and the image is gone.</summary>
    internal void Finish()
    {
        if (_phase == Phase.Done) return;
        _phase = Phase.Done;
        _deadline.Stop();
        StopRendering();
        if (_card is { } card)
        {
            card.ContentRendered -= OnCardRendered;
            card.Closed -= OnCardClosed;
            card.CompleteLanding();
        }
        _successor?.Close();
        _successor = null;
        if (!_window.IsClosed) _window.Close();
    }

    /// <summary>Applies the frame for the clock's current time; the live clock calls this every frame.</summary>
    internal void Tick() => Advance(_clock());

    private void Advance(double now)
    {
        switch (_phase)
        {
            case Phase.Lifted or Phase.Landing or Phase.HandingOver:
                double lift = now - _liftedAt;
                Draw(LiftFrame(lift, 1));
                // Once the shadow is in, the lifted image is still and draws nothing more.
                if (lift >= FluidMotion.NormalDuration.TimeSpan.TotalMilliseconds) StopRendering();
                break;

            case Phase.Flying:
                if (_card is not { IsVisible: true } card || FluidMotion.IsExiting(card)
                    || card.PreviewScreenBounds is not { } target || !Contains(_window.Bounds, target))
                {
                    // The card left or moved away from where the image can reach: land at once.
                    Finish();
                    return;
                }

                double flight = (now - _phaseStart) / FluidMotion.DeliberateDuration.TimeSpan.TotalMilliseconds;
                Draw(FlightFrame(ToRect(Source), target, Math.Clamp(flight, 0, 1), _scale, LiftLevel(now - _liftedAt)));
                if (flight >= 1)
                {
                    // The card shows the same pixels where the image lands. The image leaves on the
                    // next frame, so for one frame both are drawn and never neither.
                    card.RevealPreview();
                    _phase = Phase.Arrived;
                }
                break;

            case Phase.Arrived:
                Finish();
                break;

            case Phase.Leaving:
                double leave = (now - _phaseStart) / FluidMotion.FastDuration.TimeSpan.TotalMilliseconds;
                if (leave >= 1)
                {
                    Finish();
                    return;
                }
                Draw(LiftFrame(now - _liftedAt, 1 - FluidMotion.StandardEasing.Ease(leave)));
                break;
        }
    }

    private void OnCardRendered(object? sender, EventArgs e)
    {
        if (_card is { } card) card.ContentRendered -= OnCardRendered;
        if (_phase != Phase.Landing || _card is null) return;
        if (!FluidMotion.AnimationsEnabled)
        {
            Finish();
            return;
        }

        try
        {
            if (_card.PreviewScreenBounds is not { } target
                || PlanBounds(Rect.Union(ToRect(Source), target), ReadMonitors()) is not { } plan)
            {
                SkipFlight();
                return;
            }

            if (Contains(_window.Bounds, plan.Bounds))
            {
                // The card was shown after the lift window: put the image back on top before it moves.
                _window.Raise();
                StartFlying();
                return;
            }

            var successor = new CaptureFlightDecoration(Image, plan.Bounds, plan.Scale);
            successor.Draw(_window.Frame);
            if (!successor.ShowDecoration())
            {
                successor.Close();
                SkipFlight();
                return;
            }

            _successor = successor;
            _phase = Phase.HandingOver;
            successor.ContentRendered += OnSuccessorRendered;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SkipFlight();
        }
    }

    private void OnSuccessorRendered(object? sender, EventArgs e)
    {
        if (sender is CaptureFlightDecoration successor) successor.ContentRendered -= OnSuccessorRendered;
        if (_phase != Phase.HandingOver || _successor is not { } next) return;
        // The new window shows the image where the lift window does; the lift window can go.
        _window.Close();
        _window = next;
        _successor = null;
        StartFlying();
    }

    private void StartFlying()
    {
        _phase = Phase.Flying;
        _phaseStart = _clock();
        // Frames stop while nothing renders (a locked session); the card still lands shortly after.
        _deadline.Stop();
        _deadline.Interval = FluidMotion.DeliberateDuration.TimeSpan + TimeSpan.FromSeconds(1);
        _deadline.Start();
        _card?.BeginLandingFade();
        StartRendering();
        Tick();
    }

    /// <summary>The card enters with its usual entrance and the lifted image fades away.</summary>
    private void SkipFlight()
    {
        if (_card is { } card)
        {
            card.Closed -= OnCardClosed;
            card.CompleteLanding();
            _card = null;
        }
        Leave();
    }

    private void Leave()
    {
        _successor?.Close();
        _successor = null;
        if (!FluidMotion.AnimationsEnabled)
        {
            Finish();
            return;
        }

        _phase = Phase.Leaving;
        _phaseStart = _clock();
        _deadline.Stop();
        // Frames stop while nothing renders (a locked session); the image still goes shortly after.
        _deadline.Interval = FluidMotion.FastDuration.TimeSpan + TimeSpan.FromMilliseconds(250);
        _deadline.Start();
        StartRendering();
    }

    private void OnDeadline(object? sender, EventArgs e)
    {
        _deadline.Stop();
        if (_phase == Phase.Lifted) Cancel();
        else Finish();
    }

    private void OnCardClosed(object? sender, EventArgs e) => Finish();

    private void Draw(CaptureFlightFrame frame) => _window.Draw(frame);

    private void StartRendering()
    {
        if (_rendering) return;
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs e) => Tick();

    private CaptureFlightFrame LiftFrame(double elapsedMs, double opacity) => LiftFrame(ToRect(Source), elapsedMs, _scale, opacity);

    /// <summary>
    /// The image lifted <paramref name="elapsedMs"/> ago, at rest on <paramref name="source"/>
    /// (physical pixels at <paramref name="scale"/>), its shadow fading in.
    /// </summary>
    internal static CaptureFlightFrame LiftFrame(Rect source, double elapsedMs, double scale, double opacity = 1) => new(
        source, LiftBlurDip * scale, LiftOffsetDip * scale, LiftShadowOpacity * LiftLevel(elapsedMs), opacity);

    /// <summary>How far the lift's shadow has faded in, 0 to 1, over the shared 167 ms token.</summary>
    internal static double LiftLevel(double elapsedMs)
    {
        double t = Math.Clamp(elapsedMs / FluidMotion.NormalDuration.TimeSpan.TotalMilliseconds, 0, 1);
        return FluidMotion.SoftLandingEasing.Ease(t);
    }

    /// <summary>
    /// The image <paramref name="progress"/> of the way (by time, 0 to 1) from
    /// <paramref name="source"/> to <paramref name="target"/>, both in physical pixels.
    /// </summary>
    internal static CaptureFlightFrame FlightFrame(Rect source, Rect target, double progress, double scale, double liftLevel = 1)
    {
        double p = Math.Clamp(progress, 0, 1);
        double eased = 1 - Math.Pow(1 - p, 3);
        var image = new Rect(
            Lerp(source.Left, target.Left, eased), Lerp(source.Top, target.Top, eased),
            Lerp(source.Width, target.Width, eased), Lerp(source.Height, target.Height, eased));
        // The image rises off the screen on the way and settles into the card at the end.
        double rise = Math.Sin(Math.PI * eased);
        double leave = SmoothStep(ShadowLeaveFrom, 1, p);
        return new CaptureFlightFrame(image,
            Lerp(LiftBlurDip, PeakBlurDip, rise) * scale,
            Lerp(LiftOffsetDip, PeakOffsetDip, rise) * scale,
            Lerp(LiftShadowOpacity * liftLevel, PeakShadowOpacity, rise) * (1 - leave),
            1);
    }

    /// <summary>
    /// The decoration window for an image path (physical pixels): the path with room for the
    /// shadow, trimmed to the displays it crosses. Null when those displays differ in scale or
    /// the window would be larger than <see cref="MaxSurfacePixels"/>.
    /// </summary>
    internal static (RectD Bounds, double Scale)? PlanBounds(Rect path, IReadOnlyList<MonitorInfo> monitors)
    {
        if (path.IsEmpty || path.Width <= 0 || path.Height <= 0) return null;
        MonitorInfo[] crossed = monitors.Where(monitor => Overlaps(ToRect(monitor.Bounds), path)).ToArray();
        if (crossed.Length == 0) return null;
        double scale = crossed[0].ScaleFactor;
        if (!double.IsFinite(scale) || scale <= 0 || crossed.Any(monitor => monitor.ScaleFactor != scale)) return null;

        Rect room = path;
        double margin = Math.Ceiling(MarginDip * scale);
        room.Inflate(margin, margin);
        // The shadow stays on the displays the image crosses: a full-screen capture needs no room
        // beyond its display, and a neighbouring display never widens the window.
        Rect desk = ToRect(crossed[0].Bounds);
        foreach (MonitorInfo monitor in crossed.Skip(1)) desk.Union(ToRect(monitor.Bounds));
        room.Intersect(desk);
        if (room.IsEmpty || room.Width <= 0 || room.Height <= 0) return null;
        RectD bounds = new RectD(room.X, room.Y, room.Width, room.Height).ToPixelBounds();
        // One window has one scale: no part of it may lie on a display of another scale (displays
        // the image crosses can enclose another one in an L-shaped layout).
        if (monitors.Any(monitor => monitor.ScaleFactor != scale && Overlaps(ToRect(monitor.Bounds), ToRect(bounds))))
            return null;
        if (bounds.Width * bounds.Height > MaxSurfacePixels) return null;
        return (bounds, scale);
    }

    // Shared edges do not count: a capture ending at a display's edge does not cross the next one.
    private static bool Overlaps(Rect a, Rect b) =>
        !a.IsEmpty && !b.IsEmpty && a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static bool Contains(RectD outer, Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5
        && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    private static bool Contains(RectD outer, RectD inner) => Contains(outer, ToRect(inner));

    private static Rect ToRect(RectD rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static double Lerp(double from, double to, double progress) => from + ((to - from) * progress);

    private static double SmoothStep(double from, double to, double value)
    {
        double t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
