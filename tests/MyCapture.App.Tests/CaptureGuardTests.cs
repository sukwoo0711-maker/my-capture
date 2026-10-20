using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A capture command that cannot start says why. These tests pin the decision (which blocker is
/// reported) and the editor's side of it: the unsaved-edits question comes forward, a second press
/// while it is open waits for the answer instead of re-entering Close, and every answer ends with
/// the capture started or the reason on screen.
/// </summary>
public sealed class CaptureGuardTests
{
    [Fact]
    public void NothingInTheWay_LetsEveryCaptureStart()
    {
        Assert.Equal(CaptureBlock.None, CaptureGuard.Find(default, allowRetake: false));
        Assert.Equal(CaptureBlock.None, CaptureGuard.Find(default, allowRetake: true));
        var stillsAllowed = new CaptureGuardState { RecordingActive = true, RecordingCanCaptureStill = true };
        Assert.Equal(CaptureBlock.None, CaptureGuard.Find(stillsAllowed, allowRetake: false));
    }

    [Fact]
    public void EachBlocker_IsReportedAsItself()
    {
        (CaptureGuardState State, CaptureBlock Block)[] cases =
        [
            (new() { AppUpdating = true }, CaptureBlock.AppUpdating),
            (new() { AppExiting = true }, CaptureBlock.AppExiting),
            (new() { CaptureStarting = true }, CaptureBlock.CaptureStarting),
            (new() { Selecting = true }, CaptureBlock.Selecting),
            (new() { EditorOpening = true }, CaptureBlock.EditorOpening),
            (new() { EditorOpen = true }, CaptureBlock.EditorOpen),
            (new() { EditorOpen = true, EditorAsking = true }, CaptureBlock.EditorAsking),
            (new() { EditorOpen = true, EditorSaving = true }, CaptureBlock.EditorSaving),
            (new() { EditorOpen = true, EditorBusy = true }, CaptureBlock.EditorBusy),
            (new() { Countdown = true }, CaptureBlock.Countdown),
            (new() { ScrollingCapture = true }, CaptureBlock.ScrollingCapture),
            (new() { RecordingActive = true }, CaptureBlock.RecordingBusy),
        ];
        foreach ((CaptureGuardState state, CaptureBlock block) in cases)
            Assert.Equal(block, CaptureGuard.Find(state, allowRetake: false));
    }

    [Fact]
    public void BlockersAreReported_InTheOrderTheShellChecksThem()
    {
        var state = new CaptureGuardState
        {
            AppUpdating = true, AppExiting = true, CaptureStarting = true, Selecting = true, EditorOpening = true,
            EditorOpen = true, EditorAsking = true, Countdown = true, ScrollingCapture = true, RecordingActive = true,
        };
        CaptureBlock[] order =
        [
            CaptureBlock.AppUpdating, CaptureBlock.AppExiting, CaptureBlock.CaptureStarting, CaptureBlock.Selecting,
            CaptureBlock.EditorOpening, CaptureBlock.EditorAsking, CaptureBlock.Countdown, CaptureBlock.ScrollingCapture,
            CaptureBlock.RecordingBusy, CaptureBlock.None,
        ];
        foreach (CaptureBlock expected in order)
        {
            Assert.Equal(expected, CaptureGuard.Find(state, allowRetake: true));
            state = expected switch
            {
                CaptureBlock.AppUpdating => state with { AppUpdating = false },
                CaptureBlock.AppExiting => state with { AppExiting = false },
                CaptureBlock.CaptureStarting => state with { CaptureStarting = false },
                CaptureBlock.Selecting => state with { Selecting = false },
                CaptureBlock.EditorOpening => state with { EditorOpening = false },
                CaptureBlock.EditorAsking => state with { EditorOpen = false, EditorAsking = false },
                CaptureBlock.Countdown => state with { Countdown = false },
                CaptureBlock.ScrollingCapture => state with { ScrollingCapture = false },
                _ => state with { RecordingActive = false },
            };
        }
    }

    [Fact]
    public void AnIdleEditor_IsReplacedOnlyByCommandsThatRetake()
    {
        var idle = new CaptureGuardState { EditorOpen = true };
        Assert.Equal(CaptureBlock.EditorOpen, CaptureGuard.Find(idle, allowRetake: false));
        Assert.Equal(CaptureBlock.None, CaptureGuard.Find(idle, allowRetake: true));
        // Replacing the editor still waits for whatever else is in the way.
        Assert.Equal(CaptureBlock.RecordingBusy, CaptureGuard.Find(idle with { RecordingActive = true }, allowRetake: true));
    }

    [Theory]
    [InlineData(true, false, false, nameof(CaptureBlock.EditorAsking))]
    [InlineData(false, true, false, nameof(CaptureBlock.EditorSaving))]
    [InlineData(false, false, true, nameof(CaptureBlock.EditorBusy))]
    [InlineData(true, true, true, nameof(CaptureBlock.EditorAsking))]
    [InlineData(false, true, true, nameof(CaptureBlock.EditorSaving))]
    public void AnEditorThatIsAskingSavingOrBusy_BlocksEvenARetake(bool asking, bool saving, bool busy, string expected)
    {
        var state = new CaptureGuardState { EditorOpen = true, EditorAsking = asking, EditorSaving = saving, EditorBusy = busy };
        Assert.Equal(Enum.Parse<CaptureBlock>(expected), CaptureGuard.Find(state, allowRetake: true));
        Assert.Equal(Enum.Parse<CaptureBlock>(expected), CaptureGuard.Find(state, allowRetake: false));
    }

    [Fact]
    public async Task Retake_ClosesTheEditorOnlyWhenNothingElseBlocks()
    {
        int closes = 0;
        Task<CaptureBlock> Close() { closes++; return Task.FromResult(CaptureBlock.None); }
        Assert.Equal(CaptureBlock.EditorAsking, await CaptureGuard.PrepareAsync(true, _ => CaptureBlock.EditorAsking, Close));
        Assert.Equal(CaptureBlock.Countdown, await CaptureGuard.PrepareAsync(true, _ => CaptureBlock.Countdown, Close));
        Assert.Equal(0, closes);
        Assert.Equal(CaptureBlock.None, await CaptureGuard.PrepareAsync(false, _ => CaptureBlock.None,
            () => throw new InvalidOperationException("No owned editor to close.")));
        Assert.Equal(CaptureBlock.None, await CaptureGuard.PrepareAsync(true, _ => CaptureBlock.None, Close));
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(nameof(CaptureBlock.EditorKept))]
    [InlineData(nameof(CaptureBlock.EditorSaveFailed))]
    public async Task Retake_ReportsWhyTheEditorStayedOpen(string reason)
    {
        CaptureBlock kept = Enum.Parse<CaptureBlock>(reason);
        var arguments = new List<bool>();
        CaptureBlock result = await CaptureGuard.PrepareAsync(true,
            allowRetake => { arguments.Add(allowRetake); return CaptureBlock.None; },
            () => Task.FromResult(kept));
        Assert.Equal(kept, result);
        Assert.Equal(new[] { true }, arguments);
    }

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void EveryBlock_TheEditorAnswers_HasAOneLineReason_InBothLanguages(string language)
    {
        using IDisposable locale = UiText.UseLanguage(language);
        foreach (CaptureBlock block in Enum.GetValues<CaptureBlock>())
        {
            bool editorAnswers = block is CaptureBlock.EditorAsking or CaptureBlock.EditorSaving or CaptureBlock.EditorBusy
                or CaptureBlock.EditorOpen or CaptureBlock.EditorKept or CaptureBlock.EditorSaveFailed;
            Assert.Equal(editorAnswers, CaptureGuard.EditorNoticeKey(block) is not null);
            Assert.Equal(editorAnswers, CaptureGuard.EditorNoticeKey(block, recording: true) is not null);
        }

        Assert.Equal("CaptureBlocked_EditorRecording", CaptureGuard.EditorNoticeKey(CaptureBlock.EditorOpen, recording: true));
        Assert.Equal("CaptureBlocked_EditorSaveFailed", CaptureGuard.EditorNoticeKey(CaptureBlock.EditorSaveFailed));
        string[] keys =
        [
            "CaptureBlocked_Editor", "CaptureBlocked_EditorRecording", "CaptureBlocked_EditorSaveFailed",
            "CaptureBlocked_Scrolling", "CaptureBlocked_Updating", "CaptureBlocked_Exiting",
        ];
        foreach (string key in keys)
        {
            string text = UiText.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('!', text);
            // Korean sentences are in the formal 합니다 style.
            if (language == "ko") Assert.EndsWith("니다.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ANoticeThatAnswersACommand_ShowsDuringASession_WhileOtherNotificationsWait() => StaTestHost.Run(() =>
    {
        // A capture or recording session holds background notifications back; a capture command that
        // could not start still gets its answer, without taking the focus.
        using var presenter = new ThemedShellPresenter(() => true);
        presenter.ShowNotification("title", "message", TrayBalloonKind.Information);
        Assert.False(presenter.NotificationForTest.Open);
        presenter.ShowNotification("title", "message", TrayBalloonKind.Information, answersCommand: true);
        Assert.True(presenter.NotificationForTest.Open);
        Assert.False(presenter.NotificationWindowForTest!.ShowActivated);
    });

    [Fact]
    public void SecondPressWhileTheRealQuestionIsOpen_WaitsForTheAnswer_InsteadOfReenteringClose() => WithEditor((coordinator, editor) =>
    {
        // The default ConfirmClose shows the real modal question. Before the fix, a second press in its
        // nested message loop called Close on the closing window and failed with an exception that the
        // suppressed tray notice never showed.
        Task<CaptureBlock>? second = null;
        bool asking = false, canRetake = true, noticeShown = false;
        Window? waitedOn = null;
        var answer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            asking = editor.IsAskingToClose;
            canRetake = coordinator.CanRetake;
            second = Prepare(coordinator);
            noticeShown = coordinator.ShowEditorBlocked(UiText.Get("CaptureBlocked_Editor"));
            waitedOn = WindowForeground.WaitingDialog(editor);
            (waitedOn as EditorCloseDialog)?.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }, Dispatcher.CurrentDispatcher);
        using IDisposable watchdog = CloseQuestionsAfter(TimeSpan.FromSeconds(5));
        Task<CaptureBlock> first = Prepare(coordinator);
        PumpUntil(() => first.IsCompleted && second is not null);
        Assert.True(asking);
        Assert.False(canRetake);
        Assert.True(second!.IsCompletedSuccessfully);
        Assert.Equal(CaptureBlock.EditorAsking, second.Result);
        Assert.IsType<EditorCloseDialog>(waitedOn);
        Assert.True(noticeShown);
        Assert.Equal(CaptureBlock.EditorKept, first.Result);
        Assert.Equal(EditorCloseChoice.Cancel, editor.LastCloseChoice);
        Assert.True(editor.IsVisible);
        Assert.True(coordinator.CanRetake);

        // The press refused while asking left nothing behind: a close from code does not ask again.
        int askedAgain = 0;
        editor.ConfirmClose = () => { askedAgain++; return EditorCloseChoice.DontSave; };
        coordinator.Cancel();
        Assert.Equal(0, askedAgain);
        Assert.False(editor.IsVisible);
    });

    [Fact]
    public void RetakeQuestion_RestoresAMinimizedEditorFirst_AndKeptEditorSaysWhy() => WithEditor((coordinator, editor) =>
    {
        editor.WindowState = WindowState.Minimized;
        int asked = 0;
        WindowState stateWhenAsked = WindowState.Minimized;
        editor.ConfirmClose = () =>
        {
            asked++;
            stateWhenAsked = editor.WindowState;
            return EditorCloseChoice.Cancel;
        };
        Task<CaptureBlock> retake = Prepare(coordinator);
        PumpUntil(() => retake.IsCompleted);
        Assert.Equal(CaptureBlock.EditorKept, retake.Result);
        Assert.Equal(1, asked);
        Assert.Equal(WindowState.Normal, stateWhenAsked);
        Assert.True(coordinator.ShowEditorBlocked(UiText.Get(CaptureGuard.EditorNoticeKey(retake.Result)!)));
        Assert.Equal(UiText.Get("CaptureBlocked_Editor"), editor.Editor.StatusText);

        // Answered once, the question is not repeated by a close from code.
        coordinator.Cancel();
        Assert.Equal(1, asked);
        Assert.False(editor.IsVisible);
    });

    [Fact]
    public void SaveAndClose_FromTheRetakeQuestion_StartsTheCaptureOnceSaved()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int acquired = 0;
        WithEditor((coordinator, editor) =>
        {
            editor.ConfirmClose = () => EditorCloseChoice.SaveAndClose;
            Task<CaptureBlock> retake = Prepare(coordinator);
            PumpUntil(() => editor.Editor.IsCommitInProgress);
            Assert.False(retake.IsCompleted);
            Assert.Equal(CaptureBlock.EditorSaving, CaptureGuard.Find(coordinator.WithSession(default), allowRetake: true));
            saved.SetResult(true);
            PumpUntil(() => retake.IsCompleted);
            Assert.Equal(CaptureBlock.None, retake.Result);
            Assert.False(editor.IsVisible);
            coordinator.Start(false, false, false);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.Equal(1, acquired);
            coordinator.Cancel();
        }, commit: _ => saved.Task, acquire: () => { acquired++; return Frame(); });
    }

    [Fact]
    public void SaveAndClose_WhenSavingFails_KeepsTheEditorAndSaysSo() => WithEditor((coordinator, editor) =>
    {
        editor.ConfirmClose = () => EditorCloseChoice.SaveAndClose;
        Task<CaptureBlock> retake = Prepare(coordinator);
        PumpUntil(() => retake.IsCompleted);
        Assert.Equal(CaptureBlock.EditorSaveFailed, retake.Result);
        Assert.True(editor.IsVisible);
        Assert.True(coordinator.ShowEditorBlocked(UiText.Get(CaptureGuard.EditorNoticeKey(retake.Result)!)));
        Assert.Equal(UiText.Get("CaptureBlocked_EditorSaveFailed"), editor.Editor.StatusText);
        Assert.True(coordinator.CanRetake);
    }, commit: _ => Task.FromResult(false));

    [Fact]
    public void AnotherDialogOverTheEditor_BlocksTheRetake_AndIsWhatComesForward() => WithEditor((coordinator, editor) =>
    {
        int asked = 0;
        editor.ConfirmClose = () => { asked++; return EditorCloseChoice.DontSave; };
        var dialog = new Window { Owner = editor, Width = 200, Height = 120, ShowInTaskbar = false };
        bool hasOpenDialog = false, canRetake = true, noticeShown = false;
        Task<CaptureBlock>? blocked = null;
        Window? waitedOn = null;
        // Queued now, run inside the dialog's modal loop once it is shown and the editor is disabled.
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            hasOpenDialog = editor.HasOpenDialog;
            canRetake = coordinator.CanRetake;
            blocked = Prepare(coordinator);
            waitedOn = WindowForeground.WaitingDialog(editor);
            noticeShown = coordinator.ShowEditorBlocked(UiText.Get("CaptureBlocked_Editor"));
            dialog.Close();
        }));
        using IDisposable watchdog = CloseQuestionsAfter(TimeSpan.FromSeconds(5), dialog);
        _ = dialog.ShowDialog();
        Assert.True(hasOpenDialog);
        Assert.False(canRetake);
        Assert.True(blocked!.IsCompletedSuccessfully);
        Assert.Equal(CaptureBlock.EditorBusy, blocked.Result);
        Assert.Same(dialog, waitedOn);
        Assert.True(noticeShown);
        Assert.Equal(UiText.Get("CaptureBlocked_Editor"), editor.Editor.StatusText);
        Assert.Equal(0, asked);
        Assert.False(editor.HasOpenDialog);
        Assert.True(coordinator.CanRetake);
    });

    [Fact]
    public void DoneSavingOutOfSightIsWaitedForAndNeverBroughtBackToSayItIsBusy()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        WithEditor((coordinator, editor) =>
        {
            Assert.True(editor.FinishesOutOfSight);
            editor.Editor.SaveAndClose();
            PumpUntil(() => !editor.IsVisible);
            Task finished = coordinator.WaitForEditorFinishedAsync();
            Assert.False(finished.IsCompleted);
            // The session still holds the saving editor, but the user no longer sees it, so a
            // capture command waits for it instead of bringing it back with a notice.
            Assert.Equal(CaptureBlock.EditorSaving, CaptureGuard.Find(coordinator.WithSession(default), allowRetake: true));
            Assert.False(coordinator.ShowEditorBlocked(UiText.Get("CaptureBlocked_Editor")));
            Assert.False(editor.IsVisible);

            saved.SetResult(true);
            PumpUntil(() => finished.IsCompleted);
            Assert.True(editor.WasCommitted);
            Assert.False(coordinator.IsActive);
            Assert.True(coordinator.WaitForEditorFinishedAsync().IsCompleted);
        }, commit: result =>
        {
            result.CopiedToClipboard?.Invoke();
            return saved.Task;
        });
    }

    [Fact]
    public void DoneSaveFailingOutOfSightBringsTheEditorBackAndIsReportedAtOnce()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureOverlayCoordinator? session = null;
        bool? reportedOutOfSight = null;
        WithEditor((coordinator, editor) =>
        {
            session = coordinator;
            editor.Editor.SaveAndClose();
            // A capture command pressed before the editor leaves the screen already waits.
            Task finished = coordinator.WaitForEditorFinishedAsync();
            Assert.False(finished.IsCompleted);
            PumpUntil(() => !editor.IsVisible);
            Assert.True(coordinator.IsEditorFinishingOutOfSight);

            saved.SetException(new System.IO.IOException("The library save failed."));
            PumpUntil(() => finished.IsCompleted);
            // App's commit handler saw the editor off the screen when the save failed, so it
            // answers the Done command at once instead of a notice the session would drop.
            Assert.True(reportedOutOfSight);
            Assert.True(editor.IsVisible);
            Assert.False(editor.Editor.IsCommitInProgress);
            Assert.False(coordinator.IsEditorFinishingOutOfSight);
            Assert.True(coordinator.IsActive);
            Assert.True(coordinator.CanRetake);
            Assert.Equal(CaptureBlock.None, CaptureGuard.Find(coordinator.WithSession(default), allowRetake: true));
            Assert.True(editor.Editor.HasUnsavedChanges);
        }, commit: async result =>
        {
            // As App.HandleCommitAsync: a failed save is caught, reported and keeps the editor.
            result.CopiedToClipboard?.Invoke();
            try
            {
                return await saved.Task;
            }
            catch (System.IO.IOException)
            {
                reportedOutOfSight = session!.IsEditorFinishingOutOfSight;
                return false;
            }
        });
    }

    private static Task<CaptureBlock> Prepare(CaptureOverlayCoordinator coordinator) =>
        CaptureGuard.PrepareAsync(coordinator.CanRetake,
            allowRetake => CaptureGuard.Find(coordinator.WithSession(default), allowRetake),
            coordinator.CloseEditorForRetakeAsync);

    /// <summary>Opens a capture editor with an unsaved edit (a rotation), as a selection does.</summary>
    private static void WithEditor(Action<CaptureOverlayCoordinator, AnnotationEditorWindow> body,
        Func<AnnotationEditingResult, Task<bool>>? commit = null, Func<FrozenFrame>? acquire = null) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Window? created = null;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance, _ => (acquire ?? Frame)(), () => new RectD(0, 0, 32, 20));
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { created ??= window; return true; };
        coordinator.CommitRequested = commit;
        try
        {
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            var editor = Assert.IsType<AnnotationEditorWindow>(created);
            Assert.True(EditorKeysTests.Press(editor, Key.Q, ime: false));
            Assert.True(editor.Editor.HasUnsavedChanges);
            Assert.True(coordinator.CanRetake);
            body(coordinator, editor);
            if (editor.IsVisible)
            {
                editor.ConfirmClose = () => EditorCloseChoice.DontSave;
                coordinator.Cancel();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    /// <summary>Closes any question still open after <paramref name="limit"/>, so a failure cannot hang the run.</summary>
    private static IDisposable CloseQuestionsAfter(TimeSpan limit, Window? dialog = null)
    {
        var timer = new DispatcherTimer(limit, DispatcherPriority.Send, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            dialog?.Close();
            foreach (EditorCloseDialog open in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                .Select(source => source.RootVisual).OfType<EditorCloseDialog>().ToArray())
                open.Close();
        }, Dispatcher.CurrentDispatcher);
        return new Stop(timer);
    }

    private sealed class Stop(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private static FrozenFrame Frame()
    {
        var bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgr32, null, new byte[32 * 20 * 4], 128);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
