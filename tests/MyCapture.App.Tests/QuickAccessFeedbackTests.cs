using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
            Assert.Equal((QuickAccessWindow.FailureText("Save"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
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

    [Fact]
    public Task CopyTextIsTheFifthActionOnTAndReportsEachOutcomeInItsOwnWords() => RunStaAsync(async () =>
    {
        var recognition = new TaskCompletionSource<TextCaptureOutcome>();
        QuickAccessWindow card = Card(copyText: () => recognition.Task);
        try
        {
            Assert.Equal(["Copy", "Edit", "Pin", "Save", "CopyText"], QuickAccessWindow.ActionOrder);
            Button text = card.ActionButtonsForTest[4];
            Assert.Equal("텍스트 복사", AutomationProperties.GetName(text));
            Assert.Equal("T", AutomationProperties.GetAcceleratorKey(text));

            // T runs it like a click; the button is disabled only while its own work runs.
            Assert.True(card.HandleShortcut(Key.T, ModifierKeys.None));
            Assert.False(text.IsEnabled);
            Assert.True(card.ActionButtonsForTest[0].IsEnabled);
            Assert.Equal(("텍스트 인식 중…", FeedbackKind.Progress, true, false), card.StatusForTest);
            recognition.SetResult(new TextCaptureOutcome(TextCaptureStatus.Copied, null, 12));
            await Task.Yield();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(text.IsEnabled);
            Assert.Equal(("텍스트 복사됨", FeedbackKind.Confirmation, true, false), card.StatusForTest);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData(nameof(TextCaptureStatus.NoText), nameof(FeedbackKind.Warning), "텍스트를 찾지 못했습니다.")]
    [InlineData(nameof(TextCaptureStatus.Unavailable), nameof(FeedbackKind.Warning), "이 PC에서는 텍스트 인식을 사용할 수 없습니다.")]
    [InlineData(nameof(TextCaptureStatus.ClipboardFailed), nameof(FeedbackKind.ActionRequired), "텍스트를 복사하지 못했습니다 · T로 다시 시도")]
    [InlineData(nameof(TextCaptureStatus.Failed), nameof(FeedbackKind.ActionRequired), "텍스트를 복사하지 못했습니다 · T로 다시 시도")]
    public void TextOutcomesThatRetryingCannotChangeAreNotHeldAsFailures(string status, string kind, string message)
    {
        (string text, FeedbackKind shown) = QuickAccessWindow.DescribeTextOutcome(
            new TextCaptureOutcome(Enum.Parse<TextCaptureStatus>(status), null, 0));
        Assert.Equal(message, Plain(text));
        Assert.Equal(Enum.Parse<FeedbackKind>(kind), shown);
    }

    [Fact]
    public Task WithoutRecognitionTheTextActionSaysItIsUnavailable() => RunStaAsync(async () =>
    {
        QuickAccessWindow card = Card();
        try
        {
            card.ShortcutActions["CopyText"]();
            await Task.Yield();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal((UiText.Get("TextCapture_Unavailable"), FeedbackKind.Warning, true, false), card.StatusForTest);
            // Nothing to retry: the card still closes on its own.
            Assert.DoesNotContain("held=True", card.TimeoutStateForTest, StringComparison.Ordinal);
        }
        finally { card.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "Copy", "복사하지 못했습니다 · C로 다시 시도")]
    [InlineData("ko-KR", "Pin", "핀으로 띄우지 못했습니다 · P로 다시 시도")]
    [InlineData("ko-KR", "Save", "저장하지 못했습니다 · S로 다시 시도")]
    [InlineData("ko-KR", "Edit", "편집기를 열지 못했습니다 · E로 다시 시도")]
    [InlineData("ko-KR", "CopyText", "텍스트를 복사하지 못했습니다 · T로 다시 시도")]
    [InlineData("ko-KR", "Drag", "파일을 준비하지 못했습니다 · 다시 끌어서 시도")]
    [InlineData("en", "Copy", "Could not copy · Press C to retry")]
    [InlineData("en", "Pin", "Could not pin · Press P to retry")]
    [InlineData("en", "Save", "Could not save · Press S to retry")]
    [InlineData("en", "Edit", "Could not open the editor · Press E to retry")]
    [InlineData("en", "CopyText", "Could not copy text · Press T to retry")]
    public void EachActionNamesWhatFailedAndItsRetryKey(string language, string action, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        string text = QuickAccessWindow.FailureText(action);
        Assert.Equal(expected, Plain(text));
        Assert.DoesNotContain("작업을 완료하지", text, StringComparison.Ordinal);
        // A wrapped line breaks before the retry hint, never inside it.
        string hint = text[(text.LastIndexOf(" · ", StringComparison.Ordinal) + 3)..];
        Assert.DoesNotContain(' ', hint);
    }

    [Fact]
    public Task FailedPinAndEditStayOnTheCardWithTheirOwnMessage() => RunStaAsync(async () =>
    {
        int closed = 0;
        QuickAccessWindow card = new(Image(32, 32), () => Task.FromResult(true),
            () => throw new InvalidOperationException("private detail"),
            () => Task.FromException(new InvalidOperationException("private detail")),
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        card.Closed += (_, _) => closed++;
        try
        {
            card.ShortcutActions["Pin"]();
            await Task.Yield();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal((QuickAccessWindow.FailureText("Pin"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);

            // Edit used to close the card even when the editor did not open.
            Assert.True(card.HandleShortcut(Key.E, ModifierKeys.None));
            Assert.Equal(0, closed);
            Assert.Equal((QuickAccessWindow.FailureText("Edit"), FeedbackKind.ActionRequired, true, false), card.StatusForTest);
            Assert.Contains("held=True", card.TimeoutStateForTest, StringComparison.Ordinal);
        }
        finally { if (closed == 0) card.Close(); }
    });

    private static string Plain(string text) => text.Replace(' ', ' ');

    private static Button ActionButton(QuickAccessWindow card, string key) =>
        card.ActionButtonsForTest.Single(button => AutomationProperties.GetName(button) == UiText.Get(key));

    private static QuickAccessWindow Card(Func<Task<bool>>? copy = null, Func<Task<bool>>? save = null,
        Func<Task<TextCaptureOutcome>>? copyText = null) =>
        new(Image(32, 32), copy ?? (() => Task.FromResult(true)), () => { }, () => Task.CompletedTask,
            save ?? (() => Task.FromResult(true)), () => { }, () => Task.FromResult("unused.png"), copyText: copyText);

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
        var thread = WpfTestThread.Create(() =>
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
