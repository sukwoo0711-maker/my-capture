using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

public sealed class SettingsBackgroundSaveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ITestOutputHelper _output;

    public SettingsBackgroundSaveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void SaveInBackground_CoalescesWaitingSnapshotsAndUsesOneWriter()
    {
        using var workspace = new TempWorkspace();
        using var writer = new BlockingWriter();
        var store = CreateStore(workspace, writer.Write);
        try
        {
            store.SaveInBackground(Settings(100));
            Assert.True(writer.Entered.Wait(Timeout));

            for (int value = 200; value <= 210; value++)
                store.SaveInBackground(Settings(value));

            Assert.Equal(1, writer.WriteCount);
            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));

            Assert.Equal(2, writer.WriteCount);
            Assert.Equal(1, writer.MaxActiveWrites);
            Assert.Equal(210, Reload(workspace).Queue.MaxItems);
        }
        finally
        {
            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));
        }
    }

    [Fact]
    public void Save_WaitsForInFlightBackgroundWriteAndOlderQueueCannotOverwriteIt()
    {
        using var workspace = new TempWorkspace();
        using var writer = new BlockingWriter();
        var store = CreateStore(workspace, writer.Write);
        Exception? failure = null;
        var synchronousSave = new Thread(() =>
        {
            try { store.Save(Settings(300)); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        try
        {
            store.SaveInBackground(Settings(100));
            Assert.True(writer.Entered.Wait(Timeout));
            store.SaveInBackground(Settings(200));

            synchronousSave.Start();
            Assert.True(SpinWait.SpinUntil(
                () => synchronousSave.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), Timeout));
            Assert.Equal(1, writer.WriteCount);

            writer.Release.Set();
            Assert.True(synchronousSave.Join(Timeout));
            Assert.True(store.WaitForPendingSaves(Timeout));

            Assert.Null(failure);
            Assert.Equal(300, Reload(workspace).Queue.MaxItems);
            Assert.Equal(1, writer.MaxActiveWrites);
        }
        finally
        {
            writer.Release.Set();
            if (synchronousSave.IsAlive) Assert.True(synchronousSave.Join(Timeout));
            Assert.True(store.WaitForPendingSaves(Timeout));
        }
    }

    [Fact]
    public void SaveInBackground_QueuedAfterWaitingSynchronousSaveKeepsNewerSnapshot()
    {
        using var workspace = new TempWorkspace();
        using var writer = new BlockingWriter();
        var store = CreateStore(workspace, writer.Write);
        Exception? failure = null;
        var synchronousSave = new Thread(() =>
        {
            try { store.Save(Settings(200)); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        try
        {
            store.SaveInBackground(Settings(100));
            Assert.True(writer.Entered.Wait(Timeout));

            synchronousSave.Start();
            Assert.True(SpinWait.SpinUntil(
                () => synchronousSave.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), Timeout));
            store.SaveInBackground(Settings(300));

            writer.Release.Set();
            Assert.True(synchronousSave.Join(Timeout));
            Assert.True(store.WaitForPendingSaves(Timeout));

            Assert.Null(failure);
            Assert.Equal(300, Reload(workspace).Queue.MaxItems);
            Assert.Equal(1, writer.MaxActiveWrites);
        }
        finally
        {
            writer.Release.Set();
            if (synchronousSave.IsAlive) Assert.True(synchronousSave.Join(Timeout));
            Assert.True(store.WaitForPendingSaves(Timeout));
        }
    }

    [Fact]
    public void WaitForPendingSaves_TimesOutWithoutDroppingTheExitSnapshot()
    {
        using var workspace = new TempWorkspace();
        using var writer = new BlockingWriter();
        var store = CreateStore(workspace, writer.Write);
        try
        {
            Assert.True(store.WaitForPendingSaves(TimeSpan.Zero));
            store.SaveInBackground(Settings(100));
            Assert.True(writer.Entered.Wait(Timeout));
            store.SaveInBackground(Settings(200));

            Assert.False(store.WaitForPendingSaves(TimeSpan.FromMilliseconds(20)));
            Assert.False(File.Exists(workspace.Paths.SettingsFile));

            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));
            Assert.Equal(200, Reload(workspace).Queue.MaxItems);
            Assert.True(store.WaitForPendingSaves(TimeSpan.Zero));
        }
        finally
        {
            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));
        }
    }

    [Fact]
    public void SaveInBackground_DeepCloneIsolatedFromLaterLiveSettingsMutations()
    {
        using var workspace = new TempWorkspace();
        using var writer = new BlockingWriter();
        var store = CreateStore(workspace, writer.Write);
        var live = Settings(200);
        live.Annotation.StrokeThickness = 7;
        live.Annotation.RecentColors = [ColorRgba.FromRgb(1, 2, 3)];
        live.Annotation.RecentFontFamilies = ["Consolas"];
        live.Ocr.PreferredLanguages = ["en-US"];
        live.Export.FileNamePattern = "snapshot_{yyyy}";
        try
        {
            store.SaveInBackground(Settings(100));
            Assert.True(writer.Entered.Wait(Timeout));
            store.SaveInBackground(live.DeepClone());

            live.Queue.MaxItems = 999;
            live.Annotation.StrokeThickness = 19;
            live.Annotation.RecentColors.Clear();
            live.Annotation.RecentFontFamilies[0] = "Arial";
            live.Ocr.PreferredLanguages.Add("ja-JP");
            live.Export.FileNamePattern = "mutated_{yyyy}";

            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));
            AppSettings saved = Reload(workspace);

            Assert.Equal(200, saved.Queue.MaxItems);
            Assert.Equal(7, saved.Annotation.StrokeThickness);
            Assert.Equal([ColorRgba.FromRgb(1, 2, 3)], saved.Annotation.RecentColors);
            Assert.Equal(["Consolas"], saved.Annotation.RecentFontFamilies);
            Assert.Equal(["en-US"], saved.Ocr.PreferredLanguages);
            Assert.Equal("snapshot_{yyyy}", saved.Export.FileNamePattern);
        }
        finally
        {
            writer.Release.Set();
            Assert.True(store.WaitForPendingSaves(Timeout));
        }
    }

    [Theory]
    [InlineData("Could not save settings")]
    [InlineData("Could not save editor preferences")]
    [InlineData("Could not save export options")]
    public void SaveInBackground_WriteFailureLogsOnceAndNextSaveRecovers(string message)
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var failure = new IOException("Simulated storage failure");
        int attempts = 0;
        var store = new SettingsStore(workspace.Paths, logger)
        {
            WriteAllText = (path, json) =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw failure;
                AtomicFile.WriteAllText(path, json);
            },
        };

        store.SaveInBackground(Settings(100), message);
        Assert.True(store.WaitForPendingSaves(Timeout));

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(failure, warning.Error);
        Assert.Equal(message, warning.Message);
        Assert.False(File.Exists(workspace.Paths.SettingsFile));

        store.SaveInBackground(Settings(200), message);
        Assert.True(store.WaitForPendingSaves(Timeout));
        Assert.Equal(200, Reload(workspace).Queue.MaxItems);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void SaveInBackground_SerializationFailureLogsWarningAndCompletes()
    {
        using var workspace = new TempWorkspace();
        var logger = new RecordingLogger();
        var store = new SettingsStore(workspace.Paths, logger);
        AppSettings invalid = Settings(100);
        invalid.Annotation.StrokeThickness = double.NaN;

        store.SaveInBackground(invalid);

        Assert.True(store.WaitForPendingSaves(Timeout));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.NotNull(warning.Error);
        Assert.Equal("Could not save settings", warning.Message);
        Assert.False(File.Exists(workspace.Paths.SettingsFile));
    }

    [Fact]
    public void Save_SynchronousFailureStillReachesItsCaller()
    {
        using var workspace = new TempWorkspace();
        var failure = new IOException("Simulated storage failure");
        var logger = new RecordingLogger();
        var store = new SettingsStore(workspace.Paths, logger)
        {
            WriteAllText = (_, _) => throw failure,
        };

        Assert.Same(failure, Assert.Throws<IOException>(() => store.Save(Settings(100))));
        Assert.Empty(logger.Entries);
        Assert.True(store.WaitForPendingSaves(TimeSpan.Zero));
    }

    [Fact]
    public void SlowStorage_MeasuresSynchronousAndSnapshotQueueCallerTime()
    {
        using var workspace = new TempWorkspace();
        const int delayMilliseconds = 175;
        const int sampleCount = 5;
        int callerThread = Environment.CurrentManagedThreadId;
        var writeThreads = new ConcurrentQueue<int>();
        var writeTimes = new ConcurrentQueue<double>();
        var store = CreateStore(workspace, (path, json) =>
        {
            writeThreads.Enqueue(Environment.CurrentManagedThreadId);
            var watch = Stopwatch.StartNew();
            Thread.Sleep(delayMilliseconds);
            AtomicFile.WriteAllText(path, json);
            writeTimes.Enqueue(watch.Elapsed.TotalMilliseconds);
        });
        AppSettings live = Settings(200);
        _ = live.DeepClone();
        store.Save(live);
        writeThreads.Clear();
        writeTimes.Clear();
        var synchronous = new List<double>();
        var background = new List<double>();

        for (int sample = 0; sample < sampleCount; sample++)
        {
            var watch = Stopwatch.StartNew();
            store.Save(live);
            synchronous.Add(watch.Elapsed.TotalMilliseconds);
        }
        Assert.All(writeThreads, thread => Assert.Equal(callerThread, thread));
        double synchronousWriteMedian = writeTimes.Order().ElementAt(sampleCount / 2);
        writeThreads.Clear();
        writeTimes.Clear();

        for (int sample = 0; sample < sampleCount; sample++)
        {
            var watch = Stopwatch.StartNew();
            store.SaveInBackground(live.DeepClone());
            background.Add(watch.Elapsed.TotalMilliseconds);
            Assert.True(store.WaitForPendingSaves(Timeout));
        }

        Assert.Equal(sampleCount, writeThreads.Count);
        Assert.All(writeThreads, thread => Assert.NotEqual(callerThread, thread));
        Assert.Equal(200, Reload(workspace).Queue.MaxItems);
        _output.WriteLine(
            $"InjectedWriteDelayMs={delayMilliseconds}; Samples={sampleCount}; " +
            $"SynchronousCallerMedianMs={synchronous.Order().ElementAt(sampleCount / 2):F3}; " +
            $"SynchronousCallerMaxMs={synchronous.Max():F3}; " +
            $"DeepCloneAndQueueCallerMedianMs={background.Order().ElementAt(sampleCount / 2):F3}; " +
            $"DeepCloneAndQueueCallerMaxMs={background.Max():F3}; " +
            $"SynchronousWriteMedianMs={synchronousWriteMedian:F3}; " +
            $"BackgroundWriteMedianMs={writeTimes.Order().ElementAt(sampleCount / 2):F3}; " +
            "DurableWrites=AtomicFile.WriteAllText; AllBackgroundWritesOffCallerThread=True");
    }

    private static AppSettings Settings(int maxItems) => new() { Queue = { MaxItems = maxItems } };

    private static SettingsStore CreateStore(TempWorkspace workspace, Action<string, string> write) =>
        new(workspace.Paths, NullLogger<SettingsStore>.Instance) { WriteAllText = write };

    private static AppSettings Reload(TempWorkspace workspace) =>
        new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();

    private sealed class BlockingWriter : IDisposable
    {
        private int _writeCount;
        private int _activeWrites;
        private int _maxActiveWrites;

        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int MaxActiveWrites => Volatile.Read(ref _maxActiveWrites);

        public void Write(string path, string json)
        {
            int active = Interlocked.Increment(ref _activeWrites);
            int maximum;
            do { maximum = Volatile.Read(ref _maxActiveWrites); }
            while (active > maximum
                && Interlocked.CompareExchange(ref _maxActiveWrites, active, maximum) != maximum);
            try
            {
                if (Interlocked.Increment(ref _writeCount) == 1)
                {
                    Entered.Set();
                    if (!Release.Wait(Timeout)) throw new TimeoutException("First settings write was not released");
                }
                AtomicFile.WriteAllText(path, json);
            }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class RecordingLogger : ILogger<SettingsStore>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Entries.Enqueue((logLevel, exception, formatter(state, exception)));
        }
    }
}
