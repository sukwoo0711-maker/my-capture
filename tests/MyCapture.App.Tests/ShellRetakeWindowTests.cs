using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Shell;
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
    public void Retake_PreservesOwnedAndOtherEditors_StartsFreshSession_AndKeepsBothOriginals() => StaTestHost.Run(() =>
    {
        string originals = OwnedTestDirectory.Create("retake-original");
        var paths = AppPaths.CreateForRoot(originals);
        var settings = new QueueSettings();
        var queue = new CaptureQueue(paths, settings, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
        CaptureRecord? initial = null, separate = null;
        int acquired = 0, outsideClosed = 0;
        CaptureOverlayWindow? selector = null;
        using var outsideCoordinator = Create(Frame);
        Assert.True(outsideCoordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
        PumpUntil(() => outsideCoordinator.LastTransitionForTest.IsCompleted);
        var outside = EditorWindow(outsideCoordinator);
        outside.Closing += (_, _) => outsideClosed++;
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.ApplyCaptureExclusion = window => { selector = Assert.IsType<CaptureOverlayWindow>(window); return true; };
        coordinator.SelectionPersistRequested = selection =>
        {
            initial = persistence.PersistOriginal(selection.SelectedBitmap, 1, "original", "test");
            return Task.CompletedTask;
        };
        coordinator.EditorScreenCaptureRequested = selection =>
        {
            separate = persistence.PersistOriginal(selection.SelectedBitmap, 1, "new capture", "test");
            return Task.CompletedTask;
        };
        try
        {
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            var editor = EditorWindow(coordinator);
            Assert.NotNull(initial);
            using IDisposable originalLease = persistence.AcquireEditLease(initial.Id);
            string originalPath = queue.GetFilePath(initial, CaptureFileNames.Original);
            byte[] original = File.ReadAllBytes(originalPath);
            Assert.True(coordinator.CanRetake);
            coordinator.Start(false, false, false, preserveEditor: true);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.Equal(1, acquired);
            var nested = Assert.IsType<CaptureOverlayCoordinator>(typeof(CaptureOverlayCoordinator)
                .GetField("_editorScreenCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
            Assert.NotSame(coordinator, nested);
            Assert.Null(nested.CurrentEditor);
            Assert.Same(editor, EditorWindow(coordinator));
            Assert.Same(outside, EditorWindow(outsideCoordinator));
            Assert.True(editor.IsVisible);
            Assert.True(outside.IsVisible);
            Assert.True(coordinator.IsActive);
            Assert.True(Assert.IsType<CaptureOverlayView>(selector!.Content).CaptureFullScreen());
            PumpUntil(() => coordinator.CanRetake);
            Assert.NotNull(separate);
            Assert.NotEqual(initial.Id, separate.Id);
            Assert.Equal(2, queue.Count);
            Assert.Equal(original, File.ReadAllBytes(originalPath));
            string separatePath = queue.GetFilePath(separate, CaptureFileNames.Original);
            Assert.NotEqual(originalPath, separatePath);
            Assert.NotEmpty(File.ReadAllBytes(separatePath));
            Assert.True(persistence.IsBusy(initial.Id));
            Assert.False(persistence.IsBusy(separate.Id));
            Assert.Same(editor, EditorWindow(coordinator));
            Assert.Same(outside, EditorWindow(outsideCoordinator));
            Assert.True(editor.IsVisible);
            Assert.True(outside.IsVisible);
            Assert.Equal(0, outsideClosed);
        }
        finally { coordinator.Cancel(); outsideCoordinator.Cancel(); OwnedTestDirectory.Delete(originals); }
    });

    [Fact]
    public void Retake_DoesNotCloseTheEditor_AndRejectsConcurrentSelectionAndPersistence() => StaTestHost.Run(() =>
    {
        int acquired = 0, closes = 0, writes = 0;
        CaptureOverlayWindow? selector = null;
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.ApplyCaptureExclusion = window => { selector = Assert.IsType<CaptureOverlayWindow>(window); return true; };
        coordinator.EditorScreenCaptureRequested = _ => { writes++; return persisted.Task; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        var editor = EditorWindow(coordinator);
        CancelEventHandler veto = (_, args) => { closes++; args.Cancel = true; };
        editor.Closing += veto;
        try
        {
            coordinator.Start(false, false, false, preserveEditor: true);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            CaptureOverlayWindow firstSelector = Assert.IsType<CaptureOverlayWindow>(selector);
            Assert.Equal(1, acquired);
            coordinator.Start(false, false, false, preserveEditor: true);
            Assert.Same(firstSelector, selector);
            Assert.Equal(1, acquired);
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty, recordForRepeat: false, preserveEditor: true));
            Assert.True(Assert.IsType<CaptureOverlayView>(firstSelector.Content).CaptureFullScreen());
            PumpUntil(() => writes == 1);
            coordinator.Start(false, false, false, preserveEditor: true);
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty, recordForRepeat: false, preserveEditor: true));
            Assert.Equal(1, acquired);
            Assert.False(coordinator.CanRetake);
            persisted.SetResult();
            PumpUntil(() => coordinator.CanRetake);
            Assert.True(editor.IsVisible);
            Assert.True(coordinator.IsActive);
            Assert.Same(editor, EditorWindow(coordinator));
            Assert.Equal(1, writes);
            Assert.Equal(0, closes);
        }
        finally { persisted.TrySetResult(); editor.Closing -= veto; coordinator.Cancel(); }
    });

    [Fact]
    public void Retake_PreservesAnnotationsWithoutAskingToDiscard() => StaTestHost.Run(() =>
    {
        int separateWrites = 0;
        using var coordinator = Create(Frame);
        coordinator.EditorScreenCaptureRequested = _ => { separateWrites++; return Task.CompletedTask; };
        coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        var annotation = EditorWindow(coordinator);
        int asked = 0;
        annotation.ConfirmClose = () => { asked++; return EditorCloseChoice.Cancel; };
        AddAnnotation(annotation);
        string document = annotation.Editor.CreateCurrentImageSnapshot().ToJson();
        Assert.True(annotation.Editor.HasUnsavedChanges);
        try
        {
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 16, 12), string.Empty, recordForRepeat: false, preserveEditor: true));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.Equal(0, asked);
            Assert.Equal(1, separateWrites);
            Assert.True(annotation.IsVisible);
            Assert.True(coordinator.IsActive);
            Assert.Same(annotation, EditorWindow(coordinator));
            Assert.True(annotation.Editor.HasUnsavedChanges);
            Assert.Equal(document, annotation.Editor.CreateCurrentImageSnapshot().ToJson());
        }
        finally { annotation.ConfirmClose = () => EditorCloseChoice.DontSave; coordinator.Cancel(); }
    });

    [Fact]
    public void Retake_WithRealDraftSession_KeepsItsIdentityContentsAndRetirementState() => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        int retireCalls = 0, separateWrites = 0;
        string root = TestRecycleBin.CreateTempSubdirectory("mc-retake-draft-").FullName;
        var store = new EditDraftStore(root, _ => Interlocked.Increment(ref retireCalls));
        // Preserve this fixture; no Shell operation is performed.
        using var coordinator = Create(Frame);
        coordinator.EditorScreenCaptureRequested = _ => { separateWrites++; return Task.CompletedTask; };
        try
        {
            coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            var annotation = EditorWindow(coordinator);
            Guid draftId = Guid.NewGuid();
            annotation.AdoptRecoveredDraft(store, draftId, 1);
            AddAnnotation(annotation);
            string before = annotation.Editor.CreateCurrentImageSnapshot().ToJson();
            Task flush = annotation.FlushDraftAsync();
            PumpUntil(() => flush.IsCompleted);
            flush.GetAwaiter().GetResult();
            EditDraft durableDraft = Assert.Single(store.ReadAvailable()).Draft;
            var session = Assert.IsType<EditDraftSession>(typeof(AnnotationEditorWindow)
                .GetField("_draftSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(annotation));
            Assert.Equal(draftId, session.Id);
            annotation.ConfirmClose = () => throw new InvalidOperationException("A new capture must not retire the draft.");
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 16, 12), string.Empty, recordForRepeat: false, preserveEditor: true));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.Equal(1, separateWrites);
            Assert.Same(annotation, EditorWindow(coordinator));
            Assert.Same(session, typeof(AnnotationEditorWindow)
                .GetField("_draftSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(annotation));
            Assert.True(annotation.IsVisible);
            Assert.True(annotation.IsEnabled);
            Assert.True(coordinator.CanRetake);
            Assert.True(annotation.Editor.HasUnsavedChanges);
            Assert.Equal(before, annotation.Editor.CreateCurrentImageSnapshot().ToJson());
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(durableDraft),
                System.Text.Json.JsonSerializer.Serialize(Assert.Single(store.ReadAvailable()).Draft));
            Assert.False(store.IsRetired(draftId));
            Assert.Equal(0, retireCalls);
            bool responsive = false;
            annotation.Dispatcher.BeginInvoke(new Action(() => responsive = true));
            PumpUntil(() => responsive);
        }
        finally
        {
            if (coordinator.CurrentEditor is { } current)
                ((AnnotationEditorWindow)Window.GetWindow(current)).ConfirmClose = () => EditorCloseChoice.DontSave;
            coordinator.Cancel();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    [Theory]
    [InlineData("asking")]
    [InlineData("saving")]
    [InlineData("busy")]
    public void Retake_WhileEditorIsAskingSavingOrBusy_BlocksWithItsNotice(string state) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        int acquired = 0, separateWrites = 0;
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = Create(() => { acquired++; return Frame(); });
        coordinator.EditorScreenCaptureRequested = _ => { separateWrites++; return Task.CompletedTask; };
        coordinator.CommitRequested = _ => saved.Task;
        Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
        var editor = EditorWindow(coordinator);
        AddAnnotation(editor);
        FieldInfo busy = typeof(AnnotationEditorControl).GetField("_redactionInProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            if (state == "asking")
            {
                int asked = 0;
                editor.ConfirmClose = () => { asked++; AssertBlocked(CaptureBlock.EditorAsking); return EditorCloseChoice.Cancel; };
                editor.RequestClose();
                Assert.Equal(1, asked);
            }
            else if (state == "saving")
            {
                Assert.True(editor.Editor.HandleShortcut(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control));
                PumpUntil(() => editor.Editor.IsCommitInProgress);
                AssertBlocked(CaptureBlock.EditorSaving);
                saved.SetResult(false);
                PumpUntil(() => !editor.Editor.IsCommitInProgress);
                Assert.True(editor.Editor.IsHitTestVisible);
                Assert.Equal(UiText.Get("Text_06F58809C083"), editor.Editor.StatusText);
            }
            else
            {
                busy.SetValue(editor.Editor, true);
                Assert.True(editor.Editor.IsEditingOperationInProgress);
                AssertBlocked(CaptureBlock.EditorBusy);
            }
            Assert.True(editor.IsVisible);
            Assert.True(editor.Editor.HasUnsavedChanges);
        }
        finally
        {
            try
            {
                busy.SetValue(editor.Editor, false);
                saved.TrySetResult(false);
                // Even a failed assertion must let the async commit resume on its UI
                // dispatcher before this test closes the editor and shuts that dispatcher down.
                PumpUntil(() => !editor.Editor.IsCommitInProgress);
                editor.ConfirmClose = () => EditorCloseChoice.DontSave;
                coordinator.Cancel();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        void AssertBlocked(CaptureBlock expected)
        {
            Assert.False(coordinator.CanRetake);
            CaptureBlock block = CaptureGuard.Find(coordinator.WithSession(default), GlobalHotkeyCommand.CaptureRegion);
            Assert.Equal(expected, block);
            string notice = UiText.Get(CaptureGuard.EditorNoticeKey(block)!);
            Assert.True(coordinator.ShowEditorBlocked(notice));
            Assert.Equal(notice, editor.Editor.StatusText);
            coordinator.Start(false, false, false, preserveEditor: true);
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty, recordForRepeat: false, preserveEditor: true));
            Assert.Equal(0, acquired);
            Assert.Equal(0, separateWrites);
            Assert.False(coordinator.IsSelecting);
            Assert.Same(editor, EditorWindow(coordinator));
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
        Assert.Null(coordinator.CurrentEditor);
        Assert.Equal(0, windows);
    });

    private static CaptureOverlayCoordinator Create(Func<FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance, _ => acquire(), () => new RectD(0, 0, 32, 20), _ => [])
        {
            RequiresCaptureExclusion = () => true,
            ApplyCaptureExclusion = _ => true,
            ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
            ReadWindowSources = null,
        };

    private static AnnotationEditorWindow EditorWindow(CaptureOverlayCoordinator coordinator) =>
        Assert.IsType<AnnotationEditorWindow>(Window.GetWindow(Assert.IsType<AnnotationEditorControl>(coordinator.CurrentEditor)));

    private static void AddAnnotation(AnnotationEditorWindow window)
    {
        EditorKeysTests.Invoke(window.Editor, "PlaceTextBox", new PointD(2, 2));
        EditorKeysTests.Field<System.Windows.Controls.TextBox>(window.Editor, "_activeTextBox").Text = "보존할 주석";
        EditorKeysTests.Invoke(window.Editor, "CommitActiveText");
        Assert.Single(window.Editor.CreateCurrentImageSnapshot().Items);
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
