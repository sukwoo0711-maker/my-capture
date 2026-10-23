using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorMosaicAsyncTests
{
    [Fact]
    public void WorkerLeavesUiAvailableKeepsDraftAndAddsExactlyOneUndoStep() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        int uiThread = Environment.CurrentManagedThreadId;
        var region = new RectD(8, 6, 24, 18);
        editor.ApplyMosaic(region, 6);
        Task settled = editor.MosaicOperationsSettledAsync();
        WaitFor(gate.Started.Task);
        Assert.NotEqual(uiThread, gate.ThreadId);
        Assert.True(gate.Input!.Bitmap.IsFrozen);
        Assert.Same(editor.DisplayedBitmap, gate.Input.Bitmap);
        Assert.False(settled.IsCompleted);
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(Controller(editor).Document.Items));
        Assert.True(editor.HasUnsavedChanges);
        Assert.False(editor.IsEditingOperationInProgress); // Closing must still cancel it.
        Assert.Equal(region, Assert.Single(Field<AnnotationEditorSurface>(editor, "_surface").PendingMosaicRegions));
        bool inputRan = false;
        WaitFor(editor.Dispatcher.InvokeAsync(() => inputRan = true, DispatcherPriority.Input).Task);
        Assert.True(inputRan);

        gate.Release.TrySetResult();
        WaitFor(settled);
        ImageAnnotation patch = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(editor).Document.Items));
        Assert.Equal(region, patch.Rect);
        Assert.Equal(1, Controller(editor).Undo.UndoCount);
        Assert.Empty(Field<AnnotationEditorSurface>(editor, "_surface").PendingMosaicRegions);
        Assert.True(editor.MosaicsReadyForExport);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Empty(Controller(editor).Document.Items);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        Assert.Same(patch, Assert.Single(Controller(editor).Document.Items));
    });

    [Fact]
    public void RapidMosaicsComputeAndInsertInGestureOrder() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        RectD first = new(0, 0, 24, 18), second = new(8, 6, 30, 24);
        editor.ApplyMosaic(first, 4);
        WaitFor(gate.Started.Task);
        editor.ApplyMosaic(second, 12);
        Task settled = editor.MosaicOperationsSettledAsync();
        Assert.Equal(1, gate.Calls);
        Assert.Equal(2, Field<AnnotationEditorSurface>(editor, "_surface").PendingMosaicRegions.Count);
        gate.Release.TrySetResult();
        WaitFor(settled);
        Assert.Equal(2, gate.Calls);
        Assert.Equal(new[] { first, second }, Controller(editor).Document.Items.Cast<ImageAnnotation>().Select(image => image.Rect));
        Assert.Equal(2, Controller(editor).Undo.UndoCount);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Equal(first, Assert.IsType<ImageAnnotation>(Assert.Single(Controller(editor).Document.Items)).Rect);
    });

    [Theory]
    [InlineData("Done")]
    [InlineData("CopyToClipboard")]
    [InlineData("QuickSave")]
    [InlineData("SaveAs")]
    public void EveryCommitWaitsForTheFinishedGestureAndIncludesItsAsset(string actionName) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        var committed = new TaskCompletionSource<AnnotationEditingResult>();
        int commits = 0;
        editor.CommitRequested = result => { commits++; committed.TrySetResult(result); return Task.FromResult(true); };
        editor.ApplyMosaic(new RectD(2, 4, 24, 18), 6);
        WaitFor(gate.Started.Task);
        EditorCommitAction action = Enum.Parse<EditorCommitAction>(actionName);
        Invoke(editor, "Commit", action, false);
        Assert.True(editor.IsCommitInProgress);
        Assert.False(committed.Task.IsCompleted);
        Invoke(editor, "Commit", action, false);
        gate.Release.TrySetResult();
        WaitFor(committed.Task);
        AnnotationEditingResult result = committed.Task.Result;
        Assert.Equal(1, commits);
        Assert.Equal(action, result.Action);
        ImageAnnotation patch = Assert.IsType<ImageAnnotation>(Assert.Single(result.Document.Items));
        Assert.NotNull(result.ImageAssetBitmaps[patch.AssetFileName]);
        Assert.Equal(1, Controller(editor).Undo.UndoCount);
    });

    [Fact]
    public void RenderWaitsDraftDefersUntilMosaicCompletionAndSynchronousSnapshotFailsClosed() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 64, 48), 12);
        WaitFor(gate.Started.Task);
        Assert.Throws<InvalidOperationException>(() => editor.CreateCurrentImageSnapshot());
        Task<BitmapSource> render = editor.RenderCurrentImageAsync();
        Task<DraftSnapshot?> draft = editor.CaptureDraftAsync();
        Assert.False(render.IsCompleted);
        Assert.True(draft.IsCompletedSuccessfully);
        Assert.Null(draft.Result);
        Task<DraftSnapshot?>? completedDraft = null;
        editor.DraftChanged += (_, _) => completedDraft = editor.CaptureDraftAsync();
        gate.Release.TrySetResult();
        WaitFor(render);
        Assert.NotNull(completedDraft);
        WaitFor(completedDraft);
        Assert.NotNull(completedDraft.Result);
        ImageAnnotation patch = Assert.IsType<ImageAnnotation>(Assert.Single(editor.CreateCurrentImageSnapshot().Items));
        BitmapSource asset = Field<AnnotationImageStore>(editor, "_imageStore").Get(patch.AssetFileName)!;
        Assert.Equal(Pixels(asset), Pixels(render.Result));
        Assert.Contains(patch.AssetFileName, completedDraft.Result.Document);
    });

    [Fact]
    public void ClosingCancelsWorkerAndNeverInsertsItsLateResult() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        Task settled = editor.MosaicOperationsSettledAsync();
        WaitFor(gate.Started.Task);
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        WaitFor(settled);
        Assert.True(gate.Token.IsCancellationRequested);
        gate.Release.TrySetResult();
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(Controller(editor).Document.Items));
        Assert.Equal(1, Controller(editor).Undo.UndoCount); // Reserved when the gesture finished.
        Assert.False(editor.MosaicsReadyForExport);
    });

    [Fact]
    public void GeometryChangeDiscardsStaleResultAndBlocksExportUntilExplicitUndo() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        Task settled = editor.MosaicOperationsSettledAsync();
        WaitFor(gate.Started.Task);
        Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        WaitFor(settled);
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(Controller(editor).Document.Items));
        Assert.False(editor.MosaicsReadyForExport);
        Assert.Throws<InvalidOperationException>(() => editor.CreateCurrentImageSnapshot());
        int commits = 0;
        editor.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        editor.SaveAndClose();
        WaitFor(editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        Assert.Equal(0, commits);
        Assert.False(editor.IsCommitInProgress);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.False(editor.MosaicsReadyForExport); // The later rotation is undone first.
        Assert.Equal(1, Controller(editor).Undo.UndoCount);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.True(editor.MosaicsReadyForExport);
        Assert.Equal(0, Controller(editor).Undo.UndoCount);
        gate.Release.TrySetResult();
    });

    [Fact]
    public void UnrelatedAnnotationEditRemainsCompatibleWithPendingMosaic() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        AnnotationEditorController controller = Controller(editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(30, 20), false, false);
        controller.PointerMove(new PointD(45, 35), false);
        controller.PointerUp(new PointD(45, 35), false);
        gate.Release.TrySetResult();
        WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.Equal(2, controller.Document.Items.Count);
        Assert.IsType<ImageAnnotation>(controller.Document.Items[0]);
        Assert.Equal(EditorTool.Rectangle, controller.Tool);
        Assert.True(editor.MosaicsReadyForExport);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterShapeKeepsUndoLabelAndPaintOrderBeforeAndAfterCompletion(bool completeFirst) => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        string? mosaicLabel = controller.Undo.NextUndoDescription;
        RectangleAnnotation shape = DrawRectangle(controller);
        string? shapeLabel = controller.Undo.NextUndoDescription;
        Assert.NotEqual(mosaicLabel, shapeLabel);
        if (completeFirst)
        {
            gate.Release.TrySetResult();
            WaitFor(editor.MosaicOperationsSettledAsync());
        }
        Assert.Equal(shapeLabel, controller.Undo.NextUndoDescription);
        Assert.Same(shape, controller.Document.Items[1]);
        Assert.True(controller.PerformUndo());
        Assert.DoesNotContain(shape, controller.Document.Items);
        Assert.Equal(mosaicLabel, controller.Undo.NextUndoDescription);
        Assert.Single(controller.Document.Items);
        Assert.True(controller.PerformUndo());
        Assert.Empty(controller.Document.Items);
        Assert.True(editor.MosaicsReadyForExport);
        Assert.True(controller.PerformRedo());
        Assert.True(controller.PerformRedo());
        Assert.Same(shape, controller.Document.Items[1]);
        Assert.Equal(shapeLabel, controller.Undo.NextUndoDescription);
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        ImageAnnotation image = Assert.IsType<ImageAnnotation>(controller.Document.Items[0]);
        Assert.Same(shape, controller.Document.Items[1]);
        Assert.Equal(2, controller.Undo.UndoCount);
        Assert.Contains(image.AssetFileName, controller.Undo.ReferencedImageAssets);
        Assert.True(controller.PerformUndo());
        Assert.Same(image, Assert.Single(controller.Document.Items));
        Assert.True(controller.PerformRedo());
        Assert.Same(shape, controller.Document.Items[1]);
    });

    [Fact]
    public void UndoneWorkerCanFinishPrivatelyThenRedoRestoresItsAssetAndOriginalSlot() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        RectangleAnnotation shape = DrawRectangle(controller);
        Assert.True(controller.PerformUndo());
        Assert.True(controller.PerformUndo());
        Assert.False(gate.Token.IsCancellationRequested);
        Assert.True(editor.MosaicsReadyForExport);
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        Assert.Empty(controller.Document.Items);
        string asset = Assert.Single(controller.Undo.ReferencedImageAssets);
        Assert.True(controller.PerformRedo());
        ImageAnnotation image = Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
        Assert.Equal(asset, image.AssetFileName);
        Assert.True(controller.PerformRedo());
        Assert.Same(shape, controller.Document.Items[1]);
        Assert.True(editor.MosaicsReadyForExport);
    });

    [Fact]
    public void PendingSlotCannotBePickedSelectedDuplicatedOrDeleted() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        AnnotationItem slot = Assert.Single(controller.Document.Items);
        Assert.Null(controller.HitTest(new PointD(8, 8)));
        controller.SetSelected(slot);
        Assert.Empty(controller.SelectedItems);
        controller.ToggleSelected(slot);
        Assert.Empty(controller.SelectedItems);
        controller.SelectAll();
        Assert.Empty(controller.SelectedItems);
        controller.PointerDown(new PointD(-1, -1), false, false);
        controller.PointerMove(new PointD(50, 40), false);
        controller.PointerUp(new PointD(50, 40), false);
        Assert.Empty(controller.SelectedItems);
        controller.DuplicateSelected();
        controller.DeleteSelected();
        Assert.Same(slot, Assert.Single(controller.Document.Items));
        Assert.Equal(1, controller.Undo.UndoCount);
        gate.Release.TrySetResult();
        WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
    });

    [Fact]
    public void ReorderingLaterShapeAcrossPendingSlotKeepsCompletionAndUndoOrder() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        RectangleAnnotation shape = DrawRectangle(controller);
        controller.SetSelected(shape);
        controller.ReorderSelected(forward: false);
        Assert.Same(shape, controller.Document.Items[0]);
        Assert.IsType<PendingMosaicAnnotation>(controller.Document.Items[1]);
        Assert.False(gate.Token.IsCancellationRequested);
        gate.Release.TrySetResult();
        WaitFor(editor.MosaicOperationsSettledAsync());
        ImageAnnotation image = Assert.IsType<ImageAnnotation>(controller.Document.Items[1]);
        Assert.Same(shape, controller.Document.Items[0]);
        Assert.True(editor.MosaicsReadyForExport);
        Assert.True(controller.PerformUndo());
        Assert.Same(image, controller.Document.Items[0]);
        Assert.Same(shape, controller.Document.Items[1]);
        Assert.True(controller.PerformRedo());
        Assert.Same(shape, controller.Document.Items[0]);
        Assert.Same(image, controller.Document.Items[1]);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoAfterHistoryLimitCanExplicitlyRemoveUnretainedMosaic(bool failWorker) => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        if (failWorker)
            editor.ComputeMosaic = (_, _) => Task.FromException<BitmapSource>(new InvalidOperationException("test failure"));
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        for (int i = 0; i < UndoStack.DefaultDepthLimit; i++)
            controller.Undo.Execute(new AddAnnotationCommand(controller.Document,
                new RectangleAnnotation { Rect = new RectD(30, 20, 10, 10) }));
        Assert.Equal(UndoStack.DefaultDepthLimit, controller.Undo.UndoCount);
        if (failWorker)
        {
            gate.Release.TrySetResult();
            WaitFor(editor.MosaicOperationsSettledAsync());
        }
        for (int i = 0; i < UndoStack.DefaultDepthLimit; i++)
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.False(controller.CanUndo);
        Assert.False(editor.MosaicsReadyForExport);
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(controller.Document.Items));
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.Empty(controller.Document.Items);
        Assert.True(editor.MosaicsReadyForExport);
        Assert.Equal(UndoStack.DefaultDepthLimit, controller.Undo.RedoCount);
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        Assert.Empty(controller.Document.Items);
        Assert.True(controller.PerformRedo());
        Assert.IsType<RectangleAnnotation>(Assert.Single(controller.Document.Items));
    });

    [Fact]
    public void UndoPendingGestureRemovesItWithoutUndoingEarlierHistory() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        Task settled = editor.MosaicOperationsSettledAsync();
        WaitFor(gate.Started.Task);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        WaitFor(settled);
        Assert.Equal(1, Controller(editor).Undo.UndoCount);
        Assert.Empty(Controller(editor).Document.Items);
        Assert.True(editor.MosaicsReadyForExport);
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
    });

    [Fact]
    public void WorkerFailureKeepsExportClosedUntilUserRemovesTheFailedGesture() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        editor.ComputeMosaic = (_, _) => Task.FromException<BitmapSource>(new InvalidOperationException("test failure"));
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.False(editor.MosaicsReadyForExport);
        Assert.Throws<InvalidOperationException>(() => editor.CreateCurrentImageSnapshot());
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        Assert.True(editor.MosaicsReadyForExport);
    });

    [Fact]
    public void CompletionSelectionFailureKeepsRedoPlaceholderBlockedFromExport() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        AnnotationEditorController controller = Controller(editor);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        EventHandler? failOnce = null;
        failOnce = (_, _) =>
        {
            controller.SelectionChanged -= failOnce;
            throw new InvalidOperationException("test selection failure");
        };
        controller.SelectionChanged += failOnce;
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        Assert.IsType<ImageAnnotation>(Assert.Single(controller.Document.Items));
        Assert.False(editor.MosaicsReadyForExport);
        Assert.True(controller.PerformUndo());
        Assert.True(editor.MosaicsReadyForExport);
        Assert.True(controller.PerformRedo());
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(controller.Document.Items));
        Assert.False(editor.MosaicsReadyForExport);
        Assert.Throws<InvalidOperationException>(() => editor.CreateCurrentImageSnapshot());
        Assert.Throws<InvalidOperationException>(() => { _ = editor.RenderCurrentImageAsync(); });
        Assert.Null(editor.CaptureDraftAsync().GetAwaiter().GetResult());
    });

    [Fact]
    public void CompletionDraftCallbackFailureStillSettlesAndAllowsNextQueuedMosaic() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        editor.ApplyMosaic(new RectD(8, 6, 24, 18), 6);
        EventHandler? failOnce = null;
        failOnce = (_, _) =>
        {
            editor.DraftChanged -= failOnce;
            throw new InvalidOperationException("test draft notification failure");
        };
        editor.DraftChanged += failOnce;
        Task settled = editor.MosaicOperationsSettledAsync();
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        Assert.True(settled.IsCompletedSuccessfully);
        Assert.Equal(2, Controller(editor).Document.Items.OfType<ImageAnnotation>().Count());
        Assert.True(editor.MosaicsReadyForExport);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettledHistoryCommandReleasesFullSizeSourceReferences(bool failWorker) => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        if (failWorker)
            editor.ComputeMosaic = (_, _) => Task.FromException<BitmapSource>(new InvalidOperationException("test failure"));
        var gate = new WorkerGate(editor);
        var region = new RectD(0, 0, 24, 18);
        editor.ApplyMosaic(region, 6);
        WaitFor(gate.Started.Task);
        object request = Assert.Single(Field<System.Collections.IEnumerable>(editor, "_mosaicRequests").Cast<object>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.NotNull(request.GetType().GetProperty("Input", flags)!.GetValue(request));
        Assert.NotNull(request.GetType().GetProperty("DisplayedBase", flags)!.GetValue(request));
        gate.Release.TrySetResult();
        WaitFor(Field<Task>(editor, "_mosaicTail"));
        Assert.Null(request.GetType().GetProperty("Input", flags)!.GetValue(request));
        Assert.Null(request.GetType().GetProperty("DisplayedBase", flags)!.GetValue(request));
        Assert.Equal(region, Assert.IsType<RectD>(request.GetType().GetProperty("Region", flags)!.GetValue(request)));
        Assert.Equal(1, Controller(editor).Undo.UndoCount);
        Assert.Equal(!failWorker, editor.MosaicsReadyForExport);
    });

    [Fact]
    public void ClosingWhileCommitWaitsDoesNotInvokeExport() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        int commits = 0;
        editor.CommitRequested = _ => { commits++; return Task.FromResult(true); };
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        Task settled = editor.MosaicOperationsSettledAsync();
        WaitFor(gate.Started.Task);
        editor.SaveAndClose();
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        WaitFor(settled);
        WaitFor(editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        Assert.Equal(0, commits);
        Assert.IsType<PendingMosaicAnnotation>(Assert.Single(Controller(editor).Document.Items));
        gate.Release.TrySetResult();
    });

    [Fact]
    public void MutableBitmapIsSnapshottedBeforeWorkerStarts() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl template = Editor();
        var mutable = new WriteableBitmap(template.DisplayedBitmap);
        var bounds = new RectD(0, 0, 64, 48);
        var editor = new AnnotationEditorControl(new FrozenFrame(mutable, bounds, null, 0), bounds, mutable, null, null);
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(bounds, 12);
        WaitFor(gate.Started.Task);
        Assert.True(gate.Input!.Bitmap.IsFrozen);
        Assert.NotSame(mutable, gate.Input.Bitmap);
        byte[] snapshot = Pixels(gate.Input.Bitmap);
        mutable.WritePixels(new Int32Rect(0, 0, 64, 48), new byte[64 * 48 * 4], 64 * 4, 0);
        Assert.Equal(snapshot, Pixels(gate.Input.Bitmap));
        gate.Release.TrySetResult();
        WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.Single(Controller(editor).Document.Items);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CropChosenWhileMosaicRunsSurvivesInsertionAndCommit(bool commit) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(10, 8, 24, 18), 6);
        WaitFor(gate.Started.Task);
        editor.ShortcutActions["Crop"]();
        var crop = new RectD(8, 6, 40, 30);
        editor.PendingCrop!.SetRegion(crop);
        Invoke(editor, "RefreshCrop", true);
        var committed = new TaskCompletionSource<AnnotationEditingResult>();
        editor.CommitRequested = result => { committed.TrySetResult(result); return Task.FromResult(true); };
        if (commit) editor.SaveAndClose();
        gate.Release.TrySetResult();
        WaitFor(editor.MosaicOperationsSettledAsync());
        if (commit)
        {
            WaitFor(committed.Task);
            Assert.Equal(40, committed.Task.Result.Document.CanvasWidth);
            Assert.Equal(30, committed.Task.Result.Document.CanvasHeight);
            ImageAnnotation image = Assert.IsType<ImageAnnotation>(Assert.Single(committed.Task.Result.Document.Items));
            Assert.Equal(new RectD(2, 2, 24, 18), image.Rect);
        }
        else
        {
            Assert.Equal(crop, editor.PendingCrop!.Region);
            Assert.Equal(1, Controller(editor).Undo.UndoCount);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadCanEditAndCommitAndInterruptedGestureNeedsExplicitUndo(bool pending) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        Task interrupted = Task.CompletedTask;
        if (pending)
        {
            editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
            interrupted = editor.MosaicOperationsSettledAsync();
            WaitFor(gate.Started.Task);
        }
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        WaitFor(interrupted);
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        if (pending)
        {
            Assert.False(editor.MosaicsReadyForExport);
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        }
        Assert.True(editor.MosaicsReadyForExport);
        gate.Release.TrySetResult();
        editor.ApplyMosaic(new RectD(8, 6, 24, 18), 6);
        WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.Single(Controller(editor).Document.Items);
        var committed = new TaskCompletionSource<AnnotationEditingResult>();
        editor.CommitRequested = result => { committed.TrySetResult(result); return Task.FromResult(true); };
        Invoke(editor, "Commit", EditorCommitAction.CopyToClipboard, false);
        WaitFor(committed.Task);
        Assert.Single(committed.Task.Result.Document.Items);
    });

    [Fact]
    public void DispatcherShutdownWhileCommitWaitsDoesNotFaultTheCommitTask() => StaTestHost.Run(() =>
    {
        AnnotationEditorControl editor = Editor();
        var gate = new WorkerGate(editor);
        editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
        WaitFor(gate.Started.Task);
        Task commit = (Task)typeof(AnnotationEditorControl).GetMethod("CommitAfterMosaicsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [EditorCommitAction.Done, false])!;
        editor.CancelPendingMosaics(closing: true);
        editor.Dispatcher.InvokeShutdown();
        // No dispatcher remains to pump. Cancellation must settle from the worker side and
        // the commit continuation must recognize shutdown without posting an async-void fault.
        Assert.True(commit.Wait(TimeSpan.FromSeconds(15)));
        commit.GetAwaiter().GetResult();
        gate.Release.TrySetResult();
    });

    private sealed class WorkerGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AnnotationEditorControl.MosaicInput? Input { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal int ThreadId { get; private set; }
        internal int Calls;

        internal WorkerGate(AnnotationEditorControl editor)
        {
            var compute = editor.ComputeMosaic;
            editor.ComputeMosaic = async (input, token) =>
            {
                Interlocked.Increment(ref Calls);
                Input = input; Token = token; ThreadId = Environment.CurrentManagedThreadId;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token).ConfigureAwait(false);
                return await compute(input, token).ConfigureAwait(false);
            };
        }
    }

    internal static void WaitFor(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, dispatcher);
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
        }
        Assert.True(task.IsCompleted, "The mosaic operation did not settle while the dispatcher was running.");
        task.GetAwaiter().GetResult();
    }

    private static AnnotationEditorControl Editor()
    {
        byte[] pixels = new byte[64 * 48 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4); pixels[i + 1] = (byte)(i / 17); pixels[i + 2] = 128; pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        bitmap.Freeze();
        var bounds = new RectD(0, 0, 64, 48);
        return new AnnotationEditorControl(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap, null, null);
    }

    private static RectangleAnnotation DrawRectangle(AnnotationEditorController controller)
    {
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(30, 20), false, false);
        controller.PointerMove(new PointD(45, 35), false);
        controller.PointerUp(new PointD(45, 35), false);
        return Assert.IsType<RectangleAnnotation>(controller.Document.Items[^1]);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        internal DispatcherContextScope() => SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
