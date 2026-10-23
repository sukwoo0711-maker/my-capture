using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorMosaicRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardDuringMosaicAutosaveClosesPromptlyOrPreservesTheUsableEditorWhenRetirementFails(bool failRetirement) => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var fixture = new EditorFixture();
        AnnotationEditorWindow window = fixture.Window;
        AnnotationEditorControl editor = window.Editor;
        AnnotationEditorController controller = Controller(editor);
        var session = Field<EditDraftSession>(window, "_draftSession");
        EditDraft original = Assert.Single(fixture.Store.ReadAvailable()).Draft;
        if (failRetirement)
            Directory.CreateDirectory(Path.Combine(fixture.Root, fixture.Id.ToString("N"), "retired"));
        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.DraftRecordSource = () => { snapshotEntered.TrySetResult(); return null; };
        var gate = new WorkerGate(editor);
        try
        {
            editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
            WaitFor(gate.Started.Task);
            PendingMosaicAnnotation placeholder = Assert.IsType<PendingMosaicAnnotation>(Assert.Single(controller.Document.Items));
            // Wait for the real debounce timer to enter the production snapshot callback.
            // The old callback held the writer here until the gated mosaic finished.
            WaitFor(snapshotEntered.Task);
            Assert.False(gate.Release.Task.IsCompleted);
            int prompts = 0;
            window.ConfirmClose = () => { prompts++; return EditorCloseChoice.DontSave; };
            window.RequestClose();
            Task<bool> close = Field<Task<bool>>(window, "_discardCloseTask");
            WaitFor(close);
            Assert.Equal(!failRetirement, close.Result);
            Assert.Equal(1, prompts);
            Assert.True(session.LastWrite.IsCompletedSuccessfully);
            Assert.False(gate.Release.Task.IsCompleted);
            Assert.Equal(!failRetirement, gate.Token.IsCancellationRequested);
            Assert.Equal(failRetirement, window.IsVisible);

            if (failRetirement)
            {
                Assert.True(window.IsEnabled);
                Assert.False(window.IsCloseInProgress);
                Assert.False(fixture.Store.IsRetired(fixture.Id));
                Assert.Equal(original.Document, Assert.Single(fixture.Store.ReadAvailable()).Draft.Document);
                Assert.Same(placeholder, Assert.Single(controller.Document.Items));
                Assert.Equal(1, controller.Undo.UndoCount);
                Assert.True(editor.HasUnsettledMosaicWork);
                controller.Tool = EditorTool.Rectangle;
                controller.PointerDown(new PointD(30, 20));
                controller.PointerMove(new PointD(45, 35));
                controller.PointerUp(new PointD(45, 35));
                RectangleAnnotation rectangle = Assert.IsType<RectangleAnnotation>(controller.Document.Items[1]);
                Task previousWrite = session.LastWrite;
                gate.Release.TrySetResult();
                WaitFor(editor.MosaicOperationsSettledAsync());
                ImageAnnotation mosaic = Assert.IsType<ImageAnnotation>(controller.Document.Items[0]);
                Assert.Same(rectangle, controller.Document.Items[1]);
                Assert.Equal(2, controller.Undo.UndoCount);
                Assert.True(editor.MosaicsReadyForExport);
                // Completion must restart autosaving after the failed retirement reopens it.
                PumpUntil(() => !ReferenceEquals(previousWrite, session.LastWrite) && session.LastWrite.IsCompleted);
                session.LastWrite.GetAwaiter().GetResult();
                AssertDraftIncludesMosaic(fixture.Store, mosaic);
            }
            else
            {
                Assert.True(fixture.Store.IsRetired(fixture.Id));
                Assert.Empty(fixture.Store.ReadAvailable());
                WaitFor(Field<Task>(editor, "_mosaicTail"));
                Assert.Same(placeholder, Assert.Single(controller.Document.Items));
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    });

    [Fact]
    public void ProtectedExitWaitsForMosaicAndFlushesItsPixelsBeforeAskingToClose() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using var fixture = new EditorFixture();
        AnnotationEditorWindow window = fixture.Window;
        AnnotationEditorControl editor = window.Editor;
        var gate = new WorkerGate(editor);
        try
        {
            editor.ApplyMosaic(new RectD(0, 0, 24, 18), 6);
            WaitFor(gate.Started.Task);
            int prompts = 0;
            window.ConfirmClose = () =>
            {
                prompts++;
                Assert.True(gate.Release.Task.IsCompleted);
                Assert.False(editor.HasUnsettledMosaicWork);
                AssertDraftIncludesMosaic(fixture.Store,
                    Assert.IsType<ImageAnnotation>(Assert.Single(Controller(editor).Document.Items)));
                return EditorCloseChoice.Cancel;
            };
            Task<bool> exit = window.PrepareForAppExitAsync();
            Assert.False(exit.IsCompleted);
            Assert.False(window.IsEnabled);
            // Several turns of the 50 ms exit guard. Without the mosaic guard the flush skipped
            // the unfinished gesture and the question appeared here, before the gate opened.
            PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.False(exit.IsCompleted);
            Assert.Equal(0, prompts);
            Assert.False(gate.Token.IsCancellationRequested);
            gate.Release.TrySetResult();
            WaitFor(exit);
            Assert.False(exit.Result);
            Assert.Equal(1, prompts);
            Assert.True(window.IsVisible);
            Assert.True(window.IsEnabled);
            Assert.False(gate.Token.IsCancellationRequested);
            Assert.False(fixture.Store.IsRetired(fixture.Id));
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    });

    private static void AssertDraftIncludesMosaic(EditDraftStore store, ImageAnnotation mosaic)
    {
        EditDraft draft = Assert.Single(store.ReadAvailable()).Draft;
        AnnotationDocument document = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(draft.Document));
        ImageAnnotation recovered = Assert.Single(document.Items.OfType<ImageAnnotation>());
        Assert.Equal(mosaic.AssetFileName, recovered.AssetFileName);
        string file = draft.Metadata["asset:" + recovered.AssetFileName];
        Assert.True(File.Exists(store.ResolveFile(draft, file)));
        Assert.NotEmpty(File.ReadAllBytes(store.ResolveFile(draft, file)));
    }

    private sealed class EditorFixture : IDisposable
    {
        internal string Root { get; } = OwnedTestDirectory.Create("mc-mosaic-recovery-");
        internal Guid Id { get; } = Guid.NewGuid();
        internal EditDraftStore Store { get; }
        internal AnnotationEditorWindow Window { get; }

        internal EditorFixture()
        {
            Store = new EditDraftStore(Root, _ => { });
            var bitmap = Solid(64, 48, 0x30);
            var bounds = new RectD(0, 0, 64, 48);
            Window = new AnnotationEditorWindow(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            Window.AdoptRecoveredDraft(Store, Id, 1);
            Window.Show();
            Window.UpdateLayout();
            WaitFor(Window.FlushDraftAsync());
        }

        public void Dispose()
        {
            // Each test releases its worker gate before disposing this fixture. A failed
            // assertion may leave the asynchronous user close owning the window; let it
            // settle before issuing an unprompted close or removing its draft directory.
            if (Field<Task<bool>?>(Window, "_discardCloseTask") is { } pendingClose)
                WaitFor(pendingClose);
            if (Window.IsVisible) Window.Close();
            WaitFor(Field<Task>(Window.Editor, "_mosaicTail"));
            Assert.False(Window.IsVisible);
            WaitFor(EditDraftSession.WaitForFinalizationsAsync());
            OwnedTestDirectory.Delete(Root);
        }
    }

    private sealed class WorkerGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }

        internal WorkerGate(AnnotationEditorControl editor)
        {
            var compute = editor.ComputeMosaic;
            editor.ComputeMosaic = async (input, token) =>
            {
                Token = token;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token).ConfigureAwait(false);
                return await compute(input, token).ConfigureAwait(false);
            };
        }
    }

    private static void WaitFor(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void PumpFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        PumpUntil(() => watch.Elapsed >= duration);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The editor did not finish while the mosaic gate remained controlled by the test.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        internal DispatcherContextScope() => SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }
}
