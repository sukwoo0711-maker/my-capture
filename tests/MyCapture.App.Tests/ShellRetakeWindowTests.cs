using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShellRetakeWindowTests
{
    [Fact]
    public void TitleIdentity_PreservesBindingUpdatesAndContext() => StaTestHost.Run(() =>
    {
        var source = new Caption { Text = "Settings" };
        var window = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
        window.SetBinding(Window.TitleProperty, new Binding(nameof(Caption.Text)) { Source = source });
        WindowIdentity.Attach(window);
        Assert.Equal(AppIdentity.WindowLabel + " — Settings", window.Title);
        Assert.True(BindingOperations.IsDataBound(window, Window.TitleProperty));
        source.Text = "Gallery — capture.png";
        Assert.Equal(AppIdentity.WindowLabel + " — Gallery — capture.png", window.Title);
        WindowIdentity.Attach(window);
        Assert.Equal(AppIdentity.WindowLabel + " — Gallery — capture.png", window.Title);
        window.Close();
    });

    [Fact]
    public void Retake_ClosesOnlyOwnedEditor_StartsFreshFrame_AndKeepsPersistedOriginal() => StaTestHost.Run(() =>
    {
        string originals = OwnedTestDirectory.Create("retake-original");
        string original = System.IO.Path.Combine(originals, "original.png");
        Window? editor = null;
        int acquired = 0;
        var outside = new Window { Width = 200, Height = 100, ShowInTaskbar = false };
        outside.Show();
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor ??= window; return true; };
        coordinator.SelectionPersistRequested = _ => { System.IO.File.WriteAllText(original, "durable original"); return Task.CompletedTask; };
        try
        {
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.True(coordinator.CanRetake);
            Assert.True(editor!.IsVisible);
            coordinator.CloseEditorForRetake();
            Assert.False(editor.IsVisible);
            Assert.True(outside.IsVisible);
            Assert.False(coordinator.IsActive);
            coordinator.Start(false, false, false);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.Equal(1, acquired);
            Assert.True(coordinator.IsActive);
            Assert.Equal("durable original", System.IO.File.ReadAllText(original));
            coordinator.Cancel();
        }
        finally { outside.Close(); OwnedTestDirectory.Delete(originals); }
    });

    [Fact]
    public void Retake_RespectsEditorCloseVeto_AndDoesNotStartConcurrentCapture() => StaTestHost.Run(() =>
    {
        Window? editor = null;
        int acquired = 0;
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor = window; return true; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        CancelEventHandler veto = (_, args) => args.Cancel = true;
        editor!.Closing += veto;
        try
        {
            coordinator.CloseEditorForRetake();
            Assert.True(editor.IsVisible);
            Assert.True(coordinator.IsActive);
            coordinator.Start(false, false, false);
            Assert.Equal(0, acquired);
        }
        finally { editor.Closing -= veto; coordinator.Cancel(); }
    });

    [Fact]
    public void Retake_AsksBeforeDiscardingAnnotations_AndKeepsTheEditorWhenCancelled() => StaTestHost.Run(() =>
    {
        Window? editor = null;
        int acquired = 0;
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor ??= window; return true; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        var annotation = Assert.IsType<MyCapture.App.Editing.AnnotationEditorWindow>(editor);
        int asked = 0;
        annotation.ConfirmClose = () => { asked++; return MyCapture.App.Editing.EditorCloseChoice.Cancel; };
        // A rotation is an edit the user would lose.
        Assert.True(EditorKeysTests.Press(annotation, System.Windows.Input.Key.Q, ime: false));
        Assert.True(annotation.Editor.HasUnsavedChanges);
        try
        {
            coordinator.CloseEditorForRetake();
            Assert.Equal(1, asked);
            Assert.True(annotation.IsVisible);
            Assert.True(coordinator.IsActive);
            coordinator.Start(false, false, false);
            Assert.Equal(0, acquired);

            annotation.ConfirmClose = () => { asked++; return MyCapture.App.Editing.EditorCloseChoice.DontSave; };
            coordinator.CloseEditorForRetake();
            Assert.Equal(2, asked);
            Assert.False(annotation.IsVisible);
            Assert.False(coordinator.IsActive);
        }
        finally { coordinator.Cancel(); }
    });

    [Fact]
    public void Retake_WithRealDraftSession_WaitsForRetirementAndStartsAfterDiscard() => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Window? editor = null;
        int acquired = 0, retireCalls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-retake-draft-").FullName;
        var store = new EditDraftStore(root, _ =>
        {
            if (Interlocked.Increment(ref retireCalls) == 1)
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
        }); // Preserve this fixture; no Shell operation is performed.
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { editor ??= window; return true; };
        try
        {
            coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            var annotation = Assert.IsType<AnnotationEditorWindow>(editor);
            Guid draftId = Guid.NewGuid();
            annotation.AdoptRecoveredDraft(store, draftId, 1);
            annotation.ConfirmClose = () => EditorCloseChoice.Cancel;
            Task<bool> canceled = PrepareAndStartAsync();
            PumpUntil(() => canceled.IsCompleted);
            Assert.False(canceled.GetAwaiter().GetResult());
            Assert.True(annotation.IsVisible);
            Assert.Equal(0, retireCalls);

            annotation.ConfirmClose = () => EditorCloseChoice.DontSave;
            Task<bool> retake = PrepareAndStartAsync();
            PumpUntil(() => entered.Task.IsCompleted);
            Assert.False(retake.IsCompleted);
            Assert.True(annotation.IsVisible);
            Assert.False(annotation.IsEnabled);
            Assert.False(coordinator.CanRetake);
            Assert.Equal(0, acquired);
            bool responsive = false;
            annotation.Dispatcher.BeginInvoke(new Action(() => responsive = true));
            PumpUntil(() => responsive);
            release.TrySetResult();
            PumpUntil(() => retake.IsCompleted);
            Assert.True(retake.GetAwaiter().GetResult());
            Assert.False(annotation.IsVisible);
            Assert.True(store.IsRetired(draftId));
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.Equal(1, acquired);
            Assert.True(coordinator.IsActive);
        }
        finally
        {
            release.TrySetResult();
            coordinator.Cancel();
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        async Task<bool> PrepareAndStartAsync()
        {
            bool ready = await App.TryPrepareCaptureAsync(coordinator.CanRetake,
                allowRetake => !coordinator.IsActive || allowRetake && coordinator.CanRetake,
                coordinator.CloseEditorForRetakeAsync);
            if (ready) coordinator.Start(false, false, false);
            return ready;
        }
    });

    [Fact]
    public void PendingPersistence_CannotRetake_AndCancellationPreventsOldEditorReopening() => StaTestHost.Run(() =>
    {
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int windows = 0;
        using var coordinator = Create(Frame);
        coordinator.SelectionPersistRequested = _ => persisted.Task;
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = _ => { windows++; return true; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        Assert.False(coordinator.CanRetake);
        coordinator.CloseEditorForRetake();
        Assert.True(coordinator.IsActive);
        coordinator.Cancel();
        persisted.SetResult();
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        Assert.False(coordinator.IsActive);
        Assert.Equal(0, windows);
    });

    private static CaptureOverlayCoordinator Create(Func<FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance, _ => acquire(), () => new RectD(0, 0, 32, 20));

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
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class Caption : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
