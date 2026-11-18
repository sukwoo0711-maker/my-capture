using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Increment 5 through the real PinManager and real off-screen HWNDs: an opaque lightweight picture pin keeps
/// one copy (the native source), and every reader of its picture gets exactly what the same picture gives on a
/// standard pin, without keeping what it built. Input is called on the pin or posted to its own windows only.
/// </summary>
public sealed class PinLightweightOneCopyWindowTests
{
    private const string SourceUrl = "https://example.invalid/pinned";
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    public static TheoryData<string> Pictures => new() { "Pbgra32", "Bgr32-padding-0", "Bgra32-decoded-png", "Bgr24-decoded-png" };

    [Theory]
    [MemberData(nameof(Pictures))]
    public void EveryReaderGivesWhatAStandardPinGivesAndKeepsNothingItBuilt(string kind) => Run(() =>
    {
        BitmapSource picture = OneCopyTestImages.Opaque(kind, 120, 80);
        Readings standard = ReadEverything(picture, lightweight: false);
        Readings light = ReadEverything(picture, lightweight: true);
        Assert.Equal(standard.Names, light.Names);
        int restored = standard.Names.IndexOf("closed restore");
        for (int index = 0; index < restored; index++)
            Assert.True(standard.Seen[index].Equals(light.Seen[index]), $"{kind}: {standard.Names[index]} differs.");
        Assert.Equal(standard.ClosedPng, light.ClosedPng);
        Assert.Equal(standard.SessionPng, light.SessionPng);
        Assert.Equal("standard, both copies", standard.ClosedRestore);
        Assert.Equal("standard, both copies", standard.SessionRestore);
        Assert.Equal("lightweight, one copy", light.ClosedRestore);
        Assert.Equal("lightweight, one copy", light.SessionRestore);
        // A reopened standard pin holds a frame decoded on a pool thread, which encodes only on that thread
        // (PinRestoredStandardPathTests). The light pin's readings are what the same PNG decoded here gives.
        foreach ((string name, byte[] png) in new[] { ("closed restore", light.ClosedPng), ("session restore", light.SessionPng) })
        {
            BitmapSource decoded = ImageCodec.TryDecodePng(png)!;
            Seen expected = Seen.Of(decoded);
            int at = light.Names.IndexOf(name);
            Assert.True(expected.Equals(light.Seen[at]), $"{kind}: {name} differs.");
            Assert.True(expected.Equals(light.Seen[at + 1]), $"{kind}: {name} copy differs.");
            Assert.Equal(Seen.Try(() => ImageCodec.EncodePng(decoded, string.Empty)), light.Seen[at + 2].Png);
            Assert.Equal(OneCopyTestImages.Raw(decoded), Convert.FromBase64String(standard.Seen[at].Pixels));
        }
        // The standard pin never builds a bitmap; the light pin builds one for each reading of the original pin
        // (a copy's PNG and bitmap share one) and keeps none (asserted inside).
        Assert.Equal(0, standard.Rebuilds);
        int originalReadings = standard.Names.IndexOf("closed restore") - 1;
        Assert.True(light.Rebuilds >= originalReadings, $"{light.Rebuilds} rebuilds for {originalReadings} readings");
    });

