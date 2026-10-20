using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// The draft encoder copies a shared frozen bitmap on its own STA while the UI thread renders it.
/// It once copied through <see cref="WriteableBitmap"/>, whose constructor holds the source's lock
/// while it creates the thread's MediaContext (WPF's process-wide composition lock); the UI thread
/// takes the same locks in the opposite order, so drawing during the first autosave froze the
/// editor for good. These tests pin the copy's bytes to the old copy's and prove the deadlock gone.
/// </summary>
public sealed class EditDraftEncoderDeadlockTests
{
    private const string ChildFlag = "MYCAPTURE_DRAFT_ENCODER_STRESS_CHILD";
    private const string ReceiptVariable = "MYCAPTURE_DRAFT_ENCODER_STRESS_RECEIPT";
    private readonly ITestOutputHelper _output;

    public EditDraftEncoderDeadlockTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string> Formats() =>
    [
        "Bgra32", "Pbgra32", "Bgr32", "Bgr24", "Rgb24", "Bgr555", "Bgr565", "Bgr101010",
        "Gray2", "Gray4", "Gray8", "Gray16", "Gray32Float", "BlackWhite",
        "Indexed1", "Indexed2", "Indexed4", "Indexed8",
        "Rgb48", "Rgba64", "Prgba64", "Rgb128Float", "Rgba128Float", "Prgba128Float", "Cmyk32",
    ];

    [Theory]
    [MemberData(nameof(Formats))]
    public void CopiedPixels_EncodeTheSamePngBytesAsTheWriteableBitmapCopy(string format) => StaTestHost.Run(() =>
    {
        foreach ((int width, int height, double dpiX, double dpiY) in new[] { (17, 9, 120.0, 144.0), (33, 5, 96.0, 96.0) })
        {
            BitmapSource source = Source(format, width, height, dpiX, dpiY);
            AssertSameAsPreviousCopy(source);
        }
    });

    [Fact]
    public void DecodedTransformedAndRenderedSources_EncodeTheSamePngBytesAsTheWriteableBitmapCopy() => StaTestHost.Run(() =>
    {
        BitmapSource pixels = Source("Bgra32", 23, 11, 120, 144);
        // Decoded frames, as recovery and pasted image assets produce them, with and without delayed decoding.
        foreach (BitmapEncoder encoder in new BitmapEncoder[] { new PngBitmapEncoder(), new GifBitmapEncoder(), new JpegBitmapEncoder(), new BmpBitmapEncoder() })
        {
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            foreach (BitmapCacheOption cache in new[] { BitmapCacheOption.OnLoad, BitmapCacheOption.Default })
            {
                BitmapFrame decoded = BitmapDecoder.Create(new MemoryStream(buffer.ToArray()), BitmapCreateOptions.PreservePixelFormat, cache).Frames[0];
                decoded.Freeze();
                AssertSameAsPreviousCopy(decoded);
            }
        }

        // A pasted image asset, decoded as the image store decodes it.
        var asset = new BitmapImage();
        using (var stream = new MemoryStream())
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(pixels));
            png.Save(stream);
            stream.Position = 0;
            asset.BeginInit();
            asset.CacheOption = BitmapCacheOption.OnLoad;
            asset.StreamSource = stream;
            asset.EndInit();
        }
        asset.Freeze();
        AssertSameAsPreviousCopy(asset);

        // A frame that carries metadata and colour contexts.
        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/tEXt/{str=Description}", "Owned synthetic recovery draft");
        BitmapFrame described = BitmapFrame.Create(pixels, null, metadata, new ReadOnlyCollection<ColorContext>([new ColorContext(PixelFormats.Bgra32)]));
        described.Freeze();
        AssertSameAsPreviousCopy(described);

