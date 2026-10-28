using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Real still-capture handlers and persistence, with synthetic pixels and a clipboard override.</summary>
public sealed class PreservedCaptureDeliveryTests : KoreanCaptionTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSaveStillCompletesTheIndependentCopyAndKeepsItsFailureCard(bool beforeCardTurn) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        var failure = new IOException("synthetic library write failure");
        if (beforeCardTurn) shell.Set("_persistence", null);
        else shell.Persistence.BeforeRecordMetadataCommit = _ => throw failure;
        Task delivery = shell.SelectSeparate(shell.Selection(new RectD(10, 20, 64, 48)));
        Assert.Equal(1, shell.CopyCalls);
        Assert.False(delivery.IsCompleted);
        shell.PumpUntil(() => shell.Card?.HasCaptureFailed == true);
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.True(card.IsOpen);
        Assert.False(delivery.IsCompleted);
        if (beforeCardTurn) Assert.Empty(shell.Queue.Records);
        else AssertRecoverableOriginal(shell);
        copy.SetResult(true);
        shell.Wait(delivery);
        shell.Pump();
        Assert.Same(card, shell.Card);
        Assert.True(card.HasCaptureFailed);
        Assert.All(card.ActionButtonsForTest, button => Assert.False(button.IsEnabled));
        Assert.Equal(1, shell.CopyCalls);
        Assert.Equal(1, shell.CardsShown);
        Assert.Null(shell.Get("_latestReceipt"));
        Assert.Null(shell.Get("_currentRecord"));
        Assert.Null(shell.Get("_currentEditSession"));
        Assert.Contains("라이브러리", card.DetailForTest.Text);
        if (beforeCardTurn) Assert.Single(shell.Notices);
        else Assert.Empty(shell.Notices);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMetadataPublicationKeepsTheSameRecoverableOriginalInBothDeliveryPaths(bool preserveEditor) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic metadata publication failure");
        CaptureSelectionCompletedEventArgs selection = shell.Selection(new RectD(10, 20, 64, 48));
        shell.Wait(preserveEditor ? shell.SelectSeparate(selection) : shell.SelectNormal(selection));
        CaptureRecord pending = AssertRecoverableOriginal(shell);
        Assert.Null(shell.Get("_currentRecord"));
        Assert.Null(shell.Get("_currentEditSession"));
        Assert.Null(shell.Get("_latestReceipt"));
        Assert.Equal(1, shell.CopyCalls);
        if (!preserveEditor) Assert.Same(pending, shell.Get("_pendingRecord"));
        byte[] original = File.ReadAllBytes(shell.Queue.GetFilePath(pending, CaptureFileNames.Original));

        // The normal capture path retains this identity for recovery; a separate capture
        // must keep the same recovery contract instead of rolling the original back.
        shell.Persistence.BeforeRecordMetadataCommit = null;
        Assert.Same(pending, shell.Wait(shell.Persistence.PersistPendingOriginalAsync(pending, selection.SelectedBitmap)));
        Assert.Same(pending, Assert.Single(shell.Queue.Records));
        Assert.False(shell.Persistence.IsBusy(pending.Id));
        Assert.False(File.Exists(shell.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending)));
        Assert.Equal(original, File.ReadAllBytes(shell.Queue.GetFilePath(pending, CaptureFileNames.Original)));
    });

    private static CaptureRecord AssertRecoverableOriginal(Shell shell)
    {
        CaptureRecord pending = Assert.Single(shell.Queue.Records);
        Assert.True(shell.Persistence.IsWriteInProgress(pending.Id));
        Assert.NotEmpty(File.ReadAllBytes(shell.Queue.GetFilePath(pending, CaptureFileNames.Original)));
        string journalPath = shell.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending);
        CaptureRecord journal = System.Text.Json.JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(journalPath))!;
        Assert.Equal(pending.Id, journal.Id);
        return pending;
    }

    [Fact]
    public void FailureNotificationExceptionDoesNotAbandonTheCopy() => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        shell.Set("_persistence", null);
        shell.App.CaptureNoticeForTest = (_, _) => throw new InvalidOperationException("synthetic notification failure");
        Task delivery = shell.SelectSeparate(shell.Selection(new RectD(10, 20, 64, 48)));
        shell.Pump();
        Assert.False(delivery.IsCompleted);
        Assert.True(shell.Card?.HasCaptureFailed);
        copy.SetResult(true);
        shell.Wait(delivery);
        Assert.Equal(1, shell.CopyCalls);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedCaptureHasOneCardAndKeepsTheExistingRecordAndSession(bool copySucceeds) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        shell.Wait(shell.SelectNormal(shell.Selection(new RectD(0, 0, 64, 48))));
        CaptureRecord original = Assert.IsType<CaptureRecord>(shell.Get("_currentRecord"));
        object session = Assert.IsAssignableFrom<IDisposable>(shell.Get("_currentEditSession"));
        object pending = shell.Get("_pendingRecord")!;
        byte[] originalBytes = File.ReadAllBytes(shell.Queue.GetFilePath(original, CaptureFileNames.Original));
        shell.CopyResult = Task.FromResult(copySucceeds);
        shell.Wait(shell.SelectSeparate(shell.Selection(new RectD(30, 40, 64, 48))));
        shell.Pump();

        CaptureRecord saved = Assert.Single(shell.Queue.Records, record => record.Id != original.Id);
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.IsPending);
        Assert.True(card.IsOpen);
        Assert.Equal(saved.Id, shell.Get("_latestReceipt"));
        Assert.Equal(1, shell.CardsShown);
        Assert.Equal(2, shell.CopyCalls);
        Assert.Same(original, shell.Get("_currentRecord"));
        Assert.Same(session, shell.Get("_currentEditSession"));
        Assert.Same(pending, shell.Get("_pendingRecord"));
        Assert.Equal(originalBytes, File.ReadAllBytes(shell.Queue.GetFilePath(original, CaptureFileNames.Original)));
        Assert.True(File.Exists(shell.Queue.GetFilePath(saved, CaptureFileNames.Original)));
        Assert.Equal(copySucceeds ? UiText.Get("QuickAccess.Copied") : QuickAccessWindow.FailureText("Copy"), card.StatusForTest.Text);
        Assert.Empty(shell.Notices);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityPressureKeepsTheNewCaptureUntilItsDeferredReceiptCloses(bool bytes) => StaTestHost.Run(() =>
    {
        using var shell = new Shell(bytes ? new QueueSettings { MaxBytes = 1 } : new QueueSettings { MaxItems = 1 });
        CaptureRecord older = shell.Wait(shell.Persistence.PersistOriginalAsync(shell.Frame.Bitmap, 1, "pinned", string.Empty));
        older.IsPinned = true;
        shell.App.ShowsCardAfterSaveForTest = true;
        var copy = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.CopyResult = copy.Task;
        Task delivery = shell.SelectSeparate(shell.Selection(new RectD(10, 20, 64, 48)));
        shell.PumpUntil(() => shell.Queue.Records.Any(record => record.Id != older.Id));
        CaptureRecord saved = Assert.Single(shell.Queue.Records, record => record.Id != older.Id);
        // Drain the save continuation while the copy still waits and no card owns a lease.
        shell.Pump();
        Assert.False(delivery.IsCompleted);
        Assert.Null(shell.Card);
        Assert.Same(saved, shell.Queue.Find(saved.Id));
        // The queue protects its newest item even without a lease. Insert a successor so
        // retaining this now-older capture proves the asynchronous handoff owns a lease.
        CaptureRecord successor = shell.Wait(shell.Persistence.PersistOriginalAsync(shell.Frame.Bitmap, 1, "successor", string.Empty));
        Assert.Same(saved, shell.Queue.Find(saved.Id));
        copy.SetResult(true);
        shell.Wait(delivery);
        shell.Pump();
        QuickAccessWindow card = Assert.IsType<QuickAccessWindow>(shell.Card);
        Assert.False(card.IsPending);
        Assert.Same(saved, shell.Queue.Find(saved.Id));
        Assert.Equal(1, shell.CardsShown);
        card.CloseNow();
        // The card and transient handoff leases both release; pinned old content is retained.
        Assert.Null(shell.Queue.Find(saved.Id));
        Assert.Same(older, shell.Queue.Find(older.Id));
        Assert.Same(successor, shell.Queue.Find(successor.Id));
    });

    [Theory]
    [InlineData("library")]
    [InlineData("editor")]
    public void ManualSelectionOverPreservedWindowReplacesRepeatRegionButSyntheticSelectionDoesNot(string surface) => StaTestHost.Run(() =>
    {
        using var shell = new Shell();
        var a = new RectD(10, 20, 64, 48);
        var b = new RectD(70, 80, 96, 64);
        shell.Wait(shell.SelectNormal(shell.Selection(a)));
        CaptureRecord original = Assert.IsType<CaptureRecord>(shell.Get("_currentRecord"));
        object session = shell.Get("_currentEditSession")!;
        Window preserved = surface == "library" ? new Window { Width = 300, Height = 200 }
            : new GalleryEditorWindow(new GalleryReeditContext(original, shell.Frame,
                new RectD(0, 0, 320, 200), shell.Frame.Bitmap, AnnotationDocument.CreateFor(320, 200),
                new Dictionary<string, BitmapSource>()));
        preserved.WindowStartupLocation = WindowStartupLocation.Manual;
        preserved.Left = -32000; preserved.Top = -32000;
        preserved.ShowInTaskbar = false; preserved.ShowActivated = false;
        preserved.Show();
        using var coordinator = new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            SelectionPersistRequested = shell.SelectNormal,
            EditorScreenCaptureRequested = shell.SelectSeparate,
            OpenEditorAfterCapture = () => throw new InvalidOperationException("Separate capture must not replace the editor"),
        };
        if (surface == "library") coordinator.HasVisiblePreservedWindow = () => preserved.IsVisible;
        shell.Set("_overlay", coordinator);
        try
        {
            Assert.True(coordinator.StartWithSelection(shell.Frame, b, "region B", recordForRepeat: true, preserveEditor: true));
            shell.Wait(coordinator.LastTransitionForTest);
            shell.Pump();
            Assert.True(preserved.IsVisible);
            Assert.Same(original, shell.Get("_currentRecord"));
            Assert.Same(session, shell.Get("_currentEditSession"));
            var expected = new RectD(shell.Frame.ScreenBounds.Left + b.Left, shell.Frame.ScreenBounds.Top + b.Top, b.Width, b.Height);
            RegionHistoryEntry entry = Assert.IsType<RegionHistoryEntry>(shell.History.LastEntry);
            Assert.Equal(expected, entry.ScreenRegion);
            Assert.Equal(shell.Frame.Monitor!.DeviceName, entry.MonitorDeviceName);
            Assert.Equal(shell.Frame.Monitor.Bounds, entry.MonitorBounds);
            Assert.Equal(shell.Frame.Monitor.Dpi, entry.MonitorDpi);
            Assert.Equal(2, shell.History.Count);
            shell.Wait(shell.SelectSeparate(shell.Selection(new RectD(0, 0, 320, 200), manual: false)));
            Assert.Same(entry, shell.History.LastEntry);
            var environment = new RepeatEnvironment(shell.Frame);
            var advanced = new AdvancedCaptureService(environment, shell.History, new NoScroll(), NullLogger.Instance);
            Assert.True(advanced.RepeatLastRegion().IsCompleted);
            Assert.Equal(expected, environment.Captured);
            Assert.Equal(2, shell.History.Count);
        }
        finally
        {
            if (preserved is AnnotationEditorWindow editor) editor.ConfirmClose = () => EditorCloseChoice.DontSave;
            preserved.Close();
            shell.Set("_overlay", null);
        }
    });

    private sealed class RepeatEnvironment(FrozenFrame source) : IAdvancedCaptureEnvironment
    {
        internal RectD Captured { get; private set; }
        public FrozenFrame CaptureScreenRegion(RectD region)
        {
            Captured = region;
            return new FrozenFrame(source.Bitmap, region, source.Monitor, 0);
        }
        public FrozenFrame CaptureMonitorUnderCursor() => throw new NotSupportedException();
        public BitmapSource CaptureRegion(RectD region) => throw new NotSupportedException();
        public PointD CursorPosition => default;
        public WindowUnderCursor? WindowAt(PointD point) => null;
        public RectD? ResolveRepeatRegion(RegionHistoryEntry entry) => entry.ScreenRegion;
        public bool CanOpenEditor => true;
        public bool OpenEditor(AdvancedSelection selection) => true;
    }

    private sealed class NoScroll : IScrollInputSink
    {
        public bool ScrollDown(IntPtr window, PointD point, int notches) => throw new NotSupportedException();
    }

    private sealed class NoHotkeys : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }

    private sealed class Shell : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-preserved-delivery-").FullName;
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        private readonly ReceiptKeyboardRouter _router = new(_ => new NoHotkeys());
        private readonly bool? _previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly Action<string>? _previousTimeline = ReceiptTimeline.Mark;
        internal App App { get; } = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        internal AppSettings Settings { get; } = new();
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal FrozenFrame Frame { get; }
        internal LastRegionStore History { get; } = new(() => 10);
        internal Task<bool> CopyResult { get; set; } = Task.FromResult(true);
        internal int CopyCalls { get; private set; }
        internal int CardsShown { get; private set; }
        internal List<(string Title, string Message)> Notices { get; } = [];
        internal QuickAccessWindow? Card => (QuickAccessWindow?)Get("_quickAccess");

        internal Shell(QueueSettings? queue = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            typeof(DispatcherObject).GetField("_dispatcher", Flags)!.SetValue(App, Dispatcher.CurrentDispatcher);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ReceiptTimeline.Mark = mark => { if (mark == "card-shown") CardsShown++; };
            Settings.General.AfterCapture = AfterCaptureAction.Editor;
            Settings.General.PlayCaptureSound = false;
            Settings.General.FirstCaptureTipShown = true;
            if (queue is not null) Settings.Queue = queue;
            AppPaths paths = AppPaths.CreateForRoot(_root);
            Queue = new CaptureQueue(paths, Settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, paths, () => Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(Persistence, () => Settings, () => paths, NullLogger<CaptureCommitService>.Instance,
                (_, _) => { CopyCalls++; return CopyResult; });
            Set("_queue", Queue); Set("_settings", Settings); Set("_persistence", Persistence); Set("_commit", commit);
            Set("_capturePaths", paths); Set("_services", _services); Set("_receiptKeyboard", _router); Set("_lastRegions", History);
            Set("_sourceBackfillTask", Task.CompletedTask);
            App.CaptureNoticeForTest = (title, message) => Notices.Add((title, message));
            byte[] pixels = Enumerable.Range(0, 320 * 200 * 4).Select(i => (byte)(i * 7 % 251)).ToArray();
            BitmapSource bitmap = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
            bitmap.Freeze();
            var bounds = new RectD(-32000, -32000, 1600, 1000);
            var monitor = new MonitorInfo("TEST-PRESERVED", bounds, bounds, MonitorEnumerator.GetPrimary().Dpi, false);
            Frame = new FrozenFrame(bitmap, new RectD(-31800, -31800, 320, 200), monitor, 0);
        }

        internal CaptureSelectionCompletedEventArgs Selection(RectD region, bool manual = true) =>
            new(Frame, region, ScreenCaptureEngine.Crop(Frame, region), "synthetic source", recordForRepeat: manual);
        internal Task SelectNormal(CaptureSelectionCompletedEventArgs selection) => Invoke("OnCaptureSelectionCompletedAsync", selection);
        internal Task SelectSeparate(CaptureSelectionCompletedEventArgs selection) => Invoke("SaveEditorScreenCaptureAsync", selection);
        private Task Invoke(string method, CaptureSelectionCompletedEventArgs selection) =>
            (Task)typeof(App).GetMethod(method, Flags)!.Invoke(App, [selection])!;
        internal object? Get(string name) => typeof(App).GetField(name, Flags)!.GetValue(App);
        internal void Set(string name, object? value) => typeof(App).GetField(name, Flags)!.SetValue(App, value);
        internal void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
        internal void PumpUntil(Func<bool> done)
        {
            var timeout = Stopwatch.StartNew();
            while (!done() && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); if (!done()) Thread.Sleep(1); }
            Assert.True(done(), "The preserved capture did not settle.");
        }
        internal void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        internal T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
        public void Dispose()
        {
            Card?.CloseNow();
            (Get("_currentEditSession") as IDisposable)?.Dispose();
            _router.Dispose();
            _services.Dispose();
            FluidMotion.AnimationsEnabledOverrideForTest = _previousMotion;
            ReceiptTimeline.Mark = _previousTimeline;
            SynchronizationContext.SetSynchronizationContext(_previousContext);
            if (Directory.Exists(_root)) TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }
}
