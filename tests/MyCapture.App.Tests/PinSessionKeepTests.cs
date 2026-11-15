using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Open pins stay until the user closes them: every pin the app's exit took away comes back at the
/// next launch with its presentation, and a pin the user closed does not.
/// </summary>
public sealed class PinSessionKeepTests(ITestOutputHelper output)
{
    [Fact]
    public void SettingsFromBeforeRestoreWasTheDefault_ReopenThePinsAnExitClosed() => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"pin":{"restoreOpenPinsOnLaunch":false,"closeOnDoubleClick":true}}
            """);
        PinSettings settings = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load().Pin;
        Assert.True(settings.RestoreOpenPinsOnLaunch);
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        var manager = Manager(settings);
        try
        {
            using (var first = Create(manager, store, settings))
            {
                Wait(first.RestoreAsync());
                manager.PinRenderedText(Text("kept"), "kept", isTable: false);
                Assert.Equal(PinSessionSaveOutcome.Committed, first.SaveNow().Outcome);
            }
            manager.CloseAllForExit();

            using var second = Create(manager, store, settings);
            Wait(second.RestoreAsync());
            Assert.Equal("kept", Assert.Single(manager.SnapshotOpenPins()).Content.OriginalText);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void PinTheUserClosed_DoesNotReturn_WhileTheOthersDo() => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        var manager = Manager(settings);
        try
        {
            using (var service = Create(manager, store, settings))
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Text("stays"), "stays", isTable: false);
                PinWindow closed = manager.PinRenderedText(Text("closed by the user"), "closed by the user", isTable: false);
                Wait(service.FlushDebounceForTest());
                Assert.Equal(2, store.Load().Count);

                closed.Close();
                // The debounced save alone records the close, before any exit save.
                Wait(service.FlushDebounceForTest());
                Assert.Equal("stays", Assert.Single(store.Load()).Pin.OriginalText);
                service.SaveNow();
            }
            manager.CloseAllForExit();

            using var restart = Create(manager, store, settings);
            Wait(restart.RestoreAsync());
            Assert.Equal("stays", Assert.Single(manager.SnapshotOpenPins()).Content.OriginalText);
            Assert.Equal(0, manager.ClosedCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void EveryKind_ComesBackWithItsPresentationProvenanceAndStackingOrder() => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        var manager = Manager(settings);
        const string table = "제품\t수량  \r\n연필\t3\r\n";
        const string markdown = "# 사양\r\n\r\n- 항목 **굵게**\r\n- `code`\r\n";
        CaptureProvenance provenance = CaptureProvenance.Create(documentPath: @"C:\specs\사양서.xlsx", appName: "Excel",
            appExecutablePath: @"C:\Office\EXCEL.EXE", windowTitle: "사양서 - Excel");
        PinContent[] contents =
        [
            PinContent.FromImage(Image(40, 30), provenance: provenance),
            PinContent.FromText(Text("plain"), "plain", isTable: false),
            PinContent.FromText(ClipboardTextRenderer.Render(table, true), table, isTable: true),
            PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(markdown), markdown),
        ];
        try
        {
            IReadOnlyList<PinManager.ClosedPin> before;
            using (var service = Create(manager, store, settings))
            {
                Wait(service.RestoreAsync());
                for (int i = 0; i < contents.Length; i++)
                {
                    PinContent content = contents[i];
                    PinWindow pin = manager.RestoreSessionPin(new PinManager.ClosedPin(content,
                        content.LayoutWidthDip, content.LayoutHeightDip, 1.1 + (i * 0.1), 0.9 - (i * 0.1), null,
                        IsCollapsed: i == 2, CollapsedOpacity: i == 2 ? 0.45 : null));
                    pin.Left = 40 + (i * 90);
                    pin.Top = 60 + (i * 40);
                    if (i == 1) pin.ApplyClickThrough(enabled: true);
                }
                before = manager.SnapshotOpenPins();
                Assert.Equal(PinSessionSaveOutcome.Committed, service.SaveNow().Outcome);
            }
            manager.CloseAllForExit();

            using var restart = Create(manager, store, settings);
            Wait(restart.RestoreAsync());
            IReadOnlyList<PinManager.ClosedPin> after = manager.SnapshotOpenPins();
            IReadOnlyList<PinWindow> windows = manager.SnapshotOpenPinWindows();

            // Real window order, bottom to top: the restore opens the saved stack in the same order.
            Assert.Equal(before.Select(pin => pin.Zoom), after.Select(pin => pin.Zoom));
            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                PinManager.ClosedPin was = before[i], now = after[i];
                Assert.Equal(was.Content.Kind, now.Content.Kind);
                Assert.Equal(was.Content.TextStyle, now.Content.TextStyle);
                Assert.Equal(was.Content.OriginalText, now.Content.OriginalText);
                Assert.Equal(was.Content.Provenance, now.Content.Provenance);
                Assert.Equal(was.Content.UsesDeviceIndependentPixels, now.Content.UsesDeviceIndependentPixels);
                Assert.Equal(was.ImageWidthDip, now.ImageWidthDip);
                Assert.Equal(was.ImageHeightDip, now.ImageHeightDip);
                Assert.Equal(was.Zoom, now.Zoom);
                Assert.Equal(was.Opacity, now.Opacity);
                Assert.Equal(was.IsCollapsed, now.IsCollapsed);
                Assert.Equal(was.CollapsedOpacity, now.CollapsedOpacity);
                Assert.Equal(was.IsClickThrough, now.IsClickThrough);
                PinClosedPlacement wasAt = Assert.IsType<PinClosedPlacement>(was.Placement);
                PinClosedPlacement nowAt = Assert.IsType<PinClosedPlacement>(now.Placement);
                Assert.Equal(wasAt.DeviceName, nowAt.DeviceName);
                Assert.InRange(nowAt.Bounds.X, wasAt.Bounds.X - 1, wasAt.Bounds.X + 1);
                Assert.InRange(nowAt.Bounds.Y, wasAt.Bounds.Y - 1, wasAt.Bounds.Y + 1);
                Assert.InRange(nowAt.Bounds.Width, wasAt.Bounds.Width - 1, wasAt.Bounds.Width + 1);
                Assert.InRange(nowAt.Bounds.Height, wasAt.Bounds.Height - 1, wasAt.Bounds.Height + 1);
            }
            PinWindow through = windows.Single(pin => pin.OriginalText == "plain");
            Assert.True(through.State.IsClickThrough);
            Assert.True(WindowStyleFacade.IsClickThrough(through.Handle));
            Assert.All(windows.Where(pin => pin != through), pin => Assert.False(pin.State.IsClickThrough));
            Assert.True(windows.Single(pin => pin.OriginalText == table).State.IsCollapsed);
            Assert.True(windows.Single(pin => pin.OriginalText == markdown).PinnedContent.IsMarkdown);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ClickThroughChange_ReachesDiskThroughTheDebouncedSave() => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        var manager = Manager(settings);
        using var service = Create(manager, store, settings);
        try
        {
            Wait(service.RestoreAsync());
            PinWindow pin = manager.PinImage(Image(32, 24));
            Wait(service.FlushDebounceForTest());
            Assert.False(Assert.Single(store.Load()).Pin.IsClickThrough);
            int notified = 0;
            manager.OpenPinsChanged += () => notified++;

            pin.ApplyClickThrough(enabled: true);
            pin.ApplyClickThrough(enabled: true);

            Assert.Equal(1, notified);
            Pump(TimeSpan.FromMilliseconds(1300));
            Wait(service.PendingWorkForTest);
            Assert.True(Assert.Single(store.Load()).Pin.IsClickThrough);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SaveThatFailsBeforeTheManifestIsReplaced_KeepsThePreviousSession() => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        var manager = Manager(settings);
        try
        {
            using (var service = Create(manager, store, settings))
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Text("committed"), "committed", isTable: false);
                Assert.Equal(PinSessionSaveOutcome.Committed, service.SaveNow().Outcome);

                manager.PinRenderedText(Text("lost in the crash"), "lost in the crash", isTable: false);
                store.BeforeManifestCommitForTest = () => throw new IOException("Simulated power loss while replacing the manifest.");
                Assert.False(service.SaveNow().ManifestCommitted);
                store.BeforeManifestCommitForTest = null;
            }
            manager.CloseAllForExit();

            var reopened = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Assert.Equal("committed", Assert.Single(reopened.Load()).Pin.OriginalText);
            using var restart = Create(manager, reopened, settings);
            Wait(restart.RestoreAsync());
            Assert.Equal("committed", Assert.Single(manager.SnapshotOpenPins()).Content.OriginalText);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData("removed")]
    [InlineData("smaller")]
    public void DisplayChange_ReopensThePinInsideTheCurrentWorkArea(string change) => StaTestHost.Run(() =>
    {
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        // The saved pin sat near the far corner of a larger display that is now gone or smaller.
        PinClosedPlacement saved = change == "removed"
            ? new(new RectD(5200, 900, 120, 90), @"\\.\DISPLAY9", new RectD(3840, 0, 2560, 1440))
            : new(new RectD(2300, 1250, 120, 90), @"\\.\DISPLAY1", new RectD(0, 0, 2560, 1440));
        BitmapSource image = Image(120, 90);
        Assert.True(store.Save([new PinSessionSnapshot(new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Image,
            null, "", false, 120, 90, 1, 1, saved), ImageCodec.EncodePng(image))]));
        // A 1024x768 desktop, as on the CI runner.
        var display = new MonitorInfo(@"\\.\DISPLAY1", new RectD(0, 0, 1024, 768), new RectD(0, 0, 1024, 728), 96, true);
        var manager = Manager(settings);
        manager.ReadRestoreMonitorsForTest = () => [display];
        using var service = Create(manager, store, settings);
        try
        {
            Wait(service.RestoreAsync());

            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            (int left, int top, int right, int bottom) = pin.PhysicalBounds;
            Assert.InRange(left, 0, 1024);
            Assert.InRange(top, 0, 728);
            Assert.InRange(right, left + 1, 1024);
            Assert.InRange(bottom, top + 1, 728);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void TwentySavedPins_ReopenOnePerDispatcherTurn() => StaTestHost.Run(() =>
    {
        const int count = 20;
        using var workspace = new TempWorkspace();
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
        byte[] png = ImageCodec.EncodePng(Image(160, 120));
        Assert.True(store.Save(Enumerable.Range(0, count).Select(index => new PinSessionSnapshot(
            new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Image, null, "", false, 160, 120, 1, 1,
                new PinClosedPlacement(new RectD(20 + (index * 30), 20 + (index * 20), 160, 120), "", default),
                OpenedOrder: index + 1), png)).ToArray()));
        var manager = Manager(settings);
        using var service = Create(manager, store, settings);
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var operationTimer = new Stopwatch();
        int openedInOperation = 0, mostInOneOperation = 0, operationsWithAPin = 0;
        TimeSpan longest = TimeSpan.Zero;
        manager.PinOpened += _ => openedInOperation++;
        void Started(object? sender, DispatcherHookEventArgs e) { openedInOperation = 0; operationTimer.Restart(); }
        void Completed(object? sender, DispatcherHookEventArgs e)
        {
            if (openedInOperation == 0) return;
            operationsWithAPin++;
            mostInOneOperation = Math.Max(mostInOneOperation, openedInOperation);
            if (operationTimer.Elapsed > longest) longest = operationTimer.Elapsed;
        }
        dispatcher.Hooks.OperationStarted += Started;
        dispatcher.Hooks.OperationCompleted += Completed;
        try
        {
            var total = Stopwatch.StartNew();
            Wait(service.RestoreAsync());
            total.Stop();
            dispatcher.Hooks.OperationStarted -= Started;
            dispatcher.Hooks.OperationCompleted -= Completed;

            Assert.Equal(count, manager.Count);
            Assert.Equal(1, mostInOneOperation);
            Assert.Equal(count, operationsWithAPin);
            output.WriteLine($"restore of {count} pins: total {total.Elapsed.TotalMilliseconds:0} ms, longest pin turn {longest.TotalMilliseconds:0.0} ms");
        }
        finally
        {
            dispatcher.Hooks.OperationStarted -= Started;
            dispatcher.Hooks.OperationCompleted -= Completed;
            manager.CloseAllForExit();
        }
    });

    private static PinManager Manager(PinSettings settings) => new(() => settings, NullLogger.Instance)
    {
        BeforeShowForTest = pin => pin.ShowActivated = false,
        ActivatePinForTest = _ => { },
    };

    private static PinSessionService Create(PinManager manager, PinSessionStore store, PinSettings settings) =>
        new(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance)
        {
            SynchronousSaveTimeout = TimeSpan.FromSeconds(30),
        };

    private static BitmapSource Text(string text) => ClipboardTextRenderer.Render(text, false);

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 90; pixels[i + 2] = 160; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Assert.True(task.IsCompleted, "The pin session task did not finish.");
        }
        task.GetAwaiter().GetResult();
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("pin-keep-").FullName;
        internal AppPaths Paths => AppPaths.CreateForRoot(_root);
        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
    }
}
