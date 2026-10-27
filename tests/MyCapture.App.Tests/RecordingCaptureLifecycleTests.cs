using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingCaptureLifecycleTests
{
    [Fact]
    public void SlowInitialization_StopStillEmitsZeroFrameAndExcludesStartupFromDuration() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        using var initializing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var encoder = new Encoder();
        int factoryThread = 0;
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ =>
        {
            factoryThread = Environment.CurrentManagedThreadId;
            initializing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return encoder;
        }, NullLogger.Instance);
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings());
        Assert.True(initializing.Wait(RecordingTestTimeouts.CaptureThread));
        Assert.False(recorder.IsReady);
        Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
        Thread.Sleep(250);
        Task<RecordingResult> stopping = Task.Run(recorder.Stop);
        Thread.Sleep(30);
        release.Set();
        Assert.True(stopping.Wait(RecordingTestTimeouts.CaptureThread));
        RecordingResult result = stopping.Result;
        Assert.Equal(new[] { 0d }, encoder.Timestamps);
        Assert.True(encoder.Completed);
        Assert.True(encoder.Disposed);
        Assert.Equal(factoryThread, encoder.WriteThread);
        Assert.Equal(factoryThread, encoder.DisposedThread);
        Assert.True(result.Performance!.InitializationMs >= 250);
        Assert.True(result.DurationMs < result.Performance.InitializationMs);
        Assert.False(recorder.IsReady);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusableSurface_PreservesPixelsAndHandlesAcrossFailureAndFreshSessions(bool includeCursor) => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        byte[] expected = new byte[320 * 240 * 4];
        fixture.Engine.CaptureRegionInto(fixture.Region, includeCursor, expected, 1280);
        // Warm both paths before counting GDI objects.
        using (var warm = fixture.Engine.CreateSession(fixture.Region, includeCursor)) warm.CaptureInto(new byte[expected.Length], 1280);
        uint before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (int sessionIndex = 0; sessionIndex < 8; sessionIndex++)
        {
            using var session = fixture.Engine.CreateSession(fixture.Region, includeCursor);
            byte[] actual = new byte[expected.Length];
            Assert.Throws<ArgumentException>(() => session.CaptureInto(new byte[1], 1280));
            session.CaptureInto(actual, 1280);
            if (!includeCursor) Assert.Equal(expected, actual);
            Assert.Equal(255, actual[3]);
            session.Dispose();
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.CaptureInto(actual, 1280));
        }
        uint after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        // This counter includes every WPF/test resource in the process. Other objects can be
        // finalized during this workload; decreases are valid, but the growth limit is unchanged.
        Assert.True((long)after - before <= 2, $"Process GDI resources grew from {before} to {after}.");
    });

    [Fact]
    public void EncodeFailure_CleansNativeSessionAndAllowsRecorderRestart() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        bool fail = true;
        using var written = new ManualResetEventSlim();
        var encoder = new Encoder { OnWrite = () => { written.Set(); if (fail) throw new IOException("synthetic encoder failure"); } };
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ => encoder, NullLogger.Instance);
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings());
        Assert.True(written.Wait(RecordingTestTimeouts.CaptureThread));
        Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.True(encoder.Disposed);
        fail = false;
        written.Reset();
        recorder.Start(fixture.Region, "unused-again.mp4", new RecordingSettings());
        Assert.True(written.Wait(RecordingTestTimeouts.CaptureThread));
        Assert.True(recorder.Stop().EmittedFrames >= 1);
    });

    [Fact]
    public void EditorExclusionFailure_NeverShowsEditorAndReleasesBusyState() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        var coordinator = new CaptureOverlayCoordinator(fixture.Engine,
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            RequiresCaptureExclusion = () => true,
        };
        bool attempted = false;
        Exception? failure = null;
        coordinator.ApplyCaptureExclusion = window => { attempted = true; Assert.False(window.IsVisible); return false; };
        coordinator.TransitionFailed += error => failure = error;
        var frame = new FrozenFrame(fixture.Engine.CaptureRegion(fixture.Region, false), fixture.Region, null, 0);
        coordinator.StartWithSelection(frame, new RectD(0, 0, 50, 50));
        Assert.True(coordinator.LastTransitionForTest.IsCompletedSuccessfully);
        Assert.True(attempted);
        Assert.NotNull(failure);
        Assert.False(coordinator.IsActive);
    });

    [Fact]
    public void StopDuringPersistence_StillRequiresEditorExclusionUntilCaptureThreadJoins() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        using var written = new ManualResetEventSlim();
        using var completing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var encoder = new Encoder
        {
            OnWrite = written.Set,
            OnComplete = () => { completing.Set(); if (!finish.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); },
        };
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ => encoder, NullLogger.Instance);
        var controls = new RecordingControlWindow(fixture.Region, new RecordingSettings(), () => recorder,
            () => "unused.mp4", NullLogger<RecordingControlWindow>.Instance) { ShowActivated = false };
        // This state-only seam avoids unrelated gallery recovery I/O. The real control/recorder
        // and asynchronous selection transition below exercise the stop race.
        var recording = (RegionRecordingCoordinator)RuntimeHelpers.GetUninitializedObject(typeof(RegionRecordingCoordinator));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RegionRecordingCoordinator).GetField("_controls", flags)!.SetValue(recording, controls);
        var persistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool exclusionRequested = false;
        var capture = new CaptureOverlayCoordinator(fixture.Engine,
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            RequiresCaptureExclusion = () => recording.RequiresCaptureExclusion,
            ApplyCaptureExclusion = _ => { exclusionRequested = true; return false; },
            SelectionPersistRequested = _ => persistence.Task,
        };
        try
        {
            controls.Show();
            typeof(RecordingControlWindow).GetMethod("OnPrimaryClicked", flags)!.Invoke(controls, null);
            Assert.True(written.Wait(RecordingTestTimeouts.CaptureThread));
            Assert.True(recording.CanCaptureStill);
            var frame = new FrozenFrame(fixture.Engine.CaptureRegion(fixture.Region, false), fixture.Region, null, 0);
            capture.StartWithSelection(frame, new RectD(0, 0, 40, 40));
            controls.RequestStop();
            Assert.True(completing.Wait(RecordingTestTimeouts.CaptureThread));
            Assert.False(recording.CanCaptureStill);
            Assert.True(recording.RequiresCaptureExclusion);
            persistence.SetResult();
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!capture.LastTransitionForTest.IsCompleted && DateTime.UtcNow < deadline) SyntheticCaptureFixture.Pump(10);
            Assert.True(capture.LastTransitionForTest.IsCompletedSuccessfully);
            Assert.True(exclusionRequested);
            Assert.False(capture.IsActive);
        }
        finally
        {
            finish.Set();
            persistence.TrySetResult();
            DateTime deadline = DateTime.UtcNow + RecordingTestTimeouts.CaptureThread;
            while (controls.IsRecording && DateTime.UtcNow < deadline) SyntheticCaptureFixture.Pump(10);
            SyntheticCaptureFixture.Pump(30);
            capture.Cancel();
            controls.CompleteAndClose();
        }
        Assert.False(recording.RequiresCaptureExclusion);
    });

    private sealed class Encoder : IVideoEncoder
    {
        public List<double> Timestamps { get; } = [];
        public Action? OnWrite { get; init; }
        public Action? OnComplete { get; init; }
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }
        public int WriteThread { get; private set; }
        public int DisposedThread { get; private set; }
        public int Width => 320;
        public int Height => 240;
        public void WriteFrame(in EncoderFrame frame) { WriteThread = Environment.CurrentManagedThreadId; Timestamps.Add(frame.TimestampMs); OnWrite?.Invoke(); }
        public void Complete() { Completed = true; OnComplete?.Invoke(); }
        public void Dispose() { Disposed = true; DisposedThread = Environment.CurrentManagedThreadId; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [Fact]
    public void SyntheticSurface_RepeatedLifetimesReleaseTheNativeWindowAndBrush() => StaTestHost.Run(() =>
    {
        using (var warm = new SyntheticCaptureFixture()) { }
        using Process process = Process.GetCurrentProcess();
        uint before = GetGuiResources(process.Handle, 0);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var fixture = new SyntheticCaptureFixture();
            IntPtr window = fixture.WindowHandleForTest;
            Assert.True(IsWindow(window));
            // Construction already checks every captured pixel against the solid colour.
            fixture.Dispose();
            fixture.Dispose();
            Assert.False(IsWindow(window));
        }
        uint after = GetGuiResources(process.Handle, 0);
        Assert.True((long)after - before <= 2, $"Native fixture GDI resources grew from {before} to {after}.");
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
}

internal sealed class SyntheticCaptureFixture : IDisposable
{
    private const uint ExtendedStyle = 0x00000008 | 0x00000080 | 0x08000000; // TOPMOST, TOOLWINDOW, NOACTIVATE.
    private static readonly WindowProcedure Procedure = DefWindowProc;
    private readonly string _className = "MyCapture.SyntheticCapture." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance = GetModuleHandle(null);
    private IntPtr _window;
    private IntPtr _unregisteredBrush;
    private bool _classRegistered;
    internal ScreenCaptureEngine Engine { get; } = new(NullLogger<ScreenCaptureEngine>.Instance);
    internal RectD Region { get; }
    internal IntPtr WindowHandleForTest => _window;

    internal SyntheticCaptureFixture()
    {
        try
        {
            // A class background brush is painted synchronously by DefWindowProc. This fixture
            // needs native desktop pixels, not WPF's asynchronous composition of a solid scene.
            // A plain WPF Window also inherits the app style, whose ModernWindowChrome replaces
            // its background with Surface.Base when earlier tests have loaded app resources.
            _unregisteredBrush = CreateSolidBrush(18 | (90u << 8) | (166u << 16));
            if (_unregisteredBrush == IntPtr.Zero) ThrowNativeFailure("CreateSolidBrush");
            var windowClass = new NativeWindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeWindowClass>(), Procedure = Procedure,
                Instance = _instance, Background = _unregisteredBrush, ClassName = _className,
            };
            if (RegisterClassEx(ref windowClass) == 0) ThrowNativeFailure("RegisterClassEx");
            _classRegistered = true;
            // The registered class owns this brush; UnregisterClass releases it.
            _unregisteredBrush = IntPtr.Zero;
            _window = CreateWindowEx(ExtendedStyle, _className, string.Empty, 0x80000000, // WS_POPUP.
                100, 100, 700, 500, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) ThrowNativeFailure("CreateWindowEx");
            PresentFixture();
            var origin = new NativePoint { X = 20, Y = 20 };
            if (!ClientToScreen(_window, ref origin)) ThrowNativeFailure("ClientToScreen");
            Region = new RectD(origin.X, origin.Y, 320, 240);
            WaitForPresentedFixture();
        }
        catch { Dispose(); throw; }
    }

    private void PresentFixture()
    {
        // Raise only the owned fixture, without activating it or changing input focus.
        if (!SetWindowPos(_window, new IntPtr(-1), 100, 100, 700, 500, 0x0010 | 0x0040)) // NOACTIVATE, SHOWWINDOW.
            ThrowNativeFailure("SetWindowPos");
        if (!RedrawWindow(_window, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0100)) // INVALIDATE, ERASE, UPDATENOW.
            ThrowNativeFailure("RedrawWindow");
    }

    private void WaitForPresentedFixture()
    {
        // Synchronous GDI paint still needs DWM presentation. Capture comparisons
        // must start with every pixel of the known fixture, never another surface.
        byte[] expected = new byte[320 * 240 * 4];
        for (int i = 0; i < expected.Length; i += 4)
        {
            expected[i] = 166;
            expected[i + 1] = 90;
            expected[i + 2] = 18;
            expected[i + 3] = 255;
        }
        byte[] observed = new byte[expected.Length];
        var elapsed = Stopwatch.StartNew();
        do
        {
            PresentFixture();
            Engine.CaptureRegionInto(Region, false, observed, 1280);
            if (observed.AsSpan().SequenceEqual(expected)) return;
            Pump(20);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
        bool hasBounds = GetWindowRect(_window, out NativeRect bounds);
        Console.Error.WriteLine($"Owned capture fixture: boundsAvailable={hasBounds}, "
            + $"bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, "
            + $"visible={IsWindowVisible(_window)}, topmost={(GetWindowLong(_window, -20) & 0x00000008) != 0}, "
            + $"region={Region}.");
        Assert.Equal(expected, observed);
    }

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            if (!DestroyWindow(_window)) ThrowNativeFailure("DestroyWindow");
            _window = IntPtr.Zero;
        }
        if (_classRegistered)
        {
            if (!UnregisterClass(_className, _instance)) ThrowNativeFailure("UnregisterClass");
            _classRegistered = false;
        }
        if (_unregisteredBrush != IntPtr.Zero)
        {
            if (!DeleteObject(_unregisteredBrush)) ThrowNativeFailure("DeleteObject");
            _unregisteredBrush = IntPtr.Zero;
        }
    }

    private static void ThrowNativeFailure(string operation) =>
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Synthetic capture fixture {operation} failed.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref NativeWindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern uint GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
