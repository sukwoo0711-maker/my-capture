using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Pictures reopened from the closed history and from the saved session, through the product's own paths:
/// their PNG is decoded on a pool thread, so the frame's decoder belongs to that thread. With the setting on
/// they open as lightweight pins with one copy, like a picture pinned for the first time.
/// </summary>
public sealed class PinLightweightRestoreWindowTests(ITestOutputHelper output)
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINLIGHT-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    /// <summary>
    /// As a GDI screen copy inside one monitor: Bgr32 with the fourth byte 255 everywhere. With
    /// <paramref name="outsideMonitor"/>, a band on the left has colour and fourth byte 0, as the part of the
    /// virtual desktop that no monitor covers.
    /// </summary>
    internal static BitmapSource CaptureLike(int width, int height, bool outsideMonitor = false)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * 4);
                if (outsideMonitor && x < width / 4) continue;
                pixels[offset] = (byte)((x * 29 + y * 7) % 256);
                pixels[offset + 1] = (byte)((x * 11 + y * 31) % 256);
                pixels[offset + 2] = (byte)((x * 3 + y * 17 + 40) % 256);
                pixels[offset + 3] = 255;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    [Fact]
    public void ACaptureInsideAMonitorKeepsOneCopy() => Run(() =>
    {
        var manager = Manager(new PinSettings { LightweightImagePins = true }, NullLogger.Instance);
        try
        {
            PinWindow pin = manager.PinImage(CaptureLike(160, 90));
            Assert.True(pin.IsNativeHosted);
            Assert.True(pin.IsPictureBitmapDropped);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void ACaptureWithAnAreaOutsideTheMonitorsKeepsBothCopiesAndStaysLightweight() => Run(() =>
    {
        BitmapSource capture = CaptureLike(160, 90, outsideMonitor: true);
        var manager = Manager(new PinSettings { LightweightImagePins = true }, NullLogger.Instance);
        try
        {
            PinWindow pin = manager.PinImage(capture);
            Assert.True(pin.IsNativeHosted);
            Assert.False(pin.IsPictureBitmapDropped);
            Assert.Same(capture, pin.PinnedContent.Image);
        }
        finally { manager.CloseAllForExit(); }
    });

    public static TheoryData<string> PngSources => new()
    {
        "Bgr32", "Bgra32-half-alpha", "Pbgra32-opaque", "Gray8", "Indexed8", "Indexed4", "BlackWhite", "Gray16",
        "Rgb48", "Rgba64", "Bgr565", "Bgr24",
    };

    /// <summary>
    /// What the product's PNG round trip can hand a restored pin: each source format encoded by ImageCodec and
    /// decoded on a thread that has ended. The native source holds exactly what reading the same PNG decoded on
    /// this thread gave before (a WPF conversion of the frame), and no part of the frame's thread is touched.
    /// </summary>
    [Theory]
    [MemberData(nameof(PngSources))]
    public void AFrameDecodedOnAnotherThreadIsReadInItsOwnFormat(string kind) => StaTestHost.Run(() =>
    {
        BitmapSource source = PngSource(kind, 37, 23);
        byte[] png = ImageCodec.EncodePng(source);
        BitmapSource? elsewhere = null;
        var decoder = new Thread(() => elsewhere = ImageCodec.TryDecodePng(png));
        decoder.Start();
        decoder.Join();
        Assert.NotNull(elsewhere);
        BitmapSource here = ImageCodec.TryDecodePng(png)!;
        Assert.Equal(here.Format, elsewhere!.Format);
        output.WriteLine($"{kind} -> decoded {elsewhere.Format}");

        using NativePinPixelSource native = NativePinPixelSource.FromBitmap(elsewhere);
        Assert.Equal(Reference(here), Bytes(native));
        bool opaqueExact = here.Format == PixelFormats.Bgr24
            || (here.Format == PixelFormats.Bgra32 && kind is "Pbgra32-opaque");
        Assert.Equal(opaqueExact, native.IsExactOpaqueCopy);
        if (!native.IsExactOpaqueCopy) return;
        // The picture is made on this thread from the frame decoded on the other one, as a pin's content can be.
        // A rebuild from the native source is the frame again, encodable on this thread.
        var picture = new PinPicture(elsewhere);
        Assert.True(picture.TryDrop(native));
        OneCopyTestImages.AssertSame(here, picture.Bitmap);
        Assert.Equal(ImageCodec.EncodePng(here), ImageCodec.EncodePng(picture.Bitmap));
    });

    /// <summary>
    /// A copied capture pinned with F3: the clipboard reader decodes its PNG on a pool thread and the pin's
    /// content is made on another. The reader read the frame's metadata where it decoded, so the picture can
    /// still give up its bitmap and build the same frame again.
    /// </summary>
    [Fact]
    public void AClipboardPngDecodedOnAnotherThreadKeepsOneCopy() => StaTestHost.Run(() =>
    {
        byte[] png = ImageCodec.EncodePng(CaptureLike(37, 23), "https://example.invalid/page");
        BitmapSource? elsewhere = null;
        var decoder = new Thread(() => elsewhere = ClipboardImageReader.TryDecodePngPayload(png));
        decoder.Start();
        decoder.Join();
        Assert.NotNull(elsewhere);
        BitmapSource here = ClipboardImageReader.TryDecodePngPayload(png)!;

        using NativePinPixelSource native = NativePinPixelSource.FromBitmap(elsewhere!);
        Assert.True(native.IsExactOpaqueCopy);
        var picture = new PinPicture(elsewhere!);
        Assert.True(picture.TryDrop(native));
        OneCopyTestImages.AssertSame(here, picture.Bitmap);
        Assert.Equal(ImageCodec.EncodePng(here), ImageCodec.EncodePng(picture.Bitmap));
    });

    public static TheoryData<string> RestorePaths => new() { "closed history", "session" };

    [Theory]
    [MemberData(nameof(RestorePaths))]
    public void AReopenedCaptureIsALightweightPinWithOneCopy(string path) => Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        PinManager.ResetNativeFailureLogForTest();
        var log = new RecordingLog();
        var settings = new PinSettings { LightweightImagePins = true, RestoreOpenPinsOnLaunch = true };
        var manager = Manager(settings, log);
        BitmapSource capture = CaptureLike(320, 180);
        byte[] png = ImageCodec.EncodePng(capture);
        BitmapSource expected = ImageCodec.TryDecodePng(png)!;
        int decodingThread = Environment.CurrentManagedThreadId;
        manager.DecodeClosedForTest = encoded =>
        {
            decodingThread = Environment.CurrentManagedThreadId;
            return encoded.Decode();
        };
        string root = OwnedTestDirectory.Create("pin-light-restore-");
        try
        {
            PinWindow reopened = path == "closed history"
                ? ReopenFromHistory(manager, capture)
                : ReopenFromSession(manager, capture, settings, root, thread => decodingThread = thread);
            Assert.NotEqual(Environment.CurrentManagedThreadId, decodingThread);
            Assert.Equal(PixelFormats.Bgr24, reopened.PinnedContent.Picture.Format);
            Assert.True(reopened.IsNativeHosted);
            Assert.True(reopened.IsPictureBitmapDropped);
            Assert.Null(reopened.ImageSourceForTest);
            Assert.DoesNotContain(log.Entries, entry => entry.Contains("Lightweight pin unavailable", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Entries, entry => entry.StartsWith("Warning", StringComparison.Ordinal)
                || entry.StartsWith("Error", StringComparison.Ordinal));

            // The same pixels and files as the PNG decoded here; nothing built for the reading stays.
            AssertShowsTheSamePicture(reopened, expected);
            OneCopyTestImages.Collect();
            Assert.Null(reopened.PinnedContent.Picture.Peek);

            // Switching to the standard window still works: it builds the bitmap once and keeps it.
            reopened.ActivateAfterSwitchForTest = static _ => { };
            Assert.True(reopened.SwitchToWpf(PinSwitchReason.Command));
            PinNativeSpeedTestWait.Until(() => !reopened.IsHostSwitchPending);
            Assert.False(reopened.IsPictureBitmapDropped);
            OneCopyTestImages.AssertSame(expected, Assert.IsAssignableFrom<BitmapSource>(reopened.ImageSourceForTest));
        }
        finally
        {
            manager.CloseAllForExit();
            OwnedTestDirectory.Delete(root);
        }
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertShowsTheSamePicture(PinWindow reopened, BitmapSource expected)
    {
        BitmapSource shown = reopened.PinnedContent.Image;
        Assert.IsAssignableFrom<BitmapFrame>(shown);
        OneCopyTestImages.AssertSame(expected, shown);
        Assert.Equal(ImageCodec.EncodePng(expected), ImageCodec.EncodePng(shown));
        using NativePinPixelSource.Lease lease = reopened.NativeWindowForTest!.Source!.AcquireLease();
        Assert.Equal(Reference(expected), Bytes(lease));
    }

    private static PinWindow ReopenFromHistory(PinManager manager, BitmapSource capture)
    {
        PinWindow pin = manager.PinImage(capture);
        pin.CloseWithExit();
        PinNativeSpeedTestWait.Until(() => pin.IsClosed);
        PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
        return Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
    }

    private static PinWindow ReopenFromSession(PinManager manager, BitmapSource capture, PinSettings settings, string root,
        Action<int> decodedOn)
    {
        var store = new PinSessionStore(root, NullLogger.Instance);
        using (var service = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance))
        {
            Wait(service.RestoreAsync());
            manager.PinImage(capture);
            Assert.True(service.SaveNow().IsComplete);
        }
        manager.CloseAllForExit();
        using var restoring = new PinSessionService(manager, store, () => settings, Dispatcher.CurrentDispatcher, NullLogger.Instance,
            decode: bytes =>
            {
                decodedOn(Environment.CurrentManagedThreadId);
                return ImageCodec.TryDecodePng(bytes);
            });
        Wait(restoring.RestoreAsync());
        return Assert.Single(manager.SnapshotOpenPinWindows());
    }

    private static BitmapSource PngSource(string kind, int width, int height)
    {
        BitmapSource opaque = OneCopyTestImages.Make(PixelFormats.Bgra32, width, height, static (_, _) => 255);
        BitmapSource Converted(PixelFormat format, BitmapPalette? palette = null)
        {
            var converted = new FormatConvertedBitmap(opaque, format, palette, 0);
            converted.Freeze();
            return converted;
        }
        return kind switch
        {
            "Bgr32" => CaptureLike(width, height),
            "Bgra32-half-alpha" => OneCopyTestImages.Make(PixelFormats.Bgra32, width, height, static (x, _) => (byte)(x * 7 % 256)),
            "Pbgra32-opaque" => OneCopyTestImages.Make(PixelFormats.Pbgra32, width, height, static (_, _) => 255),
            "Gray8" => Converted(PixelFormats.Gray8),
            "Indexed8" => Indexed(PixelFormats.Indexed8, 256, width, height),
            "Indexed4" => Indexed(PixelFormats.Indexed4, 16, width, height),
            "BlackWhite" => Converted(PixelFormats.BlackWhite),
            "Gray16" => Converted(PixelFormats.Gray16),
            "Rgb48" => Converted(PixelFormats.Rgb48),
            "Rgba64" => Converted(PixelFormats.Rgba64),
            "Bgr565" => Converted(PixelFormats.Bgr565),
            "Bgr24" => Converted(PixelFormats.Bgr24),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    // A palette picture with opaque entries of distinct colours, an index per pixel from a pattern.
    private static BitmapSource Indexed(PixelFormat format, int colours, int width, int height)
    {
        var palette = new BitmapPalette(Enumerable.Range(0, colours)
            .Select(index => Color.FromRgb((byte)(index * 37), (byte)(index * 91), (byte)(255 - index * 13))).ToList());
        int bits = format.BitsPerPixel;
        int stride = (width * bits + 7) / 8;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (x * 3 + y * 5) % colours;
                int bit = x * bits;
                pixels[y * stride + bit / 8] |= (byte)(index << (8 - bits - bit % 8));
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, format, palette, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The source as increment 1 read a bitmap that its thread could convert: 32-bit formats as they are,
    /// any other through a WPF conversion to Pbgra32, then the same normalization.
    /// </summary>
    internal static byte[] Reference(BitmapSource image)
    {
        bool direct = image.Format == PixelFormats.Pbgra32 || image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32;
        BitmapSource readable = direct ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int stride = image.PixelWidth * 4;
        byte[] pixels = new byte[stride * image.PixelHeight];
        readable.CopyPixels(pixels, stride, 0);
        for (int y = 0; y < image.PixelHeight; y++)
            NativePinPixelSource.Normalize(pixels.AsSpan(y * stride, stride), readable.Format);
        return pixels;
    }

    private static byte[] Bytes(NativePinPixelSource source)
    {
        using NativePinPixelSource.Lease lease = source.AcquireLease();
        return Bytes(lease);
    }

    private static byte[] Bytes(NativePinPixelSource.Lease lease)
    {
        byte[] bytes = new byte[lease.ByteLength];
        System.Runtime.InteropServices.Marshal.Copy(lease.Pointer, bytes, 0, bytes.Length);
        return bytes;
    }

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
            test();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });

    private static PinManager Manager(PinSettings settings, ILogger log) =>
        new(() => settings, log)
        {
            ReadPlacementMonitorForTest = static () => Offscreen,
            ReadRestoreMonitorsForTest = static () => [Offscreen],
            ActivatePinForTest = static _ => { },
        };

    internal sealed class RecordingLog : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _entries = [];

        internal IReadOnlyList<string> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : " [" + exception.GetType().Name + "]"));
        }
    }
}
