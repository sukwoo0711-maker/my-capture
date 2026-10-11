using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The quick access card shows at once that an action started, shows progress only for work
/// that takes a while, and keeps the card long enough to read the result.
/// </summary>
public sealed class QuickAccessFeedbackTests : KoreanCaptionTest
{
    [Fact]
    public Task DisablesTheRunningActionAndShowsProgressOnlyAfterTheDelay() => RunStaAsync(async () =>
    {
        var copy = new TaskCompletionSource<bool>();
        QuickAccessWindow card = Card(copy: () => copy.Task);
        try
        {
            Button copyButton = ActionButton(card, "QuickAccess.Copy");
            Button saveButton = ActionButton(card, "QuickAccess.Save");
            card.ShortcutActions["Copy"]();
            Assert.False(copyButton.IsEnabled);
            Assert.True(saveButton.IsEnabled);
            Assert.Equal((UiText.Get("QuickAccess.Copying"), FeedbackKind.Progress, true, false), card.StatusForTest);
            // Pressing the same key again does not start a second copy.
            card.ShortcutActions["Copy"]();
            Assert.Contains("busy=True", card.TimeoutStateForTest, StringComparison.Ordinal);

            await Task.Delay(FeedbackTiming.ProgressRevealDelay + TimeSpan.FromMilliseconds(250));
            Assert.True(card.StatusForTest.Progress);

            copy.SetResult(true);
            await Task.Yield();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(copyButton.IsEnabled);
            Assert.Equal((UiText.Get("QuickAccess.Copied"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
        }
        finally { card.Close(); }
    });

    [Fact]
    public Task WorkFasterThanTheDelayShowsOnlyItsText() => RunStaAsync(async () =>
    {
        QuickAccessWindow card = Card();
        try
        {
            card.ShortcutActions["Save"]();
            await Task.Delay(FeedbackTiming.ProgressRevealDelay + TimeSpan.FromMilliseconds(250));
            Assert.Equal((UiText.Get("QuickAccess.Saved"), FeedbackKind.Confirmation, true, false), card.StatusForTest);
        }
        finally { card.Close(); }
    });

    [Fact]
    public Task ResultRestartsTheCountdownAndAFailureHoldsTheCard() => RunStaAsync(async () =>
    {
        bool saved = false;
        int closed = 0;
        QuickAccessWindow card = Card(save: () => Task.FromResult(saved));
        card.Closed += (_, _) => closed++;
        try
        {
            card.Advance(TimeSpan.FromSeconds(5), interacting: false);
            Assert.Equal(TimeSpan.FromSeconds(1), card.RemainingForTest);

            // A failure stays with the card until it is closed.
            await card.RunAsync("Save", () => Task.FromResult(saved), "QuickAccess.Saving", "QuickAccess.Saved");
            Assert.Equal((UiText.Get("QuickAccess.Failed"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            card.Advance(TimeSpan.FromHours(1), interacting: false);
            Assert.Equal(0, closed);

            // A successful retry releases it, with at least the hold time left to read the result.
            saved = true;
            await card.RunAsync("Save", () => Task.FromResult(saved), "QuickAccess.Saving", "QuickAccess.Saved");
            Assert.Equal(FeedbackKind.Confirmation, card.StatusForTest.Kind);
            Assert.Equal(FeedbackTiming.HoldAfterResult, card.RemainingForTest);
            card.Advance(TimeSpan.FromSeconds(30), interacting: true);
            Assert.Equal(FeedbackTiming.HoldAfterResult, card.RemainingForTest);
            card.Advance(FeedbackTiming.HoldAfterResult - TimeSpan.FromMilliseconds(100), interacting: false);
            Assert.Equal(0, closed);
            card.Advance(TimeSpan.FromMilliseconds(200), interacting: false);
            Assert.Equal(1, closed);
        }
        finally { if (closed == 0) card.Close(); }
    });

    [Fact]
    public Task KeepsALongerRemainingTimeAfterAResult() => RunStaAsync(async () =>
    {
        QuickAccessWindow card = Card();
        try
        {
            card.Advance(TimeSpan.FromSeconds(1), interacting: false);
            await card.RunAsync("Copy", () => Task.FromResult(true), "QuickAccess.Copying", "QuickAccess.Copied");
            Assert.Equal(QuickAccessWindow.Lifetime - TimeSpan.FromSeconds(1), card.RemainingForTest);
        }
        finally { card.Close(); }
    });

    private static Button ActionButton(QuickAccessWindow card, string key)
    {
        var panel = (StackPanel)((Border)card.Content).Child;
        UniformGrid actions = panel.Children.OfType<DockPanel>().First().Children.OfType<UniformGrid>().Single();
        return actions.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiText.Get(key));
    }

    private static QuickAccessWindow Card(Func<Task<bool>>? copy = null, Func<Task<bool>>? save = null) =>
        new(Image(32, 32), copy ?? (() => Task.FromResult(true)), () => { }, () => Task.CompletedTask,
            save ?? (() => Task.FromResult(true)), () => { }, () => Task.FromResult("unused.png"));

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)200, width * height * 4).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                Task work = action();
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
