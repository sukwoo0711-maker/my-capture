using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The landing ornament never scales content, takes focus, or outlives its pin.</summary>
public sealed class PinLandingDecorationTests
{
    // Independent samples of the authored curves, calculated by solving the cubic's x
    // coordinate before evaluating y. Keep constants here rather than duplicating Render.
    public static IEnumerable<object[]> Frames()
    {
        yield return [0d, new double[] { -15, -17, -30, 1.35, 0, .4, 0, 22, 36, 0, 0, 0 }, false];
        yield return [83d, new double[]
        {
            -11.7021544575, -13.2624417185, -23.4043089150, 1.2730502707, 1,
            .4, 0, 17.8227289795, 29.8440216540, .4824114904, 0, 0,
        }, false];
        yield return [150d, new double[] { 0, 0, 0, 1, 1, .4, .7, 3, 8, .44, .38, 1 }, false];
        yield return [195d, new double[]
        {
            0, 0, 0, .86, 1, 1.3994616238, .4786612426, 4.3036455963,
            11.4763882567, .3878541761, .2755427203, .7074036474,
        }, false];
        yield return [300d, new double[]
        {
            0, 0, 0, 1.0051934340, 1, 2.5258249886, .1252958580, 5.7728152025,
            15.3941738735, .3290873919, .1301571649, .3154212746,
        }, false];
        yield return [420d, new double[] { 0, 0, 0, 1, 1, 2.7, 0, 6, 16, .32, .0552351665, .1253152048 }, false];
        yield return [560d, new double[] { 0, 0, 0, 1, 0, 2.7, 0, 6, 16, 0, 0, .0426876275 }, true];
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void Render_MatchesAuthoredLandingAtFixedTimes(double ms, double[] expected, bool complete)
    {
        PinLandingFrame frame = PinLandingDecoration.Render(ms);
        double[] actual =
        [
            frame.PinOffsetX, frame.PinOffsetY, frame.PinRotation, frame.PinScale, frame.PinOpacity,
            frame.RingScale, frame.RingOpacity, frame.ShadowOffsetY, frame.ShadowBlur,
            frame.ShadowOpacity, frame.GlowOpacity, frame.BorderAccentAmount,
        ];
        string[] names =
        [
            "pin x", "pin y", "rotation", "pin scale", "pin opacity", "ring scale", "ring opacity",
            "shadow y", "shadow blur", "shadow opacity", "glow opacity", "border accent",
        ];
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) < .00001,
                $"At {ms}ms, {names[i]}: expected {expected[i]:R}, actual {actual[i]:R}.");
        }
        Assert.Equal(complete, frame.IsComplete);
    }

    [Fact]
    public void Render_ClampsBeforeStartAndCompletesBorderAfterItsDecay()
    {
        Assert.Equal(PinLandingDecoration.Render(0), PinLandingDecoration.Render(-10));
        Assert.Equal(PinLandingDecoration.Render(0), PinLandingDecoration.Render(double.NaN));
        Assert.InRange(PinLandingDecoration.Render(559.999).PinOpacity, double.Epsilon, .00001);
        PinLandingFrame complete = PinLandingDecoration.Render(570);
        Assert.True(complete.IsComplete);
        Assert.Equal(0, complete.BorderAccentAmount);
        Assert.Equal(0, complete.PinOpacity);
        Assert.Equal(0, complete.RingOpacity);
        Assert.Equal(0, complete.ShadowOpacity);
        Assert.Equal(0, complete.GlowOpacity);
        Assert.Equal(complete, PinLandingDecoration.Render(10_000));
    }

    [Theory]
    [InlineData(1d, -1864d, -264d, 428d, 328d)]
    [InlineData(1.5, -1896d, -296d, 642d, 492d)]
    [InlineData(2d, -1928d, -328d, 856d, 656d)]
    public void Layout_UsesPhysicalOriginAndMonitorScaleForAmbientSurface(
        double scale, double left, double top, double width, double height)
    {
        PinLandingLayout layout = PinLandingDecoration.LayoutFor(new RectD(-1800, -200, 300 * scale, 200 * scale), scale);
        Assert.Equal(new RectD(left, top, width, height), layout.PhysicalBounds);
        Assert.Equal(new Rect(64, 64, 300, 200), layout.PinBoundsDip);
        Assert.True(layout.HasAmbientEffects);
        Assert.Equal(scale, layout.Scale);
        // The needle target remains eight DIPs inside the physical pin corner on any monitor.
        Assert.Equal(-1800 + (8 * scale), layout.PhysicalBounds.Left + ((layout.PinBoundsDip.Left + 8) * scale));
        Assert.Equal(-200 + (8 * scale), layout.PhysicalBounds.Top + ((layout.PinBoundsDip.Top + 8) * scale));
    }

    [Theory]
    [InlineData(1d, -1864d, -264d, 120d)]
    [InlineData(1.5, -1896d, -296d, 180d)]
    [InlineData(2d, -1928d, -328d, 240d)]
    public void Layout_LargePinUsesOnlySmallCornerAtEveryScale(double scale, double left, double top, double size)
    {
        PinLandingLayout layout = PinLandingDecoration.LayoutFor(new RectD(-1800, -200, 1600 * scale, 1000 * scale), scale);
        Assert.False(layout.HasAmbientEffects);
        Assert.Equal(new RectD(left, top, size, size), layout.PhysicalBounds);
        Assert.Equal(new Rect(64, 64, 1600, 1000), layout.PinBoundsDip);
        Assert.Equal(scale, layout.Scale);
    }

    [Fact]
    public void Layout_BudgetIncludesMarginAndPhysicalScale()
    {
        double surfaceWidth = PinLandingDecoration.MaxAmbientPixelArea / 500;
        var boundaryPin = new RectD(100, 200, surfaceWidth - 128, 500 - 128);
        Assert.True(PinLandingDecoration.LayoutFor(boundaryPin, 1).HasAmbientEffects);
        Assert.False(PinLandingDecoration.LayoutFor(boundaryPin with { Width = boundaryPin.Width + 1 }, 1).HasAmbientEffects);
        Assert.False(PinLandingDecoration.LayoutFor(
            new RectD(100, 200, boundaryPin.Width * 1.5, boundaryPin.Height * 1.5), 1.5).HasAmbientEffects);
        Assert.Equal(PinLandingDecoration.LayoutFor(boundaryPin, 1), PinLandingDecoration.LayoutFor(boundaryPin, 0));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void Eligibility_RequiresMotionLocalSessionAndLayeredSupport(bool motion, bool remote, bool layered, bool expected) =>
        Assert.Equal(expected, PinLandingDecoration.IsEligible(motion, remote, layered));

    [Fact]
    public void Throttle_SuppressesFourthAndCountsSuppressedOpeningsUntilTheyExpire()
    {
        var throttle = new PinLandingThrottle();
        Assert.True(throttle.TryAcquire(0));
        Assert.True(throttle.TryAcquire(100));
        Assert.True(throttle.TryAcquire(200));
        Assert.False(throttle.TryAcquire(300));
        Assert.False(throttle.TryAcquire(999));
        // Expiring the first three still leaves the two suppressed openings in this second.
        Assert.True(throttle.TryAcquire(1200));
        Assert.False(throttle.TryAcquire(1201));
        Assert.True(throttle.TryAcquire(2201));
    }

    [Fact]
    public void Throttle_ExpiryBoundaryIsExactlyOneSecond()
    {
        var throttle = new PinLandingThrottle();
        Assert.True(throttle.TryAcquire(0));
        Assert.True(throttle.TryAcquire(0));
        Assert.True(throttle.TryAcquire(0));
        Assert.False(throttle.TryAcquire(999.999));
        Assert.True(throttle.TryAcquire(1000));
        Assert.True(throttle.TryAcquire(1000));
        Assert.False(throttle.TryAcquire(1000));
    }

    [Fact]
    public void Reveal_IsLinear83msOpacityWithoutContentScaleAnimation() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        DoubleAnimation animation = PinWindow.CreateRevealAnimation();
        Assert.Equal(TimeSpan.FromMilliseconds(83), animation.Duration.TimeSpan);
        Assert.Equal(0d, animation.From);
        Assert.Equal(1d, animation.To);
        Assert.Null(animation.EasingFunction);
        Assert.Equal(0, animation.AccelerationRatio);
        Assert.Equal(0, animation.DecelerationRatio);
        Assert.Equal(FillBehavior.Stop, animation.FillBehavior);

        PinWindow pin = NewPin();
        try
        {
            // The same entry point is used by Shift+F3, without starting the ornament.
            pin.ShowPin();
            AssertContentScaleUnchanged(pin);
            Assert.False(pin.LandingStarted);
            Assert.Null(pin.LandingDecoration);
            Assert.Equal(1d, (double)Assert.IsType<Grid>(pin.Content).GetAnimationBaseValue(UIElement.OpacityProperty));
            pin.Hide();
            pin.ShowPin();
            AssertContentScaleUnchanged(pin);
            Assert.Null(pin.LandingDecoration);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void Decoration_IsPassiveClickThroughCaptureExcludedAndClosesAt560ms() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        var excluded = new List<Window>();
        PinLandingDecoration.ApplyCaptureExclusion = window => { excluded.Add(window); return true; };
        PinWindow pin = NewPin();
        PinLandingDecoration? decoration = null;
        try
        {
            pin.Show();
            decoration = NewDecoration(pin);
            IntPtr foreground = NativeExclusion.GetForegroundWindow();
            Assert.True(decoration.ShowDecoration());
            Assert.Equal(foreground, NativeExclusion.GetForegroundWindow());
            Assert.False(decoration.IsActive);
            Assert.False(decoration.ShowActivated);
            Assert.False(decoration.ShowInTaskbar);
            Assert.False(decoration.Focusable);
            Assert.False(decoration.IsHitTestVisible);
            Assert.True(decoration.Topmost);
            Assert.True(decoration.AllowsTransparency);
            Assert.Same(pin, decoration.Owner);
            Assert.Equal([decoration], excluded);
            Assert.True(decoration.IsCaptureExcluded);
            IntPtr handle = new WindowInteropHelper(decoration).Handle;
            const long requiredStyles = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            Assert.Equal(requiredStyles, GetWindowLongPtr(handle, -20).ToInt64() & requiredStyles);

            int closed = 0;
            decoration.Closed += (_, _) => closed++;
            decoration.Advance(559.999);
            Assert.False(decoration.IsClosed);
            Assert.True(decoration.IsVisible);
            Assert.Equal(PinLandingDecoration.Render(559.999), decoration.State);
            decoration.Advance(560);
            Assert.True(decoration.IsClosed);
            Assert.False(decoration.IsVisible);
            Assert.Equal(PinLandingDecoration.Render(560), decoration.State);
            Assert.Equal(1, closed);
            decoration.Advance(700);
            Assert.Equal(1, closed);
            Assert.Equal(foreground, NativeExclusion.GetForegroundWindow());
        }
        finally { decoration?.Close(); pin.Close(); }
    });

    [Fact]
    public void Decoration_PreservesExistingForegroundWindow() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinWindow pin = NewPin();
        PinLandingDecoration? decoration = null;
        try
        {
            pin.Show();
            decoration = NewDecoration(pin);
            IntPtr foreground = NativeExclusion.GetForegroundWindow();
            Assert.True(decoration.ShowDecoration());
            Assert.False(decoration.IsActive);
            Assert.Equal(foreground, NativeExclusion.GetForegroundWindow());
            decoration.Advance(560);
            Assert.Equal(foreground, NativeExclusion.GetForegroundWindow());
            Assert.True(foreground != IntPtr.Zero,
                "The desktop did not expose a foreground HWND; foreground preservation needs an interactive desktop run.");
        }
        finally { decoration?.Close(); pin.Close(); }
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5)]
    [InlineData(2d)]
    public void Decoration_AppliesPhysicalBoundsAtEachSuppliedMonitorScale(double scale) => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinWindow pin = NewPin();
        PinLandingDecoration? decoration = null;
        try
        {
            pin.Show();
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            PinLandingLayout layout = PinLandingDecoration.LayoutFor(new RectD(left, top, right - left, bottom - top), scale);
            decoration = new PinLandingDecoration(pin, layout, new Stopwatch());
            Assert.True(decoration.ShowDecoration());
            (int actualLeft, int actualTop, int actualRight, int actualBottom) =
                WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(decoration).Handle);
            Assert.Equal(layout.PhysicalBounds.Left, actualLeft);
            Assert.Equal(layout.PhysicalBounds.Top, actualTop);
            Assert.Equal(layout.PhysicalBounds.Right, actualRight);
            Assert.Equal(layout.PhysicalBounds.Bottom, actualBottom);
        }
        finally { decoration?.Close(); pin.Close(); }
    });

    [Fact]
    public void CaptureExclusionFailure_DoesNotShowDecorationOrDiscardPin() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        int attempts = 0;
        PinLandingDecoration.ApplyCaptureExclusion = _ => { attempts++; return false; };
        PinWindow pin = NewPin();
        try
        {
            pin.Show();
            pin.StartLandingEffect();
            Assert.Equal(1, attempts);
            Assert.True(pin.IsVisible);
            Assert.False(pin.IsClosed);
            Assert.True(pin.LandingStarted);
            Assert.Null(pin.LandingDecoration);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityProbeFailure_KeepsPinAndCompletesOpenNotifications(bool nativeFailure) => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinLandingDecoration.ReadLayeredSupport = () =>
        {
            if (nativeFailure) throw new System.ComponentModel.Win32Exception(5);
            throw new InvalidOperationException("Layered-window capability unavailable.");
        };
        PinManager manager = NewManager();
        int activated = 0;
        int created = 0;
        int changed = 0;
        manager.ActivatePinForTest = _ => activated++;
        manager.PinCreated += () => created++;
        manager.OpenPinsChanged += () => changed++;
        try
        {
            PinWindow pin = manager.PinImage(Image());
            Assert.True(pin.IsVisible);
            Assert.False(pin.IsClosed);
            Assert.True(pin.LandingStarted);
            Assert.Null(pin.LandingDecoration);
            Assert.Equal(1, manager.Count);
            Assert.Equal(1, activated);
            Assert.Equal(1, created);
            Assert.Equal(1, changed);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void SuppressedEnvironments_NeverCreateDecoration(bool motion, bool remote, bool layered) => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks(motion, remote, layered);
        int attempts = 0;
        PinLandingDecoration.ApplyCaptureExclusion = _ => { attempts++; return true; };
        PinWindow pin = NewPin();
        try
        {
            pin.Resources[PinFrameColor.AccentKey] = new SolidColorBrush(Colors.Crimson);
            pin.ShowPin();
            pin.StartLandingEffect();
            Assert.True(pin.LandingStarted);
            Assert.Null(pin.LandingDecoration);
            Assert.Equal(0, attempts);
            AssertContentScaleUnchanged(pin);
            if (!motion)
            {
                Grid chrome = Assert.IsType<Grid>(pin.Content);
                Assert.Equal(1, chrome.Opacity);
                Assert.False(DependencyPropertyHelper.GetValueSource(chrome, UIElement.OpacityProperty).IsAnimated);
                Assert.Equal(Colors.Crimson, pin.FrameColorForTest);
            }
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReducedMotionBorder_RealTimerRestoresCurrentThemeAndInteractionState(bool activeAtExpiry) => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks(motion: false);
        PinWindow pin = NewPin();
        DispatcherTimer? guard = null;
        try
        {
            pin.Resources[PinFrameColor.AccentKey] = new SolidColorBrush(Colors.Crimson);
            pin.Resources[PinFrameColor.NeutralKey] = new SolidColorBrush(Colors.White);
            pin.Show();
            pin.SetChromeActiveForTest(false);
            pin.StartLandingEffect();
            Assert.Equal(Colors.Crimson, pin.FrameColorForTest);
            DispatcherTimer reset = Assert.IsType<DispatcherTimer>(Field<DispatcherTimer?>(pin, "_landingResetTimer"));
            Assert.Equal(TimeSpan.FromMilliseconds(570), reset.Interval);
            Assert.True(reset.IsEnabled);
            Assert.False(Field<SolidColorBrush>(pin, "_chromeBrush").HasAnimatedProperties);

            // Theme publication replaces the token while the landing emphasis is held.
            pin.Resources[PinFrameColor.AccentKey] = new SolidColorBrush(Colors.RoyalBlue);
            Assert.Equal(Colors.RoyalBlue, pin.FrameColorForTest);
            SetChromeHover(pin, true);
            Assert.Equal(Colors.RoyalBlue, pin.FrameColorForTest);
            SetChromeHover(pin, false);
            pin.SetChromeActiveForTest(activeAtExpiry);
            Assert.Equal(Colors.RoyalBlue, pin.FrameColorForTest);

            bool ticked = false;
            var frame = new DispatcherFrame();
            reset.Tick += (_, _) => { ticked = true; frame.Continue = false; };
            guard = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, pin.Dispatcher);
            Dispatcher.PushFrame(frame);
            guard.Stop();

            Assert.True(ticked, "The production 570ms border reset timer did not tick within 900ms.");
            Assert.False(reset.IsEnabled);
            Assert.Null(Field<DispatcherTimer?>(pin, "_landingResetTimer"));
            Assert.Null(Field<Stopwatch?>(pin, "_landingClock"));
            Assert.Equal(0, Field<double>(pin, "_landingAccentAmount"));
            Assert.Null(pin.LandingDecoration);
            bool contrast = ThemeService.IsSystemHighContrast || ThemeService.Current == AppTheme.HighContrast;
            Color resting = contrast ? Colors.White : Color.FromArgb(153, Colors.RoyalBlue.R, Colors.RoyalBlue.G, Colors.RoyalBlue.B);
            Assert.Equal(activeAtExpiry ? Colors.RoyalBlue : resting, pin.FrameColorForTest);
            Assert.False(Field<SolidColorBrush>(pin, "_chromeBrush").HasAnimatedProperties);
            pin.SetChromeActiveForTest(false);
            SetChromeHover(pin, true);
            Assert.Equal(Colors.RoyalBlue, pin.FrameColorForTest);
            SetChromeHover(pin, false);
            Assert.Equal(resting, pin.FrameColorForTest);
        }
        finally { guard?.Stop(); pin.Close(); }
    });

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("hide")]
    [InlineData("close")]
    public void OwnerChanges_CancelDecorationImmediately(string change) => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinWindow pin = NewPin();
        try
        {
            pin.Show();
            pin.StartLandingEffect();
            PinLandingDecoration decoration = Assert.IsType<PinLandingDecoration>(pin.LandingDecoration);
            Assert.False(decoration.IsClosed);
            switch (change)
            {
                case "move": pin.Left += 20; break;
                case "resize": pin.Width += 20; pin.UpdateLayout(); break;
                case "hide": pin.HidePin(); break;
                case "close": pin.Close(); break;
                default: throw new InvalidOperationException(change);
            }
            Assert.True(decoration.IsClosed);
            Assert.False(decoration.IsVisible);
            Assert.Null(pin.LandingDecoration);
            if (change != "close") Assert.False(pin.IsClosed);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void Restart_ClosesPreviousDecorationBeforeShowingAnother() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinWindow pin = NewPin();
        try
        {
            pin.Show();
            pin.StartLandingEffect();
            PinLandingDecoration first = Assert.IsType<PinLandingDecoration>(pin.LandingDecoration);
            bool firstClosedBeforeSecondShow = false;
            PinLandingDecoration.ApplyCaptureExclusion = _ => { firstClosedBeforeSecondShow = first.IsClosed; return true; };
            pin.StartLandingEffect();
            PinLandingDecoration second = Assert.IsType<PinLandingDecoration>(pin.LandingDecoration);
            Assert.NotSame(first, second);
            Assert.True(firstClosedBeforeSecondShow);
            Assert.True(first.IsClosed);
            Assert.False(second.IsClosed);
            Assert.True(second.IsVisible);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SessionRestore_HasNoOrnamentButExplicitReopenDoes() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinManager manager = NewManager();
        try
        {
            PinContent content = PinContent.FromImage(Image());
            var snapshot = new PinManager.ClosedPin(content, 300, 200, 1, 1, null);
            PinWindow restored = manager.RestoreSessionPin(snapshot);
            Assert.False(restored.LandingStarted);
            Assert.Null(restored.LandingDecoration);
            restored.Close();
            PinWindow reopened = Assert.IsType<PinWindow>(manager.RestoreLastClosed());
            Assert.True(reopened.LandingStarted);
            Assert.IsType<PinLandingDecoration>(reopened.LandingDecoration);
            Assert.Same(content, reopened.PinnedContent);
            manager.HideOrShowAll();
            Assert.Null(reopened.LandingDecoration);
            manager.HideOrShowAll();
            Assert.Null(reopened.LandingDecoration);
            Assert.True(reopened.IsVisible);
            AssertContentScaleUnchanged(reopened);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void NewPins_StartDecorationOnlyAfterShowReturnsInTheSameTurn() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinManager manager = NewManager();
        var shown = new List<PinWindow>();
        manager.ShowPinForTest = (pin, show) =>
        {
            Assert.False(pin.LandingStarted);
            show();
            Assert.False(pin.LandingStarted);
            shown.Add(pin);
        };
        PinLandingDecoration.ApplyCaptureExclusion = decoration =>
        {
            Assert.Contains(Assert.IsType<PinWindow>(decoration.Owner), shown);
            return true;
        };
        try
        {
            PinWindow pin = manager.PinImage(Image());
            Assert.Equal([pin], shown);
            Assert.True(pin.LandingStarted);
            Assert.IsType<PinLandingDecoration>(pin.LandingDecoration);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void BurstOpening_FourthAndLaterKeepFadeAndBorderWithoutDecoration() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinManager manager = NewManager();
        var excluded = new List<Window>();
        PinLandingDecoration.ApplyCaptureExclusion = window => { excluded.Add(window); return true; };
        try
        {
            for (int index = 0; index < 5; index++)
            {
                PinWindow pin = manager.PinImage(Image());
                Assert.True(pin.LandingStarted);
                Assert.True(pin.IsVisible);
                AssertContentScaleUnchanged(pin);
                if (index < 3) Assert.IsType<PinLandingDecoration>(pin.LandingDecoration);
                else Assert.Null(pin.LandingDecoration);
                // The border clock still runs when the expensive ornament is suppressed.
                Assert.NotNull(Field<Stopwatch?>(pin, "_landingClock"));
            }
            Assert.Equal(3, excluded.Count);
            Assert.Equal(5, manager.Count);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void AmbientDrawing_LeavesPinInteriorTransparent() => StaTestHost.Run(() =>
    {
        using var hooks = new LandingHooks();
        PinWindow pin = NewPin();
        PinLandingDecoration? decoration = null;
        try
        {
            pin.Show();
            decoration = new PinLandingDecoration(pin,
                PinLandingDecoration.LayoutFor(new RectD(-10000, -10000, 300, 200), 1), new Stopwatch());
            decoration.Advance(195);
            var surface = Assert.IsAssignableFrom<FrameworkElement>(decoration.Content);
            surface.Measure(new Size(428, 328));
            surface.Arrange(new Rect(0, 0, 428, 328));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(428, 328, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[428 * 328 * 4];
            bitmap.CopyPixels(pixels, 428 * 4, 0);
            // Central content and inner lower edge stay untouched; the shadow exists just outside.
            Assert.Equal(0, AlphaAt(pixels, 428, 210, 160));
            Assert.Equal(0, AlphaAt(pixels, 428, 210, 262));
            Assert.InRange(AlphaAt(pixels, 428, 210, 268), (byte)1, byte.MaxValue);
        }
        finally { decoration?.Close(); pin.Close(); }
    });

    private static byte AlphaAt(byte[] pixels, int width, int x, int y) => pixels[((y * width) + x) * 4 + 3];

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void SetChromeHover(PinWindow pin, bool hovering) => typeof(PinWindow)
        .GetMethod("SetChromeHover", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pin, [hovering]);

    private static void AssertContentScaleUnchanged(PinWindow pin)
    {
        ScaleTransform scale = Assert.IsType<ScaleTransform>(Assert.IsType<Grid>(pin.Content).RenderTransform);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
        Assert.False(scale.HasAnimatedProperties);
    }

    private static PinLandingDecoration NewDecoration(PinWindow pin)
    {
        (int left, int top, int right, int bottom) = pin.PhysicalBounds;
        double scale = MonitorEnumerator.GetFromWindow(pin.Handle).ScaleFactor;
        // A stopped clock prevents real rendering ticks from advancing a deterministic test.
        return new PinLandingDecoration(pin,
            PinLandingDecoration.LayoutFor(new RectD(left, top, right - left, bottom - top), scale), new Stopwatch());
    }

    private static PinWindow NewPin() => new(PinContent.FromImage(Image()), new PinViewState(300, 200, 1, 1, .1), -10000, -10000)
    {
        ShowActivated = false,
    };

    private static PinManager NewManager() => new(() => new PinSettings(), NullLogger.Instance)
    {
        ReadPlacementMonitorForTest = () => OffscreenMonitor,
        ReadRestoreMonitorsForTest = () => [OffscreenMonitor],
        BeforeShowForTest = pin => pin.ShowActivated = false,
        ActivatePinForTest = _ => { },
    };

    private static MonitorInfo OffscreenMonitor => new("landing-test", new RectD(-12000, -12000, 4000, 4000),
        new RectD(-12000, -12000, 4000, 4000), 96, false);

    private static BitmapSource Image()
    {
        var pixels = new byte[300 * 200 * 4];
        Array.Fill(pixels, (byte)0xF0);
        BitmapSource image = BitmapSource.Create(300, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 300 * 4);
        image.Freeze();
        return image;
    }

    private sealed class LandingHooks : IDisposable
    {
        private readonly bool? _motion = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly Func<bool> _remote = PinLandingDecoration.ReadRemoteSession;
        private readonly Func<bool> _layered = PinLandingDecoration.ReadLayeredSupport;
        private readonly Func<Window, bool> _exclusion = PinLandingDecoration.ApplyCaptureExclusion;
        private readonly PinLandingThrottle _throttle = PinLandingDecoration.Throttle;
        private readonly Func<double> _openingTime = PinLandingDecoration.ReadOpeningTime;

        internal LandingHooks(bool motion = true, bool remote = false, bool layered = true)
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            PinLandingDecoration.ReadRemoteSession = () => remote;
            PinLandingDecoration.ReadLayeredSupport = () => layered;
            PinLandingDecoration.ApplyCaptureExclusion = _ => true;
            PinLandingDecoration.Throttle = new PinLandingThrottle();
            PinLandingDecoration.ReadOpeningTime = () => 0;
        }

        public void Dispose()
        {
            FluidMotion.AnimationsEnabledOverrideForTest = _motion;
            PinLandingDecoration.ReadRemoteSession = _remote;
            PinLandingDecoration.ReadLayeredSupport = _layered;
            PinLandingDecoration.ApplyCaptureExclusion = _exclusion;
            PinLandingDecoration.Throttle = _throttle;
            PinLandingDecoration.ReadOpeningTime = _openingTime;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
