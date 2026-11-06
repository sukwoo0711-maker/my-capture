using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Media Foundation starts once per process and shuts down once at exit (#243). Encoders and
/// readers used to start and stop the whole platform each time they were created and released,
/// and on a Windows 10 PC each such cycle left kernel handles behind: 100 encodes grew the handle
/// count by 252–384, an 8-hour soak by 8,221.
/// </summary>
public sealed class MediaFoundationPlatformLifetimeTests
{
    private const string ChildFlag = "MYCAPTURE_MF_LIFETIME_CHILD";
    private const string ReceiptVariable = "MYCAPTURE_MF_LIFETIME_RECEIPT";
    private const string ReceiptFileName = "receipt.json";
    private const int Width = 640, Height = 360, Fps = 30, FramesPerClip = 30;
    private const int WarmupCycles = 3, MeasuredCycles = 40;

    // MF_E_SHUTDOWN: what Media Foundation answers while nobody holds MFStartup.
    private const int PlatformShutDown = unchecked((int)0xC00D3E85);

    private static readonly (string Name, int X, int Y, byte B, byte G, byte R)[] Patches =
    [
        ("red", 80, 80, 40, 40, 220),
        ("green", 320, 80, 40, 200, 40),
        ("blue", 560, 80, 220, 40, 40),
    ];

    private readonly ITestOutputHelper _output;

    public MediaFoundationPlatformLifetimeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SequentialUses_StartThePlatformOnce_AndExitShutsItDownOnce()
    {
        var native = new FakeNative();
        MediaFoundationLifetime lifetime = native.Create();
        Assert.False(lifetime.IsStarted);

        for (int use = 0; use < 100; use++)
        {
            using MediaFoundationLifetime.Lease lease = lifetime.Acquire();
            Assert.True(lifetime.IsStarted);
        }

        // Returning the last lease while the app runs keeps the platform: that cycle leaked handles.
        Assert.Equal((1, 0), (native.Startups, native.Shutdowns));
        Assert.True(lifetime.IsStarted);
        Assert.Equal(0, lifetime.ActiveLeases);

        lifetime.Shutdown();
        Assert.Equal((1, 1), (native.Startups, native.Shutdowns));
        Assert.False(lifetime.IsStarted);
        Assert.Equal((1, 1), (lifetime.StartupCount, lifetime.ShutdownCount));

        lifetime.Shutdown();
        Assert.Equal(1, native.Shutdowns);
    }

    [Fact]
    public void ExitWhileARecordingAndAnEditAreOpen_ShutsDownOnlyAfterTheLastOneCloses()
    {
        var native = new FakeNative();
        MediaFoundationLifetime lifetime = native.Create();
        MediaFoundationLifetime.Lease recording = lifetime.Acquire();
        MediaFoundationLifetime.Lease edit = lifetime.Acquire();
        Assert.Equal((1, 2), (native.Startups, lifetime.ActiveLeases));

        lifetime.Shutdown();
        Assert.Equal(0, native.Shutdowns);
        recording.Dispose();
        Assert.Equal(0, native.Shutdowns);
        edit.Dispose();
        Assert.Equal((1, 1), (native.Startups, native.Shutdowns));

        // Disposing a lease twice returns it once.
        edit.Dispose();
        recording.Dispose();
        Assert.Equal((1, 0), (native.Shutdowns, lifetime.ActiveLeases));
    }

    [Fact]
    public void FailedStartup_HoldsNothing_AndExitHasNothingToShutDown()
    {
        // MF_E_BAD_STARTUP_VERSION.
        var native = new FakeNative { StartupResult = unchecked((int)0xC00D36E3) };
        MediaFoundationLifetime lifetime = native.Create();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => lifetime.Acquire());
        Assert.Contains("0xC00D36E3", failure.Message, StringComparison.Ordinal);
        Assert.Equal((0, 0, false), (lifetime.StartupCount, lifetime.ActiveLeases, lifetime.IsStarted));

        // An exception from the native call itself (mfplat.dll missing on an N edition) is the same.
        var missing = new MediaFoundationLifetime(() => throw new DllNotFoundException("mfplat.dll"), () => 0);
        Assert.Throws<DllNotFoundException>(() => missing.Acquire());
        Assert.Equal((0, 0, false), (missing.StartupCount, missing.ActiveLeases, missing.IsStarted));
        missing.Shutdown();
        Assert.Equal(0, missing.ShutdownCount);

        // The next use retries the startup and keeps the platform as usual.
        native.StartupResult = 0;
        using (lifetime.Acquire())
        {
        }

        Assert.Equal((1, 0), (native.Startups, native.Shutdowns));
        lifetime.Shutdown();
        Assert.Equal((1, 1), (native.Startups, native.Shutdowns));
    }

    [Fact]
    public void UseAfterExitBegan_StartsAgain_AndItsReleaseShutsDownAgain()
    {
        var native = new FakeNative();
        MediaFoundationLifetime lifetime = native.Create();
        using (lifetime.Acquire())
        {
        }

        lifetime.Shutdown();
        Assert.Equal((1, 1), (native.Startups, native.Shutdowns));

        MediaFoundationLifetime.Lease late = lifetime.Acquire();
        Assert.Equal((2, 1), (native.Startups, native.Shutdowns));
        late.Dispose();
        Assert.Equal((2, 2), (native.Startups, native.Shutdowns));
        Assert.False(lifetime.IsStarted);
    }

    [Fact]
    public async Task ConcurrentUses_ShareOneStartup_AndAnExitRacingThemLeavesEveryStartupBalanced()
    {
        var native = new FakeNative();
        MediaFoundationLifetime lifetime = native.Create();
        long uses = 0;
        bool stop = false;
        using var go = new ManualResetEventSlim();
        Task[] workers = Enumerable.Range(0, 6).Select(_ => Task.Factory.StartNew(() =>
        {
            go.Wait();
            while (!Volatile.Read(ref stop))
            {
                using MediaFoundationLifetime.Lease outer = lifetime.Acquire();
                using MediaFoundationLifetime.Lease inner = lifetime.Acquire();
                Interlocked.Increment(ref uses);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        go.Set();
        SpinWait.SpinUntil(() => Interlocked.Read(ref uses) >= 20_000, TimeSpan.FromSeconds(30));
        // While the app runs, concurrent recordings and edits never restart the platform.
        Assert.Equal((1, 0), (native.Startups, native.Shutdowns));

        lifetime.Shutdown();
        long atExit = Interlocked.Read(ref uses);
        SpinWait.SpinUntil(() => Interlocked.Read(ref uses) >= atExit + 20_000, TimeSpan.FromSeconds(30));
        Volatile.Write(ref stop, true);
        await Task.WhenAll(workers);

        Assert.True(Interlocked.Read(ref uses) >= atExit + 20_000, "The workers stopped making progress.");
        Assert.Equal(0, lifetime.ActiveLeases);
        Assert.False(lifetime.IsStarted);
        Assert.Equal(native.Startups, native.Shutdowns);
        Assert.Equal((native.Startups, native.Shutdowns), (lifetime.StartupCount, lifetime.ShutdownCount));
    }

    [Fact]
    public void OnlyThePlatformOwnerCallsMFStartupOrMFShutdown()
    {
        string root = RepositoryRoot();
        var call = new Regex(@"\bMF(Startup|Shutdown)\s*\(", RegexOptions.CultureInvariant);
        string[] callers = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(file => call.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .ToArray();
        Assert.Equal(new[] { "src/MyCapture.Platform/Recording/MediaFoundationPlatform.cs" }, callers);
    }

    [Fact]
    public void AppExit_ShutsMediaFoundationDownAfterTheServicesThatRecordAndExport()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "App.xaml.cs"));
        int exit = source.IndexOf("protected override void OnExit(ExitEventArgs e)", StringComparison.Ordinal);
        Assert.True(exit >= 0, "App.OnExit was not found.");
        int services = source.IndexOf("_services?.Dispose();", exit, StringComparison.Ordinal);
        int shutdown = source.IndexOf("MediaFoundationPlatform.Shutdown();", exit, StringComparison.Ordinal);
        int end = source.IndexOf("base.OnExit(e);", exit, StringComparison.Ordinal);
        Assert.True(services > exit && shutdown > services && end > shutdown,
            $"OnExit must call MediaFoundationPlatform.Shutdown() after _services?.Dispose() (exit {exit}, services {services}, shutdown {shutdown}, end {end}).");
    }

    /// <summary>
    /// The component check from #243 in a fresh process: encode and read back the same 640x360,
    /// 30-frame clip repeatedly, record and read at the same time, take the failure paths, then exit.
    /// A fresh process is the only place where Media Foundation has no other user and its handles
    /// can be counted exactly.
    /// </summary>
    [Fact]
    public async Task RepeatedRecordingAndEditing_KeepsHandlesFlat_AndExitShutsTheStartupDown()
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) == "1")
        {
            string receiptRoot = DiagnosticOutputPaths.Create(
                Environment.GetEnvironmentVariable(ReceiptVariable) ?? throw new IOException("Missing parent test receipt."));
            string work = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(receiptRoot, "clips"));
            Dictionary<string, object?> receipt = RunLifetimeScenario(work);
            File.WriteAllText(DiagnosticOutputPaths.Child(receiptRoot, ReceiptFileName),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
        Assert.True(File.Exists(dotnet), "The test runtime must provide its dotnet host.");
        string root = TestRecycleBin.CreateTempSubdirectory("mc-mf-lifetime-").FullName;
        try
        {
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(MediaFoundationPlatformLifetimeTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(MediaFoundationPlatformLifetimeTests).FullName
                + "." + nameof(RepeatedRecordingAndEditing_KeepsHandlesFlat_AndExitShutsTheStartupDown));
            start.ArgumentList.Add("--Logger:console;verbosity=normal");
            start.Environment[ChildFlag] = "1";
            start.Environment[ReceiptVariable] = root;
            start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            using Process child = Process.Start(start) ?? throw new IOException("Could not start the Media Foundation lifetime process.");
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
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
                string receiptPath = Path.Combine(root, ReceiptFileName);
                string receipt = File.Exists(receiptPath) ? File.ReadAllText(receiptPath) : "NO RECEIPT";
                _output.WriteLine(receipt);
                Assert.True(child.ExitCode == 0, console);
                using JsonDocument document = JsonDocument.Parse(receipt);
                string[] failures = document.RootElement.GetProperty("failures").EnumerateArray()
                    .Select(item => item.GetString() ?? "").ToArray();
                Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            try
            {
                TestRecycleBin.DeleteDirectory(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Runs in the child process; returns the measurements and every failed expectation.</summary>
    private static Dictionary<string, object?> RunLifetimeScenario(string work)
    {
        var failures = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        MediaFoundationLifetime life = MediaFoundationPlatform.Lifetime;
        // Handles by kernel object type, recorded for a failing run to show what grew.
        var census = new Dictionary<string, SortedDictionary<string, int>>();
        int queueBeforeStart = AllocateWorkQueue();
        Expect(life.StartupCount == 0 && !life.IsStarted && life.ActiveLeases == 0 && queueBeforeStart == PlatformShutDown,
            $"A fresh process already started Media Foundation (startups {life.StartupCount}, leases {life.ActiveLeases}, "
            + $"work queue 0x{queueBeforeStart:X8}).");
        census["start"] = HandleCensus.Take();

        // 1. The same clip encoded and read back again and again: one startup, flat handles, same output.
        byte[] pixels = new byte[Width * 4 * Height];
        var handles = new List<long>();
        var clips = new List<ClipCheck>();
        for (int cycle = 0; cycle < WarmupCycles + MeasuredCycles; cycle++)
        {
            string path = Path.Combine(work, $"clip-{cycle:D2}.mp4");
            Encode(path, pixels, FramesPerClip, complete: true);
            clips.Add(ReadBack(path));
            if (cycle >= WarmupCycles) handles.Add(HandleCount());
        }

        census["afterCycles"] = HandleCensus.Take();
        // The count alternates between two levels about 20 apart from one sample to the next, so the
        // halves are compared by their means. A cycle used to start Media Foundation four times, and
        // on Windows 10 each start leaked 2.5 to 3.8 handles: 200 or more between these halves.
        const int half = MeasuredCycles / 2;
        double firstHalf = handles.Take(half).Average(), lastHalf = handles.Skip(half).Average();
        int queueBetweenUses = AllocateWorkQueue();
        Expect(life.StartupCount == 1 && life.ShutdownCount == 0 && life.IsStarted && life.ActiveLeases == 0 && queueBetweenUses == 0,
            $"{WarmupCycles + MeasuredCycles} encodes and read-backs: startups {life.StartupCount}, shutdowns {life.ShutdownCount}, "
            + $"leases {life.ActiveLeases}, work queue 0x{queueBetweenUses:X8}.");
        Expect(lastHalf - firstHalf <= half,
            $"Handles grew from a mean of {firstHalf:F1} over the first {half} encode and read-back cycles to {lastHalf:F1} over the last {half}.");

        ClipCheck reference = clips[0];
        foreach (ClipCheck clip in clips)
        {
            Expect(clip.Width == Width && clip.Height == Height && clip.FrameCount == FramesPerClip,
                $"{clip.Name}: {clip.Width}x{clip.Height}, {clip.FrameCount} frames.");
            Expect(Math.Abs(clip.ContainerMs - 1000) <= 1, $"{clip.Name}: container {clip.ContainerMs} ms.");
            Expect(clip.Colours.All(colour => colour.Ok), $"{clip.Name}: colours {string.Join(", ", clip.Colours)}.");
            Expect(clip.StudioRange, $"{clip.Name}: the source probe no longer reports studio range.");
            Expect(clip.Size == reference.Size && clip.SamplesSha256 == reference.SamplesSha256 && clip.PixelsSha256 == reference.PixelsSha256,
                $"{clip.Name} differs from {reference.Name} although the frames were the same "
                + $"(size {clip.Size}/{reference.Size}, samples {clip.SamplesSha256[..12]}/{reference.SamplesSha256[..12]}).");
        }

        // 2. Recording while a reader decodes and probes another clip: both hold a lease at once.
        int leasesWhileRecording = -1, leasesWhileBoth = -1, leasesWhileProbing = -1;
        string overlapPath = Path.Combine(work, "overlap.mp4");
        using (var recorderOpen = new ManualResetEventSlim())
        using (var editDone = new ManualResetEventSlim())
        {
            Exception? recordFailure = null;
            var recorder = new Thread(() =>
            {
                try
                {
                    Encode(overlapPath, new byte[Width * 4 * Height], FramesPerClip, complete: true,
                        midway: () =>
                        {
                            recorderOpen.Set();
                            if (!editDone.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("The edit did not finish.");
                        });
                }
                catch (Exception ex)
                {
                    recordFailure = ex;
                    recorderOpen.Set();
                }
            })
            {
                IsBackground = true,
                Name = "MF lifetime recorder",
            };
            recorder.Start();
            Expect(recorderOpen.Wait(TimeSpan.FromSeconds(60)), "The recorder did not start.");
            leasesWhileRecording = life.ActiveLeases;
            using (var reader = new MediaFoundationVideoFrameReader(Path.Combine(work, "clip-00.mp4")))
            {
                leasesWhileBoth = life.ActiveLeases;
                for (int frame = 0; frame < FramesPerClip; frame++) _ = reader.ReadFrameAt((frame * 1000.0 / Fps) + 1.0);
                VideoSourceInfo info = MediaFoundationVideoFrameReader.ReadSourceInfo(Path.Combine(work, "clip-01.mp4"));
                Expect(info.StudioRange, "ReadSourceInfo during a recording no longer reports studio range.");
                leasesWhileProbing = life.ActiveLeases;
            }

            editDone.Set();
            Expect(recorder.Join(TimeSpan.FromSeconds(60)), "The recorder did not finish.");
            Expect(recordFailure is null, $"Recording during the edit failed: {recordFailure}");
        }

        ClipCheck overlap = ReadBack(overlapPath);
        Expect(leasesWhileRecording == 1 && leasesWhileBoth == 2 && leasesWhileProbing == 2,
            $"Leases while recording {leasesWhileRecording}, with a reader {leasesWhileBoth}, after a probe {leasesWhileProbing} (expected 1, 2, 2).");
        Expect(overlap.FrameCount == FramesPerClip && overlap.Colours.All(colour => colour.Ok),
            $"The clip recorded during the edit has {overlap.FrameCount} frames, colours {string.Join(", ", overlap.Colours)}.");
        Expect(life.StartupCount == 1 && life.ActiveLeases == 0,
            $"After recording and editing at once: startups {life.StartupCount}, leases {life.ActiveLeases}.");

        // 3. Failure paths return their lease and never shut the platform down.
        string notVideo = Path.Combine(work, "not-a-video.mp4");
        File.WriteAllText(notVideo, "This file is not a video.");
        var failurePaths = new Dictionary<string, string>();
        void Failing(string name, Action action, int startups = 1, int shutdowns = 0)
        {
            try
            {
                action();
                failurePaths[name] = "did not throw";
                failures.Add($"{name}: expected an exception.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException or InvalidDataException)
            {
                failurePaths[name] = ex.GetType().Name;
            }

            Expect(life.ActiveLeases == 0 && life.StartupCount == startups && life.ShutdownCount == shutdowns,
                $"{name}: leases {life.ActiveLeases}, startups {life.StartupCount}, shutdowns {life.ShutdownCount} afterwards "
                + $"(expected 0, {startups}, {shutdowns}).");
        }

        Failing("encoder without an output folder", () => new MediaFoundationVideoEncoder(
            Options(Path.Combine(work, "missing", "clip.mp4")), NullLogger.Instance).Dispose());
        Failing("source probe on a file that is not a video", () => MediaFoundationVideoFrameReader.ReadSourceInfo(notVideo));
        Failing("duration probe on a file that is not a video", () => MediaFoundationVideoFrameReader.ReadContainerDurationMs(notVideo));
        Failing("frame that does not match the encoder", () =>
        {
            using var encoder = new MediaFoundationVideoEncoder(Options(Path.Combine(work, "mismatch.mp4")), NullLogger.Instance);
            encoder.WriteFrame(new EncoderFrame(new byte[16 * 4 * 16], 16, 16, 16 * 4, 0));
        });

        // A recording disposed without Complete, as a crash path does, still returns its lease.
        string abandoned = Path.Combine(work, "abandoned.mp4");
        Encode(abandoned, pixels, FramesPerClip / 2, complete: false);
        Expect(life.ActiveLeases == 0, $"An encoder disposed without Complete kept its lease ({life.ActiveLeases}).");
        Expect(ReadBack(abandoned).FrameCount == FramesPerClip / 2, "The finalised-on-dispose clip lost frames.");

        // 4. Exit: the one startup is balanced, so Media Foundation itself is no longer started.
        // Windows 11 keeps its thread pool's kernel objects after MFShutdown, so they prove nothing.
        census["beforeExit"] = HandleCensus.Take();
        MediaFoundationPlatform.Shutdown();
        int queueAfterExit = AllocateWorkQueue();
        Expect(life.StartupCount == 1 && life.ShutdownCount == 1 && !life.IsStarted && life.ActiveLeases == 0
                && life.LastShutdownResult == 0 && queueAfterExit == PlatformShutDown,
            $"At exit: startups {life.StartupCount}, shutdowns {life.ShutdownCount}, started {life.IsStarted}, leases {life.ActiveLeases}, "
            + $"MFShutdown 0x{life.LastShutdownResult:X8}, work queue 0x{queueAfterExit:X8}.");
        census["afterExit"] = HandleCensus.Take();

        // 5. A reader that fails to open after exit began starts the platform again, and its failure
        // shuts it down again. Last: when a Source Reader given attributes cannot resolve a file,
        // Windows itself keeps Media Foundation started past the balancing MFShutdown.
        Failing("reader on a file that is not a video, after exit",
            () => new MediaFoundationVideoFrameReader(notVideo).Dispose(), startups: 2, shutdowns: 2);
        int queueAfterFailedOpen = AllocateWorkQueue();

        return new Dictionary<string, object?>
        {
            ["failures"] = failures,
            ["handleSamples"] = handles,
            ["firstHalfMean"] = firstHalf,
            ["lastHalfMean"] = lastHalf,
            ["workQueue"] = new Dictionary<string, string>
            {
                ["beforeStart"] = $"0x{queueBeforeStart:X8}",
                ["betweenUses"] = $"0x{queueBetweenUses:X8}",
                ["afterExit"] = $"0x{queueAfterExit:X8}",
                ["afterFailedOpenAfterExit"] = $"0x{queueAfterFailedOpen:X8}",
            },
            ["startups"] = life.StartupCount,
            ["shutdowns"] = life.ShutdownCount,
            ["failurePaths"] = failurePaths,
            ["leasesWhileRecording"] = leasesWhileRecording,
            ["leasesWhileRecordingAndReading"] = leasesWhileBoth,
            ["clip"] = reference,
            ["census"] = census,
        };
    }

    private static VideoEncoderOptions Options(string path) =>
        new(path, Width, Height, Fps, VideoEncoderOptions.DeriveBitrate(Width, Height, Fps))
        {
            // The software encoder gives every machine, with or without a GPU, the same output.
            AllowHardwareEncoder = false,
        };

    private static void Encode(string path, byte[] pixels, int frames, bool complete, Action? midway = null)
    {
        using var encoder = new MediaFoundationVideoEncoder(Options(path), NullLogger.Instance);
        for (int frame = 0; frame < frames; frame++)
        {
            if (frame == frames / 2) midway?.Invoke();
            Fill(pixels, frame);
            encoder.WriteFrame(new EncoderFrame(pixels, Width, Height, Width * 4, frame * 1000.0 / Fps));
        }

        if (complete) encoder.Complete();
    }

    /// <summary>A gradient, three fixed colour patches and one square that moves every frame.</summary>
    private static void Fill(byte[] pixels, int frame)
    {
        const int stride = Width * 4;
        for (int y = 0; y < Height; y++)
        {
            byte green = (byte)(y * 255 / (Height - 1));
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)(x * 255 / (Width - 1));
                pixels[offset + 1] = green;
                pixels[offset + 2] = 128;
                pixels[offset + 3] = 255;
            }
        }

        foreach ((_, int x, int y, byte b, byte g, byte r) in Patches) FillRect(pixels, x - 32, y - 32, 64, b, g, r);
        FillRect(pixels, 20 + (frame * 18), 240, 40, 250, 250, 250);
    }

    private static void FillRect(byte[] pixels, int left, int top, int size, byte b, byte g, byte r)
    {
        const int stride = Width * 4;
        for (int y = top; y < top + size; y++)
        {
            for (int x = left; x < left + size; x++)
            {
                int offset = (y * stride) + (x * 4);
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }
    }

    private sealed record ColourCheck(string Patch, int Frame, byte B, byte G, byte R, bool Ok);

    private sealed record ClipCheck(
        string Name, long Size, string SamplesSha256, string PixelsSha256, double ContainerMs, bool StudioRange,
        int Width, int Height, int FrameCount, ColourCheck[] Colours);

    /// <summary>Opens a clip the way an edit does: source probe, duration probe, then every frame.</summary>
    private static ClipCheck ReadBack(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        VideoSourceInfo info = MediaFoundationVideoFrameReader.ReadSourceInfo(path);
        double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(path);
        using var reader = new MediaFoundationVideoFrameReader(path);
        using var pixelHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var colours = new List<ColourCheck>();
        int width = 0, height = 0, frames = 0;
        double last = double.NegativeInfinity;
        // One request inside every frame interval, then one past the final frame.
        for (int request = 0; request <= FramesPerClip; request++)
        {
            DecodedVideoFrame frame = reader.ReadFrameAt((request * 1000.0 / Fps) + 1.0);
            (width, height) = (frame.Width, frame.Height);
            if (frame.TimestampMs > last)
            {
                frames++;
                last = frame.TimestampMs;
                pixelHash.AppendData(frame.Pixels);
            }

            if (request is 0 or FramesPerClip / 2 && frame.Width == Width && frame.Height == Height)
            {
                foreach ((string name, int x, int y, byte b, byte g, byte r) in Patches)
                {
                    int offset = (y * frame.Stride) + (x * 4);
                    byte db = frame.Pixels[offset], dg = frame.Pixels[offset + 1], dr = frame.Pixels[offset + 2];
                    bool ok = Math.Abs(db - b) <= 24 && Math.Abs(dg - g) <= 24 && Math.Abs(dr - r) <= 24;
                    colours.Add(new ColourCheck(name, request, db, dg, dr, ok));
                }
            }
        }

        return new ClipCheck(Path.GetFileName(path), bytes.LongLength, SamplesSha256(bytes),
            Convert.ToHexString(pixelHash.GetHashAndReset()), containerMs, info.StudioRange,
            width, height, frames, [.. colours]);
    }

    /// <summary>The encoded samples: the payload of the top-level <c>mdat</c> box.</summary>
    private static string SamplesSha256(byte[] bytes)
    {
        long offset = 0;
        while (offset + 8 <= bytes.LongLength)
        {
            long size = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16) | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
            string type = System.Text.Encoding.ASCII.GetString(bytes, (int)offset + 4, 4);
            long header = 8;
            if (size == 1)
            {
                size = 0;
                for (int i = 0; i < 8; i++) size = (size << 8) | bytes[offset + 8 + i];
                header = 16;
            }
            else if (size == 0)
            {
                size = bytes.LongLength - offset;
            }

            if (size < header || offset + size > bytes.LongLength) return "malformed";
            if (type == "mdat") return Convert.ToHexString(SHA256.HashData(bytes.AsSpan((int)(offset + header), (int)(size - header))));
            offset += size;
        }

        return "absent";
    }

    private static long HandleCount() =>
        GetProcessHandleCount(GetCurrentProcess(), out uint count) ? count : throw new InvalidOperationException("GetProcessHandleCount failed.");

    /// <summary>
    /// Asks Media Foundation whether anyone holds it started: a work queue needs the platform and
    /// is otherwise refused with MF_E_SHUTDOWN.
    /// </summary>
    private static int AllocateWorkQueue()
    {
        int result = MFAllocateWorkQueue(out uint queue);
        if (result >= 0) _ = MFUnlockWorkQueue(queue);
        return result;
    }

    [DllImport("mfplat.dll")]
    private static extern int MFAllocateWorkQueue(out uint queue);

    [DllImport("mfplat.dll")]
    private static extern int MFUnlockWorkQueue(uint queue);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The source checkout is required for the Media Foundation ownership audit.");
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    /// <summary>Counts the native startups and shutdowns a lifetime asks for.</summary>
    private sealed class FakeNative
    {
        private int _startups;
        private int _shutdowns;

        internal int StartupResult { get; set; }

        internal int Startups => Volatile.Read(ref _startups);

        internal int Shutdowns => Volatile.Read(ref _shutdowns);

        internal MediaFoundationLifetime Create() => new(
            () =>
            {
                if (StartupResult < 0) return StartupResult;
                Interlocked.Increment(ref _startups);
                return 0;
            },
            () =>
            {
                Interlocked.Increment(ref _shutdowns);
                return 0;
            });
    }

    /// <summary>This process's handles by kernel object type.</summary>
    private static class HandleCensus
    {
        private const int SystemExtendedHandleInformation = 64, ObjectTypeInformation = 2;
        private const int InfoLengthMismatch = unchecked((int)0xC0000004);
        private static readonly Dictionary<ushort, string> TypeNames = new();

        internal static SortedDictionary<string, int> Take()
        {
            long self = Environment.ProcessId;
            int size = 8 << 20;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                while (true)
                {
                    buffer = Marshal.AllocHGlobal(size);
                    int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                    if (status != InfoLengthMismatch)
                    {
                        if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed with 0x{status:X8}.");
                        break;
                    }

                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    size = Math.Max(size * 2, needed + (1 << 20));
                }

                // SYSTEM_HANDLE_INFORMATION_EX: two pointer-sized counts, then entries of three pointers
                // (object, owner process, handle) and 16 bytes of access, indices and attributes.
                int pointer = IntPtr.Size, entrySize = (3 * pointer) + 16;
                long count = Marshal.ReadIntPtr(buffer).ToInt64();
                var byType = new Dictionary<ushort, int>();
                var sample = new Dictionary<ushort, IntPtr>();
                for (long index = 0; index < count; index++)
                {
                    IntPtr entry = IntPtr.Add(buffer, checked((int)((2 * pointer) + (index * entrySize))));
                    if (Marshal.ReadIntPtr(entry, pointer).ToInt64() != self) continue;
                    ushort type = unchecked((ushort)Marshal.ReadInt16(entry, (3 * pointer) + 6));
                    byType[type] = byType.GetValueOrDefault(type) + 1;
                    sample.TryAdd(type, Marshal.ReadIntPtr(entry, 2 * pointer));
                }

                foreach ((ushort type, IntPtr handle) in sample)
                    if (!TypeNames.ContainsKey(type) && TypeName(handle) is { } name) TypeNames[type] = name;
                var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach ((ushort type, int n) in byType)
                {
                    string name = TypeNames.TryGetValue(type, out string? known) ? known : $"type{type}";
                    result[name] = result.GetValueOrDefault(name) + n;
                }

                return result;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static string? TypeName(IntPtr handle)
        {
            IntPtr info = Marshal.AllocHGlobal(4096);
            try
            {
                if (NtQueryObject(handle, ObjectTypeInformation, info, 4096, out _) < 0) return null;
                int bytes = Marshal.ReadInt16(info, 0) & 0xFFFF;
                IntPtr text = Marshal.ReadIntPtr(info, IntPtr.Size); // UNICODE_STRING.Buffer
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int informationClass, IntPtr information, int length, out int returnLength);
    }
}
