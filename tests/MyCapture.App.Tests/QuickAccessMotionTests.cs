using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The quick access card enters and leaves like the other floating windows, confirms a success
/// it was already showing (feel bench 2-B), and counts down on a continuous bar. Its state,
/// countdown and events never wait for the motion, and everything snaps without motion.
/// </summary>
public sealed class QuickAccessMotionTests
{
    private static readonly Geometry CheckGeometry = Frozen(Geometry.Parse("M3.75,10.25 L8.15,14.65 L16.25,5.35"));

    [Fact]
    public void CardEntersWithTheSharedEntranceUnlessACaptureLandsInIt() => Run(motion: true, router =>
    {
        QuickAccessWindow card = Card(router);
        QuickAccessWindow landing = Card(router);
        try
        {
            Assert.True(FluidMotion.GetWindowEntrance(card));

            landing.PrepareLanding();
            Assert.True(landing.IsLanding);
            Assert.False(FluidMotion.GetWindowEntrance(landing));
            Assert.Equal(0, landing.SurfaceForTest.Opacity);
            Assert.Equal(0, landing.PreviewForTest.Opacity);
            // A flight that never started leaves the card to the shared entrance after all.
            landing.CompleteLanding();
            Assert.False(landing.IsLanding);
            Assert.True(FluidMotion.GetWindowEntrance(landing));
            Assert.Equal(1, landing.SurfaceForTest.Opacity);
            Assert.Equal(1, landing.PreviewForTest.Opacity);
        }
        finally { card.Close(); landing.Close(); }
    });

