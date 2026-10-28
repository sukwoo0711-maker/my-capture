using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class LaunchVisibilityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietLaunchNeverCreatesASurfaceEvenPastTheDelay(bool secondaryReady)
    {
        int created = 0;
        using var status = LaunchStatusPresentation.Start(true, () => Assert.Fail("Quiet launch became visible."),
            () => { created++; return new FakeSurface(); }, TimeSpan.Zero);
        await status.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, created);
        Assert.False(ResidentReadySignal.NeedsSecondaryNotice(true, secondaryReady));
    }

    [Fact]
    public async Task FastStartupCompletesWithoutCreatingOrFlashingASurface()
    {
        int created = 0;
        using var status = LaunchStatusPresentation.Start(false, () => Assert.Fail("Fast launch flashed."),
            () => { Interlocked.Increment(ref created); return new FakeSurface(); });
        status.Complete();
        await status.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, created);
        Assert.Equal(TimeSpan.FromMilliseconds(400), LaunchStatusPresentation.ShowDelay);
    }

    [Fact]
    public async Task SlowStartupShowsWhileCallerIsBlockedAndCompletionClosesIt()
    {
        using var visible = new ManualResetEventSlim();
        var surface = new FakeSurface();
        var elapsed = Stopwatch.StartNew();
        using var status = LaunchStatusPresentation.Start(false, visible.Set, () => surface);
        // No dispatcher pump or awaited continuation on the caller: synchronous startup is blocked.
        Assert.True(visible.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(elapsed.Elapsed >= LaunchStatusPresentation.ShowDelay);
        Assert.NotEqual(Environment.CurrentManagedThreadId, surface.ThreadId);
        Assert.False(surface.Disposed);
        status.Complete();
        await status.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(surface.Disposed);
    }

    [Fact]
    public async Task CompletionRacingTheDelayNeverLeavesALateSurfaceBehind()
    {
        for (int iteration = 0; iteration < 40; iteration++)
        {
            var surface = new FakeSurface();
            using var status = LaunchStatusPresentation.Start(false, () => { }, () => surface, TimeSpan.Zero);
            status.Complete();
            await status.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(surface.Shown == 0 || surface.Disposed);
            Assert.InRange(surface.Shown, 0, 1);
        }
    }

    [Fact]
    public void FirstPresentationRenderingClosesTheSlowStartupSurface() => StaTestHost.Run(() =>
    {
        using var visible = new ManualResetEventSlim();
        var surface = new FakeSurface();
        using var status = LaunchStatusPresentation.Start(false, visible.Set, () => surface, TimeSpan.Zero);
        Assert.True(visible.Wait(TimeSpan.FromSeconds(2)));
        var dialog = new ThemedDialog("시작 확인", "MyCapture", MessageBoxButton.OK, MessageBoxResult.OK);
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        bool rendered = false;
        App.ObserveLaunchWindow(dialog, () => { rendered = true; status.Complete(); frame.Continue = false; });
        try
        {
            dialog.Show();
            Dispatcher.PushFrame(frame);
            Assert.True(rendered);
            Assert.True(status.Completion.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(surface.Disposed);
        }
        finally { timeout.Stop(); dialog.Close(); }
    });

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void SecondaryOnlyNotifiesForAnInteractiveUnreadyResident(bool quiet, bool ready, bool expected) =>
        Assert.Equal(expected, ResidentReadySignal.NeedsSecondaryNotice(quiet, ready));

    [Fact]
    public void NamedReadinessIsNotConsumedBySecondaryAndOwnerExitResetsIt()
    {
        string name = @"Local\MyCapture.Test.Ready." + Guid.NewGuid().ToString("N");
        Assert.False(ResidentReadySignal.IsReady(name));
        using var heldSecondary = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        using (var resident = new ResidentReadySignal(name))
        {
            Assert.False(ResidentReadySignal.IsReady(name));
            resident.MarkReady();
            Assert.True(ResidentReadySignal.IsReady(name));
            Assert.True(ResidentReadySignal.IsReady(name));
        }
        Assert.False(heldSecondary.WaitOne(0));
        Assert.False(ResidentReadySignal.IsReady(name));
        heldSecondary.Set(); // A stale handle cannot mark the next resident ready.
        using var nextResident = new ResidentReadySignal(name);
        Assert.False(ResidentReadySignal.IsReady(name));
    }

    [Fact]
    public async Task SecondaryNoticeStaysForTwoSecondsAfterItBecomesVisible()
    {
        var surface = new FakeSurface();
        var visibleAt = new Stopwatch();
        using var status = LaunchStatusPresentation.Secondary(visibleAt.Start, () => surface);
        await status.Completion.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(TimeSpan.FromSeconds(2), surface.Duration);
        Assert.True(visibleAt.Elapsed >= LaunchStatusPresentation.SecondaryDuration);
        Assert.Equal(1, surface.Shown);
        Assert.True(surface.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegroundHelperRestoresOriginalTopmost(bool topmost) => StaTestHost.Run(() =>
    {
        var window = new Window { Width = 160, Height = 80, ShowInTaskbar = false, Topmost = topmost };
        try
        {
            window.Show();
            WindowForeground.BringForward(window);
            Assert.Equal(topmost, window.Topmost);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OwnerlessErrorReleasesTemporaryTopmostAfterRendering() => StaTestHost.Run(() =>
    {
        var dialog = new ThemedDialog("시작 확인", "MyCapture", MessageBoxButton.OK, MessageBoxResult.OK);
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        bool rendered = false;
        ThemedDialog.PlaceOwnerless(dialog, MessageBoxImage.Error);
        Assert.True(dialog.Topmost);
        dialog.ContentRendered += (_, _) => { rendered = true; frame.Continue = false; };
        try
        {
            dialog.Show();
            Dispatcher.PushFrame(frame);
            Assert.True(rendered);
            Assert.False(dialog.Topmost);
        }
        finally { timeout.Stop(); dialog.Close(); }
    });

    [Theory]
    [InlineData((int)LaunchActivationKind.Regular, "regular")]
    [InlineData((int)LaunchActivationKind.Background, "background")]
    [InlineData((int)LaunchActivationKind.StartupTask, "startup-task")]
    [InlineData((int)LaunchActivationKind.Secondary, "secondary")]
    public void TimingLogBuffersOnlyFixedActivationAndOneVisibleTime(int kind, string expected)
    {
        var timing = new LaunchTimingLog();
        var lines = new List<string>();
        timing.Activation((LaunchActivationKind)kind);
        timing.FirstVisible(LaunchVisibleKind.Starting);
        timing.Attach(lines.Add);
        timing.FirstVisible(LaunchVisibleKind.Presentation);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("launch activation=" + expected, lines[0]);
        Assert.Contains("launch first-visible=starting elapsed-ms=", lines[1]);
        Assert.All(lines, line => Assert.True(SafeLogLine.IsWellFormed(line), line));
        foreach (string fragment in RollingFileLogTests.PersonalFragments)
            Assert.DoesNotContain(fragment, string.Join('\n', lines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSurfacePaintsAndRespondsWhileCallerIsBlocked(bool secondary) => StaTestHost.Run(() =>
    {
        using var visible = new ManualResetEventSlim();
        var surface = new NativeLaunchSurface(secondary);
        using var status = LaunchStatusPresentation.Start(false, visible.Set, () => surface, TimeSpan.Zero);
        Assert.True(visible.Wait(TimeSpan.FromSeconds(5)));
        IntPtr handle = surface.Handle;
        Assert.NotEqual(IntPtr.Zero, handle);
        Assert.True(IsWindowVisible(handle));
        // WM_NULL is processed on the notice thread even though this calling STA is not pumping.
        Assert.NotEqual(IntPtr.Zero, SendMessageTimeout(handle, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out _));
        status.Complete();
        Assert.True(status.Completion.Wait(TimeSpan.FromSeconds(3)));
        Assert.False(IsWindow(handle));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurfaceFailureCompletesWithoutEscapingTheWorker(bool failDuringCreation)
    {
        var surface = new FakeSurface { ThrowOnShow = true };
        using var status = LaunchStatusPresentation.Start(false, () => Assert.Fail("Failed surface reported visible."),
            () => failDuringCreation ? throw new InvalidOperationException("test creation failure") : surface,
            TimeSpan.Zero);
        await status.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(!failDuringCreation, surface.Disposed);
    }

    [Fact]
    public async Task FallbackLogPathFailureDoesNotEscapeOrPreventCompletion()
    {
        int attempts = 0;
        using var log = new LaunchFallbackLog(() =>
        {
            Interlocked.Increment(ref attempts);
            return Path.GetFullPath("\0");
        });
        log.Append(SafeLogLine.LaunchActivation(DateTimeOffset.UtcNow, LaunchActivationKind.Secondary));
        await log.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ConcurrentFallbackLogsPreserveEachRecordAndTheResidentLog()
    {
        string root = OwnedTestDirectory.Create("mc-launch-log-");
        string mutex = @"Local\MyCapture.Test.LaunchLog." + Guid.NewGuid().ToString("N");
        var fallbacks = Enumerable.Range(0, 12).Select(_ => new LaunchFallbackLog(() => root, mutex)).ToArray();
        var elapsed = Stopwatch.StartNew();
        Exception? recordFailure = null;
        output.WriteLine("Writing resident and fallback records; fixture: {0}", root);
        try
        {
            using var resident = new RollingFileLog(root);
            string before = SafeLogLine.LaunchActivation(DateTimeOffset.UtcNow, LaunchActivationKind.Regular);
            string after = SafeLogLine.LaunchFirstVisible(DateTimeOffset.UtcNow, LaunchVisibleKind.Presentation, 800);
            resident.Append(before);
            resident.Flush();
            string[] expected = Enumerable.Range(0, fallbacks.Length)
                .Select(index => SafeLogLine.LaunchFirstVisible(DateTimeOffset.UtcNow, LaunchVisibleKind.SecondaryNotice, index)).ToArray();
            for (int index = 0; index < fallbacks.Length; index++) fallbacks[index].Append(expected[index]);
            await Task.WhenAll(fallbacks.Select(log => log.Completion)).WaitAsync(TimeSpan.FromSeconds(8));
            output.WriteLine("All fallback writers completed at {0} ms.", elapsed.ElapsedMilliseconds);
            resident.Append(after);
            resident.Flush();
            // The resident deliberately keeps a write handle open. Windows sharing checks are
            // bidirectional: File.ReadAllLines uses FileShare.Read and rejects that live writer.
            using (var residentStream = new FileStream(resident.CurrentPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var residentReader = new StreamReader(residentStream))
            {
                Assert.Equal(before, residentReader.ReadLine());
                Assert.Equal(after, residentReader.ReadLine());
                Assert.Null(residentReader.ReadLine());
            }
            // Completion must also mean every short-lived writer released its file handle.
            using var stream = new FileStream(Path.Combine(root, "launch", "mycapture.log"),
                FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(stream);
            var actual = new List<string>();
            while (reader.ReadLine() is { } line) actual.Add(line);
            Assert.Equal(expected.OrderBy(line => line, StringComparer.Ordinal), actual.OrderBy(line => line, StringComparer.Ordinal));
            Assert.All(actual, line => Assert.True(SafeLogLine.IsWellFormed(line), line));
            output.WriteLine("All {0} fallback and 2 resident records verified at {1} ms.", actual.Count, elapsed.ElapsedMilliseconds);
        }
        catch (Exception error)
        {
            recordFailure = error;
            output.WriteLine("Record verification failed at {0} ms: {1}", elapsed.ElapsedMilliseconds, error);
            throw;
        }
        finally
        {
            output.WriteLine("Disposing fallback writers at {0} ms.", elapsed.ElapsedMilliseconds);
            foreach (LaunchFallbackLog log in fallbacks) log.Dispose();
            output.WriteLine("Recycling the fixture at {0} ms.", elapsed.ElapsedMilliseconds);
            try
            {
                OwnedTestDirectory.Delete(root);
                output.WriteLine("Fixture recycling completed at {0} ms.", elapsed.ElapsedMilliseconds);
            }
            catch (Exception cleanupError)
            {
                output.WriteLine("Fixture recycling failed at {0} ms: {1}", elapsed.ElapsedMilliseconds, cleanupError);
                // A Shell failure must not replace an earlier timeout or missing-record assertion.
                if (recordFailure is not null)
                    throw new AggregateException("Record verification and fixture recycling both failed.", recordFailure, cleanupError);
                throw;
            }
        }
    }

    private sealed class FakeSurface : ILaunchSurface
    {
        internal int ThreadId;
        internal int Shown;
        internal bool Disposed;
        internal TimeSpan? Duration;
        internal bool ThrowOnShow;
        public void Show()
        {
            if (ThrowOnShow) throw new InvalidOperationException("test show failure");
            ThreadId = Environment.CurrentManagedThreadId;
            Shown++;
        }
        public void Run(WaitHandle finished, TimeSpan? duration)
        {
            Duration = duration;
            finished.WaitOne(duration ?? Timeout.InfiniteTimeSpan);
        }
        public void Dispose() => Disposed = true;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
