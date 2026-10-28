using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayCoordinatorTests
{
    [Theory]
    [InlineData("video", false, AfterCaptureAction.Editor)]
    [InlineData("video", false, AfterCaptureAction.ClipboardOnly)]
    [InlineData("video", true, AfterCaptureAction.Editor)]
    [InlineData("video", true, AfterCaptureAction.ClipboardOnly)]
    [InlineData("reedit", false, AfterCaptureAction.Editor)]
    [InlineData("reedit", false, AfterCaptureAction.ClipboardOnly)]
    [InlineData("reedit", true, AfterCaptureAction.Editor)]
    [InlineData("reedit", true, AfterCaptureAction.ClipboardOnly)]
    [InlineData("library", false, AfterCaptureAction.Editor)]
    [InlineData("library", false, AfterCaptureAction.ClipboardOnly)]
    [InlineData("library", true, AfterCaptureAction.Editor)]
    [InlineData("library", true, AfterCaptureAction.ClipboardOnly)]
    public void CaptureOverAnotherVisibleWindowUsesSeparateReceiptDelivery(
        string surface, bool advanced, AfterCaptureAction afterCapture) => RunSta(() =>
    {
        using var coordinator = Coordinator(_ => Frame());
        Window existing = OpenPreservedWindow(surface);
        int normalWrites = 0, separateWrites = 0, afterCaptureReads = 0, ended = 0, started = 0;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureOverlayWindow? selector = null;
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = surface == "library"
            ? _ => Task.FromResult<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>(new Dictionary<IntPtr, WindowSourceSnapshot>())
            : null;
        coordinator.ApplyCaptureExclusion = window => { selector = (CaptureOverlayWindow)window; return true; };
        coordinator.SelectionPersistRequested = _ => { normalWrites++; return Task.CompletedTask; };
        // App wires this path to durable separate persistence, its usual clipboard copy and a
        // forced quick-access receipt. It must win over both normal after-capture preferences.
        coordinator.EditorScreenCaptureRequested = _ => { separateWrites++; return saved.Task; };
        coordinator.OpenEditorAfterCapture = () => { afterCaptureReads++; return afterCapture == AfterCaptureAction.Editor; };
        coordinator.SessionStarted += () => started++;
        coordinator.OverlayClosed += (_, _) => ended++;
        if (surface == "library")
            coordinator.HasVisiblePreservedWindow = () => existing.IsVisible && existing.WindowState != WindowState.Minimized;
        else
            Assert.True(CaptureEditorRegistry.HasVisibleEditor);
        try
        {
            Assert.Null(coordinator.CurrentEditor);
            if (advanced)
                Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty,
                    recordForRepeat: false, preserveEditor: true));
            else
            {
                coordinator.Start(false, false, false, preserveEditor: true);
                PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
                Assert.True(Assert.IsType<CaptureOverlayView>(selector!.Content).CaptureFullScreen());
            }
            Assert.True(existing.IsVisible);
            Assert.Null(coordinator.CurrentEditor);
            Assert.True(coordinator.IsActive);
            Assert.False(coordinator.CanAcceptSelection);
            Assert.Equal(0, normalWrites);
            Assert.Equal(1, separateWrites);
            var nested = Assert.IsType<CaptureOverlayCoordinator>(typeof(CaptureOverlayCoordinator)
                .GetField("_editorScreenCapture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(coordinator));
            Assert.Same(coordinator.ReadPageUrls, nested.ReadPageUrls);
            Assert.Same(coordinator.ReadWindowSources, nested.ReadWindowSources);
            Assert.Equal(0, afterCaptureReads);
            Assert.Equal(0, ended);
            Assert.True(started > 0);

            // There is no coordinator-owned editor or visible selector during the write.
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty,
                recordForRepeat: false, preserveEditor: true));
            Assert.Equal(1, separateWrites);
            Assert.Equal(0, normalWrites);
            saved.SetResult();
            PumpUntil(() => !coordinator.IsActive);
            Assert.Equal(1, ended);
            Assert.True(coordinator.CanAcceptSelection);
            Assert.True(existing.IsVisible);
            Assert.Null(coordinator.CurrentEditor);
            Assert.Equal(0, afterCaptureReads);
            if (existing is VideoEditorWindow video)
            {
                Assert.True(video.HasUnsavedEditsForTest);
                Assert.Equal(1_000, video.TimelineForTest.InMs);
                Assert.Equal(9_000, video.TimelineForTest.OutMs);
            }
            if (existing is GalleryEditorWindow reedit)
                Assert.True(reedit.Editor.HasUnsavedChanges);
        }
        finally
        {
            saved.TrySetResult();
            PumpUntil(() => !coordinator.IsActive);
            if (existing is AnnotationEditorWindow image) image.ConfirmClose = () => EditorCloseChoice.DontSave;
            existing.Close();
        }
    });

    [Fact]
    public void HiddenAndMinimizedRegisteredEditorsDoNotForceReceiptButKeepGuardState() => RunSta(() =>
    {
        var editor = (GalleryEditorWindow)OpenPreservedWindow("reedit");
        try
        {
            Assert.True(CaptureEditorRegistry.HasVisibleEditor);
            editor.WindowState = WindowState.Minimized;
            Assert.False(CaptureEditorRegistry.HasVisibleEditor);
            Assert.True(CaptureEditorRegistry.WithEditors(new()).EditorOpen);
            editor.WindowState = WindowState.Normal;
            editor.Hide();
            Assert.False(CaptureEditorRegistry.HasVisibleEditor);
            Assert.True(CaptureEditorRegistry.WithEditors(new()).EditorOpen);
        }
        finally { editor.ConfirmClose = () => EditorCloseChoice.DontSave; editor.Close(); }
        Assert.False(CaptureEditorRegistry.HasVisibleEditor);
        Assert.False(CaptureEditorRegistry.WithEditors(new()).EditorOpen);
    });

    private static Window OpenPreservedWindow(string surface)
    {
        if (surface == "video")
        {
            VideoEditorWindow video = VideoEditorUxTrimTests.OpenReadyEditor(
                Path.Combine(AppContext.BaseDirectory, "capture-delivery-in-memory"), 10_000, show: true);
            Assert.True(video.TimelineForTest.SetTrimRange(1_000, 9_000));
            Assert.True(video.HasUnsavedEditsForTest);
            video.UnsavedEditsPromptForTest = () => throw new InvalidOperationException("Capture must keep the video editor.");
            return video;
        }
        Window window;
        if (surface == "reedit")
        {
            FrozenFrame frame = Frame();
            var record = new CaptureRecord { Width = 32, Height = 20 };
            var context = new GalleryReeditContext(record, frame, new RectD(0, 0, 32, 20), frame.Bitmap,
                AnnotationDocument.CreateFor(32, 20), new Dictionary<string, BitmapSource>());
            window = new GalleryEditorWindow(context);
        }
        else window = new Window { Width = 300, Height = 200 };
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10_000; window.Top = -10_000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show();
        PumpUntil(() => window.IsLoaded);
        if (window is GalleryEditorWindow image)
        {
            Assert.True(EditorKeysTests.Press(image, System.Windows.Input.Key.Q, ime: false));
            Assert.True(image.Editor.HasUnsavedChanges);
            image.ConfirmClose = () => throw new InvalidOperationException("Capture must keep the library re-edit window.");
        }
        return window;
    }

    [Fact]
    public void AdvancedCaptureOverEditorPreservesUnsavedWorkAndUsesSeparatePersistence() => RunSta(() =>
    {
        using var coordinator = Coordinator(_ => Frame());
        int originalWrites = 0, screenshotWrites = 0, closed = 0;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SelectionPersistRequested = _ => { originalWrites++; return Task.CompletedTask; };
        coordinator.EditorScreenCaptureRequested = _ => { screenshotWrites++; return saved.Task; };
        coordinator.OverlayClosed += (_, _) => closed++;
        Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 16, 12)));
        PumpUntil(() => coordinator.CurrentEditor?.IsLoaded == true);
        AnnotationEditorControl original = coordinator.CurrentEditor!;
        var editor = (AnnotationEditorWindow)Window.GetWindow(original);
        Assert.True(EditorKeysTests.Press(editor, System.Windows.Input.Key.Q, ime: false));
        Assert.True(original.HasUnsavedChanges);
        editor.ConfirmClose = () => throw new InvalidOperationException("A new capture must keep the editor.");
        try
        {
            Assert.True(coordinator.CanAcceptSelection);
            Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8), string.Empty,
                recordForRepeat: false, preserveEditor: true));
            Assert.False(coordinator.CanAcceptSelection);
            Assert.Same(original, coordinator.CurrentEditor);
            Assert.True(editor.IsVisible);
            Assert.Equal(1, originalWrites);
            Assert.Equal(1, screenshotWrites);
            saved.SetResult();
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.True(coordinator.CanAcceptSelection);
            Assert.Same(original, coordinator.CurrentEditor);
            Assert.True(original.HasUnsavedChanges);
            Assert.Equal(0, closed);
        }
        finally { saved.TrySetResult(); editor.ConfirmClose = () => EditorCloseChoice.DontSave; }
    });

    [Fact]
    public void TextCaptureOverEditorKeepsTextModeAndDoesNotPersistOrReplaceTheEditor() => RunSta(() =>
    {
        using var coordinator = Coordinator(_ => Frame());
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = null;
        CaptureOverlayWindow? selector = null;
        coordinator.ApplyCaptureExclusion = window => { if (window is CaptureOverlayWindow overlay) selector = overlay; return true; };
        int persisted = 0, recognised = 0;
        coordinator.EditorScreenCaptureRequested = _ => { persisted++; return Task.CompletedTask; };
        coordinator.TextCaptureRequested = selection => { Assert.True(selection.TextCapture); recognised++; };
        Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 16, 12)));
        PumpUntil(() => coordinator.CurrentEditor?.IsLoaded == true);
        AnnotationEditorControl original = coordinator.CurrentEditor!;
        var editor = (AnnotationEditorWindow)Window.GetWindow(original);
        coordinator.Start(false, false, false, textCapture: true, preserveEditor: true);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(selector!.TextCaptureMode);
        Assert.True(Assert.IsType<CaptureOverlayView>(selector.Content).CaptureFullScreen());
        PumpUntil(() => coordinator.CanRetake);
        Assert.Equal(1, recognised);
        Assert.Equal(0, persisted);
        Assert.Same(original, coordinator.CurrentEditor);
        Assert.True(editor.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureOverEditorPreservesTheEditorAndUsesSeparatePersistence(bool cancel) => RunSta(() =>
    {
        using var coordinator = Coordinator(_ => Frame());
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = null;
        CaptureOverlayWindow? selector = null;
        coordinator.ApplyCaptureExclusion = window => { if (window is CaptureOverlayWindow overlay) selector = overlay; return true; };
        int originalWrites = 0, screenshotWrites = 0, originalClosed = 0;
        coordinator.SelectionPersistRequested = _ => { originalWrites++; return Task.CompletedTask; };
        coordinator.EditorScreenCaptureRequested = _ => { screenshotWrites++; return Task.CompletedTask; };
        coordinator.OverlayClosed += (_, _) => originalClosed++;
        Assert.True(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 32, 20)));
        PumpUntil(() => coordinator.CurrentEditor?.IsLoaded == true);
        AnnotationEditorControl original = coordinator.CurrentEditor!;
        var window = (AnnotationEditorWindow)Window.GetWindow(original);
        EditorKeysTests.Invoke(original, "PlaceTextBox", new PointD(2, 2));
        EditorKeysTests.Field<System.Windows.Controls.TextBox>(original, "_activeTextBox").Text = "보존할 편집";
        EditorKeysTests.Invoke(original, "CommitActiveText");
        Assert.Single(original.CreateCurrentImageSnapshot().Items);
        window.ConfirmClose = () => throw new InvalidOperationException("Capture must not ask to discard the editor.");
        try
        {
            coordinator.Start(false, false, false, preserveEditor: true);
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
            Assert.NotNull(selector);
            Assert.Same(original, coordinator.CurrentEditor);
            Assert.True(window.IsVisible);
            Assert.Equal(CaptureBlock.Selecting, CaptureGuard.Find(coordinator.WithSession(new()), false));
            if (cancel) coordinator.Cancel();
            else Assert.True(Assert.IsType<CaptureOverlayView>(selector.Content).CaptureFullScreen());
            PumpUntil(() => coordinator.CanRetake);
            Assert.Same(original, coordinator.CurrentEditor);
            Assert.True(window.IsVisible);
            Assert.Equal(1, originalWrites);
            Assert.Equal(cancel ? 0 : 1, screenshotWrites);
            Assert.Equal(0, originalClosed);
            Assert.Single(original.CreateCurrentImageSnapshot().Items);
            Assert.True(original.HasUnsavedChanges);
        }
        finally { window.ConfirmClose = () => EditorCloseChoice.DontSave; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingColorCopyBlocksReleasedDragAndQueuedConfirmationWithoutRecording(bool releaseBeforeCopy) => RunSta(() =>
    {
        CaptureOverlayWindow? overlay = null;
        var pending = new TaskCompletionSource<bool>();
        int persisted = 0;
        int edited = 0;
        int editorRequests = 0;
        int confirmations = 0;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => WindowDetectOverlayTests.Frame(),
            () => new RectD(0, 0, 640, 360));
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.CopyColorText = _ => pending.Task;
        coordinator.SelectionPersistRequested = _ => { persisted++; return Task.CompletedTask; };
        coordinator.OpenEditorAfterCapture = () => { editorRequests++; return true; };
        coordinator.EditingCompleted += (_, _) => edited++;
        coordinator.ColorCopied = _ => confirmations++;

        coordinator.Start(false, false, false);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.NotNull(overlay);
        var view = Assert.IsType<CaptureOverlayView>(overlay.Content);
        PointD pointer = new(120, 180);
        view.ReadCursor = () => pointer;
        view.DetectWindows = false;
        view.InitializePointer();
        view.SimulatePointerDown();
        pointer = new PointD(300, 280);
        view.SimulatePointerMove();
        if (releaseBeforeCopy) view.SimulatePointerUp(); // Confirmation is queued, but has not run.
        Task<string?> copy = view.CopyColorAtPointerAsync();
        if (!releaseBeforeCopy) view.SimulatePointerUp();
        Assert.NotNull(view.Selection);
        view.RaiseEvent(WindowDetectOverlayTests.KeyArgs(System.Windows.Input.Key.Enter));
        Assert.False(view.CaptureFullScreen());
        WindowDetectOverlayTests.Pump();
        Assert.False(copy.IsCompleted);
        Assert.True(coordinator.IsActive);
        Assert.Equal(0, persisted);
        Assert.Equal(0, editorRequests);

        pending.SetResult(true);
        PumpUntil(() => copy.IsCompleted);
        Assert.True(copy.IsCompletedSuccessfully);
        Assert.False(coordinator.IsActive);
        Assert.False(overlay.IsVisible);
        Assert.Equal(1, confirmations);
        Assert.Equal(0, persisted);
        Assert.Equal(0, editorRequests);
        Assert.Equal(0, edited);
        Assert.Null(coordinator.CurrentEditor);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ColorCopyEndsOnlyOnSuccessAndNeverRecordsACapture(bool succeeds) => RunSta(() =>
    {
        CaptureOverlayWindow? overlay = null;
        int closed = 0;
        int persisted = 0;
        int edited = 0;
        ColorCopyFeedback? feedback = null;
        using var coordinator = Coordinator(_ => Frame());
        coordinator.ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.CopyColorText = _ => Task.FromResult(succeeds);
        coordinator.SelectionPersistRequested = _ => { persisted++; return Task.CompletedTask; };
        coordinator.EditingCompleted += (_, _) => edited++;
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.ColorCopied = result =>
        {
            Assert.False(coordinator.IsActive);
            Assert.False(overlay!.IsVisible);
            Assert.Equal(1, closed);
            feedback = result;
        };

        coordinator.Start(false, false, true);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.NotNull(overlay);
        var view = Assert.IsType<CaptureOverlayView>(overlay.Content);
        view.ReadCursor = () => new PointD(5, 5);
        view.InitializePointer();
        Task<string?> copy = view.CopyColorAtPointerAsync();
        PumpUntil(() => copy.IsCompleted);
        Assert.True(copy.IsCompletedSuccessfully);
        Assert.Equal(succeeds ? "#7A7A7A" : null, copy.Result);
        Assert.Equal(!succeeds, coordinator.IsActive);
        Assert.Equal(!succeeds, overlay.IsVisible);
        Assert.Equal(succeeds ? 1 : 0, closed);
        Assert.Equal(0, persisted);
        Assert.Equal(0, edited);
        Assert.Null(coordinator.CurrentEditor);
        if (succeeds)
        {
            Assert.NotNull(feedback);
            Assert.Equal(Color.FromRgb(0x7A, 0x7A, 0x7A), feedback.ChipColor);
        }
        else
        {
            Assert.Null(feedback);
            Assert.Equal(MyCapture.Core.Localization.UiText.Get("ColorPick_CopyFailed"), view.ToastMessageForTest);
        }
    });

    [Theory]
    [InlineData(0L, long.MinValue, false)]
    [InlineData(12345678L, long.MinValue, false)]
    [InlineData(long.MaxValue, long.MinValue, false)]
    [InlineData(10000L, 10000L, true)]
    [InlineData(10000L, 9251L, true)]
    [InlineData(10000L, 9250L, false)]
    [InlineData(10000L, 9249L, false)]
    [InlineData(10000L, 10001L, false)]
    public void PresentationSettle_OnlyWaitsForAnActualRecentDismissal(
        long currentTick, long lastDismissedTick, bool expected)
    {
        Assert.Equal(expected, CaptureOverlayCoordinator.NeedsPresentationSettle(currentTick, lastDismissedTick));
    }

    [Fact]
    public void PreparingFrame_LeavesDispatcherResponsiveAndReservesOneSessionUntilCancelledCaptureDrains() => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int ownerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        int closed = 0;
        int windows = 0;
        System.Windows.Window? pendingOverlay = null;
        using var coordinator = Coordinator(includeCursor =>
        {
            Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
            Assert.True(includeCursor);
            Interlocked.Increment(ref calls);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.OverlayClosed += (_, _) => { Assert.Equal(ownerThread, Environment.CurrentManagedThreadId); closed++; };
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window => { windows++; pendingOverlay = window; return true; };
        try
        {
            // Native WM_HOTKEY does not guarantee a WPF synchronization context.
            SynchronizationContext.SetSynchronizationContext(null);
            coordinator.Start(true, false, false);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(coordinator.IsActive);
            Assert.Equal(1, windows);
            Assert.NotNull(pendingOverlay);
            Assert.False(pendingOverlay.IsVisible); // No black placeholder while acquisition is pending.
            bool dispatched = false;
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => dispatched = true));
            PumpUntil(() => dispatched);
            Assert.False(coordinator.LastPreparationForTest.IsCompleted);
            coordinator.Start(false, true, true);
            Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
            coordinator.Cancel();
            Assert.True(coordinator.IsActive); // Native work still owns the only acquisition slot.
            coordinator.Start(false, true, true);
            Assert.Equal(1, calls);
            Assert.Equal(0, closed);
        }
        finally
        {
            release.Set();
            PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        }
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, windows);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void AcquisitionFailure_IsReportedOnOwnerDispatcherAndNextHotkeyCanShowFreshFrame() => RunSta(() =>
    {
        int ownerThread = Environment.CurrentManagedThreadId;
        int calls = 0;
        int failures = 0;
        int closed = 0;
        System.Windows.Window? shown = null;
        using var coordinator = Coordinator(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("synthetic acquisition failure");
            return Frame();
        });
        coordinator.TransitionFailed += error =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.IsType<IOException>(error);
            failures++;
        };
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = window =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            Assert.False(window.IsVisible);
            shown = window;
            return true;
        };
        SynchronizationContext.SetSynchronizationContext(null);
        coordinator.Start(false, false, false);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(1, failures);
        Assert.Equal(1, closed);
        Assert.False(coordinator.IsActive);
        coordinator.Start(false, false, false);
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(2, calls);
        Assert.True(coordinator.IsActive);
        Assert.NotNull(shown);
        Assert.True(shown.IsVisible);
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
        Assert.Equal(2, closed);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeOrDispatcherShutdown_DiscardsLateFrameWithoutWindowOrFailure(bool shutdown) => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int windows = 0;
        int failures = 0;
        using var coordinator = Coordinator(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.RequiresCaptureExclusion = () => true;
        coordinator.ApplyCaptureExclusion = _ => { windows++; return true; };
        coordinator.TransitionFailed += _ => failures++;
        coordinator.Start(false, false, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        if (shutdown) Dispatcher.CurrentDispatcher.InvokeShutdown();
        else coordinator.Dispose();
        Assert.False(coordinator.IsActive);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Start(false, false, false));
        release.Set();
        if (shutdown) Assert.True(coordinator.LastPreparationForTest.Wait(TimeSpan.FromSeconds(5)));
        else PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.Equal(1, windows);
        Assert.Equal(0, failures);
    });

    [Fact]
    public void PreparationRechecksRecordingExclusionBeforeShowingOverlay() => RunSta(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool requiresExclusion = false;
        System.Windows.Window? rejected = null;
        int failures = 0;
        int closed = 0;
        using var coordinator = Coordinator(_ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Frame();
        });
        coordinator.RequiresCaptureExclusion = () => requiresExclusion;
        coordinator.ApplyCaptureExclusion = window => { rejected = window; Assert.False(window.IsVisible); return false; };
        coordinator.TransitionFailed += _ => failures++;
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.Start(false, false, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        requiresExclusion = true;
        release.Set();
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.NotNull(rejected);
        Assert.False(rejected.IsVisible);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, failures);
        Assert.Equal(1, closed);
    });

    [Fact]
    public void ReadsStartWithTheGrabAndNeitherTheSelectorNorTheSaveWaitsForThem() => RunSta(() =>
    {
        const string url = "https://example.com/confirmed";
        var first = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int reads = 0;
        IReadOnlyList<WindowCandidate> asked = [];
        CaptureOverlayWindow? overlay = null;
        CaptureSelectionCompletedEventArgs? persisted = null;
        using var coordinator = Coordinator(_ => { events.Enqueue("pixels"); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { events.Enqueue("selector built"); overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.OpenEditorAfterCapture = () => false;
        coordinator.SelectionPersistRequested = selection => { persisted = selection; return Task.CompletedTask; };
        coordinator.ReadPageUrls = candidates =>
        {
            events.Enqueue("read");
            if (Interlocked.Increment(ref reads) > 1) return confirming.Task;
            asked = candidates;
            return first.Task;
        };

        coordinator.Start(false, false, false);
        // The first read is asked at the hotkey, before the grab and before the selector window is built.
        Assert.Equal("read", events.First());
        Assert.True(events.ToList().IndexOf("read") < events.ToList().IndexOf("selector built"));
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.Equal(1, reads); // Shown while the first read is still walking.
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.True(overlay!.SourceDetailsPendingForTest);
        Assert.False(coordinator.LastSourceDetailsForTest.IsCompleted);

        overlay.CompleteSelection(new RectD(4, 4, 16, 10));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted && persisted is not null);
        Assert.Equal(new RectD(4, 4, 16, 10), persisted!.BitmapRegion);
        Assert.Empty(persisted.SourcePageUrl);
        Task<CaptureSelectionSource> late = Assert.IsAssignableFrom<Task<CaptureSelectionSource>>(persisted.PendingSource);
        Assert.False(overlay.IsVisible);
        Assert.False(coordinator.IsActive); // The session ends with the selector, not with the reads.

        // Every exposed window reports one stable page: the confirming read runs after the first one.
        first.SetResult(asked.ToDictionary(candidate => candidate.Handle,
            candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", url)));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 2, TimeSpan.FromSeconds(5)));
        Assert.False(late.IsCompleted);
        confirming.SetResult(first.Task.Result);
        Assert.True(late.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(coordinator.LastSourceDetailsForTest.IsCompletedSuccessfully);
        Assert.Contains(coordinator.LastSourceDetailsForTest.Result, window => window.SourcePageUrl == url);
        // The late source is the selection's window resolved from that confirmed list.
        Assert.Equal(CaptureOverlayWindow.ResolveSelectionSource(coordinator.LastSourceDetailsForTest.Result,
            new RectD(4, 4, 16, 10), null, wholeMonitor: false), late.Result);
    });

    [Fact]
    public void HotkeysOverAShownSelectorActAsOpenWhileThePageReadIsConfirming() => RunSta(() =>
    {
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, acquisitions = 0;
        CaptureOverlayWindow? overlay = null;
        using var coordinator = Coordinator(_ => { Interlocked.Increment(ref acquisitions); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.TextCaptureRequested = _ => { };
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.ReadPageUrls = candidates => Interlocked.Increment(ref reads) > 1
            ? confirming.Task
            : Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(candidates.ToDictionary(
                candidate => candidate.Handle,
                candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", "https://example.com/")));

        coordinator.Start(false, false, false);
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.False(overlay!.TextCaptureMode);
        // The text capture command over the shown selector switches it, exactly as once it is ready.
        coordinator.Start(false, false, false, textCapture: true);
        Assert.True(overlay.TextCaptureMode);
        Assert.False(coordinator.StartWithSelection(Frame(), new RectD(0, 0, 8, 8)));
        Assert.Equal(1, acquisitions);
        Assert.Equal(2, reads);

        confirming.SetResult(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        PumpUntil(() => coordinator.LastPreparationForTest.IsCompleted);
        Assert.True(coordinator.LastPreparationForTest.IsCompletedSuccessfully);
        Assert.True(overlay.IsVisible);
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
    });

    [Fact]
    public void SelectorClosedWhileThePageReadIsConfirmingEndsTheSessionAndTheNextCaptureStarts() => RunSta(() =>
    {
        var confirming = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int closed = 0;
        int acquisitions = 0;
        CaptureOverlayWindow? overlay = null;
        using var coordinator = Coordinator(_ => { Interlocked.Increment(ref acquisitions); return Frame(); });
        coordinator.ReadWindowSources = null;
        coordinator.ApplyCaptureExclusion = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.OverlayClosed += (_, _) => closed++;
        coordinator.ReadPageUrls = candidates => Interlocked.Increment(ref reads) == 2
            ? confirming.Task
            : Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(candidates.ToDictionary(
                candidate => candidate.Handle,
                candidate => new BrowserDocumentSnapshot(candidate.ProcessId, candidate.Title, "document", "https://example.com/")));

        coordinator.Start(false, false, false);
        PumpUntil(() => overlay?.IsVisible == true);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 2, TimeSpan.FromSeconds(5)));
        overlay!.Close();
        Assert.Equal(1, closed);
        Assert.False(coordinator.IsActive);

        // The reader itself serializes native probes (waiting within its deadline); the capture does not.
        CaptureOverlayWindow first = overlay;
        coordinator.Start(false, false, false);
        PumpUntil(() => overlay is { IsVisible: true } next && !ReferenceEquals(next, first));
        Assert.Equal(2, acquisitions);
        Assert.True(Volatile.Read(ref reads) >= 3);
        confirming.SetResult(new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        coordinator.Cancel();
        Assert.False(coordinator.IsActive);
        Assert.Equal(2, closed);
    });

    [Fact]
    public void EditorOpensWholeOnThePortraitSecondaryTheSelectionWasTakenOn() => RunSta(() =>
    {
        // The reported layout, moved far off the real desktop so the window never shows on screen:
        // an ultrawide primary with a 768 x 1366 portrait display on its right.
        const double x = -30000, y = -20000;
        using var dpiProbe = new HwndSource(new HwndSourceParameters("Capture editor DPI probe")
        {
            PositionX = (int)(x + 3440), PositionY = (int)y, Width = 1, Height = 1,
            WindowStyle = unchecked((int)0x80000000), // hidden WS_POPUP on the same offscreen host
        });
        uint dpi = GetDpiForWindow(dpiProbe.Handle);
        Assert.True(dpi > 0);
        var primary = new MonitorInfo(@"\\.\DISPLAY1", new RectD(x, y, 3440, 1440), new RectD(x, y, 3440, 1392), dpi, true);
        var portrait = new MonitorInfo(@"\\.\DISPLAY2", new RectD(x + 3440, y, 768, 1366), new RectD(x + 3440, y, 768, 1318), dpi, false);
        using var coordinator = Coordinator(_ => Frame());
        coordinator.ReadMonitors = () => [primary, portrait];
        var frame = new FrozenFrame(Solid(32, 20), new RectD(x + 3500, y + 100, 32, 20), null, 0);

        Assert.True(coordinator.StartWithSelection(frame, new RectD(0, 0, 32, 20)));
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted && coordinator.CurrentEditor?.IsLoaded == true);
        System.Windows.Window editor = System.Windows.Window.GetWindow(coordinator.CurrentEditor!)!;
        Assert.True(GetWindowRect(new System.Windows.Interop.WindowInteropHelper(editor).Handle, out NativeRect rect));
        try
        {
            Assert.Equal(dpi, GetDpiForWindow(new WindowInteropHelper(editor).Handle));
            MonitorWindowPlacement.WindowFit expected = MonitorWindowPlacement.Fit(portrait.WorkArea, dpi / 96.0,
                new Size(980, 620), AnnotationEditorWindow.MinimumSize, AnnotationEditorWindow.WorkAreaMargin);
            var actual = new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            Assert.Equal(expected.Bounds, actual);
            Assert.True(actual.Left >= portrait.WorkArea.Left && actual.Top >= portrait.WorkArea.Top);
            Assert.True(actual.Right <= portrait.WorkArea.Right && actual.Bottom <= portrait.WorkArea.Bottom);
        }
        finally
        {
            coordinator.Cancel();
        }
    });

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private static CaptureOverlayCoordinator Coordinator(Func<bool, FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
        NullLogger<CaptureOverlayCoordinator>.Instance,
        acquire,
        static () => new RectD(0, 0, 32, 20),
        static bounds => [new WindowCandidate(new IntPtr(1), bounds, "Synthetic browser", ProcessId: 42)]);

    private static FrozenFrame Frame() => new(Solid(32, 20), new RectD(0, 0, 32, 20), null, 0);

    [Fact]
    public void PendingPersistence_RejectsSecondCaptureAndCanBeCancelled() => RunSta(() =>
    {
        var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance);
        var releasePersistence = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int persistenceCalls = 0;
        int closedEvents = 0;
        CaptureSelectionCompletedEventArgs? persistedSelection = null;
        coordinator.SelectionPersistRequested = selection =>
        {
            persistenceCalls++;
            persistedSelection = selection;
            return releasePersistence.Task;
        };
        coordinator.OverlayClosed += (_, _) => closedEvents++;

        BitmapSource bitmap = Solid(32, 20);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);

        Assert.True(coordinator.StartWithSelection(frame, new RectD(2, 3, 18, 11)));
        Assert.True(coordinator.IsActive);
        Assert.Equal(1, persistenceCalls);
        Assert.NotNull(persistedSelection);
        Assert.False(persistedSelection!.RecordForRepeat);

        // No overlay/editor window exists during the awaited durable write. This second call is
        // the exact race that previously started another session and overwrote App._currentRecord.
        Assert.False(coordinator.StartWithSelection(frame, new RectD(1, 1, 8, 8)));
        Assert.Equal(1, persistenceCalls);

        coordinator.Cancel();
        releasePersistence.SetResult(null);
        PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);

        Assert.True(coordinator.LastTransitionForTest.IsCompletedSuccessfully);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, closedEvents);
    });

    [Fact]
    public void ManualRegionSelection_RecordsManualRepeatMetadata()
    {
        BitmapSource bitmap = Solid(30, 18);
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 30, 18), null, 0);
        var selection = new CaptureSelectionCompletedEventArgs(
            frame,
            new RectD(3, 2, 15, 9),
            bitmap);

        Assert.True(selection.RecordForRepeat);
        Assert.Equal(new RectD(3, 2, 15, 9), selection.BitmapRegion);
    }

    private static BitmapSource Solid(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = Enumerable.Repeat((byte)0x7A, width * height * 4).ToArray();
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The capture transition did not complete.");
            }

            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