    [Theory]
    [InlineData("Close", true)]
    [InlineData("Dialog.Close", true)]
    [InlineData("Edit", true)]
    [InlineData("QuickAccess.Library", true)]
    [InlineData("Timeout", true)]
    [InlineData("Close", false)]
    [InlineData("Timeout", false)]
    public void EveryCloseFadesDownAfterStoppingTheCountdownAtOnce(string path, bool motion) => Run(motion, router =>
    {
        int closed = 0, edited = 0, library = 0;
        QuickAccessWindow card = new(Image(), () => Task.FromResult(true), () => edited++, () => Task.CompletedTask,
            () => Task.FromResult(true), () => library++, () => Task.FromResult("unused.png"),
            targetMonitor: Offscreen(), keyboardRouter: router);
        card.Closed += (_, _) => closed++;
        try
        {
            card.Show();
            ControlStateMotionTests.Drain(card);
            Assert.Contains("enabled=True", card.TimeoutStateForTest, StringComparison.Ordinal);
            Assert.True(card.CanActivateByHotkey);

            switch (path)
            {
                case "Close": card.ShortcutActions["Close"](); break;
                case "Edit": card.ShortcutActions["Edit"](); break;
                case "Timeout": card.Advance(QuickAccessWindow.Lifetime, interacting: false); break;
                default: Click(card, path); break;
            }

            // The command and the countdown are done at once; only the pixels may still be leaving.
            Assert.Equal(path == "Edit" ? 1 : 0, edited);
            Assert.Equal(path == "QuickAccess.Library" ? 1 : 0, library);
            Assert.Contains("enabled=False", card.TimeoutStateForTest, StringComparison.Ordinal);
            Assert.False(card.CanActivateByHotkey);
            Assert.Equal(motion ? 0 : 1, closed);
            Assert.Equal(motion, FluidMotion.IsExiting(card));
            if (motion)
            {
                Assert.False(card.IsHitTestVisible);
                Assert.IsType<TranslateTransform>(card.SurfaceForTest.RenderTransform);
                // A second request while leaving changes nothing.
                card.RequestClose();
                FluidMotion.CompleteExit(card);
                Assert.Equal(1, closed);
            }
            card.CloseNow();
            Assert.Equal(1, closed);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Fact]
    public void ANewerCardOrACaptureClosesTheCardAtOnce() => Run(motion: true, router =>
    {
        int closed = 0, dismissed = 0;
        QuickAccessWindow leaving = Shown(router);
        leaving.Closed += (_, _) => closed++;
        leaving.RequestClose();
        Assert.Equal(0, closed);
        // A newer card does not wait for the older one's exit.
        leaving.CloseNow();
        Assert.Equal(1, closed);

        QuickAccessWindow card = Shown(router);
        card.Closed += (_, _) => closed++;
        card.DismissForCapture(() => dismissed++);
        Assert.Equal((2, 1), (closed, dismissed));
        Assert.False(card.IsVisible);
    });

    [Fact]
    public Task RepeatSuccessRedrawsTheCheckTintsTheTextAndMarksTheButton() => RunAsync(motion: true, async router =>
    {
        var copy = new TaskCompletionSource<bool>();
        QuickAccessWindow card = Shown(router, () => copy.Task);
        try
        {
            SymbolIcon glyph = card.StatusGlyphForTest;
            Button copyButton = card.ActionButtonsForTest[4];
            SymbolIcon icon = ActionIcon(copyButton);
            Geometry copyIcon = icon.Data!;
            Brush restText = card.StatusForegroundForTest;

            card.ShortcutActions["Copy"]();
            Assert.False(card.ShowsSuccessCheckForTest("Copy"));
            copy.SetResult(true);
            await Settle();

            // The card already said "copied"; this time the check draws in again and the text glows.
            Assert.Equal((UiText.Get("QuickAccess.Copied"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
            Assert.True(StrokeDrawMotion.IsDrawing(glyph));
            Assert.InRange(StrokeDrawMotion.GetProgress(glyph), 0, 0.999);
            Assert.NotSame(restText, card.StatusForegroundForTest);
            Assert.True(card.StatusForegroundForTest.HasAnimatedProperties);
            // The button's icon crossfades to a check, which is state for 1.2 s.
            Assert.True(card.ShowsSuccessCheckForTest("Copy"));
            Assert.Same(CheckGeometry, icon.Data);
            Assert.True(icon.HasAnimatedProperties);
            var face = (Grid)icon.Parent;
            Assert.Equal(3, face.Children.Count);
            Assert.True(PumpUntil(() => face.Children.Count == 2 && !StrokeDrawMotion.IsDrawing(glyph)));
            Assert.Same(CheckGeometry, glyph.Data);

            card.ElapseSuccessCheckForTest();
            Assert.False(card.ShowsSuccessCheckForTest("Copy"));
            Assert.Same(copyIcon, icon.Data);
        }
        finally { card.CloseNow(); }
    });

    [Fact]
    public Task WithoutMotionTheCheckIsStillShownButNothingAnimates() => RunAsync(motion: false, async router =>
    {
        QuickAccessWindow card = Shown(router);
        try
        {
            SymbolIcon icon = ActionIcon(card.ActionButtonsForTest[2]);
            Brush restText = card.StatusForegroundForTest;
            card.ShortcutActions["Save"]();
            await Settle();
            Assert.True(card.ShowsSuccessCheckForTest("Save"));
            Assert.Same(CheckGeometry, icon.Data);
            Assert.False(icon.HasAnimatedProperties);
            Assert.Equal(2, ((Grid)icon.Parent).Children.Count);
            Assert.False(StrokeDrawMotion.IsDrawing(card.StatusGlyphForTest));
            Assert.Same(restText, card.StatusForegroundForTest);
        }
        finally { card.CloseNow(); }
    });

    [Fact]
    public Task AFailureLeavesNoCheckAndARetryRestartsIt() => RunAsync(motion: false, async router =>
    {
        bool saved = false;
        QuickAccessWindow card = Shown(router, save: () => Task.FromResult(saved));
        try
        {
            card.ShortcutActions["Save"]();
            await Settle();
            Assert.False(card.ShowsSuccessCheckForTest("Save"));
            saved = true;
            card.ShortcutActions["Save"]();
            await Settle();
            Assert.True(card.ShowsSuccessCheckForTest("Save"));
            Assert.False(card.ShowsSuccessCheckForTest("Copy"));
        }
        finally { card.CloseNow(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CountdownBarGlidesBetweenTicksAndStopsWhenPaused(bool motion) => Run(motion, router =>
    {
        QuickAccessWindow card = Shown(router);
        try
        {
            ProgressBar bar = card.RemainingBarForTest;
            TimeSpan before = card.RemainingForTest;
            card.Advance(TimeSpan.FromMilliseconds(100), interacting: false);
            TimeSpan after = card.RemainingForTest;
            Assert.Equal(before - TimeSpan.FromMilliseconds(100), after);
            double exact = 100 * after.TotalSeconds / QuickAccessWindow.Lifetime.TotalSeconds;
            // The countdown itself is exact at once; only the drawing glides to it over the tick.
            Assert.Equal(exact, (double)bar.GetAnimationBaseValue(RangeBase.ValueProperty), 6);
            Assert.Equal(motion, bar.HasAnimatedProperties);
            if (!motion) Assert.Equal(exact, bar.Value, 6);

            // Paused, neither the countdown nor the bar's value moves.
            card.Advance(TimeSpan.FromMilliseconds(100), interacting: true);
            Assert.Equal(after, card.RemainingForTest);
            Assert.Equal(exact, (double)bar.GetAnimationBaseValue(RangeBase.ValueProperty), 6);
        }
        finally { card.CloseNow(); }
    });

    private static void Click(QuickAccessWindow card, string automationId)
    {
        Button button = QuickAccessDetachedFixture.Descendants(card).OfType<Button>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == automationId);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static SymbolIcon ActionIcon(Button button) => button.Content switch
    {
        SymbolIcon icon => icon,
        Grid face => Assert.IsType<SymbolIcon>(face.Children[0]),
        _ => throw new Xunit.Sdk.XunitException("The action has no icon."),
    };

    private static QuickAccessWindow Card(ReceiptKeyboardRouter router, Func<Task<bool>>? copy = null,
        Func<Task<bool>>? save = null)
    {
        var card = new QuickAccessWindow(Image(), copy ?? (() => Task.FromResult(true)), () => { },
            () => Task.CompletedTask, save ?? (() => Task.FromResult(true)), () => { }, () => Task.FromResult("unused.png"),
            targetMonitor: Offscreen(), keyboardRouter: router);
        card.Resources["Icon.Check"] = CheckGeometry;
        card.Resources["State.Success"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        card.Resources["Text.Primary"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xEB));
        return card;
    }

    private static QuickAccessWindow Shown(ReceiptKeyboardRouter router, Func<Task<bool>>? copy = null,
        Func<Task<bool>>? save = null)
    {
        QuickAccessWindow card = Card(router, copy, save);
        card.Show();
        ControlStateMotionTests.Drain(card);
        return card;
    }

    // Off the visible desktop, at the primary display's scale.
    private static MonitorInfo Offscreen()
    {
        var bounds = new RectD(-32000, -32000, 1600, 1000);
        return new MonitorInfo("TEST", bounds, bounds, MonitorEnumerator.GetPrimary().Dpi, false);
    }

    private static async Task Settle()
    {
        await Task.Yield();
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private static bool PumpUntil(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            tick.Stop();
        }
        return done();
    }

    private static void Run(bool motion, Action<ReceiptKeyboardRouter> body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        using var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            body(router);
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = previous; }
    });

    private static Task RunAsync(bool motion, Func<ReceiptKeyboardRouter, Task> body)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
            var router = new ReceiptKeyboardRouter(_ => new NoHotkeys());
            try
            {
                FluidMotion.AnimationsEnabledOverrideForTest = motion;
                Task work = body(router);
                _ = work.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Dispatcher.Run();
                work.GetAwaiter().GetResult();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(new Xunit.Sdk.XunitException($"Async STA body threw: {ex}"));
            }
            finally
            {
                router.Dispose();
                FluidMotion.AnimationsEnabledOverrideForTest = previous;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id)
        {
        }
    }

    private static BitmapSource Image()
    {
        byte[] pixels = Enumerable.Repeat((byte)200, 64 * 48 * 4).ToArray();
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        image.Freeze();
        return image;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
