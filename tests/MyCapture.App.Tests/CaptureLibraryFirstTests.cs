using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureLibraryFirstTests
{
    [Fact]
    public void Selection_IsDurableBeforeEditorOpens_AndEscapeKeepsLibraryOriginal() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Queue.BeforePublicationWriteForTest = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        AnnotationEditorWindow? editor = null;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            SelectionPersistRequested = fixture.Select,
            RequiresCaptureExclusion = () => true,
            ApplyCaptureExclusion = window =>
            {
                editor = Assert.IsType<AnnotationEditorWindow>(window);
                CaptureRecord record = Assert.Single(fixture.Queue.Records);
                Assert.True(File.Exists(fixture.Paths.IndexFile));
                Assert.True(File.Exists(fixture.Queue.GetFilePath(record, CaptureFileNames.Original)));
                Assert.True(File.Exists(Path.Combine(fixture.Queue.GetDirectory(record), "meta.json")));
                Assert.False(File.Exists(fixture.Queue.GetFilePath(record, CaptureFileNames.OriginalPending)));
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;
                return true;
            },
        };
        try
        {
            Assert.True(coordinator.StartWithSelection(fixture.Frame, fixture.Region, "매출.xlsx - Excel"));
            PumpUntil(() => entered.IsSet);
            Assert.Null(editor);
            Assert.False(coordinator.LastTransitionForTest.IsCompleted);
            release.Set();
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.NotNull(editor);
            Assert.True(editor.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            Assert.False(coordinator.IsActive);
            var reloaded = new CaptureQueue(fixture.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.Equal("매출.xlsx - Excel", Assert.Single(reloaded.Records).SourceWindowTitle);
        }
        finally
        {
            release.Set();
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            coordinator.Cancel();
        }
    });

    [Fact]
    public void EditedSave_ReplacesRenderedImageForTheSameRecord_AndUsesTitleInExportSuggestion() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord record = Assert.Single(fixture.Queue.Records);
        string originalPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Original);
        string renderedPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered);
        byte[] original = File.ReadAllBytes(originalPath);
        var document = AnnotationDocument.CreateFor(64, 48);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(4, 4, 40, 30),
            Stroke = ColorRgba.FromRgb(255, 0, 0),
            StrokeThickness = 4,
        });
        AnnotationEditingResult edit = fixture.Result(document, EditorCommitAction.Done);
        Assert.True(fixture.Wait(fixture.Commit(edit)));
        Assert.Equal(record.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Equal(original, File.ReadAllBytes(originalPath));
        Assert.NotEqual(original, File.ReadAllBytes(renderedPath));
        Assert.True(record.HasAnnotations);
        Assert.Single(AnnotationDocument.TryFromJson(File.ReadAllText(
            fixture.Queue.GetFilePath(record, CaptureFileNames.Layers)))!.Items);
        Assert.True(fixture.Wait(fixture.Commit(edit)));
        Assert.Equal(record.Id, Assert.Single(fixture.Queue.Records).Id);

        string? suggested = null;
        fixture.CommitService.SaveAsPrompt = path => { suggested = path; return null; };
        Assert.False(fixture.Wait(fixture.Commit(fixture.Result(document, EditorCommitAction.SaveAs))));
        Assert.Equal(QuickSaveNaming.BuildStem(QuickSaveNaming.DefaultPattern, record.CreatedAt,
            fixture.Selection.SourceTitle) + ".png", Path.GetFileName(suggested));
    });

    [Fact]
    public void FailedInitialSave_RemainsUncommitted_AndRetryUsesOriginalIdentity() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic storage failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        fixture.Persistence.BeforeRecordMetadataCommit = null;
        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.Done))));
        Assert.Equal(pending.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Equal(pending.Id, fixture.CurrentRecord!.Id);
        Assert.False(File.Exists(fixture.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending)));
    });

    [Fact]
    public void FirstQuickSaveRetry_UsesRecoveredRecordTitleAndOriginalCaptureTime() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic storage failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        // Distinguish capture time from retry time deterministically without a timed wait.
        DateTimeOffset capturedAt = pending.CreatedAt.AddMinutes(-5);
        pending.CreatedAt = capturedAt;
        fixture.Persistence.BeforeRecordMetadataCommit = null;

        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.QuickSave))));

        CaptureRecord recovered = Assert.Single(fixture.Queue.Records);
        Assert.Equal(pending.Id, recovered.Id);
        Assert.Same(recovered, fixture.CurrentRecord);
        Assert.Equal(capturedAt, recovered.CreatedAt);
        string exported = Assert.Single(Directory.GetFiles(fixture.Paths.QuickSaveRoot, "*.png"));
        Assert.Equal(QuickSaveNaming.BuildStem(QuickSaveNaming.DefaultPattern, capturedAt,
            fixture.Selection.SourceTitle) + ".png", Path.GetFileName(exported));
        using FileStream stream = File.OpenRead(exported);
        BitmapFrame image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(64, image.PixelWidth);
        Assert.Equal(48, image.PixelHeight);
        Assert.False(File.Exists(fixture.Queue.GetFilePath(recovered, CaptureFileNames.OriginalPending)));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CapacityPressure_CannotEvictCaptureBetweenPersistenceAndEditor(bool retry, bool byteLimit) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(byteLimit
            ? new QueueSettings { MaxBytes = 1 }
            : new QueueSettings { MaxItems = 1 });
        if (!byteLimit)
        {
            CaptureRecord older = fixture.Wait(fixture.Persistence.PersistOriginalAsync(
                fixture.Frame.Bitmap, 1, "older pinned capture", string.Empty));
            older.IsPinned = true;
        }
        var evicted = new List<Guid>();
        fixture.Queue.Evicted += (_, args) => evicted.Add(args.Record.Id);
        if (retry)
            fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic capacity-pressure save failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord pending = fixture.PendingRecord;
        AnnotationEditingResult result = fixture.Result(AnnotationDocument.CreateFor(64, 48), EditorCommitAction.Done);
        if (retry)
        {
            Assert.Null(fixture.CurrentRecord);
            Assert.Null(fixture.CurrentEditSession);
            // A second failure must also release its editor lease; a later successful retry
            // still publishes and edits the same identity while the capacity is exceeded.
            Assert.False(fixture.Wait(fixture.Commit(result)));
            Assert.Null(fixture.CurrentEditSession);
            fixture.Persistence.BeforeRecordMetadataCommit = null;
            Assert.True(fixture.Wait(fixture.Commit(result)));
        }
        Assert.Same(pending, fixture.CurrentRecord);
        Assert.Same(pending, fixture.Queue.Find(pending.Id));
        Assert.DoesNotContain(pending.Id, evicted);
        Assert.NotNull(fixture.CurrentEditSession);
        Assert.True(File.Exists(fixture.Queue.GetFilePath(pending, CaptureFileNames.Original)));
        Assert.True(fixture.Wait(fixture.Commit(result)));
        Assert.Same(pending, fixture.Queue.Find(pending.Id));
        Assert.DoesNotContain(pending.Id, evicted);

        // The queue deliberately retains the newest sole unpinned/oversized capture.
        // After the editor releases its lease, a newer capture makes this one eligible
        // for normal capacity eviction. A leaked retry lease would prevent that eviction.
        fixture.ReleaseEditSession();
        Assert.False(fixture.Persistence.IsBusy(pending.Id));
        CaptureRecord successor = fixture.Wait(fixture.Persistence.PersistOriginalAsync(
            fixture.Frame.Bitmap, 1, "next capture", string.Empty));
        Assert.Same(successor, fixture.Queue.Find(successor.Id));
        Assert.Null(fixture.Queue.Find(pending.Id));
        Assert.Contains(pending.Id, evicted);
    });

    private static void PumpUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(completed(), "The capture transition did not finish.");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-library-first-").FullName;
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService CommitService { get; }
        internal FrozenFrame Frame { get; }
        internal RectD Region { get; } = new(0, 0, 64, 48);
        internal CaptureSelectionCompletedEventArgs Selection => new(Frame, Region, Frame.Bitmap,
            "CaptureService.cs - my-capture - Visual Studio Code", recordForRepeat: false, copyToClipboardImmediately: false);
        internal CaptureRecord? CurrentRecord => (CaptureRecord?)Get("_currentRecord");
        internal CaptureRecord PendingRecord => (CaptureRecord)Get("_pendingRecord")!;
        internal IDisposable? CurrentEditSession => (IDisposable?)Get("_currentEditSession");

        internal Fixture(QueueSettings? queueSettings = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Paths = AppPaths.CreateForRoot(_root);
            var settings = new AppSettings();
            if (queueSettings is not null) settings.Queue = queueSettings;
            Queue = new CaptureQueue(Paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            CommitService = new CaptureCommitService(Persistence, () => settings, () => Paths,
                NullLogger<CaptureCommitService>.Instance, _ => Task.FromResult(true));
            Set("_queue", Queue);
            Set("_persistence", Persistence);
            Set("_commit", CommitService);
            BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32,
                null, Enumerable.Repeat((byte)255, 64 * 48 * 4).ToArray(), 64 * 4);
            bitmap.Freeze();
            Frame = new FrozenFrame(bitmap, Region, null, 0);
        }

        internal Task Select(CaptureSelectionCompletedEventArgs selection) =>
            (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Flags)!.Invoke(_app, [selection])!;
        internal Task<bool> Commit(AnnotationEditingResult result) =>
            (Task<bool>)typeof(App).GetMethod("HandleCommitAsync", Flags)!.Invoke(_app, [result])!;
        internal AnnotationEditingResult Result(AnnotationDocument document, EditorCommitAction action) =>
            new(Frame, Region, Frame.Bitmap, document, action,
                new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
        internal void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        internal T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
        private object? Get(string name) => typeof(App).GetField(name, Flags)!.GetValue(_app);
        private void Set(string name, object value) => typeof(App).GetField(name, Flags)!.SetValue(_app, value);

        internal void ReleaseEditSession()
        {
            CurrentEditSession?.Dispose();
            typeof(App).GetField("_currentEditSession", Flags)!.SetValue(_app, null);
        }

        public void Dispose()
        {
            ReleaseEditSession();
            SynchronizationContext.SetSynchronizationContext(_previous);
            if (Directory.Exists(_root))
                TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }
}
