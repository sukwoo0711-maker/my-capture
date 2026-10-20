using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPreferenceBackgroundSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreferencePaths_QueueIsolatedSnapshotsWithoutBlockingDispatcher(bool export) => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int writerThread = 0;
        using var fixture = new Fixture((path, json) =>
        {
            writerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test writer was not released.");
            AtomicFile.WriteAllText(path, json);
        });
        try
        {
            fixture.Store.SaveInBackground(fixture.Settings.DeepClone());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            if (export)
            {
                fixture.InitializeQueue();
                fixture.Commit.RememberExportOptions(new ImageExportOptions { LongEdge = 1280 });
            }
            else
            {
                fixture.Remember(new AnnotationDefaults { FontSize = 37, RecentFontFamilies = ["Consolas"] });
                fixture.Flush();
            }
            fixture.Settings.Annotation.FontSize = 71;
            fixture.Settings.Annotation.RecentFontFamilies.Clear();
            fixture.Settings.Export.LastImageExport = new ImageExportOptions { LongEdge = 800 };

            bool heartbeat = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => heartbeat = true));
            PumpUntil(() => heartbeat);
            Assert.NotEqual(Environment.CurrentManagedThreadId, writerThread);
            Assert.False(fixture.Store.WaitForPendingSaves(TimeSpan.Zero));

            release.Set();
            Assert.True(fixture.Store.WaitForPendingSaves(TimeSpan.FromSeconds(5)));
            AppSettings saved = fixture.Read();
            if (export) Assert.Equal(1280, saved.Export.LastImageExport.LongEdge);
            else
            {
                Assert.Equal(37, saved.Annotation.FontSize);
                Assert.Equal(["Consolas"], saved.Annotation.RecentFontFamilies);
            }
        }
        finally { release.Set(); }
    });

    [Fact]
    public void ExitFlush_QueuesPendingTimerAndAwaitsDurabilityWithoutBlockingDispatcher() => StaTestHost.Run(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture((path, json) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test writer was not released.");
            AtomicFile.WriteAllText(path, json);
        });
        try
        {
            fixture.Remember(new AnnotationDefaults { FontSize = 43 });
            Assert.True(fixture.Timer.IsEnabled);
            Assert.False(File.Exists(fixture.Paths.SettingsFile));

            Task flushing = fixture.FlushForExit();
            Assert.False(fixture.Timer.IsEnabled);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(flushing.IsCompleted);
            bool heartbeat = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => heartbeat = true));
            PumpUntil(() => heartbeat);
            Assert.False(flushing.IsCompleted);

            release.Set();
            PumpUntil(() => flushing.IsCompleted);
            flushing.GetAwaiter().GetResult();
            Assert.Equal(43, fixture.Read().Annotation.FontSize);
        }
        finally { release.Set(); }
    });

    [Theory]
    [InlineData(false, "Could not save editor preferences")]
    [InlineData(true, "Could not persist the last export options")]
    public void PreferencePaths_LogOneWarningWithoutThrowingIntoDispatcher(bool export, string message) => StaTestHost.Run(() =>
    {
        var logger = new WarningLogger();
        using var fixture = new Fixture((_, _) => throw new IOException("Isolated test failure"), logger);
        if (export)
        {
            fixture.InitializeQueue();
            fixture.Commit.RememberExportOptions(new ImageExportOptions { LongEdge = 1280 });
        }
        else
        {
            fixture.Remember(new AnnotationDefaults { FontSize = 37 });
            fixture.Flush();
        }
        Assert.True(fixture.Store.WaitForPendingSaves(TimeSpan.FromSeconds(5)));
        var warning = Assert.Single(logger.Warnings);
        Assert.Equal(message, warning.Message);
        Assert.IsType<IOException>(warning.Error);
    });

    private static void PumpUntil(Func<bool> completed)
    {
        var timer = Stopwatch.StartNew();
        while (!completed() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(completed(), "The dispatcher operation did not finish.");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mc-pref-").FullName;
        private readonly ServiceProvider _services;
        internal AppSettings Settings { get; } = new();
        internal AppPaths Paths { get; }
        internal SettingsStore Store { get; }
        internal DispatcherTimer Timer => (DispatcherTimer)Get("_preferenceSaveTimer")!;
        internal CaptureCommitService Commit => (CaptureCommitService)Get("_commit")!;

        internal Fixture(Action<string, string> writer, ILogger<SettingsStore>? logger = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Paths = AppPaths.CreateForRoot(_root);
            Store = new SettingsStore(Paths, logger ?? NullLogger<SettingsStore>.Instance) { WriteAllText = writer };
            _services = new ServiceCollection().AddLogging().AddSingleton(Paths).AddSingleton(Store).BuildServiceProvider();
            typeof(App).GetField("_services", Flags)!.SetValue(_app, _services);
            typeof(App).GetField("_settings", Flags)!.SetValue(_app, Settings);
        }

        internal void InitializeQueue() => Invoke("InitializeQueue");
        internal void Remember(AnnotationDefaults defaults) => Invoke("RememberEditorPreferences", defaults);
        internal void Flush() => Invoke("FlushEditorPreferences");
        internal Task FlushForExit() => (Task)Invoke("FlushPreferenceSavesForExitAsync")!;
        internal AppSettings Read() => JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(Paths.SettingsFile), JsonDefaults.Readable)!;
        private object? Get(string name) => typeof(App).GetField(name, Flags)!.GetValue(_app);
        private object? Invoke(string name, params object[] args) => typeof(App).GetMethod(name, Flags)!.Invoke(_app, args);

        public void Dispose()
        {
            if (Get("_preferenceSaveTimer") is DispatcherTimer timer) timer.Stop();
            Assert.True(Store.WaitForPendingSaves(TimeSpan.FromSeconds(5)));
            _services.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previous);
            TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }

    private sealed class WarningLogger : ILogger<SettingsStore>
    {
        internal List<(string Message, Exception? Error)> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add((formatter(state, exception), exception));
        }
    }
}