    [Fact]
    public void ASemiTransparentPictureKeepsBothCopiesAndItsBitmapInstance() => Run(() =>
    {
        BitmapSource picture = PinNativeTestImages.Picture(90, 60, transparent: true);
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            Assert.True(pin.IsNativeHosted);
            Assert.False(pin.IsPictureBitmapDropped);
            Assert.Same(picture, pin.PinnedContent.Image);
            Assert.Same(picture, pin.ImageSourceForTest);
            long rebuilds = PinPicture.RebuildCount;
            pin.HandleKey(Key.C, ModifierKeys.Control);
            Assert.Equal(rebuilds, PinPicture.RebuildCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void WithTheSettingOffAndForTextPinsNothingChanges() => Run(() =>
    {
        BitmapSource picture = OneCopyTestImages.Opaque("Pbgra32", 90, 60);
        var manager = Manager(new PinSettings());
        var light = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            Assert.False(pin.IsNativeHosted);
            Assert.False(pin.IsPictureBitmapDropped);
            Assert.Same(picture, pin.PinnedContent.Image);
            Assert.Same(picture, pin.ImageSourceForTest);

            BitmapSource rendered = OneCopyTestImages.Opaque("Pbgra32", 60, 30);
            PinWindow text = light.PinRenderedText(rendered, "text", isTable: false);
            Assert.False(text.IsNativeHosted);
            Assert.False(text.IsPictureBitmapDropped);
        }
        finally { manager.CloseAllForExit(); light.CloseAllForExit(); }
    });

    [Fact]
    public void TheSwitchBuildsTheBitmapOnceKeepsItAndThenReleasesTheNativeSource() => Run(() =>
    {
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        long sourceBytes = NativePinPixelSource.LiveBytes;
        BitmapSource picture = OneCopyTestImages.Opaque("Bgr32-padding-0", 120, 80);
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            Assert.True(pin.IsPictureBitmapDropped);
            Assert.Null(pin.ImageSourceForTest);
            Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
            pin.ActivateAfterSwitchForTest = static _ => { };
            long rebuilds = PinPicture.RebuildCount;
            bool sourceAliveAtHandOver = false;
            pin.BeforeNativeRetiredForTest = (_, _) => sourceAliveAtHandOver = NativePinPixelSource.LiveAllocationCount == sources + 1;

            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            PinNativeSpeedTestWait.Until(() => !pin.IsHostSwitchPending);

            Assert.True(sourceAliveAtHandOver);
            Assert.Equal(rebuilds + 1, PinPicture.RebuildCount);
            Assert.False(pin.IsPictureBitmapDropped);
            BitmapSource shown = Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest);
            Assert.Same(shown, pin.PinnedContent.Image);
            Assert.Same(shown, pin.PinnedContent.Image);
            Assert.Equal(rebuilds + 1, PinPicture.RebuildCount);
            OneCopyTestImages.AssertSame(picture, shown);
            // The switched pin holds one copy again: its bitmap; the native source is gone.
            Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
            Assert.Equal(sourceBytes, NativePinPixelSource.LiveBytes);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void AHiddenPinSwitchesWithItsBitmapAndWithoutItsSource() => Run(() =>
    {
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        BitmapSource picture = OneCopyTestImages.Opaque("Pbgra32", 80, 60);
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.HidePin();
            Assert.True(pin.SwitchToWpf(PinSwitchReason.Command));
            Assert.False(pin.IsHostSwitchPending);
            Assert.False(pin.IsPictureBitmapDropped);
            OneCopyTestImages.AssertSame(picture, Assert.IsAssignableFrom<BitmapSource>(pin.ImageSourceForTest));
            Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void AContentOpenedAgainSharesItsSourceAndAClosedPinsSourceGoesWithItsLastReference() => Run(() =>
    {
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        var manager = Manager(new PinSettings { LightweightImagePins = true });
        try
        {
            WeakReference[] held = OpenSnapshotCloseAndReopen(manager, sources);
            manager.CloseAllForExit();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
            // WPF lets a closed window go once its queued work has run; collect between short pumps.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (held[0].IsAlive && watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                long until = watch.ElapsedMilliseconds + 50;
                PinNativeSpeedTestWait.Until(() => watch.ElapsedMilliseconds >= until);
                OneCopyTestImages.Collect();
            }
            Assert.False(held[0].IsAlive, $"content alive; first pin alive {held[1].IsAlive}, reopened pin alive {held[2].IsAlive}");
            Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenSnapshotCloseAndReopen(PinManager manager, int sources)
    {
        BitmapSource picture = OneCopyTestImages.Opaque("Pbgra32", 80, 60);
        PinWindow pin = manager.PinImage(picture);
        PinManager.ClosedPin snapshot = manager.SnapshotOpenPins().Single();
        pin.Close();
        PinWindow again = manager.RestoreSessionPin(snapshot);
        Assert.True(again.IsNativeHosted);
        Assert.True(again.IsPictureBitmapDropped);
        Assert.Same(snapshot.Content, again.PinnedContent);
        // One allocation for both windows of the same picture.
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
        OneCopyTestImages.AssertSame(picture, again.PinnedContent.Image);
        return [new WeakReference(snapshot.Content), new WeakReference(pin), new WeakReference(again)];
    }

    private sealed record Readings(List<string> Names, List<Seen> Seen, byte[] ClosedPng, byte[] SessionPng, long Rebuilds,
        string ClosedRestore, string SessionRestore);

    /// <summary>
    /// What a reader can observe of a bitmap: format, size, resolution, bytes, and its PNG and BMP files.
    /// A reading that fails is recorded by its exception type, so a failure must also be the same on both pins.
    /// </summary>
    private sealed record Seen(PixelFormat Format, int Width, int Height, double DpiX, double DpiY, bool Frame,
        string Pixels, string Png, string PngWithSource, string Bmp)
    {
        internal static Seen Of(BitmapSource image) => new(image.Format, image.PixelWidth, image.PixelHeight,
            image.DpiX, image.DpiY, image is BitmapFrame, Try(() => OneCopyTestImages.Raw(image)),
            Try(() => ImageCodec.EncodePng(image)), Try(() => ImageCodec.EncodePng(image, SourceUrl)),
            Try(() => OneCopyTestImages.Bmp(image)));

        internal static string Try(Func<byte[]> read)
        {
            try { return Convert.ToBase64String(read()); }
            catch (InvalidOperationException error) { return "throws " + error.GetType().Name; }
        }
    }

    private static Readings ReadEverything(BitmapSource picture, bool lightweight)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = OwnedTestDirectory.Create("pin-one-copy-session-");
        var settings = new PinSettings { LightweightImagePins = lightweight, RestoreOpenPinsOnLaunch = true };
        var names = new List<string>();
        var seen = new List<Seen>();
        Seen? copied = null;
        string? copiedPng = null;
        byte[]? closedPng = null;
        var manager = new PinManager(() => settings, null,
            (image, url, _, _, cache) =>
            {
                // As ClipboardImageService: the PNG through the pin's cache, then the bitmap itself.
                copiedPng = Seen.Try(() => cache is null ? ImageCodec.EncodePng(image, url) : cache.GetOrEncode(image, url, ImageCodec.EncodePng));
                copied = Seen.Of(image);
                return Task.FromResult(true);
            },
            static _ => Task.FromResult(true), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
            BeforeShowForTest = static pin => pin.ShowActivated = false,
        };
        // The closed PNG comes from the copy's cache or from a new encoding; either way it passes through here.
        manager.DecodeClosedForTest = encoded =>
        {
            closedPng = encoded.Png;
            return encoded.Decode();
        };
        long rebuilds = PinPicture.RebuildCount;
        string closedState = string.Empty, sessionState = string.Empty;
        try
        {
            var store = new PinSessionStore(root, NullLogger.Instance);
            byte[] sessionPng;
            PinWindow pin;
            using (var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance))
            {
                Wait(service.RestoreAsync());
                pin = manager.PinImage(picture);
                Assert.Equal(lightweight, pin.IsNativeHosted);
                Assert.Equal(lightweight, pin.IsPictureBitmapDropped);
                if (lightweight) Assert.Null(pin.ImageSourceForTest);
                void After(string name, Seen? value)
                {
                    names.Add(name);
                    seen.Add(value ?? throw new InvalidOperationException(name + " was not read."));
                    if (!lightweight) return;
                    // Nothing the reader built stays referenced, so the pin holds its native source alone.
                    OneCopyTestImages.Collect();
                    Assert.True(pin.IsPictureBitmapDropped, name);
                    Assert.Null(pin.PinnedContent.Picture.Peek);
                    Assert.Null(pin.ImageSourceForTest);
                }

                pin.HandleKey(Key.C, ModifierKeys.Control);
                PinNativeSpeedTestWait.Until(() => copied is not null);
                After("copy", copied);
                After("copy png", new Seen(PixelFormats.Default, 0, 0, 0, 0, false, string.Empty,
                    copiedPng!, string.Empty, string.Empty));
                // A second copy encodes again on the light pin (the first bitmap is gone) and gives the same file.
                copied = null;
                pin.HandleKey(Key.C, ModifierKeys.Control);
                PinNativeSpeedTestWait.Until(() => copied is not null);
                After("second copy", copied);

                Seen? saved = null;
                pin.SaveRequested += (_, e) => saved ??= Seen.Of(e.Image);
                pin.HandleKey(Key.S, ModifierKeys.Control);
                PinNativeSpeedTestWait.Until(() => saved is not null);
                After("save", saved);

                Seen? recognized = null;
                pin.OcrRequested += (_, image) => recognized = Seen.Of(image);
                pin.OnNativeCtrlDoubleClick();
                After("text recognition", recognized);

                // The tray's open-pin thumbnail and other readers of the content's bitmap.
                After("content image", ReadContent(pin));

                Assert.True(service.SaveNow().IsComplete);
                sessionPng = Assert.Single(store.Load()).PngBytes!;
                After("session save", Seen.Of(ImageCodec.TryDecodePng(sessionPng)!));
            }

            // The store keeps that save: a disposed service saves nothing more.
            pin.CloseWithExit();
            PinNativeSpeedTestWait.Until(() => pin.IsClosed);
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            PinWindow reopened = Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
            Assert.NotNull(closedPng);
            closedState = State(reopened);
            ReadRestored("closed restore", reopened);
            manager.CloseAllForExit();
            using (var restoredService = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance))
            {
                Wait(restoredService.RestoreAsync());
                PinWindow restored = Assert.Single(manager.SnapshotOpenPinWindows());
                sessionState = State(restored);
                ReadRestored("session restore", restored);
                manager.CloseAllForExit();
            }
            return new Readings(names, seen, closedPng!, sessionPng, PinPicture.RebuildCount - rebuilds, closedState, sessionState);

            // A reopened pin's picture was decoded on a worker; its readers are compared as they behave today.
            void ReadRestored(string name, PinWindow restored)
            {
                names.Add(name);
                seen.Add(ReadContent(restored));
                copied = null;
                restored.HandleKey(Key.C, ModifierKeys.Control);
                PinNativeSpeedTestWait.Until(() => copied is not null);
                names.Add(name + " copy");
                seen.Add(copied!);
                names.Add(name + " copy png");
                seen.Add(new Seen(PixelFormats.Default, 0, 0, 0, 0, false, string.Empty, copiedPng!, string.Empty, string.Empty));
                if (!restored.IsPictureBitmapDropped) return;
                OneCopyTestImages.Collect();
                Assert.Null(restored.PinnedContent.Picture.Peek);
            }
        }
        finally
        {
            manager.CloseAllForExit();
            OwnedTestDirectory.Delete(root);
        }
    }

    private static string State(PinWindow pin) =>
        (pin.IsNativeHosted ? "lightweight" : "standard") + (pin.IsPictureBitmapDropped ? ", one copy" : ", both copies");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Seen ReadContent(PinWindow pin) => Seen.Of(pin.PinnedContent.Image);

    private static void Wait(Task task)
    {
        PinNativeSpeedTestWait.Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Run(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        NativePinWindow.ReadWindowScaleForTest = static _ => null;
        try
        {
            Assert.Equal(0, NativePinWindow.LiveWindowCount);
            test();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    private static PinManager Manager(PinSettings settings) =>
        new(() => settings, NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };
}
