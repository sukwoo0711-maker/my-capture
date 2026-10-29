using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SavedLocationCommitTests
{
    [Theory]
    [InlineData((int)EditorCommitAction.Done, false)]
    [InlineData((int)EditorCommitAction.Done, true)]
    [InlineData((int)EditorCommitAction.QuickSave, false)]
    [InlineData((int)EditorCommitAction.QuickSave, true)]
    [InlineData((int)EditorCommitAction.SaveAs, false)]
    [InlineData((int)EditorCommitAction.SaveAs, true)]
    public void BothHostCallbacksShowReceiptAfterFinishingCommit(int value, bool libraryEdit) => RunOnDispatcher(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using var fixture = new Fixture();
        fixture.Settings.General.NotifyOnQuickSave = false;
        using var presenter = Presenter();
        CaptureRecord record = fixture.PersistOriginal();
        using CaptureEditSession session = fixture.Commit.BeginEditSession(record);
        App app = ConnectHostReceipt(fixture, presenter, record, session);

        Assert.True(Wait(InvokeHostCommit(app, fixture, record, session, Result((EditorCommitAction)value), libraryEdit)));

        Assert.True(presenter.HasNotification);
        Button open = FirstRunWelcomeTests.Descendants(presenter.NotificationWindowForTest!).OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "MyCapture.Receipt.Open");
        Assert.Equal("라이브러리에 저장됨", AutomationProperties.GetName(open));
        Assert.Equal(1, fixture.Settings.General.SavedLocationTipCount);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothHostCallbacksLeaveNoReceiptWhenCommitFailsOrIsCancelled(bool libraryEdit, bool cancel) => RunOnDispatcher(() =>
    {
        using var fixture = new Fixture(copySucceeds: cancel);
        using var presenter = Presenter();
        CaptureRecord record = fixture.PersistOriginal();
        using CaptureEditSession session = fixture.Commit.BeginEditSession(record);
        App app = ConnectHostReceipt(fixture, presenter, record, session);
        if (cancel) fixture.Commit.SaveAsPrompt = _ => null;

        Assert.False(Wait(InvokeHostCommit(app, fixture, record, session,
            Result(cancel ? EditorCommitAction.SaveAs : EditorCommitAction.Done), libraryEdit)));

        Assert.False(presenter.HasNotification);
        Assert.Equal(0, fixture.Settings.General.SavedLocationTipCount);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done, false)]
    [InlineData((int)EditorCommitAction.Done, true)]
    [InlineData((int)EditorCommitAction.QuickSave, false)]
    [InlineData((int)EditorCommitAction.QuickSave, true)]
    [InlineData((int)EditorCommitAction.SaveAs, false)]
    [InlineData((int)EditorCommitAction.SaveAs, true)]
    [InlineData((int)EditorCommitAction.Pin, false)]
    [InlineData((int)EditorCommitAction.Pin, true)]
    public void FinishingCommitAnnouncesDurableRecordForCaptureAndLibraryEdit(int value, bool libraryEdit) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        using CaptureEditSession? session = libraryEdit ? fixture.Commit.BeginEditSession(record) : null;
        var announced = new List<CaptureRecord>();
        var durableRevisions = new List<long>();
        fixture.Commit.FinishingCommitCompleted += saved =>
        {
            announced.Add(saved);
            var reopened = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
            reopened.Load();
            durableRevisions.Add(reopened.Find(saved.Id)?.ContentRevision ?? -1);
        };

        Assert.True(fixture.Commit.CommitAsync(record, Result((EditorCommitAction)value), session).GetAwaiter().GetResult());

        Assert.Same(record, Assert.Single(announced));
        Assert.True(record.ContentRevision > 0);
        Assert.Equal(record.ContentRevision, Assert.Single(durableRevisions));
        Assert.Equal(1, fixture.CopyCalls);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done)]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    public void NewlyPublishedCaptureIsTheRecordAnnounced(int value) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord? created = null;
        CaptureRecord? announced = null;
        fixture.Commit.FinishingCommitCompleted += saved => announced = saved;

        bool completed = fixture.Commit.CommitAsync(null, Result((EditorCommitAction)value), createRecordAsync: () =>
        {
            created = fixture.PersistOriginal();
            return Task.FromResult<(CaptureRecord, CaptureEditSession?)>((created, null));
        }).GetAwaiter().GetResult();

        Assert.True(completed);
        Assert.NotNull(created);
        Assert.Same(created, announced);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done)]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    [InlineData((int)EditorCommitAction.Pin)]
    public void FailedClipboardDoesNotAnnounceFinishingCommit(int value) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(copySucceeds: false);
        CaptureRecord record = fixture.PersistOriginal();
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;

        Assert.False(fixture.Commit.CommitAsync(record, Result((EditorCommitAction)value)).GetAwaiter().GetResult());
        Assert.Empty(announced);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done)]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    public void RefusedLibrarySaveDoesNotAnnounceFinishingCommit(int value) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        // The capture was deleted while it was edited: the library save is refused. A metadata
        // write that fails after the files are committed is not a failure (it is recorded as
        // pending metadata and the edit stays), so it is not the case tested here.
        Assert.True(fixture.Queue.Remove(record.Id));
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;

        Assert.Throws<CaptureGenerationConflictException>(() => fixture.Commit.CommitAsync(record, Result((EditorCommitAction)value)).GetAwaiter().GetResult());
        Assert.Empty(announced);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledSaveAsDoesNotAnnounceOrPersist(bool reduced) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        long previousRevision = record.ContentRevision;
        fixture.Commit.SaveAsPrompt = _ => null;
        fixture.Commit.ReducedExportPrompt = (_, _) => false;
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;
        AnnotationEditingResult result = Result(EditorCommitAction.SaveAs, reduceExport: reduced);

        Assert.False(fixture.Commit.CommitAsync(record, result).GetAwaiter().GetResult());
        Assert.Empty(announced);
        Assert.Equal(previousRevision, record.ContentRevision);
        Assert.Equal(0, fixture.CopyCalls);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    public void FailedFileExportDoesNotAnnounce(int value) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        string blocker = Path.Combine(fixture.Root, "not-a-directory");
        File.WriteAllText(blocker, "fixture");
        fixture.Settings.Export.QuickSaveDirectoryOverride = blocker;
        fixture.Commit.SaveAsPrompt = _ => Path.Combine(blocker, "capture.png");
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;

        Assert.False(fixture.Commit.CommitAsync(record, Result((EditorCommitAction)value)).GetAwaiter().GetResult());
        Assert.Empty(announced);
    });

    [Theory]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    public void RecoveryExportWithoutLibraryRecordDoesNotAnnounce(int value) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;

        Assert.True(fixture.Commit.CommitAsync(null, Result((EditorCommitAction)value)).GetAwaiter().GetResult());
        Assert.Empty(announced);
    });

    [Fact]
    public void CopyOnlyDoesNotAnnounceOrAdvanceLibraryGeneration() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        long previousRevision = record.ContentRevision;
        var announced = new List<CaptureRecord>();
        fixture.Commit.FinishingCommitCompleted += announced.Add;

        Assert.True(fixture.Commit.CommitAsync(record, Result(EditorCommitAction.CopyToClipboard)).GetAwaiter().GetResult());
        Assert.Empty(announced);
        Assert.Equal(previousRevision, record.ContentRevision);
    });

    [Fact]
    public void ReceiptFailureDoesNotUndoCompletedCommit() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.PersistOriginal();
        fixture.Commit.FinishingCommitCompleted += _ => throw new InvalidOperationException("Synthetic receipt failure");

        Assert.True(fixture.Commit.CommitAsync(record, Result(EditorCommitAction.Done)).GetAwaiter().GetResult());
        Assert.True(record.ContentRevision > 0);
    });

    [Fact]
    public void TipCountSurvivesSettingsStoreRoundTrip()
    {
        string root = OwnedTestDirectory.Create("mycapture-saved-location-settings-");
        try
        {
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            AppSettings settings = store.Load();
            Assert.Equal(0, settings.General.SavedLocationTipCount);
            settings.General.SavedLocationTipCount = 4;
            store.Save(settings);
            AppSettings loaded = store.Load();
            Assert.Equal(4, loaded.General.SavedLocationTipCount);
            Assert.True(FirstUseTips.ShowSavedLocation(loaded.General, () => true));
            store.Save(loaded);
            Assert.Equal(5, store.Load().General.SavedLocationTipCount);
            Assert.False(FirstUseTips.ShowSavedLocation(store.Load().General, () => true));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static AnnotationEditingResult Result(EditorCommitAction action, bool reduceExport = false)
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        return new AnnotationEditingResult(new FrozenFrame(image, region, null, 0), region, image,
            AnnotationDocument.CreateFor(image.PixelWidth, image.PixelHeight), action,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(), reduceExport: reduceExport);
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
        image.Freeze();
        return image;
    }

    private static ThemedShellPresenter Presenter() => new(() => false)
    {
        NotificationMonitor = () => new MonitorInfo("TEST", new RectD(-20000, -20000, 1080, 1920),
            new RectD(-20000, -20000, 1080, 1880), 96, false),
    };

    private static App ConnectHostReceipt(Fixture fixture, ThemedShellPresenter presenter,
        CaptureRecord record, CaptureEditSession session)
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(App).GetField("_settings", flags)!.SetValue(app, fixture.Settings);
        typeof(App).GetField("_shellPresenter", flags)!.SetValue(app, presenter);
        typeof(App).GetField("_commit", flags)!.SetValue(app, fixture.Commit);
        typeof(App).GetField("_currentRecord", flags)!.SetValue(app, record);
        typeof(App).GetField("_currentEditSession", flags)!.SetValue(app, session);
        typeof(App).GetMethod("SubscribeSavedLocationReceipt", flags)!.Invoke(app, [fixture.Commit]);
        return app;
    }

    private static Task<bool> InvokeHostCommit(App app, Fixture fixture, CaptureRecord record,
        CaptureEditSession session, AnnotationEditingResult result, bool libraryEdit)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (!libraryEdit)
            return (Task<bool>)typeof(App).GetMethod("HandleCommitAsync", flags)!.Invoke(app, [result])!;
        var gallery = (GalleryWindow)RuntimeHelpers.GetUninitializedObject(typeof(GalleryWindow));
        typeof(GalleryWindow).GetField("_commitService", flags)!.SetValue(gallery, fixture.Commit);
        return (Task<bool>)typeof(GalleryWindow).GetMethod("CommitReeditAsync", flags)!.Invoke(gallery, [record, result, session])!;
    }

    private static void RunOnDispatcher(Action action) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static T Wait<T>(Task<T> task)
    {
        Task<T> guarded = task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!guarded.IsCompleted)
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = guarded.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return guarded.GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = OwnedTestDirectory.Create("mycapture-saved-location-commit-");
        internal AppPaths Paths { get; }
        internal AppSettings Settings { get; } = new();
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService Commit { get; }
        internal int CopyCalls { get; private set; }

        internal Fixture(bool copySucceeds = true)
        {
            Paths = AppPaths.CreateForRoot(Root);
            Settings.Export.QuickSaveDirectoryOverride = Path.Combine(Root, "exports");
            var queue = Queue = new CaptureQueue(Paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(queue, Paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            Commit = new CaptureCommitService(Persistence, () => Settings, () => Paths, NullLogger<CaptureCommitService>.Instance,
                (_, _) => { CopyCalls++; return Task.FromResult(copySucceeds); });
            Commit.SaveAsPrompt = _ => Path.Combine(Root, "save-as.png");
        }

        internal CaptureRecord PersistOriginal() => Persistence.PersistOriginal(Image(), 1, string.Empty, string.Empty);
        public void Dispose() => OwnedTestDirectory.Delete(Root);
    }
}
