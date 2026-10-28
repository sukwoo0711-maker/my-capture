using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorPinCommandTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("none", true, 1, 0)]
    [InlineData("save", false, 0, 0)]
    [InlineData("copy", false, 0, 0)]
    [InlineData("pin", true, 1, 1)]
    public void PinSavesCopiesThenPinsAndClosesOnlyWhenDurable(string failure, bool closes, int pins, int notices) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("editor-pin-");
        output.WriteLine("Fixture: " + root);
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            var image = Solid(80, 60, 0x30);
            CaptureRecord record = persistence.PersistOriginal(image, 1, "Source window", "display");
            record.SourcePageUrl = "https://example.com/capture";
            long revision = record.ContentRevision;
            var events = new List<string>();
            System.Windows.Media.Imaging.BitmapSource? copied = null;
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (bitmap, _) =>
                {
                    Assert.True(record.ContentRevision > revision);
                    copied = bitmap;
                    events.Add("copy");
                    return Task.FromResult(failure != "copy");
                });
            int pinCount = 0, noticeCount = 0;
            commit.PinImage = (bitmap, provenance) =>
            {
                Assert.Same(copied, bitmap);
                Assert.Equal(CaptureProvenance.FromRecord(record), provenance);
                Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Rendered)));
                events.Add("pin");
                pinCount++;
                if (failure == "pin") throw new InvalidOperationException("Synthetic pin failure");
            };
            commit.PinFailed = () => { events.Add("notice"); noticeCount++; };
            using CaptureEditSession session = commit.BeginEditSession(record);
            using var host = EditorWindowHost.Create(image);
            var controller = Controller(host.Editor);
            controller.Tool = EditorTool.Arrow;
            controller.PointerDown(new PointD(10, 10));
            controller.PointerUp(new PointD(60, 40));
            bool completed = false;
            host.Window.CommitRequested = result => commit.CommitAsync(record, result, session);
            host.Editor.EditingCompleted += (_, result) => { Assert.Equal(EditorCommitAction.Pin, result.Action); completed = true; };
            host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
            // The first staged file swap fails, so the save rolls back. A metadata write that fails
            // after the files are committed is recorded as pending metadata and the edit stays
            // saved, so it is not a failed save.
            if (failure == "save")
                persistence.BeforeStagedFileCommit = (_, _) => throw new IOException("Synthetic save failure");
            host.Editor.PinAndClose();
            PumpUntil(() => completed || !host.Editor.IsCommitInProgress);
            Assert.Equal(closes, completed);
            Assert.Equal(!closes, host.Window.IsVisible);
            Assert.Equal(pins, pinCount);
            Assert.Equal(notices, noticeCount);
            Assert.Equal(failure == "save" ? [] : failure == "copy" ? ["copy"] : failure == "pin" ? ["copy", "pin", "notice"] : new[] { "copy", "pin" }, events);
            output.WriteLine("Pin contract assertions completed before fixture cleanup.");
        }
        catch (Exception error) { output.WriteLine("Body failure: " + error); throw; }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(true, true, "editor")]
    [InlineData(true, false, "editor")]
    [InlineData(false, true, "selector")]
    [InlineData(false, false, "clipboard")]
    public void RoutingPrioritizesForegroundEditorThenSelector(bool editor, bool selector, string expected)
    {
        var calls = new List<string>();
        PinCommandRouter.Execute(() => editor ? () => calls.Add("editor") : null, () => selector,
            () => calls.Add("selector"), () => calls.Add("clipboard"));
        Assert.Equal([expected], calls);
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("tiny", 0)]
    [InlineData("text", 0)]
    [InlineData("recording", 0)]
    [InlineData("region", 1)]
    public void SelectorUsesEnterTargetAndPreservesNoOpCases(string mode, int expected) => StaTestHost.Run(() =>
    {
        var image = Solid(80, 60, 0x20);
        var view = new CaptureOverlayView(new FrozenFrame(image, new RectD(-200, 100, 80, 60), null, 0), false);
        int confirmed = 0;
        view.SelectionConfirmed += (_, result) =>
        {
            confirmed++;
            Assert.True(result.PinRequested);
            Assert.Equal(new RectD(5, 6, 40, 30), result.BitmapRegion);
        };
        if (mode != "none") Set(view, "_selection", mode == "tiny" ? new RectD(1, 1, 1, 1) : new RectD(5, 6, 40, 30));
        view.TextCaptureAvailable = true;
        view.TextCaptureMode = mode == "text";
        view.RecordingSelection = mode == "recording";
        view.ConfirmSelectionAsPin();
        Assert.Equal(expected, confirmed);
        view.ReleaseResources();
    });

    [Fact]
    public void SelectedCaptureUsesRealLibraryAndClipboardPipelineAndPinsAtOneToOne() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("selector-pin-");
        output.WriteLine("Fixture: " + root);
        SynchronizationContext? previous = SynchronizationContext.Current;
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        var pins = new PinManager(() => new PinSettings(), NullLogger.Instance);
        var captureLog = new CaptureFailureLog();
        PinWindow? pin = null;
        RectD? initialBounds = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            typeof(DispatcherObject).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, Dispatcher.CurrentDispatcher);
            var paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            settings.General.PlayCaptureSound = false;
            settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            int copies = 0;
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => { copies++; return Task.FromResult(true); });
            var monitor = new MonitorInfo("test", new RectD(-1600, 0, 1600, 1000), new RectD(-1600, 0, 1600, 960), 144, false);
            pins.ReadRestoreMonitorsForTest = () => [monitor];
            pins.ShowPinForTest = (window, _) => { pin = window; initialBounds = Field<RectD?>(window, "_restorePhysicalBounds"); };
            pins.ActivatePinForTest = _ => { };
            Set(app, "_settings", settings); Set(app, "_queue", queue); Set(app, "_persistence", persistence);
            Set(app, "_commit", commit); Set(app, "_pins", pins);
            Set(app, "_log", captureLog);
            var image = Solid(300, 180, 0x40);
            var selection = new CaptureSelectionCompletedEventArgs(new FrozenFrame(image,
                new RectD(-1400, 100, 600, 400), monitor, 0), new RectD(40, 50, 300, 180), image, "Capture source") { PinRequested = true };
            Task task = (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, [selection])!;
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            foreach (Exception error in captureLog.Errors) output.WriteLine("Capture pipeline error: " + error);
            CaptureRecord saved = Assert.Single(queue.Records);
            Assert.Equal(1, copies);
            Assert.NotNull(pin);
            Assert.Equal(CaptureProvenance.FromRecord(saved), pin.Provenance);
            Assert.Equal(1, pin.State.Zoom);
            Assert.Equal(200, pin.State.ImageWidthDip);
            Assert.Equal(new RectD(-1360, 150, 300, 180), initialBounds);
            Assert.Null(Field<RectD?>(pin, "_restorePhysicalBounds"));
            Assert.Null(Field<object?>(app, "_quickAccess"));
            output.WriteLine("Selector persistence, copy, provenance, 100% size and screen-position assertions completed before fixture cleanup.");
        }
        catch (Exception error) { output.WriteLine("Body failure: " + error); throw; }
        finally
        {
            (Field<IDisposable?>(app, "_currentEditSession"))?.Dispose();
            pin?.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void RegionPinPlacesPhysicalPixelsAtOneToOneBeforeShowing() => StaTestHost.Run(() =>
    {
        var monitor = new MonitorInfo("test", new RectD(-1600, 0, 1600, 1000), new RectD(-1600, 0, 1600, 960), 144, false);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        manager.ReadRestoreMonitorsForTest = () => [monitor];
        RectD? initialBounds = null;
        manager.ShowPinForTest = (window, _) => initialBounds = Field<RectD?>(window, "_restorePhysicalBounds");
        manager.ActivatePinForTest = _ => { };
        var provenance = CaptureProvenance.Create(url: "https://example.com/selected");
        PinWindow pin = manager.PinImageAtRegion(Solid(300, 180, 0x40), provenance, new RectD(-1360, 150, 300, 180));
        try
        {
            Assert.Equal(new RectD(-1360, 150, 300, 180), initialBounds);
            Assert.Null(Field<RectD?>(pin, "_restorePhysicalBounds"));
            Assert.Equal(1, pin.State.Zoom);
            Assert.Equal(200, pin.State.ImageWidthDip);
            Assert.Equal(120, pin.State.ImageHeightDip);
            Assert.Equal(provenance, pin.Provenance);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ForegroundLookupRequiresAnExactImageEditorWindowHandle() => StaTestHost.Run(() =>
    {
        using var host = EditorWindowHost.Create();
        var handle = new System.Windows.Interop.WindowInteropHelper(host.Window).Handle;
        Assert.Same(host.Window, PinCommandRouter.FindForegroundEditor([host.Window], handle));
        Assert.Null(PinCommandRouter.FindForegroundEditor([host.Window], 0));
        Assert.Null(PinCommandRouter.FindForegroundEditor([host.Window], handle + 1));
        host.Window.FrameStillImage = new MyCapture.App.Recording.VideoFrameStillImage(0, []);
        Assert.Null(PinCommandRouter.FindForegroundEditor([host.Window], handle));
        var button = Descendants(host.Editor).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "EditorFlowPin");
        Assert.Equal(Visibility.Collapsed, button.Visibility);
        bool committed = false;
        host.Window.CommitRequested = _ => { committed = true; return Task.FromResult(true); };
        host.Editor.PinAndClose();
        Assert.False(committed);
    });

    [Fact]
    public void PinCaptionReadsReassignedAndUnassignedHotkey() => StaTestHost.Run(() =>
    {
        Func<Hotkey>? previous = AnnotationEditorPreferences.ReadPinHotkey;
        try
        {
            Hotkey key = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkF3);
            AnnotationEditorPreferences.ReadPinHotkey = () => key;
            using var host = EditorWindowHost.Create();
            var button = Descendants(host.Editor).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "EditorFlowPin");
            Assert.Equal("Ctrl+Shift+F3", AutomationProperties.GetAcceleratorKey(button));
            Assert.Contains("Ctrl+Shift+F3", Assert.IsType<string>(button.ToolTip));
            key = Hotkey.None;
            host.Editor.RefreshPinShortcut();
            Assert.Empty(AutomationProperties.GetAcceleratorKey(button));
            Assert.DoesNotContain("F3", Assert.IsType<string>(button.ToolTip));
        }
        finally { AnnotationEditorPreferences.ReadPinHotkey = previous; }
    });

    internal static void PumpUntil(Func<bool> ready)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "Operation did not settle.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class CaptureFailureLog : ILogger<App>
    {
        internal List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (error is not null) Errors.Add(error);
        }
    }
}
