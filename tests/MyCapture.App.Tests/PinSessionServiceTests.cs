using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSessionServiceTests
{
    [Fact]
    public void Session_RoundTripsAllKindsAndExactText_WithoutActivationOrCreationTips()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            const string text = "\t한글\tvalue  \r\n  끝\t\r\n";
            try
            {
                using (var service = Create(manager, store, settings))
                {
                    manager.PinRenderedText(Image(), text, isTable: false);
                    manager.RestoreSessionPin(new PinManager.ClosedPin(
                        PinContent.FromSession(Image(), PinContentKind.Table, text, "https://example.com/table", false),
                        15, 10, 1.5, 0.7, null));
                    manager.RestoreSessionPin(new PinManager.ClosedPin(
                        PinContent.FromImage(Image(), "https://example.com/image"), 20, 12, 2, 0.6, null));
                    manager.HideOrShowAll();
                    service.SaveNow();
                }
                IReadOnlyList<PinManager.ClosedPin> before = manager.SnapshotOpenPins();
                manager.CloseAllForExit();
                int created = 0;
                manager.PinCreated += () => created++;
                int uiThread = Environment.CurrentManagedThreadId;
                var decodeThreads = new List<int>();
                using var restore = Create(manager, store, settings, decode: bytes =>
                {
                    decodeThreads.Add(Environment.CurrentManagedThreadId);
                    return ImageCodec.TryDecodePng(bytes);
                });

                Wait(restore.RestoreAsync());
                Wait(restore.RestoreAsync());

                IReadOnlyList<PinManager.ClosedPin> after = manager.SnapshotOpenPins();
                Assert.Equal(3, after.Count);
                Assert.Equal(0, created);
                Assert.Equal(0, manager.ClosedCount);
                Assert.False(manager.AreHidden);
                Assert.Equal(3, decodeThreads.Count);
                Assert.All(decodeThreads, thread => Assert.NotEqual(uiThread, thread));
                for (int i = 0; i < before.Count; i++)
                {
                    Assert.Equal(before[i].Content.Kind, after[i].Content.Kind);
                    Assert.Equal(before[i].Content.OriginalText, after[i].Content.OriginalText);
                    Assert.Equal(before[i].Content.SourcePageUrl, after[i].Content.SourcePageUrl);
                    Assert.Equal(before[i].Content.UsesDeviceIndependentPixels, after[i].Content.UsesDeviceIndependentPixels);
                    Assert.Equal(before[i].ImageWidthDip, after[i].ImageWidthDip);
                    Assert.Equal(before[i].ImageHeightDip, after[i].ImageHeightDip);
                    Assert.Equal(before[i].Zoom, after[i].Zoom);
                    Assert.Equal(before[i].Opacity, after[i].Opacity);
                    Assert.True(after[i].Content.Image.IsFrozen);
                }
                Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(after[0].Content.OriginalText!));
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void Save_ReusesPngForTheSameContent_AndReadsLivePresentation()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int encodes = 0;
            using var service = Create(manager, store, settings, image => { encodes++; return ImageCodec.EncodePng(image); });
            try
            {
                PinWindow pin = manager.PinImage(Image());
                service.SaveNow();
                Guid id = Assert.Single(store.Load()).Pin.Id;
                string png = Path.Combine(workspace.Paths.PinSessionRoot, $"{id:N}.png");
                DateTime written = File.GetLastWriteTimeUtc(png);
                pin.State.SetZoom(1.75);
                pin.State.SetOpacity(0.55);
                pin.Left += 24;
                pin.Top += 16;
                service.SaveNow();
                PinSessionPin saved = Assert.Single(store.Load()).Pin;
                Assert.Equal(1, encodes);
                Assert.Equal(id, saved.Id);
                Assert.Equal(written, File.GetLastWriteTimeUtc(png));
                Assert.Equal(1.75, saved.Zoom);
                Assert.Equal(0.55, saved.Opacity);
                Assert.Equal(pin.GetCurrentPlacement(), saved.Placement);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SettingOff_ClearsImmediately_AndSettingOnSavesCurrentPins()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings();
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            try
            {
                manager.PinRenderedText(Image(), "private\ttext  \r\n", false);
                service.SaveNow();
                Assert.False(Directory.Exists(workspace.Paths.PinSessionRoot));
                settings.RestoreOpenPinsOnLaunch = true;
                service.ApplySettings();
                Assert.Single(store.Load());
                Wait(service.RestoreAsync());
                Assert.Equal(1, manager.Count);
                settings.RestoreOpenPinsOnLaunch = false;
                service.ApplySettings();
                Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot));
                service.SaveNow();
                Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot));
                Assert.Equal(1, manager.Count);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void OpenAndClose_AreDebounced_AndClosedHistoryDoesNotReachDisk()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                PinWindow pin = manager.PinImage(Image());
                Assert.False(File.Exists(Path.Combine(workspace.Paths.PinSessionRoot, "session.json")));
                Pump(TimeSpan.FromMilliseconds(1300));
                Assert.Single(store.Load());
                pin.Close();
                Assert.Single(store.Load());
                Pump(TimeSpan.FromMilliseconds(1300));
                Assert.Empty(store.Load());
                Assert.Equal(1, manager.ClosedCount);
                Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void DisablingDuringDecode_PreventsLateRestoreAndSave()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = Create(manager, store, settings, decode: bytes =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return ImageCodec.TryDecodePng(bytes);
            });
            try
            {
                Task restoring = service.RestoreAsync();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                settings.RestoreOpenPinsOnLaunch = false;
                service.ApplySettings();
                release.Set();
                Wait(restoring);
                Assert.Equal(0, manager.Count);
                Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot));
            }
            finally { release.Set(); manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void ExitDuringDecode_PreservesPendingPinsAndDoesNotReopenAfterDisposal()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = Create(manager, store, settings, decode: bytes =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return ImageCodec.TryDecodePng(bytes);
            });
            try
            {
                Task restoring = service.RestoreAsync();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                manager.PinRenderedText(Image(), "new", false);
                service.SaveNow();
                service.Dispose();
                manager.CloseAllForExit();
                release.Set();
                Wait(restoring);
                Assert.Equal(0, manager.Count);
                Assert.Equal(2, store.Load().Count);
            }
            finally { release.Set(); manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void DecoderFailure_SkipsOnePin_AndSaveFailureDoesNotEscape()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store, count: 2);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int decoded = 0;
            using var service = Create(manager, store, settings, _ => throw new IOException("fixture encoder failure"),
                bytes => ++decoded == 1 ? null : ImageCodec.TryDecodePng(bytes));
            try
            {
                Wait(service.RestoreAsync());
                Assert.Equal(1, manager.Count);
                manager.PinImage(Image());
                Assert.Null(Record.Exception(service.SaveNow));
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SaveBeforeQueuedRestore_PreservesThePreviousSession()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store);
            PinSessionPin original = Assert.Single(store.Load()).Pin;
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            service.SaveNow();
            Assert.Equal(original, Assert.Single(store.Load()).Pin);
        });
    }

    [Fact]
    public void DisabledLaunch_ClearsAnInterruptedSettingsChange()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings();
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            Wait(service.RestoreAsync());
            Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot));
            Assert.Equal(0, manager.Count);
        });
    }

    [Fact]
    public void BudgetExcludedContent_IsEncodedOnceAcrossRepeatedSaves()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: 1);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int encodes = 0;
            using var service = Create(manager, store, settings, image => { encodes++; return ImageCodec.EncodePng(image); });
            try
            {
                Wait(service.RestoreAsync());
                manager.PinImage(Image());
                service.SaveNow();
                service.SaveNow();
                Assert.Equal(1, encodes);
                Assert.Empty(store.Load());
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    private static PinSessionService Create(PinManager manager, PinSessionStore store, PinSettings settings,
        Func<BitmapSource, byte[]>? encode = null, Func<byte[], BitmapSource?>? decode = null) =>
        new(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance, encode, decode);

    private static BitmapSource Image()
    {
        var bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Seed(PinSessionStore store, int count = 1)
    {
        store.Save(Enumerable.Range(0, count).Select(_ => new PinSessionSnapshot(
            new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Text, "saved\t한글  \r\n", "", true,
                24, 16, 1, 1, null), ImageCodec.EncodePng(Image()))).ToArray());
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
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
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("pin-session-").FullName;
        internal AppPaths Paths => AppPaths.CreateForRoot(_root);
        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
    }
}
