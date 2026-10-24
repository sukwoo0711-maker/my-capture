using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
                    manager.RestoreSessionPin(new PinManager.ClosedPin(
                        PinContent.FromImage(Image(), provenance: CaptureProvenance.Create(
                            documentPath: @"C:\specs\사양서.xlsx", appName: "Excel",
                            appExecutablePath: @"C:\Office\EXCEL.EXE", windowTitle: "사양서 - Excel")), 20, 12, 2, 0.6, null));
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
                Assert.Equal(4, after.Count);
                Assert.Equal(0, created);
                Assert.Equal(0, manager.ClosedCount);
                Assert.False(manager.AreHidden);
                Assert.Equal(4, decodeThreads.Count);
                Assert.All(decodeThreads, thread => Assert.NotEqual(uiThread, thread));
                for (int i = 0; i < before.Count; i++)
                {
                    Assert.Equal(before[i].Content.Kind, after[i].Content.Kind);
                    Assert.Equal(before[i].Content.OriginalText, after[i].Content.OriginalText);
                    Assert.Equal(before[i].Content.SourcePageUrl, after[i].Content.SourcePageUrl);
                    Assert.Equal(before[i].Content.Provenance, after[i].Content.Provenance);
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
    public void Save_AfterDpiChangeWritesCurrentBitmapWithANewContentIdentity()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance)
            {
                ReadPlacementMonitorForTest = () => Monitor(1),
                BeforeShowForTest = pin => pin.ShowActivated = false,
                ActivatePinForTest = _ => { },
            };
            int encodes = 0;
            using var service = Create(manager, store, settings,
                image => { encodes++; return ImageCodec.EncodePng(image); });
            try
            {
                Wait(service.RestoreAsync());
                const string text = "saved at the current display scale";
                PinWindow pin = manager.PinRenderedText(ClipboardTextRenderer.Render(text, false), text, false);
                service.SaveNow();
                PinSessionSnapshot first = Assert.Single(store.Load());
                pin.SimulateDpiChangeForTest(2);
                BitmapSource current = pin.PinnedContent.Image;

                service.SaveNow();
                PinSessionSnapshot second = Assert.Single(store.Load());
                BitmapSource saved = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryDecodePng(second.PngBytes!));

                Assert.NotEqual(first.Pin.Id, second.Pin.Id);
                Assert.Equal(2, encodes);
                Assert.Equal(current.PixelWidth, saved.PixelWidth);
                Assert.Equal(current.PixelHeight, saved.PixelHeight);
                Assert.Equal(ImageCodec.EncodePng(current), second.PngBytes);
                Assert.Equal(PinTextStyle.PlainText, second.Pin.TextStyle);
                Assert.Equal(text, second.Pin.OriginalText);
                Assert.Equal(first.Pin.ImageWidthDip, second.Pin.ImageWidthDip);
                Assert.Equal(first.Pin.ImageHeightDip, second.Pin.ImageHeightDip);
                Assert.False(File.Exists(Path.Combine(workspace.Paths.PinSessionRoot, $"{first.Pin.Id:N}.png")));
                service.SaveNow();
                Assert.Equal(2, encodes);
                Assert.Equal(second.Pin.Id, Assert.Single(store.Load()).Pin.Id);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void Restore_CodeMetadataRerendersWithColoursWhileLegacyTextKeepsSavedPixels()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            const string text = "#define MAX_LEN 256";
            PinCodeDocument document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(text,
                "<pre style=\"background-color:#112233\"><span style=\"color:#FF8800\">#define</span> MAX_LEN 256</pre>"));
            BitmapSource codeImage = ClipboardCodeRenderer.Render(document, 1);
            BitmapSource legacyImage = codeImage;
            byte[] legacyPng = ImageCodec.EncodePng(legacyImage);
            BitmapSource savedLegacy = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryDecodePng(legacyPng));
            Assert.True(store.Save(
            [
                new(new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Text, text, "", true,
                    codeImage.Width, codeImage.Height, 1.25, 0.7, null, 1, PinTextStyle.Code, document),
                    ImageCodec.EncodePng(codeImage)),
                new(new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Text, text, "", true,
                    legacyImage.Width, legacyImage.Height, 1, 1, null, 2), legacyPng),
            ]));
            var manager = new PinManager(() => settings, NullLogger.Instance)
            {
                ReadPlacementMonitorForTest = () => Monitor(2),
                ReadRestoreMonitorsForTest = () => [Monitor(2)],
                BeforeShowForTest = pin => pin.ShowActivated = false,
                ActivatePinForTest = _ => { },
            };
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());

                IReadOnlyList<PinManager.ClosedPin> restored = manager.SnapshotOpenPins();
                Assert.Equal(2, restored.Count);
                PinContent code = restored[0].Content;
                PinContent legacy = restored[1].Content;
                Assert.True(code.CanRerender);
                Assert.Equal(PinTextStyle.Code, code.TextStyle);
                Assert.Equal(192, code.Image.DpiX);
                Assert.Equal(codeImage.PixelWidth * 2, code.Image.PixelWidth);
                Assert.Equal(document.Background, code.CodeDocument!.Background);
                Assert.Equal(document.Runs, code.CodeDocument.Runs);
                Assert.Equal(ImageCodec.EncodePng(ClipboardCodeRenderer.Render(document, 2)), ImageCodec.EncodePng(code.Image));
                Assert.Equal(codeImage.Width, restored[0].ImageWidthDip);
                Assert.Equal(codeImage.Height, restored[0].ImageHeightDip);
                Assert.Equal(1.25, restored[0].Zoom);
                Assert.Equal(0.7, restored[0].Opacity);
                Assert.False(legacy.CanRerender);
                Assert.Null(legacy.TextStyle);
                Assert.Null(legacy.CodeDocument);
                Assert.Equal(legacyImage.PixelWidth, legacy.Image.PixelWidth);
                Assert.Equal(legacyImage.PixelHeight, legacy.Image.PixelHeight);
                Assert.Equal(savedLegacy.DpiX, legacy.Image.DpiX);
                Assert.Equal(savedLegacy.DpiY, legacy.Image.DpiY);
                Assert.Equal(CopyPixels(legacyImage), CopyPixels(legacy.Image));
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

    [Fact]
    public void LockedManifest_RetriesWithoutReplacingThePreviousSession()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store);
            PinSessionPin original = Assert.Single(store.Load()).Pin;
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var locked = new FileStream(Path.Combine(workspace.Paths.PinSessionRoot, "session.json"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                Assert.Equal(0, manager.Count);
                manager.PinRenderedText(Image(), "new", false);
                service.SaveNow();
                Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
                locked.Dispose();

                service.SaveNow();

                IReadOnlyList<PinSessionSnapshot> saved = store.Load();
                Assert.Equal(2, saved.Count);
                Assert.Contains(saved, entry => entry.Pin == original);
                Assert.Contains(saved, entry => entry.Pin.OriginalText == "new");
                service.SaveNow();
                Assert.Contains(store.Load(), entry => entry.Pin == original);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void LockedPng_RestoresOthersAndCarriesTheUnreadableRecordForward()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(store, count: 2);
            PinSessionPin original = store.Load()[0].Pin;
            string imagePath = Path.Combine(workspace.Paths.PinSessionRoot, $"{original.Id:N}.png");
            using var locked = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                Assert.Equal(1, manager.Count);
                service.SaveNow();
                Assert.Equal(2, store.LoadDetailed().Pins.Count);
                Assert.True(File.Exists(imagePath));
                locked.Dispose();

                service.SaveNow();

                Assert.Equal(2, store.Load().Count);
                Assert.Contains(store.Load(), entry => entry.Pin == original);
                Assert.True(File.Exists(imagePath));
                Assert.Equal(1, manager.Count);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("truncated")]
    public void ProtectedPngFailure_RemainsPendingAcrossSavesAndFreshServiceRestarts(string failure)
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var seed = NewStore();
            Seed(seed);
            PinSessionPin original = Assert.Single(seed.Load()).Pin;
            string imagePath = Path.Combine(workspace.Paths.PinSessionRoot, $"{original.Id:N}.png");
            byte[] recoverable = File.ReadAllBytes(imagePath);
            byte[] damaged = recoverable.ToArray();
            switch (failure)
            {
                case "ciphertext": damaged[^1] ^= 1; break;
                case "version": damaged[6] = 99; break;
                case "purpose": damaged[7] = 1; break;
                case "truncated": damaged = damaged[..7]; break;
            }
            File.WriteAllBytes(imagePath, damaged);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            try
            {
                int decodes = 0;
                using (var service = Create(manager, NewStore(), settings, decode: bytes =>
                {
                    decodes++;
                    return ImageCodec.TryDecodePng(bytes);
                }))
                {
                    Wait(service.RestoreAsync());
                    Assert.Equal(0, manager.Count);
                    Assert.Equal(0, decodes);
                    manager.PinRenderedText(Image(), "older live", false);
                    manager.PinRenderedText(Image(), "newest live", false);
                    service.SaveNow();
                    AssertPendingAndLatest("newest live");
                    service.SaveNow();
                    AssertPendingAndLatest("newest live");
                }
                manager.CloseAllForExit();

                using (var restarted = Create(manager, NewStore(), settings))
                {
                    Wait(restarted.RestoreAsync());
                    Assert.Equal("newest live", Assert.Single(manager.SnapshotOpenPins()).Content.OriginalText);
                    manager.PinRenderedText(Image(), "after restart", false);
                    restarted.SaveNow();
                    AssertPendingAndLatest("after restart");
                    restarted.SaveNow();
                    AssertPendingAndLatest("after restart");
                }
                manager.CloseAllForExit();

                // Restoring access to the original envelope must make the retained record usable again.
                File.WriteAllBytes(imagePath, recoverable);
                using var recovered = Create(manager, NewStore(), settings);
                Wait(recovered.RestoreAsync());
                Assert.Equal(2, manager.Count);
                Assert.Contains(manager.SnapshotOpenPins(), pin => pin.Content.OriginalText == original.OriginalText);
            }
            finally { manager.CloseAllForExit(); }

            PinSessionStore NewStore() => new(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPins: 2);

            void AssertPendingAndLatest(string latest)
            {
                PinSessionLoadResult saved = NewStore().LoadDetailed();
                Assert.False(saved.ManifestUnreadable);
                Assert.Equal(2, saved.Pins.Count);
                PinSessionSnapshot pending = Assert.Single(saved.Pins, entry => entry.ProtectionPending);
                Assert.Equal(original, pending.Pin);
                Assert.Null(pending.PngBytes);
                Assert.Contains(saved.Pins, entry => entry.Pin.OriginalText == latest && entry.PngBytes is not null);
                Assert.Equal(damaged, File.ReadAllBytes(imagePath));
                Assert.Equal(2, Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Length);
            }
        });
    }

    [Fact]
    public void ProtectionPendingImage_ReservesKnownPlaintextBudgetAcrossRepeatedSaves()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            long budget = ImageCodec.EncodePng(Image()).LongLength;
            var seed = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: budget);
            Seed(seed);
            PinSessionPin original = Assert.Single(seed.Load()).Pin;
            string imagePath = Path.Combine(workspace.Paths.PinSessionRoot, $"{original.Id:N}.png");
            byte[] damaged = File.ReadAllBytes(imagePath);
            damaged[6] = 99;
            File.WriteAllBytes(imagePath, damaged);
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: budget);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int encodes = 0;
            using var service = Create(manager, store, settings, image =>
            {
                encodes++;
                return ImageCodec.EncodePng(image);
            });
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "over budget", false);
                service.SaveNow();
                service.SaveNow();
                PinSessionSnapshot pending = Assert.Single(new PinSessionStore(workspace.Paths.PinSessionRoot,
                    NullLogger.Instance, maxPngBytes: budget).Load());
                Assert.Equal(original, pending.Pin);
                Assert.True(pending.ProtectionPending);
                Assert.Null(pending.PngBytes);
                Assert.Equal(1, encodes);
                Assert.Equal(damaged, File.ReadAllBytes(imagePath));
                Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-1)]
    public void EmptyWhitespaceOrTruncatedManifest_PreservesFilesAcrossServiceSave(int length)
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var seed = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            Seed(seed);
            PinSessionPin original = Assert.Single(seed.Load()).Pin;
            string manifestPath = Path.Combine(workspace.Paths.PinSessionRoot, "session.json");
            string imagePath = Path.Combine(workspace.Paths.PinSessionRoot, $"{original.Id:N}.png");
            byte[] image = File.ReadAllBytes(imagePath);
            byte[] damaged = length < 0 ? Encoding.UTF8.GetBytes(" \t\r\n ") : File.ReadAllBytes(manifestPath)[..length];
            File.WriteAllBytes(manifestPath, damaged);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager,
                new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance), settings);
            try
            {
                Wait(service.RestoreAsync());
                Assert.Equal(0, manager.Count);
                manager.PinRenderedText(Image(), "new live", false);
                service.SaveNow();
                service.SaveNow();
                Assert.Equal(damaged, File.ReadAllBytes(manifestPath));
                Assert.Equal(image, File.ReadAllBytes(imagePath));
                Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"));
                Assert.True(new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance)
                    .LoadDetailed().ManifestUnreadable);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SettingOff_RetriesLockedFileCleanupWhileDisabled()
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
                manager.PinImage(Image());
                service.SaveNow();
                string imagePath = Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png").Single();
                using var locked = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                settings.RestoreOpenPinsOnLaunch = false;
                service.ApplySettings();
                Assert.True(service.CleanupPendingForTest);
                Assert.True(File.Exists(imagePath));

                locked.Dispose();
                PumpUntil(() => !service.CleanupPendingForTest);

                Assert.Empty(Directory.GetFiles(workspace.Paths.PinSessionRoot));
                Assert.False(settings.RestoreOpenPinsOnLaunch);
                Assert.Equal(1, manager.Count);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SaveAndRestore_DoNotRetainPngArraysOrTheCompletedReadTask()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            try
            {
                using (var service = Create(manager, store, settings))
                {
                    Wait(service.RestoreAsync());
                    manager.PinImage(Image());
                    service.SaveNow();
                    AssertIdentityMetadataOnly(service);
                    Assert.False(service.HasPendingReadForTest);
                }
                manager.CloseAllForExit();
                using var restored = Create(manager, store, settings);
                Wait(restored.RestoreAsync());
                Assert.Equal(1, manager.Count);
                AssertIdentityMetadataOnly(restored);
                Assert.False(restored.HasPendingReadForTest);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void BudgetExcludedContent_EncodesAgainOnlyWhenItCanBeAdmitted()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            long oneImageBudget = ImageCodec.EncodePng(Image()).LongLength;
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance, maxPngBytes: oneImageBudget);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int encodes = 0;
            using var service = Create(manager, store, settings, image => { Interlocked.Increment(ref encodes); return ImageCodec.EncodePng(image); });
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "older", false);
                PinWindow newest = manager.PinRenderedText(Image(), "newest", false);
                service.SaveNow();
                Assert.Equal(2, encodes);
                Assert.Equal("newest", Assert.Single(store.Load()).Pin.OriginalText);
                service.SaveNow();
                Assert.Equal(2, encodes);
                AssertIdentityMetadataOnly(service);

                newest.Close();
                service.SaveNow();
                Assert.Equal(3, encodes);
                Assert.Equal("older", Assert.Single(store.Load()).Pin.OriginalText);
                service.SaveNow();
                Assert.Equal(3, encodes);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void DebouncedSaves_RunOffDispatcherSerially_AndRejectTheStaleCapture()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var firstEntered = new ManualResetEventSlim();
            using var firstRelease = new ManualResetEventSlim();
            using var secondEntered = new ManualResetEventSlim();
            using var secondRelease = new ManualResetEventSlim();
            int uiThread = Environment.CurrentManagedThreadId;
            int calls = 0, active = 0, maximumActive = 0;
            using var service = Create(manager, store, settings, image =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                Assert.True(image.IsFrozen);
                int parallel = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, parallel);
                try
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        firstEntered.Set();
                        Assert.True(firstRelease.Wait(TimeSpan.FromSeconds(10)));
                    }
                    else
                    {
                        secondEntered.Set();
                        Assert.True(secondRelease.Wait(TimeSpan.FromSeconds(10)));
                    }
                    return ImageCodec.EncodePng(image);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            try
            {
                Wait(service.RestoreAsync());
                PinWindow pin = manager.PinImage(Image());
                Task first = service.FlushDebounceForTest();
                Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
                pin.State.SetZoom(2);
                service.ScheduleSave();
                Task latest = service.FlushDebounceForTest();
                Assert.False(secondEntered.IsSet);
                firstRelease.Set();
                Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
                Assert.True(first.IsCompleted);
                Assert.False(File.Exists(Path.Combine(workspace.Paths.PinSessionRoot, "session.json")));
                secondRelease.Set();
                Wait(latest);

                Assert.Equal(1, maximumActive);
                Assert.Equal(2, calls);
                Assert.Equal(2, Assert.Single(store.Load()).Pin.Zoom);
            }
            finally
            {
                firstRelease.Set(); secondRelease.Set();
                Wait(service.PendingWorkForTest);
                manager.CloseAllForExit();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InFlightSave_CannotCommitAfterDisposeOrDisable(bool disable)
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance,
                image => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return ImageCodec.EncodePng(image); })
                { SynchronousSaveTimeout = TimeSpan.FromMilliseconds(80) };
            try
            {
                Wait(service.RestoreAsync());
                manager.PinImage(Image());
                _ = service.FlushDebounceForTest();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                if (disable)
                {
                    settings.RestoreOpenPinsOnLaunch = false;
                    service.ApplySettings();
                }
                else service.Dispose();
                release.Set();
                Wait(service.PendingWorkForTest);

                Assert.False(File.Exists(Path.Combine(workspace.Paths.PinSessionRoot, "session.json")));
                Assert.False(Directory.Exists(workspace.Paths.PinSessionRoot)
                    && Directory.GetFiles(workspace.Paths.PinSessionRoot).Length > 0);
                if (disable) Assert.False(service.CleanupPendingForTest);
            }
            finally { release.Set(); Wait(service.PendingWorkForTest); manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void ExitSave_WaitsForInFlightWorkThenCommitsLatestStateWithoutDispatcherWork()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            using var service = Create(manager, store, settings, image =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                return ImageCodec.EncodePng(image);
            });
            try
            {
                Wait(service.RestoreAsync());
                PinWindow pin = manager.PinImage(Image());
                _ = service.FlushDebounceForTest();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                pin.State.SetZoom(2.5);
                Task unblock = Task.Run(async () => { await Task.Delay(80); release.Set(); });

                service.SaveNow();

                unblock.GetAwaiter().GetResult();
                Assert.True(service.PendingWorkForTest.IsCompleted);
                Assert.Equal(2, calls);
                Assert.Equal(2.5, Assert.Single(store.Load()).Pin.Zoom);
            }
            finally { release.Set(); Wait(service.PendingWorkForTest); manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void ExitSave_TimeoutInvalidatesBothQueuedAndInFlightWrites()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            using var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance,
                image =>
                {
                    if (Interlocked.Increment(ref calls) > 1)
                    {
                        entered.Set();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    }
                    return ImageCodec.EncodePng(image);
                })
                { SynchronousSaveTimeout = SaveCompletionTimeout };
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "previous", false);
                service.SaveNow();
                PinSessionPin original = Assert.Single(store.Load()).Pin;
                manager.PinRenderedText(Image(), "new", false);
                _ = service.FlushDebounceForTest();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                service.SynchronousSaveTimeout = TimeSpan.FromMilliseconds(100);
                var elapsed = Stopwatch.StartNew();

                PinSessionSaveResult timedOut = service.SaveNow();

                Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
                Assert.Equal(PinSessionSaveOutcome.TimedOut, timedOut.Outcome);
                Assert.False(timedOut.IsComplete);
                Assert.Equal(2, timedOut.RequestedCount);
                Assert.Empty(timedOut.SavedPinIds);
                Assert.All(timedOut.MissingPins, missing => Assert.Equal(PinSessionMissingReason.NotCommitted, missing.Reason));
                release.Set();
                Wait(service.PendingWorkForTest);
                Assert.Equal(original, Assert.Single(store.Load()).Pin);
                Assert.False(store.VerifyReadBack(timedOut));
                Assert.Equal(2, calls);
            }
            finally { release.Set(); Wait(service.PendingWorkForTest); manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void ExitSave_TimeoutWhileTheStoreHoldsItsLock_ReportsTimedOutWithoutWaitingForTheStore()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "previous", false);
                service.SaveNow();
                PinSessionPin original = Assert.Single(store.Load()).Pin;
                store.BeforeManifestCommitForTest = () =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                };
                manager.PinRenderedText(Image(), "new", false);
                _ = service.FlushDebounceForTest();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                service.SynchronousSaveTimeout = TimeSpan.FromMilliseconds(100);
                var elapsed = Stopwatch.StartNew();

                PinSessionSaveResult timedOut = service.SaveNow();

                Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
                Assert.Equal(PinSessionSaveOutcome.TimedOut, timedOut.Outcome);
                Assert.Equal(2, timedOut.RequestedCount);
                // Checking the result does not wait behind the stalled write either.
                Task<bool> verify = Task.Run(() => store.VerifyReadBack(timedOut));
                Assert.True(verify.Wait(TimeSpan.FromSeconds(2)));
                Assert.False(verify.Result);
                release.Set();
                Wait(service.PendingWorkForTest);
                Assert.Equal(original, Assert.Single(store.Load()).Pin);
            }
            finally
            {
                release.Set();
                Wait(service.PendingWorkForTest);
                store.BeforeManifestCommitForTest = null;
                manager.CloseAllForExit();
            }
        });
    }

    [Fact]
    public void Dispose_AfterManifestTempFlush_PreservesPreviousSessionAndRemovesUncommittedPng()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "previous", false);
                service.SaveNow();
                PinSessionPin original = Assert.Single(store.Load()).Pin;
                string manifestPath = Path.Combine(workspace.Paths.PinSessionRoot, "session.json");
                byte[] originalManifest = File.ReadAllBytes(manifestPath);
                store.BeforeManifestCommitForTest = () =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                };
                manager.PinRenderedText(Image(), "uncommitted", false);
                _ = service.FlushDebounceForTest();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

                service.Dispose();
                release.Set();
                Wait(service.PendingWorkForTest);

                Assert.Equal(originalManifest, File.ReadAllBytes(manifestPath));
                Assert.Equal(original, Assert.Single(store.Load()).Pin);
                Assert.Equal($"{original.Id:N}.png", Path.GetFileName(Assert.Single(
                    Directory.GetFiles(workspace.Paths.PinSessionRoot, "*.png"))));
                Assert.Single(Directory.GetFiles(workspace.Paths.PinSessionRoot), path => path != manifestPath);
            }
            finally
            {
                release.Set();
                Wait(service.PendingWorkForTest);
                store.BeforeManifestCommitForTest = null;
                manager.CloseAllForExit();
            }
        });
    }

    private static void AssertIdentityMetadataOnly(PinSessionService service)
    {
        Assert.NotEmpty(service.ContentIdentitiesForTest);
        Assert.All(service.ContentIdentitiesForTest, identity =>
        {
            FieldInfo[] fields = identity.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.All(fields, field => Assert.True(field.FieldType == typeof(Guid) || field.FieldType == typeof(long),
                $"Identity retained {field.FieldType}: only its identifier and encoded length belong here."));
            Assert.DoesNotContain(fields, field => field.GetValue(identity) is byte[]);
        });
    }

    [Fact]
    public void Save_RepairsAnExistingCorruptPngFromTheLiveFrozenContent()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            int encodes = 0;
            using var service = Create(manager, store, settings, image =>
            {
                Interlocked.Increment(ref encodes);
                return ImageCodec.EncodePng(image);
            });
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "retained live source", false);
                service.SaveNow();
                PinSessionPin original = Assert.Single(store.Load()).Pin;
                string path = Path.Combine(workspace.Paths.PinSessionRoot, $"{original.Id:N}.png");
                File.WriteAllText(path, "structurally corrupt PNG");

                service.SaveNow();

                PinSessionSnapshot repaired = Assert.Single(store.Load());
                Assert.Equal(original, repaired.Pin);
                Assert.NotNull(ImageCodec.TryDecodePng(repaired.PngBytes!));
                Assert.Equal(2, encodes);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SaveNow_ReportsACompleteSaveThatReadsBack()
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
                manager.PinRenderedText(Image(), "text", false);
                manager.PinImage(Image());

                PinSessionSaveResult result = service.SaveNow();

                Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
                Assert.True(result.IsComplete);
                Assert.Equal(2, result.RequestedCount);
                Assert.Empty(result.MissingPins);
                Assert.Equal(store.Load().Select(entry => entry.Pin.Id), result.SavedPinIds);
                Assert.True(store.VerifyReadBack(result));
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Theory]
    [InlineData(PinSessionMissingReason.ImageWriteFailed)]
    [InlineData(PinSessionMissingReason.ProtectionFailed)]
    public void SaveNow_PartialSaveNamesTheMissingPinAndRestartRestoresOnlyTheSavedOne(PinSessionMissingReason failure)
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            FailFirstImage(store, failure);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            try
            {
                using (var service = Create(manager, store, settings))
                {
                    Wait(service.RestoreAsync());
                    manager.PinRenderedText(Image(), "older", false);
                    manager.PinRenderedText(Image(), "newest", false);

                    PinSessionSaveResult partial = service.SaveNow();

                    Assert.Equal(PinSessionSaveOutcome.Committed, partial.Outcome);
                    Assert.False(partial.IsComplete);
                    Assert.Equal(2, partial.RequestedCount);
                    PinSessionMissingPin missing = Assert.Single(partial.MissingPins);
                    Assert.Equal(failure, missing.Reason);
                    PinSessionSnapshot saved = Assert.Single(store.Load());
                    Assert.Equal("older", saved.Pin.OriginalText);
                    Assert.Equal(saved.Pin.Id, Assert.Single(partial.SavedPinIds));
                    Assert.NotEqual(saved.Pin.Id, missing.PinId);
                    Assert.False(store.VerifyReadBack(partial));
                    Assert.Equal(2, manager.Count);
                    Assert.Equal(0, manager.ClosedCount);
                }
                manager.CloseAllForExit();

                using var restarted = Create(manager,
                    new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance), settings);
                Wait(restarted.RestoreAsync());

                Assert.Equal("older", Assert.Single(manager.SnapshotOpenPins()).Content.OriginalText);
                Assert.Equal(0, manager.ClosedCount);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    /// <summary>
    /// The contract for a caller that would close or replace live pins, such as a pin board switch:
    /// the pins stay open until a save is complete and reads back.
    /// </summary>
    [Fact]
    public void LivePins_MayBeReplacedOnlyAfterACompleteSaveReadsBack()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            FailFirstImage(store, PinSessionMissingReason.ImageWriteFailed);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            try
            {
                using (var service = Create(manager, store, settings))
                {
                    Wait(service.RestoreAsync());
                    manager.PinRenderedText(Image(), "older", false);
                    manager.PinRenderedText(Image(), "newest", false);

                    PinSessionSaveResult partial = service.SaveNow();
                    Assert.True(partial.ManifestCommitted);
                    Assert.False(store.VerifyReadBack(partial));

                    PinSessionSaveResult retried = service.SaveNow();
                    Assert.True(retried.IsComplete);
                    Assert.True(store.VerifyReadBack(retried));
                }
                manager.CloseAllForExit();

                using var restarted = Create(manager,
                    new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance), settings);
                Wait(restarted.RestoreAsync());

                Assert.Equal(new[] { "newest", "older" },
                    manager.SnapshotOpenPins().Select(pin => pin.Content.OriginalText).Order());
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveNow_ReportsOlderPinsOverTheSessionLimit(bool limitBytes)
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance,
                maxPins: limitBytes ? PinSessionStore.DefaultMaxPins : 1,
                maxPngBytes: limitBytes ? ImageCodec.EncodePng(Image()).LongLength : PinSessionStore.DefaultMaxPngBytes);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            using var service = Create(manager, store, settings);
            try
            {
                Wait(service.RestoreAsync());
                manager.PinRenderedText(Image(), "older", false);
                manager.PinRenderedText(Image(), "newest", false);

                PinSessionSaveResult result = service.SaveNow();

                Assert.Equal(PinSessionSaveOutcome.Committed, result.Outcome);
                Assert.False(result.IsComplete);
                Assert.Equal(2, result.RequestedCount);
                Assert.Equal(PinSessionMissingReason.OverLimit, Assert.Single(result.MissingPins).Reason);
                PinSessionSnapshot saved = Assert.Single(store.Load());
                Assert.Equal("newest", saved.Pin.OriginalText);
                Assert.Equal(saved.Pin.Id, Assert.Single(result.SavedPinIds));
                Assert.False(store.VerifyReadBack(result));
                Assert.Equal(2, manager.Count);
            }
            finally { manager.CloseAllForExit(); }
        });
    }

    [Fact]
    public void SaveNow_ReportsNotRunWhileRestoreIsOffAndAfterDispose()
    {
        StaTestHost.Run(() =>
        {
            using var workspace = new TempWorkspace();
            var settings = new PinSettings();
            var store = new PinSessionStore(workspace.Paths.PinSessionRoot, NullLogger.Instance);
            var manager = new PinManager(() => settings, NullLogger.Instance);
            PinSessionService service = Create(manager, store, settings);
            try
            {
                manager.PinRenderedText(Image(), "not saved", false);

                PinSessionSaveResult off = service.SaveNow();
                service.Dispose();
                PinSessionSaveResult disposed = service.SaveNow();

                Assert.All(new[] { off, disposed }, result =>
                {
                    Assert.Equal(PinSessionSaveOutcome.NotRun, result.Outcome);
                    Assert.False(result.IsComplete);
                    Assert.False(store.VerifyReadBack(result));
                });
                Assert.False(Directory.Exists(workspace.Paths.PinSessionRoot));
            }
            finally { service.Dispose(); manager.CloseAllForExit(); }
        });
    }

    // Fails only the first image the store protects or writes: the newest pin, which it saves first.
    private static void FailFirstImage(PinSessionStore store, PinSessionMissingReason failure)
    {
        int attempts = 0;
        Action<Guid> failFirst = _ =>
        {
            if (Interlocked.Increment(ref attempts) != 1) return;
            if (failure == PinSessionMissingReason.ProtectionFailed)
                throw new CryptographicException("fixture protection failure");
            throw new IOException("fixture image write failure");
        };
        if (failure == PinSessionMissingReason.ProtectionFailed) store.BeforeImageProtectForTest = failFirst;
        else store.BeforeImageWriteForTest = failFirst;
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var limit = Stopwatch.StartNew();
        while (!completed() && limit.Elapsed < TimeSpan.FromSeconds(5)) Pump(TimeSpan.FromMilliseconds(100));
        Assert.True(completed(), "Pending pin-session work did not complete.");
    }

    // SaveNow drops a write that outlasts its 3 s exit budget, by design; the timeout tests above set
    // their own short budget. Each save flushes a PNG and the manifest to disk, and on a hosted runner
    // busy with Core tests' gigabyte writes a save outlasted 3 s, so the next restore found no pin.
    // Saves that a test relies on get this budget instead; SaveNow still returns as soon as they finish.
    private static readonly TimeSpan SaveCompletionTimeout = TimeSpan.FromSeconds(30);

    private static PinSessionService Create(PinManager manager, PinSessionStore store, PinSettings settings,
        Func<BitmapSource, byte[]>? encode = null, Func<byte[], BitmapSource?>? decode = null) =>
        new(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance, encode, decode)
        {
            SynchronousSaveTimeout = SaveCompletionTimeout,
        };

    private static MonitorInfo Monitor(double scale) => new("fake",
        new RectD(0, 0, 3840, 2160), new RectD(0, 0, 3840, 2100), (uint)(96 * scale), true);

    private static BitmapSource Image()
    {
        var bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] CopyPixels(BitmapSource image)
    {
        BitmapSource pixels = image.Format == PixelFormats.Bgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        byte[] bytes = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(bytes, stride, 0);
        return bytes;
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