        // The editor's rotated base is a frozen TransformedBitmap; crops and conversions chain the same way.
        var rotated = new TransformedBitmap(pixels, new RotateTransform(90));
        rotated.Freeze();
        AssertSameAsPreviousCopy(rotated);
        var cropped = new CroppedBitmap(pixels, new Int32Rect(3, 2, 9, 5));
        cropped.Freeze();
        AssertSameAsPreviousCopy(cropped);
        var converted = new FormatConvertedBitmap(pixels, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        AssertSameAsPreviousCopy(converted);

        // Flattened and live-edited pixels.
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
            context.DrawEllipse(Brushes.OrangeRed, new Pen(Brushes.Navy, 2), new Point(12, 6), 9, 4);
        var rendered = new RenderTargetBitmap(23, 11, 120, 144, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        AssertSameAsPreviousCopy(rendered);
        var writable = new WriteableBitmap(pixels);
        writable.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 3, 7, 19, 255 }, 4, 0);
        writable.Freeze();
        AssertSameAsPreviousCopy(writable);
    });

    [Fact]
    public void LargeCapture_EncodesTheSamePngBytesAsTheWriteableBitmapCopy() => StaTestHost.Run(() =>
    {
        const int width = 7680, height = 2160;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(width * height * 4);
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 31 / 4096 + (index / (width * 4)));
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        AssertSameAsPreviousCopy(source);
    });

    /// <summary>
    /// The root cause, checked directly: an encode on a fresh STA never creates that thread's
    /// MediaContext, so it never asks for the composition lock while it holds a bitmap's lock.
    /// </summary>
    [Fact]
    public async Task EncoderThread_NeverCreatesAMediaContext()
    {
        // Shared frozen sources of the kinds the editor encodes, made on another STA as the UI makes them.
        BitmapSource[] sources = await StaThreadTask.RunAsync(() =>
        {
            BitmapSource plain = Source("Bgra32", 64, 48, 96, 96);
            var rotated = new TransformedBitmap(plain, new RotateTransform(90));
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen()) context.DrawRectangle(Brushes.Teal, null, new Rect(4, 4, 20, 10));
            var rendered = new RenderTargetBitmap(64, 48, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            // A pasted image asset, decoded as the image store decodes it.
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(plain));
            using var buffer = new MemoryStream();
            png.Save(buffer);
            buffer.Position = 0;
            var asset = new BitmapImage();
            asset.BeginInit();
            asset.CacheOption = BitmapCacheOption.OnLoad;
            asset.StreamSource = buffer;
            asset.EndInit();
            BitmapSource[] all = [plain, rotated, rendered, asset];
            foreach (BitmapSource source in all) source.Freeze();
            return all;
        });
        PropertyInfo mediaContextSlot = typeof(Dispatcher).GetProperty("Reserved0", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Dispatcher).FullName, "Reserved0");
        // The control: the previous WriteableBitmap copy does put a MediaContext in that slot.
        Assert.True(await StaThreadTask.RunAsync(() =>
        {
            var writable = new WriteableBitmap(sources[0]);
            writable.Freeze();
            return mediaContextSlot.GetValue(Dispatcher.CurrentDispatcher) is not null;
        }), "The probe for the thread's MediaContext no longer sees one; update it before trusting the check below.");
        foreach (BitmapSource source in sources)
        {
            Assert.True(await StaThreadTask.RunAsync(() =>
            {
                byte[] png = AnnotationEditorControl.EncodeDraftBitmap(source);
                return png.Length > 0 && (Dispatcher.FromThread(Thread.CurrentThread) is not { } dispatcher
                    || mediaContextSlot.GetValue(dispatcher) is null);
            }), $"Encoding a {source.GetType().Name} created a MediaContext, which takes the composition lock the UI thread holds while it renders.");
        }
    }

    /// <summary>
    /// A UI thread renders a large frozen bitmap again every frame, as the editor surface does
    /// while the user draws, and the draft encoder encodes that same bitmap over and over on fresh
    /// STA threads, as the first autosave does. The stress runs in a child process: a real
    /// deadlock keeps WPF's process-wide composition lock forever, which would hang every later
    /// WPF test here, so the child reports it, ends itself and this test fails.
    /// </summary>
    [Fact]
    public async Task EncodingWhileTheUiRendersTheSameBitmap_NeverDeadlocks()
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) == "1")
        {
            string receipt = Environment.GetEnvironmentVariable(ReceiptVariable) ?? throw new IOException("Missing parent test receipt.");
            string result = RenderAndEncodeConcurrently(7680, 2160, encodes: 24, limit: TimeSpan.FromSeconds(15));
            File.WriteAllText(receipt, result);
            // A deadlocked UI thread cannot shut its dispatcher down, and process exit would wait on it.
            if (!result.StartsWith("PASS", StringComparison.Ordinal)) Process.GetCurrentProcess().Kill();
            return;
        }

        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
        Assert.True(File.Exists(dotnet), "The test runtime must provide its dotnet host.");
        string receiptRoot = TestRecycleBin.CreateTempSubdirectory("mc-draft-encoder-stress-").FullName;
        string receiptPath = Path.Combine(receiptRoot, "result.txt");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(EditDraftEncoderDeadlockTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(EditDraftEncoderDeadlockTests).FullName
            + "." + nameof(EncodingWhileTheUiRendersTheSameBitmap_NeverDeadlocks));
        start.ArgumentList.Add("--Logger:console;verbosity=normal");
        start.Environment[ChildFlag] = "1";
        start.Environment[ReceiptVariable] = receiptPath;
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using Process child = Process.Start(start) ?? throw new IOException("Could not start the draft encoder stress process.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        try
        {
            // Only guards against a child that hangs outside the stress loop's own limits.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            string console = await output + Environment.NewLine + await error;
            string result = File.Exists(receiptPath) ? File.ReadAllText(receiptPath) : "NO RESULT";
            _output.WriteLine(result);
            Assert.True(result.StartsWith("PASS", StringComparison.Ordinal), result + Environment.NewLine + console);
            Assert.True(child.ExitCode == 0, console);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Runs the stress in this process and describes the outcome; never throws for a deadlock.</summary>
    internal static string RenderAndEncodeConcurrently(int width, int height, int encodes, TimeSpan limit)
    {
        var ready = new TaskCompletionSource<(Dispatcher Dispatcher, BitmapSource Bitmap, Window Window, EventHandler Tick)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long frames = 0;
        var ui = new Thread(() =>
        {
            try
            {
                byte[] pixels = GC.AllocateUninitializedArray<byte>(width * height * 4);
                pixels.AsSpan().Fill(0x5A);
                BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
                bitmap.Freeze();
                var surface = new EveryFrameBitmap(bitmap);
                var window = new Window
                {
                    Left = -10000, Top = -10000, Width = 320, Height = 180, WindowStyle = WindowStyle.None,
                    ShowActivated = false, ShowInTaskbar = false, Content = surface,
                };
                EventHandler tick = (_, _) => { surface.InvalidateVisual(); Interlocked.Increment(ref frames); };
                CompositionTarget.Rendering += tick;
                window.Show();
                ready.SetResult((Dispatcher.CurrentDispatcher, bitmap, window, tick));
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Draft encoder stress UI",
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        if (!ready.Task.Wait(limit)) return "FAIL: the rendering window did not start.";
        (Dispatcher dispatcher, BitmapSource shared, Window shown, EventHandler handler) = ready.Task.Result;
        var clock = Stopwatch.StartNew();
        while (Interlocked.Read(ref frames) < 5)
        {
            if (clock.Elapsed > limit) return "FAIL: the window rendered no frames.";
            Thread.Sleep(10);
        }

        long framesBefore = Interlocked.Read(ref frames);
        var encodeMs = new List<double>();
        for (int index = 1; index <= encodes; index++)
        {
            var watch = Stopwatch.StartNew();
            Task<byte[]> encode = StaThreadTask.RunAsync(() => AnnotationEditorControl.EncodeDraftBitmap(shared), "MyCapture image draft encoder");
            if (!encode.Wait(limit))
            {
                bool uiAlive = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send).Task.Wait(TimeSpan.FromSeconds(2));
                return $"DEADLOCK: encode {index} of {encodes} did not finish in {limit.TotalSeconds:0} s while the UI thread rendered the same bitmap; UI thread {(uiAlive ? "still responds" : "stopped responding")}.";
            }
            if (encode.Result.Length == 0) return $"FAIL: encode {index} produced no PNG.";
            encodeMs.Add(watch.Elapsed.TotalMilliseconds);
            if (!dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send).Task.Wait(limit))
                return $"DEADLOCK: the UI thread stopped responding after encode {index} of {encodes}.";
        }

        long rendered = Interlocked.Read(ref frames) - framesBefore;
        bool closed = dispatcher.InvokeAsync(() =>
        {
            CompositionTarget.Rendering -= handler;
            shown.Close();
        }).Task.Wait(limit);
        dispatcher.InvokeShutdown();
        bool joined = ui.Join(limit);
        if (!closed || !joined) return "FAIL: the rendering window did not shut down.";
        // Software rendering on a GPU-less runner is slow, but the window must still have kept rendering.
        if (rendered < 10) return $"FAIL: only {rendered} frames rendered during {encodes} encodes; the stress did not overlap rendering.";
        return FormattableString.Invariant(
            $"PASS: {encodes} encodes of {width}x{height} while {rendered} frames rendered the same bitmap; encode ms mean {encodeMs.Average():0}, max {encodeMs.Max():0}; {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    private sealed class EveryFrameBitmap(BitmapSource bitmap) : FrameworkElement
    {
        // A new drawing each frame re-adds the bitmap to the composition channel, as the editor
        // surface does on every pointer move.
        protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawImage(bitmap, new Rect(RenderSize));
    }

    private static void AssertSameAsPreviousCopy(BitmapSource source)
    {
        byte[] previous = EncodePreviousCopy(source);
        byte[] actual = AnnotationEditorControl.EncodeDraftBitmap(source);
        Assert.True(previous.AsSpan().SequenceEqual(actual),
            $"{source.GetType().Name} {source.Format} {source.PixelWidth}x{source.PixelHeight}: {previous.Length} vs {actual.Length} PNG bytes differ.");
    }

    /// <summary>The encoder as it was before this fix: a frozen <see cref="WriteableBitmap"/> copy.</summary>
    private static byte[] EncodePreviousCopy(BitmapSource source)
    {
        var independent = new WriteableBitmap(source);
        independent.Freeze();
        MethodInfo encode = typeof(AnnotationEditorControl).GetMethod("EncodeIndependentDraftBitmap", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AnnotationEditorControl), "EncodeIndependentDraftBitmap");
        return (byte[])encode.Invoke(null, [independent, source])!;
    }

    private static BitmapSource Source(string name, int width, int height, double dpiX, double dpiY)
    {
        PixelFormat format = (PixelFormat)typeof(PixelFormats).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        BitmapPalette? palette = name switch
        {
            "Indexed1" => new BitmapPalette([Colors.Black, Colors.White]),
            "Indexed2" => new BitmapPalette([Colors.Red, Colors.Green, Colors.Blue, Color.FromArgb(96, 255, 255, 0)]),
            "Indexed4" => new BitmapPalette(Enumerable.Range(0, 16).Select(index => Color.FromArgb((byte)(255 - index * 9), (byte)(index * 16), (byte)(255 - index * 16), (byte)(index * 5))).ToList()),
            "Indexed8" => new BitmapPalette([Colors.Red, Color.FromArgb(128, 0, 255, 0), Colors.Blue, Colors.Transparent]),
            _ => null,
        };
        int stride = (width * format.BitsPerPixel + 7) / 8;
        byte[] pixels = Enumerable.Range(0, stride * height).Select(index => (byte)(index * 37 + 17)).ToArray();
        if (name == "Indexed8") for (int index = 0; index < pixels.Length; index++) pixels[index] %= 4;
        if (name is "Rgb128Float" or "Rgba128Float" or "Prgba128Float" or "Gray32Float")
        {
            // Finite channel values in [0, 1], so conversions stay well defined.
            float[] values = Enumerable.Range(0, pixels.Length / 4).Select(index => (index % 11) / 10f).ToArray();
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(pixels);
        }
        if (name is "Bgra32" or "Pbgra32")
        {
            for (int index = 0; index < pixels.Length; index += 4)
            {
                byte alpha = (byte)((index / 4 % 4) * 64);
                pixels[index + 3] = alpha;
                if (name == "Pbgra32")
                    for (int channel = 0; channel < 3; channel++) pixels[index + channel] = (byte)(pixels[index + channel] * alpha / 255);
            }
        }
        BitmapSource source = BitmapSource.Create(width, height, dpiX, dpiY, format, palette, pixels, stride);
        source.Freeze();
        return source;
    }
}
