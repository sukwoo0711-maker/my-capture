using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using Xunit;
using Shapes = System.Windows.Shapes;

namespace MyCapture.App.Tests;

/// <summary>
/// The delayed-capture countdown: each new digit lands over the fading previous one, a ring runs
/// down every second, the value itself changes at once, and the window leaves with a short exit
/// only when the capture that follows cannot see it. Everything snaps with animation effects off.
/// </summary>
public sealed class CountdownWindowMotionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachSecondLandsTheNewDigitOverTheFadingOne(bool motion) => Run(motion, window =>
    {
        TextBlock label = Field<TextBlock>(window, "_label");
        TextBlock leaving = Field<TextBlock>(window, "_leaving");
        var labelScale = Assert.IsType<ScaleTransform>(label.RenderTransform);
        var leavingScale = Assert.IsType<ScaleTransform>(leaving.RenderTransform);
        // The first digit lands with the window.
        Assert.Equal("3", label.Text);
        Assert.Equal(motion, label.HasAnimatedProperties);
        Assert.Equal(string.Empty, leaving.Text);

        Tick(window);

        // The value is committed at once; only its picture moves.
        Assert.Equal("2", label.Text);
        Assert.Equal(1, (double)label.GetAnimationBaseValue(UIElement.OpacityProperty));
        Assert.Equal(1, (double)labelScale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
        Assert.Equal(motion, label.HasAnimatedProperties);
        Assert.Equal(motion, labelScale.HasAnimatedProperties);
        Assert.Equal(motion ? "3" : string.Empty, leaving.Text);
        Assert.Equal(motion, leaving.HasAnimatedProperties);
        Assert.Equal(motion, leavingScale.HasAnimatedProperties);
        Assert.Equal(0, (double)leaving.GetAnimationBaseValue(UIElement.OpacityProperty));

        // Screen readers keep one polite live value; the leaving digit is not read.
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(label));
        Assert.Equal(IsOffscreenBehavior.Offscreen, AutomationProperties.GetIsOffscreenBehavior(leaving));
        Assert.False(leaving.IsHitTestVisible);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RingRunsDownEachSecondOrStepsThroughTheCountdownWithoutMotion(bool motion) => Run(motion, window =>
    {
        Shapes.Path ring = Field<Shapes.Path>(window, "_ring");
        double length = Field<double>(window, "_ringLength");
        Assert.Equal(motion, ring.HasAnimatedProperties);
        Assert.Equal(0, (double)ring.GetAnimationBaseValue(Shapes.Shape.StrokeDashOffsetProperty));

        Tick(window);
        Assert.Equal(motion, ring.HasAnimatedProperties);
        Assert.Equal(motion ? 0 : length / 3, (double)ring.GetAnimationBaseValue(Shapes.Shape.StrokeDashOffsetProperty), 6);
        Tick(window);
        Assert.Equal(motion ? 0 : length * 2 / 3, (double)ring.GetAnimationBaseValue(Shapes.Shape.StrokeDashOffsetProperty), 6);

        // A thin stroke, and it takes no space: the digit's line decides the panel's size.
        Assert.InRange(ring.StrokeThickness, 2, 3);
        TextBlock label = Field<TextBlock>(window, "_label");
        var digit = Assert.IsType<Grid>(VisualTreeHelper.GetParent(label));
        Assert.Equal(label.DesiredSize.Height, digit.DesiredSize.Height, 6);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ElapsedShrinksAwayOnlyWhileTheCaptureCannotSeeIt(bool excluded) => Run(true, window =>
    {
        int closed = 0;
        window.Closed += (_, _) => closed++;
        // What the app does: close first, then start the capture.
        window.Elapsed += (_, _) => window.Close();
        Assert.Equal(excluded, window.IsCaptureExcluded);

        Tick(window);
        Tick(window);
        Tick(window);

        if (!excluded)
        {
            // Visible to the capture: gone before it, exactly as before.
            Assert.Equal(1, closed);
            Assert.False(FluidMotion.IsExiting(window));
            return;
        }

        Assert.Equal(0, closed);
        Assert.True(window.IsVisible);
        Assert.True(FluidMotion.IsExiting(window));
        Assert.False(window.IsHitTestVisible);
        Assert.IsType<ScaleTransform>(((FrameworkElement)window.Content).RenderTransform);

        FluidMotion.CompleteExit(window);
        Assert.Equal(1, closed);
        Assert.False(window.IsVisible);
    }, excluded: excluded);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EscFadesDownOrClosesAtOnceWithoutMotion(bool motion) => Run(motion, window =>
    {
        int closed = 0, cancelled = 0;
        window.Closed += (_, _) => closed++;
        window.Cancelled += (_, _) =>
        {
            cancelled++;
            window.Close();
        };

        window.RaiseEvent(WindowDetectOverlayTests.KeyArgs(Key.Escape));

        Assert.Equal(1, cancelled);
        Assert.Equal(motion ? 0 : 1, closed);
        Assert.Equal(motion, FluidMotion.IsExiting(window));
        if (!motion) return;
        Assert.IsType<TranslateTransform>(((FrameworkElement)window.Content).RenderTransform);

        // A second close while it leaves ends it at once, and only once.
        window.Close();
        Assert.Equal(1, closed);
        Assert.False(FluidMotion.IsExiting(window));
        FluidMotion.CompleteExit(window);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void OtherClosesAndOpaqueWindowsCloseAtOnce()
    {
        // Shutdown, diagnostics and the tutorial renderer close a countdown that never finished.
        Run(true, window =>
        {
            int closed = 0;
            window.Closed += (_, _) => closed++;
            window.Close();
            Assert.Equal(1, closed);
        });

        // An opaque countdown is not excluded by this window and cannot fade into the desktop.
        Run(true, window =>
        {
            int closed = 0;
            window.Closed += (_, _) => closed++;
            window.Elapsed += (_, _) => window.Close();
            Assert.False(window.AllowsTransparency);
            Assert.False(window.IsCaptureExcluded);
            Tick(window);
            Tick(window);
            Tick(window);
            Assert.Equal(1, closed);
        }, layered: false);
    }

    [Fact]
    public void ExclusionIsAppliedBeforeTheWindowIsFirstShown() => StaTestHost.Run(() =>
    {
        using var mode = new ExclusionMode(true);
        bool? visibleWhenExcluded = null;
        var window = new CountdownWindow(3)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        window.ApplyCaptureExclusion = target =>
        {
            // The HWND exists but has not been shown on screen yet.
            visibleWhenExcluded = IsWindowVisible(new WindowInteropHelper(target).Handle);
            return true;
        };
        try
        {
            window.Show();
            Assert.False(visibleWhenExcluded);
            Assert.True(window.IsCaptureExcluded);
        }
        finally { window.Close(); }
    });

    private static void Run(bool motion, Action<CountdownWindow> body, bool layered = true, bool excluded = true) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        using var mode = new ExclusionMode(layered);
        // Off screen and never activated: other sessions may be using this desktop.
        var window = new CountdownWindow(3)
        {
            ApplyCaptureExclusion = _ => excluded,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            ControlStateMotionTests.Drain(window);
            // The test owns the clock: each second is one explicit tick.
            Field<DispatcherTimer>(window, "_timer").Stop();
            body(window);
        }
        finally
        {
            if (!closed) window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    private static void Tick(CountdownWindow window) =>
        typeof(CountdownWindow).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [null, EventArgs.Empty]);

    private static T Field<T>(CountdownWindow window, string name) =>
        (T)typeof(CountdownWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
