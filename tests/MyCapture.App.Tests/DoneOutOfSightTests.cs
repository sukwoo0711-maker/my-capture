using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Tests;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Done takes the capture editor off the screen as soon as the clipboard holds the result and lets
/// the library save finish behind it: the window closes, and the commit's events follow, only once
/// the save is done; a failed save shows the editor again with its edits.
/// </summary>
public sealed class DoneOutOfSightTests
{
    // ---- Editor window ------------------------------------------------------------------

    [Fact]
    public void CaptureEditorLeavesTheScreenWhenCopiedAndClosesAfterTheSave() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.FinishesOutOfSight = true;
        AddRectangle(host);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        host.Window.CommitRequested = result =>
        {
            order.Add("copied");
            result.CopiedToClipboard!.Invoke();
            return saved.Task;
        };
        host.Window.IsVisibleChanged += (_, e) => order.Add((bool)e.NewValue ? "shown" : "hidden");
        host.Window.Committed += (_, _) => order.Add("committed");
        host.Window.Closed += (_, _) => order.Add("closed");

        host.Editor.SaveAndClose();
        PumpUntil(() => !host.Window.IsVisible);
        Assert.True(host.Window.IsFinishingOutOfSight);
        Assert.True(host.Editor.IsCommitInProgress);
        Assert.False(host.Window.WasCommitted);
        Task finished = host.Window.FinishedOutOfSight;
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.False(finished.IsCompleted);

