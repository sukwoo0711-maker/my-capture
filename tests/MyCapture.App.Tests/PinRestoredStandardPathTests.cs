using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
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
/// The standard pin (setting off) after a reopen: its picture is a frame decoded on a pool thread, and copy,
/// quick save, the session and the closed list encode it on whichever pool thread runs them. Every encoder of
/// the product's own paths is wrapped only to note the thread it ran on; what the product does with a failure
/// is its own. Before the decoders read a frame's metadata on the decoding thread
/// (ImageCodec.KeepFrameMetadata) each of these worked only when it happened to run on that thread: of 15
/// reopened pins, 4 or 5 copies and quick saves failed, and an encode on the UI thread failed every time.
/// </summary>
public sealed class PinRestoredStandardPathTests(ITestOutputHelper output)
{
    private const string SourceUrl = "https://example.invalid/page";
    private const int Rounds = 15;
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    private sealed record Attempt(string Path, string Operation, int DecodedOn, int RanOn, string Outcome);

    [Fact]
    public void AReopenedStandardPinCopiesSavesAndClosesWhicheverThreadEncodes() => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var attempts = new List<Attempt>();
        var results = new List<string>();
        var log = new PinLightweightRestoreWindowTests.RecordingLog();
        int uiThread = Environment.CurrentManagedThreadId;
        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                RunRound("closed history", attempts, results, log);
                RunRound("session", attempts, results, log);
            }
        }
        finally { FluidMotion.AnimationsEnabledOverrideForTest = motion; }

        foreach (var group in attempts.GroupBy(attempt => (attempt.Path, attempt.Operation)))
        {
            int same = group.Count(attempt => attempt.RanOn == attempt.DecodedOn);
            output.WriteLine($"{group.Key.Path} / {group.Key.Operation}: {group.Count()} encodes, on the decoding thread {same}, "
                + string.Join(", ", group.GroupBy(attempt => attempt.Outcome).Select(outcome => $"{outcome.Key} {outcome.Count()}")));
        }
        foreach (var group in results.GroupBy(result => result).OrderBy(group => group.Key))
            output.WriteLine($"result: {group.Key} x{group.Count()}");
        foreach (var group in log.Entries.Where(entry => !entry.StartsWith("Information", StringComparison.Ordinal))
            .GroupBy(entry => entry).OrderBy(group => group.Key))
            output.WriteLine($"log: {group.Key} x{group.Count()}");

        // The pool chooses the threads of the product's encodes; the UI thread's own encode is always elsewhere.
        Assert.Contains(attempts, attempt => attempt.RanOn == uiThread && attempt.RanOn != attempt.DecodedOn);
        Assert.All(attempts, attempt => Assert.True(attempt.Outcome == "works", $"{attempt}"));
        Assert.All(results, result => Assert.Matches(": (copied True|Saved|Committed|restorable 1)$", result));
        Assert.DoesNotContain(log.Entries, entry => entry.StartsWith("Warning", StringComparison.Ordinal)
            || entry.StartsWith("Error", StringComparison.Ordinal));
    });

    private static void RunRound(string path, List<Attempt> attempts, List<string> results,
        PinLightweightRestoreWindowTests.RecordingLog log)
    {
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        int decodedOn = 0;
        T Noted<T>(string operation, Func<T> encode)
        {
            if (Volatile.Read(ref decodedOn) == 0) return encode();
            int thread = Environment.CurrentManagedThreadId;
            try
            {
                T value = encode();
                lock (attempts) attempts.Add(new(path, operation, decodedOn, thread, "works"));
                return value;
            }
            catch (Exception error)
            {
                lock (attempts) attempts.Add(new(path, operation, decodedOn, thread, error.GetType().Name));
                throw;
            }
        }
        Task<bool>? copying = null;
        var manager = new PinManager(() => settings, null,
            (image, url, text, provenance, cache) => copying = ClipboardImageService.CopyImageWithTextAsync(image, url, text,
                static _ => { }, provenance, null, (bitmap, source) => Noted("copy", () => ImageCodec.EncodePng(bitmap, source)), cache),
            static _ => Task.FromResult(true), log)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
            BeforeShowForTest = static pin => pin.ShowActivated = false,
        };
        manager.DecodeClosedForTest = encoded =>
        {
            decodedOn = Environment.CurrentManagedThreadId;
            return encoded.Decode();
        };
        manager.EncodeClosedForTest = (content, png) => Noted("closed encode", () => EncodedPinContent.Encode(content, png));
        string root = OwnedTestDirectory.Create("pin-standard-restore-");
        var store = new PinSessionStore(Path.Combine(root, "session"), NullLogger.Instance);
        PinSessionService Service() => new(manager, store, () => settings, Dispatcher.CurrentDispatcher, log,
            encode: image => Noted("session save", () => ImageCodec.EncodePng(image)),
            decode: bytes =>
            {
                decodedOn = Environment.CurrentManagedThreadId;
                return ImageCodec.TryDecodePng(bytes);
            });
        PinSessionService service = Service();
        try
        {
            Wait(service.RestoreAsync());
            BitmapSource capture = PinLightweightRestoreWindowTests.CaptureLike(240, 135);
            PinWindow reopened;
            if (path == "closed history")
            {
                PinWindow pin = manager.PinImage(capture);
                pin.CloseWithExit();
                PinNativeSpeedTestWait.Until(() => pin.IsClosed);
                PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
                reopened = Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
            }
            else
            {
                manager.PinImage(capture);
                Assert.True(service.SaveNow().IsComplete);
                service.Dispose();
                manager.CloseAllForExit();
                service = Service();
                Wait(service.RestoreAsync());
                reopened = Assert.Single(manager.SnapshotOpenPinWindows());
            }
            Assert.False(reopened.IsNativeHosted);
            Assert.NotEqual(Environment.CurrentManagedThreadId, decodedOn);
            // The UI thread never decoded this picture.
            Noted("encode on the UI thread", () => ImageCodec.EncodePng(reopened.PinnedContent.Image));

            // Ctrl+C through the pin, then a copy with a source address, both through ClipboardImageService.
            reopened.HandleKey(Key.C, ModifierKeys.Control);
            PinNativeSpeedTestWait.Until(() => copying is { IsCompleted: true });
            results.Add($"{path} copy: copied {copying!.Result}");
            Task<bool> withSource = ClipboardImageService.CopyImageWithTextAsync(reopened.PinnedContent.Image, SourceUrl, null,
                static _ => { }, null, null, (bitmap, source) => Noted("copy with source", () => ImageCodec.EncodePng(bitmap, source)));
            PinNativeSpeedTestWait.Until(() => withSource.IsCompleted);
            results.Add($"{path} copy with source: copied {withSource.Result}");

            // Quick save through the real save service (the manager's failure dialog would wait for a person).
            BitmapSource? toSave = null;
            reopened.SaveRequested += (_, e) => toSave ??= e.Image;
            reopened.HandleKey(Key.S, ModifierKeys.Control);
            PinNativeSpeedTestWait.Until(() => toSave is not null);
            var appSettings = new AppSettings();
            appSettings.Export.QuickSaveDirectoryOverride = Path.Combine(root, "saved");
            AppPaths paths = AppPaths.CreateForRoot(root);
            var saving = new PinImageSaveService(() => appSettings, () => paths, NullLogger<PinImageSaveService>.Instance);
            Task<PinSaveResult> saved = saving.QuickSaveAsync(toSave!, string.Empty);
            PinNativeSpeedTestWait.Until(() => saved.IsCompleted);
            results.Add($"{path} quick save: " + (saved.IsFaulted
                ? "throws " + saved.Exception!.InnerException!.GetType().Name
                : saved.Result.Status.ToString()));

            PinSessionSaveResult session = service.SaveNow();
            results.Add($"{path} session save: {session.Outcome}");

            int closedBefore = manager.ClosedCount;
            // As a pin that was not copied: the close encodes its picture instead of reusing the copy's PNG.
            reopened.ClipboardCache.Clear();
            reopened.CloseWithExit();
            PinNativeSpeedTestWait.Until(() => reopened.IsClosed);
            Task encoding = manager.LastClosedEncodingForTest;
            // The observed task ends after a failed entry has left the history.
            PinNativeSpeedTestWait.Until(() => encoding.IsCompleted);
            results.Add($"{path} closed again: restorable {manager.ClosedCount - closedBefore}");
        }
        finally
        {
            service.Dispose();
            manager.CloseAllForExit();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void Wait(Task task)
    {
        PinNativeSpeedTestWait.Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
}
