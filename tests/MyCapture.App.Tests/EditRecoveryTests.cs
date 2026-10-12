using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using MyCapture.App.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditRecoveryTests
{
    private static AnnotationEditorWindow Create()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var bitmap = Solid(64, 48, 0x30); var rect = new RectD(0, 0, 64, 48);
        return new AnnotationEditorWindow(new FrozenFrame(bitmap, rect, null, 0), rect, bitmap);
    }
    private static void Edit(AnnotationEditorWindow window)
    {
        var controller = Controller(window.Editor); controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(2, 2)); controller.PointerMove(new PointD(20, 20)); controller.PointerUp(new PointD(20, 20));
    }
    private static T Await<T>(Task<T> task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(task.IsCompleted, "Bounded dispatcher operation did not finish.");
        return task.GetAwaiter().GetResult();
    }
    [Fact]
    public void AppExitCancel_PreservesTheEditorAndDocumentWithoutCommit() => StaTestHost.Run(() =>
    {
        var window = Create(); Edit(window); int commits = 0;
        window.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        window.ConfirmClose = () => EditorCloseChoice.Cancel;
        Assert.False(Await(window.PrepareForAppExitAsync()));
        Assert.Single(Controller(window.Editor).Document.Items); Assert.Equal(0, commits);
        Assert.True(window.IsEnabled);
        Assert.False(window.WasCommitted); window.Close();
    });
    [Fact]
    public void AppExitDiscard_ClosesWithoutCommit() => StaTestHost.Run(() =>
    {
        var window = Create(); Edit(window); int commits = 0;
        window.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        window.ConfirmClose = () => EditorCloseChoice.DontSave;
        Assert.True(Await(window.PrepareForAppExitAsync())); Assert.Equal(0, commits); Assert.False(window.WasCommitted);
    });
    [Fact]
    public void AppExitSaveFailure_KeepsEditsAndAllowsSuccessfulRetry() => StaTestHost.Run(() =>
    {
        var window = Create(); Edit(window); bool succeed = false;
        window.CommitRequested = result => { Assert.Equal(EditorCommitAction.Done, result.Action); return Task.FromResult(succeed); };
        window.ConfirmClose = () => EditorCloseChoice.SaveAndClose;
        Assert.False(Await(window.PrepareForAppExitAsync())); Assert.Single(Controller(window.Editor).Document.Items);
        succeed = true; Assert.True(Await(window.PrepareForAppExitAsync())); Assert.True(window.WasCommitted);
    });
    [Fact]
    public void RecoveredDocumentWithoutUndoHistory_StillRequiresUnsavedChoice() => StaTestHost.Run(() =>
    {
        var window = Create(); var store = new EditDraftStore(TestRecycleBin.CreateTempSubdirectory("MyCapture-recovered-").FullName, _ => { });
        window.AdoptRecoveredDraft(store, Guid.NewGuid(), 20);
        int prompts = 0; window.ConfirmClose = () => { prompts++; return EditorCloseChoice.Cancel; };
        Assert.False(Await(window.PrepareForAppExitAsync())); Assert.Equal(1, prompts);
        var draft = Assert.Single(store.ReadAvailable()); Assert.Equal(20, draft.Draft.Revision);
        window.Close();
    });
    [Fact]
    public void DraftSnapshot_ContainsEditableDocumentAndOriginalPixels() => StaTestHost.Run(() =>
    {
        var window = Create(); Edit(window);
        var snapshot = Await(window.Editor.CaptureDraftAsync()); Assert.NotNull(snapshot);
        var document = AnnotationDocument.TryFromJson(snapshot.Document); Assert.NotNull(document); Assert.Single(document.Items);
        Assert.True(snapshot.Files.ContainsKey("base.png")); Assert.True(snapshot.Files.ContainsKey("original.png"));
        Assert.Equal(snapshot.Files["base.png"], snapshot.Files["original.png"]); window.Close();
    });

    [Fact]
    public void DiscardWithUnavailableRetirementMarker_KeepsTheEditor() => StaTestHost.Run(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("MyCapture-draft-marker-").FullName;
        string blocked = System.IO.Path.Combine(root, "blocked"); System.IO.File.WriteAllText(blocked, "preserve");
        var window = Create(); Edit(window);
        window.AdoptRecoveredDraft(new EditDraftStore(blocked, _ => { }), Guid.NewGuid(), 1);
        window.ConfirmClose = () => EditorCloseChoice.DontSave;
        Assert.False(Await(window.PrepareForAppExitAsync()));
        Assert.Single(Controller(window.Editor).Document.Items);
        Assert.Equal("preserve", System.IO.File.ReadAllText(blocked)); window.Close();
    });

    [Fact]
    public void DiscardWithUnavailableRecycleBin_PreservesFilesAndCannotRecoverAgain() => StaTestHost.Run(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("MyCapture-draft-trash-").FullName;
        var store = new EditDraftStore(root, _ => throw new System.IO.IOException("Recycle unavailable"));
        var window = Create(); Edit(window); Guid id = Guid.NewGuid(); window.AdoptRecoveredDraft(store, id, 1);
        window.ConfirmClose = () => EditorCloseChoice.DontSave;
        Assert.True(Await(window.PrepareForAppExitAsync()));
        Assert.True(System.IO.Directory.Exists(System.IO.Path.Combine(root, id.ToString("N"))));
        Assert.Empty(store.ReadAvailable());
    });

    [Fact]
    public void RecoveredCropAndRotation_KeepTheirOriginalPixelCoordinateSpace() => StaTestHost.Run(() =>
    {
        var original = Solid(64, 48, 0x30); var region = new RectD(0, 0, 64, 48);
        var first = new AnnotationEditorWindow(new FrozenFrame(original, region, null, 0), region, original);
        Assert.True(first.Editor.HandleShortcut(System.Windows.Input.Key.W, System.Windows.Input.ModifierKeys.None));
        first.Editor.ApplyCrop(new RectD(10, 8, 20, 16));
        var snapshot = Await(first.Editor.CaptureDraftAsync())!;
        var document = AnnotationDocument.TryFromJson(snapshot.Document)!;
        var current = first.Editor.DisplayedBitmap;
        var restored = new AnnotationEditorWindow(new FrozenFrame(current, region, null, 0), region, original, initialDocument: document);
        restored.Editor.RestoreDraftGeometry(current, int.Parse(snapshot.Metadata["turns"]), snapshot.Metadata["origin"]);
        Assert.Equal(Controller(first.Editor).CropOrigin, Controller(restored.Editor).CropOrigin);
        Assert.Equal(Controller(first.Editor).RotationTurns, Controller(restored.Editor).RotationTurns);
        Assert.True(first.Editor.HandleShortcut(System.Windows.Input.Key.W, System.Windows.Input.ModifierKeys.None));
        Assert.True(restored.Editor.HandleShortcut(System.Windows.Input.Key.W, System.Windows.Input.ModifierKeys.None));
        Assert.Equal(AnnotationEditorControl.EncodeDraftBitmap(first.Editor.DisplayedBitmap),
            AnnotationEditorControl.EncodeDraftBitmap(restored.Editor.DisplayedBitmap));
        first.Close(); restored.Close();
    });

    [Theory]
    [InlineData((int)EditorCloseChoice.Cancel, false)]
    [InlineData((int)EditorCloseChoice.SaveAndClose, false)]
    [InlineData((int)EditorCloseChoice.DontSave, true)]
    public void RecoveredVideoBeforeMediaReady_PreservesDraftUnlessDiscardIsExplicit(int choiceValue, bool closes) => StaTestHost.Run(() =>
    {
        var choice = (EditorCloseChoice)choiceValue;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = TestRecycleBin.CreateTempSubdirectory("MyCapture-video-draft-").FullName;
        var store = new EditDraftStore(System.IO.Path.Combine(root, "drafts"), _ => { }); Guid id = Guid.NewGuid();
        store.WriteAsync(id, 9, "video", "document", [], new Dictionary<string, byte[]>(), () => true).GetAwaiter().GetResult();
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        video.AdoptRecoveredDraft(store, id, 9);
        int prompts = 0; video.UnsavedEditsPromptForTest = () => { prompts++; return choice; };
        Assert.True(video.HasUnsavedEditsForTest);
        Assert.Equal(closes, Await(video.PrepareForAppExitAsync())); Assert.Equal(1, prompts);
        if (closes) Assert.Empty(store.ReadAvailable()); else Assert.Single(store.ReadAvailable());
        video.Close();
        if (!closes) Assert.Single(store.ReadAvailable());
    });

    private sealed class DelayedRedaction : IPrivacyRedactionService
    {
        internal readonly TaskCompletionSource<PrivacyRedactionResult> Completion = new();
        public bool IsAvailable => true;
        public Task<PrivacyRedactionResult> FindAsync(System.Windows.Media.Imaging.BitmapSource bitmap,
            CancellationToken cancellationToken = default) => Completion.Task;
    }

    [Fact]
    public void ExitWaitsForPrivacyDetectionAndThenProtectsItsNewEdits() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var service = new DelayedRedaction(); var bitmap = Solid(64, 48, 0x30); var region = new RectD(0, 0, 64, 48);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap,
            privacyRedactionService: service);
        window.ConfirmClose = () => EditorCloseChoice.Cancel;
        Task detection = window.Editor.ApplyPrivacyRedactionsAsync(); Task<bool> exit = window.PrepareForAppExitAsync();
        Assert.False(exit.IsCompleted);
        service.Completion.SetResult(new PrivacyRedactionResult(PrivacyRedactionStatus.Success, [new RectD(2, 2, 12, 10)]));
        Assert.False(Await(exit)); Assert.True(detection.IsCompleted); Assert.Single(Controller(window.Editor).Document.Items);
        window.Close();
    });

    [Fact]
    public void UnchangedVideoSave_DisablesEditsWhileWaitingForTheDraftWriter() => StaTestHost.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = TestRecycleBin.CreateTempSubdirectory("MyCapture-video-finalize-").FullName;
        var video = new VideoEditorWindow(new RecordingResult("unused.mp4", 3000, 15, 45, 640, 360),
            AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(VideoEditorWindow).GetField("_mediaReady", flags)!.SetValue(video, true);
        var current = typeof(VideoEditorWindow).GetMethod("BuildCurrentDocument", flags)!.Invoke(video, null);
        typeof(VideoEditorWindow).GetField("_initialDocument", flags)!.SetValue(video, current);
        var snapshot = new TaskCompletionSource<DraftSnapshot?>();
        var session = new EditDraftSession(new EditDraftStore(System.IO.Path.Combine(root, "drafts"), _ => { }),
            video.Dispatcher, () => snapshot.Task, _ => { });
        typeof(VideoEditorWindow).GetField("_draftSession", flags)!.SetValue(video, session);
        Task writer = session.FlushAsync();
        var closed = new TaskCompletionSource<bool>(); video.Closed += (_, _) => closed.TrySetResult(true);
        var commit = typeof(VideoEditorWindow).GetMethod("CommitTrim", flags)!;
        commit.Invoke(video, null);
        Assert.True((bool)typeof(VideoEditorWindow).GetField("_operationRunning", flags)!.GetValue(video)!);
        Assert.False(((System.Windows.UIElement)typeof(VideoEditorWindow).GetField("_layerCanvas", flags)!.GetValue(video)!).IsEnabled);
        commit.Invoke(video, null); Assert.False(closed.Task.IsCompleted);
        snapshot.SetResult(null); Assert.True(Await(closed.Task)); Assert.True(writer.IsCompleted);
    });

    [Fact]
    public void ExitPreparation_DisablesEditorInputThroughAnAsynchronousDraftFlush() => StaTestHost.Run(() =>
    {
        var window = Create(); Edit(window); window.ConfirmClose = () => EditorCloseChoice.Cancel;
        string root = TestRecycleBin.CreateTempSubdirectory("MyCapture-exit-input-").FullName;
        var snapshot = new TaskCompletionSource<DraftSnapshot?>();
        var session = new EditDraftSession(new EditDraftStore(root, _ => { }), window.Dispatcher, () => snapshot.Task, _ => { });
        typeof(AnnotationEditorWindow).GetField("_draftSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, session);
        Task<bool> exit = window.PrepareForAppExitAsync();
        Assert.False(exit.IsCompleted); Assert.False(window.IsEnabled);
        snapshot.SetResult(null); Assert.False(Await(exit)); Assert.True(window.IsEnabled);
        window.Close();
    });
}
