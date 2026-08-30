using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// End-to-end behaviour of <see cref="SettingsApplyService"/>, using fakes for the native
/// seams (hotkey registrar and Run key) so no global hotkey or registry state is touched.
/// The window's XAML/binding path is verified separately by the <c>--selftest-settings</c>
/// live smoke, which avoids polluting this assembly's shared WPF Application state.
/// </summary>
public sealed class SettingsApplyAndWindowTests
{
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        private readonly System.Collections.Generic.HashSet<(uint, uint)> _blocked;
        private readonly System.Collections.Generic.Dictionary<int, (uint, uint)> _held = [];

        public FakeRegistrar(System.Collections.Generic.IEnumerable<Hotkey>? blocked = null)
        {
            _blocked = [];
            if (blocked is not null)
            {
                foreach (Hotkey h in blocked)
                {
                    _blocked.Add(((uint)h.Modifiers, h.VirtualKey));
                }
            }
        }

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            var key = ((uint)hotkey.Modifiers, hotkey.VirtualKey);
            if (_blocked.Contains(key) || System.Linq.Enumerable.Contains(_held.Values, key))
            {
                errorCode = 1409;
                errorMessage = "Hot key is already registered.";
                return false;
            }

            _held[id] = key;
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);

        /// <summary>The set of (modifiers, virtualKey) chords currently registered.</summary>
        public System.Collections.Generic.IReadOnlyCollection<(uint Modifiers, uint VirtualKey)> Held =>
            System.Linq.Enumerable.ToList(_held.Values);

        public bool Holds(Hotkey hotkey) =>
            System.Linq.Enumerable.Contains(_held.Values, ((uint)hotkey.Modifiers, hotkey.VirtualKey));
    }

    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string name) => _values.TryGetValue(name, out string? v) ? v : null;
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mycapture-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Apply_SavesPublishesAndReconfigures()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

                AppSettings live = store.Load();
                AppSettings published = live;

                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, new FakeRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);

                var startup = new StartupRegistrationService(new FakeRunKeyStore(), @"C:\Apps\MyCapture.exe");

                var apply = new SettingsApplyService(
                    store,
                    () => published,
                    updated => published = updated,
                    queue: null,
                    hotkeys,
                    startup,
                    NullLogger.Instance);

                var draft = new SettingsDraft(live);
                draft.MaxItems = "450";
                draft.NotifyOnQuickSave = false;
                AppSettings next = draft.ToAppSettings();

                SettingsApplyResult result = apply.Apply(next);

                Assert.True(result.Saved);
                Assert.True(result.HotkeysApplied);
                Assert.Same(next, published);                 // publish reassigned the live ref
                Assert.Equal(450, store.Load().Queue.MaxItems); // persisted atomically
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Apply_EnablesAutostartThroughFakeStore()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
                AppSettings live = store.Load();
                AppSettings published = live;

                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, new FakeRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);

                var fakeRun = new FakeRunKeyStore();
                var startup = new StartupRegistrationService(fakeRun, @"C:\Apps\MyCapture.exe");
                var apply = new SettingsApplyService(store, () => published, u => published = u, null, hotkeys, startup, NullLogger.Instance);

                var draft = new SettingsDraft(live) { LaunchAtLogin = true };
                SettingsApplyResult result = apply.Apply(draft.ToAppSettings());

                Assert.True(result.StartupApplied);
                Assert.True(startup.IsEnabled());
                Assert.Equal("\"C:\\Apps\\MyCapture.exe\"", fakeRun.GetValue(StartupRegistrationService.RunValueName));
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Apply_ReportsRestartRequiredWhenCapturesRootChanges()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
                AppSettings live = store.Load();
                AppSettings published = live;

                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, new FakeRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);
                var startup = new StartupRegistrationService(new FakeRunKeyStore(), @"C:\Apps\MyCapture.exe");
                var apply = new SettingsApplyService(store, () => published, u => published = u, null, hotkeys, startup, NullLogger.Instance);

                var draft = new SettingsDraft(live)
                {
                    CapturesDirectoryOverride = Path.Combine(root, "relocated"),
                };
                SettingsApplyResult result = apply.Apply(draft.ToAppSettings());

                Assert.True(result.RestartRequired);
                Assert.Contains(result.Messages, m => m.Contains("다시 시작", StringComparison.Ordinal));
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    /// <summary>
    /// A queue whose index Save always fails with a persistence fault, used to prove that a
    /// non-fatal queue-save error after a successful settings save does not reverse the
    /// already-persisted user settings.
    /// </summary>
    private static CaptureQueue NewQueue(AppPaths paths) =>
        new(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    /// <summary>
    /// Turns the settings file path into a directory so the next atomic write fails
    /// deterministically with a persistence fault (IOException/UnauthorizedAccessException)
    /// on every machine — no read-only volume or full disk required.
    /// </summary>
    private static void ForceSettingsSaveToFail(AppPaths paths)
    {
        // Load already ran and created the data root; a directory sitting where the file
        // must be written makes File.Move/File.Replace throw when the atomic swap runs.
        Directory.CreateDirectory(paths.SettingsFile);
    }

    [Fact]
    public void Apply_WhenSaveFails_RollsBackHotkeysAndStartup_AndReportsUnsaved()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);

                AppSettings live = store.Load();
                AppSettings published = live;

                var registrar = new FakeRegistrar();
                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);

                // The capture chord the app is running with before the (doomed) apply.
                Hotkey originalCapture = live.Hotkeys.Capture;
                Assert.True(registrar.Holds(originalCapture)); // sanity: currently held

                var fakeRun = new FakeRunKeyStore();
                var startup = new StartupRegistrationService(fakeRun, @"C:\Apps\MyCapture.exe");
                // Prior actual state: launch-at-login is OFF (Run key empty).
                Assert.False(startup.IsEnabled());

                bool published_ = false;
                var queue = NewQueue(paths);
                queue.Load();

                var apply = new SettingsApplyService(
                    store,
                    () => published,
                    u => { published = u; published_ = true; },
                    queue,
                    hotkeys,
                    startup,
                    NullLogger.Instance);

                // Candidate draft: a NEW capture chord and launch-at-login turned ON.
                var draft = new SettingsDraft(live)
                {
                    CaptureHotkey = "Ctrl+Alt+F1",
                    LaunchAtLogin = true,
                };
                AppSettings next = draft.ToAppSettings();
                Hotkey candidateCapture = next.Hotkeys.Capture;
                Assert.NotEqual(originalCapture, candidateCapture); // sanity: really different

                ForceSettingsSaveToFail(paths);

                SettingsApplyResult result = apply.Apply(next);

                // 1) Reported as unsaved with an actionable Korean message; did NOT throw.
                Assert.False(result.Saved);
                Assert.NotEmpty(result.Messages);
                Assert.Contains(result.Messages, m => m.Contains("저장하지 못했습니다", StringComparison.Ordinal));

                // 2) Old hotkeys held; the candidate chord was rolled back.
                Assert.True(registrar.Holds(originalCapture));
                Assert.False(registrar.Holds(candidateCapture));
                Assert.Contains(GlobalHotkeyCommand.CaptureRegion, hotkeys.RegisteredCommands);

                // 3) Startup restored to its exact prior state (still OFF, Run key empty).
                Assert.False(startup.IsEnabled());
                Assert.Null(fakeRun.GetValue(StartupRegistrationService.RunValueName));

                // 4) Nothing was published (live settings untouched).
                Assert.False(published_);
                Assert.Same(live, published);

                // 5) Persisted settings unchanged: reading back (after removing the blocker)
                //    still yields the original capture chord, never the candidate.
                Directory.Delete(paths.SettingsFile, recursive: true);
                AppSettings reloaded = store.Load();
                Assert.Equal(originalCapture, reloaded.Hotkeys.Capture);
                Assert.False(reloaded.General.LaunchAtLogin);
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Apply_WhenSaveFails_AfterAutostartWasEnabled_RestoresRunKeyToEnabled()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
                AppSettings live = store.Load();
                AppSettings published = live;

                var registrar = new FakeRegistrar();
                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);

                var fakeRun = new FakeRunKeyStore();
                var startup = new StartupRegistrationService(fakeRun, @"C:\Apps\MyCapture.exe");
                // Prior actual state: launch-at-login is ON.
                startup.Enable();
                Assert.True(startup.IsEnabled());

                var apply = new SettingsApplyService(
                    store, () => published, u => published = u, null, hotkeys, startup, NullLogger.Instance);

                // Candidate turns it OFF, but the save will fail so the prior ON must return.
                var draft = new SettingsDraft(live) { LaunchAtLogin = false };
                AppSettings next = draft.ToAppSettings();

                ForceSettingsSaveToFail(paths);

                SettingsApplyResult result = apply.Apply(next);

                Assert.False(result.Saved);
                // Exact prior state restored: still enabled, Run key points at this exe.
                Assert.True(startup.IsEnabled());
                Assert.Equal(
                    "\"C:\\Apps\\MyCapture.exe\"",
                    fakeRun.GetValue(StartupRegistrationService.RunValueName));
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    [Fact]
    public void Apply_WhenSaveSucceeds_ButQueueSaveFails_ReportsNonFatalWithoutReversingSettings()
    {
        RunSta(() =>
        {
            string root = NewTempRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
                AppSettings live = store.Load();
                AppSettings published = live;

                using var window = new NativeMessageWindow();
                using var hotkeys = new GlobalHotkeyService(window, new FakeRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
                hotkeys.Initialize(live.Hotkeys);
                var startup = new StartupRegistrationService(new FakeRunKeyStore(), @"C:\Apps\MyCapture.exe");

                var queue = NewQueue(paths);
                queue.Load();

                var apply = new SettingsApplyService(
                    store, () => published, u => published = u, queue, hotkeys, startup, NullLogger.Instance);

                var draft = new SettingsDraft(live) { MaxItems = "321" };
                AppSettings next = draft.ToAppSettings();

                // Block the index write only (settings.json write path stays clear).
                Directory.CreateDirectory(paths.IndexFile);

                SettingsApplyResult result = apply.Apply(next);

                // Settings persisted and published despite the queue index write failing.
                Assert.True(result.Saved);
                Assert.Same(next, published);
                Assert.Equal(321, store.Load().Queue.MaxItems);
                Assert.Contains(result.Messages, m => m.Contains("색인", StringComparison.Ordinal));

                Directory.Delete(paths.IndexFile, recursive: true);
            }
            finally
            {
                TryDelete(root);
            }
        });
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
