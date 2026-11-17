using System.Windows;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The hover frame glide both selectors share: when it snaps, how long it takes, its curve, and how a
/// new target in the middle of a glide carries on from the rectangle on screen.
/// </summary>
public sealed class HoverGlideTests
{
    private static readonly RectD Window = new(100, 100, 200, 150);
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(10);

    [Fact]
    public void AMoveOfAFewPixelsSnaps()
    {
        Assert.Null(HoverGlide.Length(Window, Window));
        // The centre moves 6 pixels, the size stays.
        Assert.Null(HoverGlide.Length(Window, Window with { X = 106 }));
        // The centre moves 5 pixels diagonally, the size stays.
        Assert.Null(HoverGlide.Length(Window, Window with { X = 103, Y = 104 }));
        // Width and height change by 6 together; the centre moves about 2 pixels.
        Assert.Null(HoverGlide.Length(Window, Window with { Width = 203, Height = 153 }));
        // A window 2 pixels larger on every side: the centre stays and the size changes by 8.
        Assert.NotNull(HoverGlide.Length(Window, new RectD(98, 98, 204, 154)));
    }

    [Fact]
    public void JustBeyondEitherSnapLimitGlides()
    {
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(Window, Window with { X = 107 }));
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(Window, Window with { Width = 207 }));
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(Window, Window with { Width = 204, Height = 153 }));
    }

    [Fact]
    public void ANearbyWindowOfASimilarSizeTakesTheFastLengthAndEverythingElseTheNormalOne()
    {
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(Window, Window with { X = 579 }));
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(Window, Window with { X = 580 }));
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(Window, Window with { X = 100 + 288, Y = 100 + 384 }));
        // Areas 30 000 and 179 400 (5.98×) around the same centre, and 180 000 (6×).
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(Window, new RectD(-30, -20, 460, 390)));
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(Window, new RectD(-50, -50, 500, 360)));
        // Either direction gives the same length.
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(new RectD(-50, -50, 500, 360), Window));
    }

    [Fact]
    public void WindowsOfAnySizeGlide()
    {
        RectD screen = new(0, 0, 3840, 2160), maximized = new(0, 0, 3840, 2100), small = new(1800, 1000, 240, 160);
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(small, screen));
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(screen, small));
        // A full-screen window and a maximized one are nearby and of a similar size.
        Assert.Equal(HoverGlide.FastLength, HoverGlide.Length(screen, maximized));
        Assert.Equal(HoverGlide.NormalLength, HoverGlide.Length(new RectD(0, 0, 400, 300), new RectD(3400, 1800, 400, 300)));
    }

    [Fact]
    public void TheLengthsAreTheMotionTokens() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var tokens = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        };
        Assert.Equal(Assert.IsType<Duration>(tokens["Motion.Fast"]).TimeSpan, HoverGlide.FastLength);
        Assert.Equal(Assert.IsType<Duration>(tokens["Motion.Normal"]).TimeSpan, HoverGlide.NormalLength);
    });

    [Fact]
    public void TheEasingIsTheCubicBezierCurve()
    {
        Assert.Equal(0, HoverGlide.Ease(0));
        Assert.Equal(1, HoverGlide.Ease(1));
        Assert.Equal(0, HoverGlide.Ease(-0.5));
        Assert.Equal(1, HoverGlide.Ease(1.5));
        // Points of cubic-bezier(0.20, 0.90, 0.30, 1.00) evaluated from its parameter.
        for (int step = 1; step < 100; step++)
        {
            double t = step / 100.0;
            double x = Bezier(t, 0.20, 0.30), y = Bezier(t, 0.90, 1.00);
            Assert.True(Math.Abs(y - HoverGlide.Ease(x)) < 1e-6, $"At t = {t}: {HoverGlide.Ease(x)} instead of {y}");
        }
        // Leaves at once: 76% of the way at a quarter of the time, 95% at half.
        Assert.Equal(0.7611, HoverGlide.Ease(0.25), 3);
        Assert.Equal(0.9500, HoverGlide.Ease(0.5), 3);
        double previous = 0;
        for (int step = 1; step <= 1000; step++)
        {
            double eased = HoverGlide.Ease(step / 1000.0);
            Assert.True(eased >= previous && eased <= 1, $"Not monotonic at {step / 1000.0}: {eased}");
            previous = eased;
        }
    }

    [Fact]
    public void EveryGlideFrameIsOnWholePixelsAndNeverTurnsInsideOut()
    {
        RectD from = new(10.4, 20.6, 0.2, 300), to = new(900, 5, 640, 0.4);
        Assert.Equal(new RectD(10, 21, 1, 300), HoverGlide.At(from, to, 0));
        Assert.Equal(new RectD(900, 5, 640, 0), HoverGlide.At(from, to, 1));
        for (int step = 0; step <= 200; step++)
        {
            RectD shown = HoverGlide.At(from, to, HoverGlide.Ease(step / 200.0));
            Assert.Equal(Math.Round(shown.X), shown.X);
            Assert.Equal(Math.Round(shown.Y), shown.Y);
            Assert.Equal(Math.Round(shown.Width), shown.Width);
            Assert.Equal(Math.Round(shown.Height), shown.Height);
            Assert.True(shown.Width >= 0 && shown.Height >= 0, shown.ToString());
        }
    }

    [Fact]
    public void TheFirstTargetSnapsAndANewOneGlidesUntilItLandsExactlyOnIt()
    {
        var glide = new HoverGlide();
        RectD next = new(400, 140, 180, 140);
        Assert.Equal(Window, glide.Update(Window, Start, animate: true));
        Assert.False(glide.IsGliding);

        Assert.Equal(Window, glide.Update(next, Start, animate: true));
        Assert.True(glide.IsGliding);
        Assert.Equal(next, glide.Target);
        RectD half = glide.Update(next, Start + (HoverGlide.FastLength / 2), animate: true)!.Value;
        Assert.True(glide.IsGliding);
        Assert.Equal(HoverGlide.At(Window, next, HoverGlide.Ease(0.5)), half);
        Assert.Equal(next, glide.Update(next, Start + HoverGlide.FastLength, animate: true));
        Assert.False(glide.IsGliding);
        Assert.Equal(next, glide.Update(next, Start + (HoverGlide.FastLength * 3), animate: true));
        Assert.False(glide.IsGliding);
    }

    [Fact]
    public void ANewTargetDuringAGlideStartsFromTheShownRectangleWithANewClock()
    {
        var glide = new HoverGlide();
        RectD far = new(1500, 900, 1200, 800), back = new(140, 120, 220, 160);
        glide.Update(Window, Start, animate: true);
        glide.Update(far, Start, animate: true);
        TimeSpan turn = Start + (HoverGlide.NormalLength / 4);
        RectD shown = glide.Update(far, turn, animate: true)!.Value;
        Assert.NotEqual(Window, shown);
        Assert.NotEqual(far, shown);

        // Nothing jumps: the turn starts exactly where the frame is.
        Assert.Equal(shown, glide.Update(back, turn, animate: true));
        Assert.True(glide.IsGliding);
        Assert.Equal(back, glide.Target);
        TimeSpan? length = HoverGlide.Length(shown, back);
        Assert.NotNull(length);
        // The new glide runs its own length from the turn, past where the first one would have ended.
        TimeSpan late = turn + (length!.Value * 0.8);
        Assert.True(late > Start + HoverGlide.Length(Window, far)!.Value);
        Assert.Equal(HoverGlide.At(shown, back, HoverGlide.Ease(0.8)), glide.Update(back, late, animate: true));
        Assert.True(glide.IsGliding);
        Assert.Equal(back, glide.Update(back, turn + length.Value, animate: true));
        Assert.False(glide.IsGliding);
    }

    [Fact]
    public void WithoutMotionEveryChangeSnapsAndAGlideStopsWhereItsTargetIs()
    {
        var glide = new HoverGlide();
        RectD next = new(900, 500, 640, 400);
        glide.Update(Window, Start, animate: false);
        Assert.Equal(next, glide.Update(next, Start, animate: false));
        Assert.False(glide.IsGliding);

        // Animation effects turned off in the middle of a glide land it at once.
        glide.Update(Window, Start, animate: true);
        Assert.True(glide.IsGliding);
        glide.Update(Window, Start + TimeSpan.FromMilliseconds(20), animate: true);
        Assert.Equal(Window, glide.Update(Window, Start + TimeSpan.FromMilliseconds(30), animate: false));
        Assert.False(glide.IsGliding);
    }

    [Fact]
    public void NoTargetForgetsTheFrameSoTheNextOneSnaps()
    {
        var glide = new HoverGlide();
        glide.Update(Window, Start, animate: true);
        glide.Update(new RectD(600, 140, 180, 140), Start, animate: true);
        Assert.True(glide.IsGliding);
        Assert.Null(glide.Update(null, Start + TimeSpan.FromMilliseconds(10), animate: true));
        Assert.False(glide.IsGliding);
        Assert.Null(glide.Shown);
        Assert.Null(glide.Target);
        RectD next = new(20, 30, 40, 50);
        Assert.Equal(next, glide.Update(next, Start + TimeSpan.FromMilliseconds(20), animate: true));
        Assert.False(glide.IsGliding);

        glide.Update(Window, Start + TimeSpan.FromMilliseconds(30), animate: true);
        Assert.True(glide.IsGliding);
        glide.Snap(next);
        Assert.False(glide.IsGliding);
        Assert.Equal(next, glide.Shown);
        Assert.Equal(next, glide.Update(next, Start + TimeSpan.FromMilliseconds(40), animate: true));
    }

    private static double Bezier(double t, double p1, double p2) =>
        (3 * (1 - t) * (1 - t) * t * p1) + (3 * (1 - t) * t * t * p2) + (t * t * t);
}
