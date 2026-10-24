using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSessionShutdownTests
{
    [Fact]
    public Task FrameworkShutdown_WithoutPreSavePreservesTheLastSavedSession() =>
        Isolated(nameof(FrameworkShutdown_WithoutPreSavePreservesTheLastSavedSession), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            pin.State.SetZoom(1.4);
            fixture.Service.SaveNow();
            pin.State.SetZoom(2.3);

            fixture.ShutdownThroughFramework();

            Assert.True(pin.IsClosed);
            Assert.Equal(0, fixture.Manager.Count);
            PinSessionPin saved = Assert.Single(fixture.Store.Load()).Pin;
            Assert.Equal(ShutdownFixture.OriginalText, saved.OriginalText);
            Assert.Equal(1.4, saved.Zoom);
        });

    [Fact]
    public Task StoreUpdatePreparation_SavesPinsBeforeDeploymentAndKeepsServiceUsable() =>
        Isolated(nameof(StoreUpdatePreparation_SavesPinsBeforeDeploymentAndKeepsServiceUsable), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            pin.State.SetZoom(1.8);

            Assert.True(fixture.PrepareStoreUpdate());

            Assert.Equal(1.8, Assert.Single(fixture.Store.Load()).Pin.Zoom);
            Assert.False(pin.IsClosed);
            pin.State.SetZoom(2.2);
            fixture.Service.SaveNow();
            Assert.Equal(2.2, Assert.Single(fixture.Store.Load()).Pin.Zoom);
            fixture.ShutdownThroughFramework();
            Assert.Equal(2.2, Assert.Single(fixture.Store.Load()).Pin.Zoom);
        });

    [Fact]
    public Task CancelledStoreUpdatePreparation_DoesNotSavePins() =>
        Isolated(nameof(CancelledStoreUpdatePreparation_DoesNotSavePins), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            fixture.App.PrepareCapturePersistenceForExitAsync = () => Task.FromResult(false);

            Assert.False(fixture.PrepareStoreUpdate());

            Assert.False(pin.IsClosed);
            Assert.False(File.Exists(Path.Combine(fixture.SessionRoot, "session.json")));
        });

    [Fact]
    public Task ExplicitShutdown_PreservesLivePinsAfterFrameworkClosesWindows() =>
        Isolated(nameof(ExplicitShutdown_PreservesLivePinsAfterFrameworkClosesWindows), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            fixture.Service.SaveNow();
            pin.State.SetZoom(1.7);
            pin.State.SetOpacity(0.6);
            bool closedBeforeExitEvent = false;
            fixture.App.Exit += (_, _) => closedBeforeExitEvent = pin.IsClosed && fixture.Manager.Count == 0;

            fixture.ShutdownExplicitly();

            Assert.True(closedBeforeExitEvent);
            PinSessionPin saved = Assert.Single(fixture.Store.Load()).Pin;
            Assert.Equal(ShutdownFixture.OriginalText, saved.OriginalText);
            Assert.Equal(1.7, saved.Zoom);
            Assert.Equal(0.6, saved.Opacity);
            Assert.Equal(0, fixture.Manager.Count);
        });

    [Fact]
    public Task SessionEnding_SavesAfterExistingHandlersAndSurvivesFrameworkClosure() =>
        Isolated(nameof(SessionEnding_SavesAfterExistingHandlersAndSurvivesFrameworkClosure), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            bool existingHandlerRan = false;
            fixture.App.SessionEnding += (_, _) =>
            {
                existingHandlerRan = true;
                pin.State.SetZoom(2.1);
                pin.State.SetOpacity(0.5);
            };

            SessionEndingCancelEventArgs ending = SessionEndingArgs(ReasonSessionEnding.Logoff);
            fixture.App.RaiseSessionEnding(ending);

            Assert.True(existingHandlerRan);
            Assert.False(ending.Cancel);
            Assert.False(pin.IsClosed);
            Assert.Equal(2.1, Assert.Single(fixture.Store.Load()).Pin.Zoom);

            fixture.ShutdownThroughFramework();

            Assert.True(pin.IsClosed);
            Assert.Equal(0, fixture.Manager.Count);
            PinSessionPin saved = Assert.Single(fixture.Store.Load()).Pin;
            Assert.Equal(ShutdownFixture.OriginalText, saved.OriginalText);
            Assert.Equal(2.1, saved.Zoom);
            Assert.Equal(0.5, saved.Opacity);
        });

    [Fact]
    public Task CancelledSessionEnding_DoesNotFinalizePinsAndLaterExitSavesCurrentState() =>
        Isolated(nameof(CancelledSessionEnding_DoesNotFinalizePinsAndLaterExitSavesCurrentState), () =>
        {
            using var fixture = new ShutdownFixture();
            PinWindow pin = fixture.OpenPin();
            fixture.App.SessionEnding += (_, args) => args.Cancel = true;
            SessionEndingCancelEventArgs ending = SessionEndingArgs(ReasonSessionEnding.Shutdown);

            fixture.App.RaiseSessionEnding(ending);

            Assert.True(ending.Cancel);
            Assert.False(pin.IsClosed);
            Assert.False(File.Exists(Path.Combine(fixture.SessionRoot, "session.json")));
            pin.State.SetZoom(2.4);

            fixture.ShutdownExplicitly();

            Assert.Equal(2.4, Assert.Single(fixture.Store.Load()).Pin.Zoom);
        });

    /// <summary>
    /// Closing for exit skips and then clears the closed-pin history, so only app shutdown may use it.
    /// A pin board switch closes live pins only after a complete save reads back.
    /// </summary>
    [Fact]
    public void ProductCode_ClosesAllPinsForExitOnlyFromAppShutdown()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MyCapture.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var uses = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(item => item.line.Contains("CloseAllForExit(", StringComparison.Ordinal)
                && !item.line.Contains("void CloseAllForExit(", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(root, item.file)}:{item.index + 1}")
            .ToArray();
        string use = Assert.Single(uses);
        Assert.StartsWith(Path.Combine("src", "MyCapture.App", "App.xaml.cs"), use);
    }

    private static Task Isolated(string name, Action verify) =>
        SettingsThemePreviewTests.RunIsolated(name, verify, typeof(PinSessionShutdownTests));

    private static SessionEndingCancelEventArgs SessionEndingArgs(ReasonSessionEnding reason)
    {
        ConstructorInfo? constructor = typeof(SessionEndingCancelEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(ReasonSessionEnding)], modifiers: null);
        Assert.NotNull(constructor);
        return (SessionEndingCancelEventArgs)constructor.Invoke([reason]);
    }

    private sealed class ShutdownFixture : IDisposable
    {
        internal const string OriginalText = "spec\t원문  \r\n  value\t\r\n";
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("pin-shutdown-").FullName;
        private readonly PinSettings _settings = new() { RestoreOpenPinsOnLaunch = true };
        private bool _exited;

        internal ShutdownFixture()
        {
            App = new StartupSuppressedApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            App.Exit += (_, _) => _exited = true;
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                App.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            SessionRoot = AppPaths.CreateForRoot(_root).PinSessionRoot;
            Store = new PinSessionStore(SessionRoot, NullLogger.Instance);
            Manager = new PinManager(() => _settings, NullLogger.Instance);
            Service = new PinSessionService(Manager, Store, () => _settings, App.Dispatcher, NullLogger.Instance);
            SetAppField("_pins", Manager);
            SetAppField("_pinSession", Service);
            Wait(Service.RestoreAsync());
        }

        internal StartupSuppressedApp App { get; }
        internal string SessionRoot { get; }
        internal PinSessionStore Store { get; }
        internal PinManager Manager { get; }
        internal PinSessionService Service { get; }

        internal PinWindow OpenPin()
        {
            var bitmap = BitmapSource.Create(24, 16, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 16 * 4], 24 * 4);
            bitmap.Freeze();
            return Manager.RestoreSessionPin(new PinManager.ClosedPin(
                PinContent.FromText(bitmap, OriginalText, isTable: false), 24, 16, 1, 1, null));
        }

        internal void ShutdownExplicitly()
        {
            Task<bool> requested = App.RequestSafeShutdownAsync();
            Wait(requested);
            Assert.True(requested.GetAwaiter().GetResult());
            WaitForExit();
        }

        internal void ShutdownThroughFramework()
        {
            App.Shutdown();
            WaitForExit();
        }

        internal bool PrepareStoreUpdate()
        {
            MethodInfo method = typeof(MyCapture.App.App).GetMethod("PrepareAndHoldStoreUpdateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var preparation = (Task<bool>)method.Invoke(App, null)!;
            Wait(preparation);
            return preparation.GetAwaiter().GetResult();
        }

        private void SetAppField(string field, object value) =>
            typeof(MyCapture.App.App).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(App, value);

        private void WaitForExit()
        {
            if (_exited) return;
            var frame = new DispatcherFrame();
            ExitEventHandler completed = (_, _) => frame.Continue = false;
            App.Exit += completed;
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, App.Dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); App.Exit -= completed; }
            Assert.True(_exited, "WPF did not finish closing its windows and raising Exit.");
        }

        public void Dispose()
        {
            Service.Dispose();
            Manager.CloseAllForExit();
            if (!_exited) ShutdownThroughFramework();
            TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Assert.True(task.IsCompleted, "The pin shutdown operation did not finish.");
        }
        task.GetAwaiter().GetResult();
    }

    private sealed class StartupSuppressedApp : MyCapture.App.App
    {
        // Application construction queues Startup when the dispatcher next pumps. Never run
        // the product startup path: no tray, clipboard, registration, or user-data services.
        protected override void OnStartup(StartupEventArgs e) { }

        internal void RaiseSessionEnding(SessionEndingCancelEventArgs args) => base.OnSessionEnding(args);
    }
}
