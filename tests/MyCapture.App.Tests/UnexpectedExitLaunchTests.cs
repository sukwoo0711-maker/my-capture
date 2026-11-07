using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The app's side of the unexpected-exit notice: which launch shows it, and which exits mark its
/// records handled. Each test runs a startup-suppressed App in a process of its own.
/// </summary>
public sealed class UnexpectedExitLaunchTests
{
    [Fact]
    public Task StepsTheAppMaySurviveKeepTheRecordsAndOnlyFinalExitsMarkThem() =>
        Isolated(nameof(StepsTheAppMaySurviveKeepTheRecordsAndOnlyFinalExitsMarkThem), () =>
        {
            using var fixture = new LaunchFixture();
            Assert.True(fixture.Exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));

            // A Store update holds the app for the installer, but a failed update leaves it running.
            UnexpectedExitNotice notice = fixture.OfferAgain();
            Assert.True(fixture.Wait(fixture.Call<Task<bool>>("PrepareAndHoldStoreUpdateAsync")));
            Assert.False(notice.Dialog.IsVisible);
            fixture.Wait(notice.Handled);
            Assert.Single(fixture.Exits.ReadPending());
            fixture.Call("ReleaseStoreUpdateProtection");

            // An import holds the app the same way and then lets it run on.
            notice = fixture.OfferAgain();
            Assert.True(fixture.Wait(fixture.Call<Task<bool>>("PrepareAndHoldForLegacyImportAsync")));
            Assert.False(notice.Dialog.IsVisible);
            fixture.Wait(notice.Handled);
            Assert.Single(fixture.Exits.ReadPending());
            fixture.Call("ReleaseStoreUpdateProtection");

            // A cancelled session end keeps the notice; one that goes ahead is final.
            notice = fixture.OfferAgain();
            SessionEndingCancelEventHandler cancel = (_, args) => args.Cancel = true;
            fixture.App.SessionEnding += cancel;
            fixture.App.RaiseSessionEnding(SessionEndingArgs(ReasonSessionEnding.Logoff));
            Assert.True(notice.Dialog.IsVisible);
            Assert.Single(fixture.Exits.ReadPending());
            fixture.App.SessionEnding -= cancel;
            fixture.App.RaiseSessionEnding(SessionEndingArgs(ReasonSessionEnding.Logoff));
            Assert.False(notice.Dialog.IsVisible);
            fixture.Wait(notice.Handled);
            Assert.Empty(fixture.Exits.ReadPending());

            // An explicit exit marks the record before the process shuts down.
            Assert.True(fixture.Exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure()) with
            { Utc = ErrorReportTests.FixedTime.AddMinutes(1) }));
            notice = fixture.OfferAgain();
            Assert.True(fixture.Wait(fixture.App.RequestSafeShutdownAsync()));
            Assert.False(notice.Dialog.IsVisible);
            Assert.True(notice.Handled.IsCompleted);
            Assert.Empty(fixture.Exits.ReadPending());
            fixture.WaitForExit();
            Assert.Equal(4, fixture.Shown.Count);
        });

    [Fact]
    public Task QuietLaunchShowsTheNoticeOnceWithTheFirstLibraryOrSettingsWindow() =>
        Isolated(nameof(QuietLaunchShowsTheNoticeOnceWithTheFirstLibraryOrSettingsWindow), () =>
        {
            using var fixture = new LaunchFixture();
            Assert.True(fixture.Exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));

            // A Windows sign-in launch stays in the tray: nothing on screen, and the record stays pending.
            fixture.Wait(fixture.Offer(quietLaunch: true));
            Ocr410Fixture.Pump();
            Assert.Empty(fixture.Shown);
            Assert.Single(fixture.Exits.ReadPending());

            // Another window does not bring it.
            var other = new Window();
            fixture.WindowLoaded(other);
            Ocr410Fixture.Pump();
            Assert.Empty(fixture.Shown);

            // The first library window does, once; settings opened after it shows no second notice.
            fixture.WindowLoaded((Window)RuntimeHelpers.GetUninitializedObject(typeof(GalleryWindow)));
            UnexpectedExitNotice notice = fixture.WaitForNotice();
            Assert.Same(notice.Dialog, Assert.Single(fixture.Shown));
            Assert.True(notice.Dialog.IsVisible);
            fixture.WindowLoaded((Window)RuntimeHelpers.GetUninitializedObject(typeof(SettingsWindow)));
            Ocr410Fixture.Pump();
            Assert.Single(fixture.Shown);
            Assert.Single(fixture.Exits.ReadPending());

            // The framework's own exit is final: the notice closes and its record is marked.
            fixture.ShutdownThroughFramework();
            Assert.False(notice.Dialog.IsVisible);
            Assert.True(notice.Handled.Wait(TimeSpan.FromSeconds(10)));
            Assert.Empty(fixture.Exits.ReadPending());
        });

    [Fact]
    public Task QuietLaunchThatEndsBeforeAnyWindowLeavesTheRecordForTheNextLaunch() =>
        Isolated(nameof(QuietLaunchThatEndsBeforeAnyWindowLeavesTheRecordForTheNextLaunch), () =>
        {
            using var fixture = new LaunchFixture();
            Assert.True(fixture.Exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));
            fixture.Wait(fixture.Offer(quietLaunch: true));
            fixture.ShutdownThroughFramework();
            Assert.Empty(fixture.Shown);
            // Nothing read or marked it, so the next launch that shows a window offers it.
            Assert.EndsWith(".exit", Assert.Single(fixture.Exits.ReadPending()).Name, StringComparison.Ordinal);
        });

    private static Task Isolated(string name, Action verify) =>
        SettingsThemePreviewTests.RunIsolated(name, verify, typeof(UnexpectedExitLaunchTests));

    private static SessionEndingCancelEventArgs SessionEndingArgs(ReasonSessionEnding reason)
    {
        ConstructorInfo? constructor = typeof(SessionEndingCancelEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(ReasonSessionEnding)], modifiers: null);
        Assert.NotNull(constructor);
        return (SessionEndingCancelEventArgs)constructor.Invoke([reason]);
    }

    private sealed class LaunchFixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("mc-unexpected-exit-launch-");
        private readonly UnexpectedExitLog? _previousExits = ErrorReporting.Exits;
        private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;
        private bool _exited;

        internal LaunchFixture()
        {
            App = new StartupSuppressedApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            App.Exit += (_, _) => _exited = true;
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                App.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            ThemeService.Apply(AppTheme.Daylight);
            // The product awaits these on its dispatcher, as OnStartup does.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(App.Dispatcher));
            Exits = new UnexpectedExitLog(Path.Combine(_root, "unexpected-exits"), path =>
            {
                Directory.CreateDirectory(Path.Combine(_root, "trash"));
                File.Move(path, Path.Combine(_root, "trash", Path.GetFileName(path)));
            });
            ErrorReporting.Exits = Exits;
            // Off screen and inactive: other sessions share this desktop.
            App.ShowUnexpectedExitNotice = dialog =>
            {
                Shown.Add(dialog);
                FluidMotion.SetWindowEntrance(dialog, false);
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -10000;
                dialog.Top = -10000;
                dialog.ShowActivated = false;
                dialog.ShowInTaskbar = false;
                dialog.Show();
            };
        }

        internal StartupSuppressedApp App { get; }
        internal UnexpectedExitLog Exits { get; }
        internal List<ThemedDialog> Shown { get; } = [];

        internal Task Offer(bool quietLaunch) => Call<Task>("OfferUnexpectedExitAfterLaunchAsync", quietLaunch);

        /// <summary>The offer of a later launch within this process: the earlier offer is forgotten.</summary>
        internal UnexpectedExitNotice OfferAgain()
        {
            SetField("_unexpectedExitNoticeOffered", false);
            SetField("_unexpectedExitNotice", null);
            Wait(Offer(quietLaunch: false));
            UnexpectedExitNotice notice = Assert.IsType<UnexpectedExitNotice>(GetField("_unexpectedExitNotice"));
            Assert.True(notice.Dialog.IsVisible);
            return notice;
        }

        /// <summary>What the app's Loaded handler does for every window.</summary>
        internal void WindowLoaded(Window window) => Call("OfferDeferredUnexpectedExitNotice", window);

        internal UnexpectedExitNotice WaitForNotice()
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                if (GetField("_unexpectedExitNotice") is UnexpectedExitNotice notice) return notice;
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The deferred notice did not appear.");
                Ocr410Fixture.Pump();
            }
        }

        internal T Call<T>(string method, params object?[] arguments) => (T)Method(method).Invoke(App, arguments)!;
        internal void Call(string method, params object?[] arguments) => Method(method).Invoke(App, arguments);

        internal bool Wait(Task<bool> task)
        {
            Wait((Task)task);
            return task.GetAwaiter().GetResult();
        }

        internal void Wait(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                Dispatcher dispatcher = App.Dispatcher;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                var timeout = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Send,
                    (_, _) => frame.Continue = false, dispatcher);
                try { Dispatcher.PushFrame(frame); }
                finally { timeout.Stop(); }
                Assert.True(task.IsCompleted, "The notice step did not finish.");
            }
            task.GetAwaiter().GetResult();
        }

        internal void ShutdownThroughFramework()
        {
            App.Shutdown();
            WaitForExit();
        }

        internal void WaitForExit()
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
            try
            {
                if (!_exited) ShutdownThroughFramework();
            }
            finally
            {
                ErrorReporting.Exits = _previousExits;
                SynchronizationContext.SetSynchronizationContext(_previousContext);
                OwnedTestDirectory.Delete(_root);
            }
        }

        private static MethodInfo Method(string name) =>
            typeof(MyCapture.App.App).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MyCapture.App.App), name);

        private object? GetField(string name) => Field(name).GetValue(App);
        private void SetField(string name, object? value) => Field(name).SetValue(App, value);

        private static FieldInfo Field(string name) =>
            typeof(MyCapture.App.App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(MyCapture.App.App), name);
    }

    private sealed class StartupSuppressedApp : MyCapture.App.App
    {
        // Application construction queues Startup when the dispatcher next pumps. Never run
        // the product startup path: no tray, clipboard, registration, or user-data services.
        protected override void OnStartup(StartupEventArgs e) { }

        internal void RaiseSessionEnding(SessionEndingCancelEventArgs args) => base.OnSessionEnding(args);
    }
}