        saved.SetResult(true);
        PumpUntil(() => finished.IsCompleted);
        Assert.Equal(["copied", "hidden", "committed", "closed"], order);
        Assert.True(host.Window.WasCommitted);
        Assert.False(host.Window.IsFinishingOutOfSight);
    });

    [Fact]
    public void FailedSaveAfterLeavingTheScreenShowsTheEditorAgainWithItsEdits() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.FinishesOutOfSight = true;
        AddRectangle(host);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Window.CommitRequested = result =>
        {
            result.CopiedToClipboard!.Invoke();
            return saved.Task;
        };
        int committed = 0, cancelled = 0;
        host.Window.Committed += (_, _) => committed++;
        host.Window.Cancelled += (_, _) => cancelled++;

        host.Editor.SaveAndClose();
        PumpUntil(() => !host.Window.IsVisible);
        Task finished = host.Window.FinishedOutOfSight;
        saved.SetException(new IOException("The library save failed."));
        PumpUntil(() => finished.IsCompleted && !host.Editor.IsCommitInProgress);

        Assert.True(host.Window.IsVisible);
        Assert.False(host.Window.IsFinishingOutOfSight);
        Assert.Equal(UiText.Get("Text_06F58809C083"), host.Editor.StatusText);
        Assert.True(host.Editor.IsHitTestVisible);
        Assert.True(host.Editor.HasUnsavedChanges);
        Assert.Single(Controller(host.Editor).Document.Items);
        Assert.Equal(0, committed);
        Assert.Equal(0, cancelled);
        Assert.False(host.Window.WasCommitted);
    });

    [Fact]
    public void EditorsThatDoNotOptInStayOnScreenUntilTheSaveEnds() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using EditorWindowHost host = EditorWindowHost.Create();
        AddRectangle(host);
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? reported = () => { };
        host.Window.CommitRequested = result =>
        {
            reported = result.CopiedToClipboard;
            return saved.Task;
        };

        // A gallery re-edit runs modally: hiding it would end ShowDialog while its save still runs.
        host.Editor.SaveAndClose();
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Null(reported);
        Assert.True(host.Window.IsVisible);
        Assert.True(host.Window.FinishedOutOfSight.IsCompleted);
        saved.SetResult(true);
        PumpUntil(() => !host.Window.IsVisible);
        Assert.True(host.Window.WasCommitted);
    });

    [Fact]
    public void CopyNeverLeavesTheScreen() => StaTestHost.Run(() =>
    {
        using var context = new DispatcherContextScope();
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.FinishesOutOfSight = true;
        AddRectangle(host);
        Action? reported = () => { };
        host.Window.CommitRequested = result =>
        {
            reported = result.CopiedToClipboard;
            return Task.FromResult(true);
        };

        // The copy-only commit (the editor stays open), as the app runs it.
        typeof(AnnotationEditorControl).GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host.Editor, [EditorCommitAction.CopyToClipboard, false]);
        PumpUntil(() => !host.Editor.IsCommitInProgress);
        Assert.Null(reported);
        Assert.True(host.Window.IsVisible);
        Assert.False(host.Window.WasCommitted);
    });

    // ---- Commit service -----------------------------------------------------------------

    [Fact]
    public void DoneReportsTheClipboardWhileTheSaveIsStillRunning() => StaTestHost.Run(() =>
    {
        using var fixture = new CommitFixture((_, _) => Task.FromResult(true));
        bool persistedWhenCopied = true;
        AnnotationEditingResult result = fixture.Result();
        result.CopiedToClipboard = () => persistedWhenCopied = fixture.Persisted;

        Assert.True(fixture.Commit(result).GetAwaiter().GetResult());
        Assert.False(persistedWhenCopied);
        Assert.True(fixture.Persisted);
        Assert.True(fixture.Record.HasAnnotations);
    });

    [Fact]
    public void DoneDoesNotReportAFailedCopyAndStillSaves() => StaTestHost.Run(() =>
    {
        using var fixture = new CommitFixture((_, _) => Task.FromResult(false));
        int reported = 0;
        AnnotationEditingResult result = fixture.Result();
        result.CopiedToClipboard = () => reported++;

        Assert.False(fixture.Commit(result).GetAwaiter().GetResult());
        Assert.Equal(0, reported);
        Assert.True(fixture.Persisted);
    });

    [Fact]
    public void DoneWaitsForTheSaveBeforeAFailedCopyThrows() => StaTestHost.Run(() =>
    {
        using var fixture = new CommitFixture((_, _) => throw new InvalidOperationException("The clipboard writer failed."));
        AnnotationEditingResult result = fixture.Result();
        result.CopiedToClipboard = () => throw new InvalidOperationException("A failed copy is never reported as copied.");

        Assert.Throws<InvalidOperationException>(() => fixture.Commit(result).GetAwaiter().GetResult());
        Assert.True(fixture.Persisted);
    });

    [Fact]
    public void DoneRefusedByTheSavesChecksNeverReachesTheClipboard() => StaTestHost.Run(() =>
    {
        int copies = 0;
        using var fixture = new CommitFixture((_, _) => { copies++; return Task.FromResult(true); });
        Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        int reported = 0;
        AnnotationEditingResult result = fixture.Result();
        result.CopiedToClipboard = () => reported++;

        // The capture was deleted while it was edited: the save is refused before it writes.
        Assert.Throws<CaptureGenerationConflictException>(() => fixture.Commit(result).GetAwaiter().GetResult());
        Assert.Equal(0, copies);
        Assert.Equal(0, reported);
        Assert.False(fixture.Persisted);
    });

    [Fact]
    public void DoneReportsAWriteFailureEvenAfterTheClipboardIsReady() => StaTestHost.Run(() =>
    {
        int copies = 0;
        using var fixture = new CommitFixture((_, _) => { copies++; return Task.FromResult(true); });
        fixture.Persistence.BeforeOriginalRewriteForTest = _ => throw new IOException("The disk refused the write.");
        AnnotationEditingResult result = fixture.Result();

        // The editor may already have left the screen; the failure still reaches it.
        Assert.Throws<IOException>(() => fixture.Service.CommitAsync(fixture.Record, result, replaceOriginal: true).GetAwaiter().GetResult());
        Assert.Equal(1, copies);
        Assert.False(fixture.Persisted);
    });

    // ---- Helpers ------------------------------------------------------------------------

    private static void AddRectangle(EditorWindowHost host)
    {
        AnnotationEditorController controller = Controller(host.Editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(20, 20));
        controller.PointerMove(new PointD(120, 80));
        controller.PointerUp(new PointD(120, 80));
        Assert.IsType<RectangleAnnotation>(Assert.Single(controller.Document.Items));
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the dispatcher.");
            PumpOnce();
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration) PumpOnce();
    }

    private static void PumpOnce()
    {
        var frame = new DispatcherFrame();
        var resume = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background,
            (sender, _) => { ((DispatcherTimer)sender!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        resume.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Runs async continuations on this test thread's dispatcher, as in the app.</summary>
    private sealed class DispatcherContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        internal DispatcherContextScope() =>
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
    }

    /// <summary>A persisted capture in an isolated library and the commit service that saves it.</summary>
    private sealed class CommitFixture : IDisposable
    {
        private readonly string _root;
        private readonly BitmapSource _original;

        internal CommitFixture(Func<BitmapSource, string?, Task<bool>> copy)
        {
            _root = TestRecycleBin.CreateTempSubdirectory("mc-done-out-of-sight-").FullName;
            AppPaths paths = AppPaths.CreateForRoot(_root);
            var settings = new AppSettings();
            Queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Service = new CaptureCommitService(Persistence, () => settings, () => paths, NullLogger<CaptureCommitService>.Instance, copy);
            _original = Solid(48, 32, 0x40);
            Record = Persistence.PersistOriginal(_original, 1.0, "Done test", "Test");
            Persistence.ImagePersisted += (_, record) =>
            {
                if (record.Id == Record.Id) Persisted = true;
            };
        }

        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService Service { get; }
        internal CaptureRecord Record { get; }
        internal bool Persisted { get; private set; }

        internal AnnotationEditingResult Result()
        {
            var document = AnnotationDocument.CreateFor(48, 32);
            document.Items.Add(new RectangleAnnotation { Rect = new RectD(4, 4, 20, 12) });
            return new AnnotationEditingResult(new FrozenFrame(_original, new RectD(0, 0, 48, 32), null, 0),
                new RectD(0, 0, 48, 32), _original, document, EditorCommitAction.Done,
                new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
        }

        internal Task<bool> Commit(AnnotationEditingResult result) => Service.CommitAsync(Record, result);

        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
    }
}
