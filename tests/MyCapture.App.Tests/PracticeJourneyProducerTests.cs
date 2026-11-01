using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeJourneyProducerTests
{
    [Fact]
    public void FindPictureCompletesOnlyWhenThePracticeRecordOpensInTheLibraryEditor() => StaTestHost.Run(() =>
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var app = (App)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField("_settings", flags)!.SetValue(app, new AppSettings());
        var guide = (WelcomeWindow)typeof(App).GetMethod("CreatePracticeWindow", flags)!.Invoke(app, [true, false, false])!;
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.C2);
            using var host = new InspectorContractHost();
            PracticeSignals.PreviewOpened(PracticeJourneyDriver.Record);
            PracticeSignals.Ready(PracticeJourneyDriver.Record, host.Editor);
            PracticeSignals.EditorOpenedFromLibrary(Guid.NewGuid());
            Assert.Equal(PracticeStep.C2, guide.Session.Step);
            Assert.Equal(PracticeStepResult.Pending, guide.Session.Results[PracticeStep.C2]);
            PracticeSignals.EditorOpenedFromLibrary(PracticeJourneyDriver.Record);
            Assert.Equal(PracticeStep.Results, guide.Session.Step);
            Assert.Equal(PracticeStepResult.Completed, guide.Session.Results[PracticeStep.C2]);
        }
        finally { guide.Close(); }
        Assert.Null(typeof(PracticeSignals).GetField("LibraryEditorOpened", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
    });

    [Fact]
    public void ShellKeepsTheBoundEditorWhenAnotherWindowOpensTheSameRecord() => StaTestHost.Run(() =>
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var app = (App)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(App));
        typeof(App).GetField("_settings", flags)!.SetValue(app, new AppSettings());
        var guide = (WelcomeWindow)typeof(App).GetMethod("CreatePracticeWindow", flags)!.Invoke(app, [true, false, false])!;
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.A3);
            using var first = new InspectorContractHost();
            using var second = new InspectorContractHost();
            PracticeSignals.Ready(PracticeJourneyDriver.Record, first.Editor);
            Guid? editor = guide.Session.EditorId;
            Assert.NotNull(editor); Assert.NotNull(first.Editor.PracticeTarget);
            PracticeSignals.Ready(PracticeJourneyDriver.Record, second.Editor);
            Assert.Null(second.Editor.PracticeTarget);
            Assert.Equal(editor, guide.Session.EditorId);
            second.Editor.NotifyPractice(PracticeEventKind.ArrowSelected);
            Assert.Equal(PracticeStep.A4, guide.Session.Step);
            first.Editor.NotifyPractice(PracticeEventKind.ArrowSelected);
            Assert.Equal(PracticeStep.A5, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void PictureButtonUsesTheClipboardGatewayAndPreservesTheAssetPixels() => StaTestHost.Run(() =>
    {
        Assert.True(ClipboardWriter.IsTestOverrideInstalled);
        long writes = RecordingClipboard.WriteCount;
        uint version = PracticePicture.Copy();
        Assert.Equal(writes + 1, RecordingClipboard.WriteCount);
        Assert.Equal(ClipboardWriter.GetVersion(), version);
        Assert.NotEqual(0u, version);
        var write = Assert.IsType<RecordingClipboard.ClipboardWrite>(RecordingClipboard.LastWrite);
        Assert.True(write.Copy);
        BitmapSource image = Assert.IsAssignableFrom<BitmapSource>(write.Formats[DataFormats.Bitmap]);
        PinHistoryTestSupport.EqualPixels(PracticePicture.Load(), image);
    });

    [Fact]
    public void EditorSignalsCarryTheBoundRecordAndOnlyCompletedArrows() => StaTestHost.Run(() =>
    {
        var signals = new List<PracticeEvent>();
        PracticeSignals.Result += signals.Add;
        try
        {
            using var host = new InspectorContractHost();
            var target = new PracticeEditorTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            host.Editor.PracticeTarget = target;
            Keyboard.ClearFocus();
            Assert.True(host.Editor.HandleShortcut(Key.A, ModifierKeys.None));
            Assert.Equal(PracticeEventKind.ArrowSelected, Assert.Single(signals).Kind);
            Point start = host.Editor.ViewportPointOf(new PointD(120, 120));
            Point end = host.Editor.ViewportPointOf(new PointD(280, 220));
            host.Editor.SimulatePointerDown(start); host.Editor.SimulatePointerMove(end, leftPressed: true);
            Assert.Single(signals);
            host.Editor.SimulatePointerUp(end);
            Assert.Equal(PracticeEventKind.ArrowCommitted, signals[1].Kind);
            Assert.All(signals, signal =>
            {
                Assert.Equal(target.SessionId, signal.SessionId);
                Assert.Equal(target.RecordId, signal.RecordId);
                Assert.Equal(target.EditorId, signal.EditorId);
            });
        }
        finally { PracticeSignals.Result -= signals.Add; }
    });

    [Fact]
    public void PinSignalsUseTheClipboardVersionAndRestoreOnlyTheExpectedLogicalPin() => StaTestHost.Run(() =>
    {
        var signals = new List<PracticeEvent>();
        var monitor = new MonitorInfo("synthetic", new RectD(-10000, -10000, 1600, 1000), new RectD(-10000, -10000, 1600, 1000), 96, true);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => monitor, ReadRestoreMonitorsForTest = () => [monitor],
            BeforeShowForTest = pin => { pin.ShowActivated = false; pin.ShowInTaskbar = false; },
            ActivatePinForTest = _ => { },
        };
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Exception? encodingError = null;
        manager.EncodeClosedForTest = (content, bytes) =>
        {
            try { return EncodedPinContent.Encode(content, bytes); }
            catch (Exception error) { encodingError = error; throw; }
        };
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PracticeSignals.Result += signals.Add;
        try
        {
            var content = PinContent.FromImage(PracticePicture.Load());
            Assert.Equal(PasteResult.Pinned, PinHistoryTestSupport.Await(manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content) with { ClipboardSequence = 42 }))));
            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            Assert.True(pin.Left < -8000 && pin.Top < -8000);
            PracticeEvent opened = Assert.Single(signals);
            Assert.Equal(PracticeEventKind.PinOpened, opened.Kind);
            Assert.Equal(42u, opened.ClipboardSequence);
            Assert.Equal(pin.LogicalPracticeId, opened.LogicalPinId);
            pin.State.SetZoom(1.2);
            Assert.Single(signals); // Programmatic state is never a user result.
            pin.HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
                { RoutedEvent = Mouse.MouseWheelEvent }, ModifierKeys.None);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.PinResized && signal.WindowPinId == pin.PracticeId);
            pin.ShowHoverBar();
            ContextMenu menu = pin.HoverBarForTest!.CreateOpacityMenu();
            Assert.Equal(4, menu.Items.Count);
            ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.OpacityChanged && Math.Abs(signal.Value - .6) < .0001);
            Guid logical = pin.LogicalPracticeId, oldWindow = pin.PracticeId;
            pin.Close(); PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            Assert.Null(encodingError);
            Assert.Equal(1, manager.ClosedCount);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.PinClosing && signal.WindowPinId == oldWindow);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.PinClosed && signal.Restorable && signal.WindowPinId == oldWindow);
            Assert.Null(PinHistoryTestSupport.Await(manager.RestoreLastClosedAsync(Guid.NewGuid())));
            Assert.Equal(1, manager.ClosedCount);
            PinWindow restored = Assert.IsType<PinWindow>(PinHistoryTestSupport.Await(manager.RestoreLastClosedAsync(logical)));
            Assert.Equal(logical, restored.LogicalPracticeId);
            Assert.NotEqual(oldWindow, restored.PracticeId);
            Assert.Equal(.6, restored.State.Opacity, 6);
            Assert.True(restored.Left < -8000 && restored.Top < -8000);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.PinRestored && signal.LogicalPinId == logical && signal.WindowPinId == restored.PracticeId);
        }
        finally { PracticeSignals.Result -= signals.Add; manager.CloseAllForExit(); FluidMotion.AnimationsEnabledOverrideForTest = motion; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturePreparationFailureReportsTheOriginalRequest(bool asynchronous) => StaTestHost.Run(() =>
    {
        var session = new PracticeSession(); Guid request = session.BeginCapture();
        var signals = new List<PracticeEvent>();
        PracticeSignals.Result += signals.Add;
        using var coordinator = new CaptureOverlayCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance,
            _ => throw new InvalidOperationException("Synthetic frame failure"),
            () => asynchronous ? new RectD(-10000, -10000, 32, 20) : throw new InvalidOperationException("Synthetic bounds failure"),
            _ => []) { ReadWindowSources = null, ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()) };
        try
        {
            void Start() => coordinator.Start(false, false, false, practiceTarget: new(session.Id, request));
            if (asynchronous) { Start(); PinHistoryTestSupport.Await(coordinator.LastPreparationForTest); }
            else Assert.Throws<InvalidOperationException>(Start);
            Assert.Contains(signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled && signal.SessionId == session.Id && signal.RequestId == request);
            foreach (PracticeEvent signal in signals) session.Accept(signal);
            Assert.False(session.CapturePending); Assert.Null(session.LastCompleted);
            Assert.Equal(PracticeStepResult.Pending, session.Results[PracticeStep.A2]);
        }
        finally { PracticeSignals.Result -= signals.Add; }
    });
}
