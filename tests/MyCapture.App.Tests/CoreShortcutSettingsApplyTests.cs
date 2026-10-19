using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The one-time answer survives settings apply and failed shortcut registration.</summary>
public sealed class CoreShortcutSettingsApplyTests
{
    [Fact]
    public void HotkeyClone_CopiesBothCoreShortcutFlags()
    {
        var source = new HotkeySettings { ForceCoreShortcuts = true, CoreShortcutsAsked = true };

        HotkeySettings clone = source.DeepCloneHotkeys();
        source.ForceCoreShortcuts = false;
        source.CoreShortcutsAsked = false;

        Assert.True(clone.ForceCoreShortcuts);
        Assert.True(clone.CoreShortcutsAsked);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Apply_PreservesAnsweredStateEvenOnRollback(bool blocked, bool previouslyAsked, bool requestedAnswer) =>
        StaTestHost.Run(() =>
        {
            var store = new SettingsStore(AppPaths.CreateForRoot(ExportTestDirectory.Create()), NullLogger<SettingsStore>.Instance);
            AppSettings published = NewInstallDefaults.Create();
            published.Hotkeys.CoreShortcutsAsked = previouslyAsked;
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, new Registrar(blocked),
                NullLogger<GlobalHotkeyService>.Instance, new UnavailableOverride());
            hotkeys.Initialize(published.Hotkeys);
            var startup = new StartupRegistrationService(new MemoryRunKeyStore(), @"C:\Apps\MyCapture.exe");
            var apply = new SettingsApplyService(store, () => published, next => published = next,
                null, hotkeys, startup, NullLogger.Instance);
            AppSettings candidate = published.DeepClone();
            CoreShortcuts.Accept(candidate.Hotkeys);
            candidate.Hotkeys.CoreShortcutsAsked = requestedAnswer;

            SettingsApplyResult result = apply.Apply(candidate);

            Assert.True(result.Saved);
            Assert.Equal(!blocked, result.HotkeysApplied);
            if (blocked)
            {
                HotkeyRegistrationFailure failure = Assert.Single(result.HotkeyFailures);
                Assert.Equal(GlobalHotkeyCommand.OpenLibrary, failure.Command);
                Assert.Equal(CoreShortcuts.OpenLibrary, failure.Hotkey);
                Assert.Equal(1409, failure.NativeErrorCode);
                Assert.Empty(hotkeys.Failures);
            }
            else Assert.Empty(result.HotkeyFailures);
            Assert.True(published.Hotkeys.CoreShortcutsAsked);
            Assert.True(store.Load().Hotkeys.CoreShortcutsAsked);
            Assert.Equal(!blocked, published.Hotkeys.ForceCoreShortcuts);
            Assert.Equal(blocked ? Hotkey.None : CoreShortcuts.OpenLibrary, published.Hotkeys.OpenLibrary);
            Assert.Equal(blocked ? Hotkey.None : CoreShortcuts.PasteToScreen, published.Hotkeys.PasteToScreen);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_BeforeWelcomeNeverAccessesStartupAndPreservesItsPreference(bool failSave) =>
        StaTestHost.Run(() =>
        {
            var paths = AppPaths.CreateForRoot(ExportTestDirectory.Create());
            paths.EnsureCreated();
            if (failSave) Directory.CreateDirectory(paths.SettingsFile);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            AppSettings published = NewInstallDefaults.Create();
            AppSettings original = published;
            Assert.True(original.General.LaunchAtLogin);
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, new Registrar(false),
                NullLogger<GlobalHotkeyService>.Instance, new UnavailableOverride());
            hotkeys.Initialize(original.Hotkeys);
            var startup = new UnavailableStartupRegistration();
            var apply = new SettingsApplyService(store, () => published, next => published = next,
                null, hotkeys, startup, NullLogger.Instance);
            AppSettings candidate = original.DeepClone();
            CoreShortcuts.Accept(candidate.Hotkeys);

            SettingsApplyResult result = apply.Apply(candidate, applyStartup: false);

            Assert.Equal(0, startup.AccessCount);
            Assert.Equal(!failSave, result.Saved);
            Assert.False(result.StartupApplied);
            Assert.True(candidate.General.LaunchAtLogin);
            Assert.True(published.General.LaunchAtLogin);
            if (failSave)
            {
                Assert.Same(original, published);
                Assert.Single(result.Messages);
                Assert.Null(hotkeys.GetAssigned(GlobalHotkeyCommand.OpenLibrary));
                Assert.Equal(original.Hotkeys.Capture, hotkeys.GetAssigned(GlobalHotkeyCommand.CaptureRegion));
            }
            else
            {
                Assert.True(result.HotkeysApplied);
                Assert.Empty(result.Messages);
                Assert.True(store.Load().General.LaunchAtLogin);
                Assert.True(store.Load().Hotkeys.CoreShortcutsAsked);
                Assert.Equal(CoreShortcuts.OpenLibrary, hotkeys.GetAssigned(GlobalHotkeyCommand.OpenLibrary));
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_AcceptBesideAnUnrelatedConflictPersistsCoreChordsOnlyWhenKeepingAvailable(bool keepAvailable) =>
        StaTestHost.Run(() =>
        {
            // Another program owns F3 and Shift+F3; the legacy set hides pins with Shift+F3.
            var shiftF3 = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
            var store = new SettingsStore(AppPaths.CreateForRoot(ExportTestDirectory.Create()), NullLogger<SettingsStore>.Instance);
            var legacyCapture = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkC);
            var published = new AppSettings();
            published.Hotkeys.Capture = legacyCapture;
            using var window = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(window, new ChordRegistrar(CoreShortcuts.PasteToScreen, shiftF3),
                NullLogger<GlobalHotkeyService>.Instance, new ClaimingOverride());
            hotkeys.Initialize(published.Hotkeys);
            var startup = new StartupRegistrationService(new MemoryRunKeyStore(), @"C:\Apps\MyCapture.exe");
            var apply = new SettingsApplyService(store, () => published, next => published = next,
                null, hotkeys, startup, NullLogger.Instance);
            AppSettings candidate = published.DeepClone();
            CoreShortcuts.Accept(candidate.Hotkeys);

            SettingsApplyResult result = apply.Apply(candidate, keepAvailableHotkeys: keepAvailable);

            Assert.True(result.Saved);
            Assert.Equal(keepAvailable, result.HotkeysApplied);
            HotkeyRegistrationFailure failure = Assert.Single(result.HotkeyFailures);
            Assert.Equal(GlobalHotkeyCommand.HideAllPins, failure.Command);
            Assert.Equal(shiftF3, failure.Hotkey);
            Assert.Equal(1409, failure.NativeErrorCode);
            AppSettings saved = store.Load();
            foreach (HotkeySettings settings in new[] { published.Hotkeys, saved.Hotkeys })
            {
                Assert.True(settings.CoreShortcutsAsked);
                Assert.Equal(keepAvailable, settings.ForceCoreShortcuts);
                Assert.Equal(keepAvailable ? CoreShortcuts.Capture : legacyCapture, settings.Capture);
                Assert.Equal(CoreShortcuts.RecordRegion, settings.RecordRegion);
                Assert.Equal(CoreShortcuts.OpenLibrary, settings.OpenLibrary);
                Assert.Equal(CoreShortcuts.PasteToScreen, settings.PasteToScreen);
                Assert.Equal(shiftF3, settings.HideAllPins);
            }
            if (keepAvailable)
            {
                Assert.Empty(result.Messages);
                Assert.Same(candidate, published);
                Assert.Equal(failure, Assert.Single(hotkeys.Failures));
                Assert.Equal(GlobalHotkeyCommand.PasteToScreen, Assert.Single(hotkeys.OverriddenCommands));
                Assert.Equal(CoreShortcuts.PasteToScreen, hotkeys.GetAssigned(GlobalHotkeyCommand.PasteToScreen));
            }
            else
            {
                Assert.NotEmpty(result.Messages);
                Assert.Empty(hotkeys.OverriddenCommands);
                Assert.Equal(2, hotkeys.Failures.Count);
            }
        });

    private sealed class ChordRegistrar(params Hotkey[] blocked) : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            bool conflict = blocked.Contains(hotkey);
            errorCode = conflict ? 1409 : 0;
            errorMessage = conflict ? "Hot key is already registered." : string.Empty;
            return !conflict;
        }

        public void Unregister(int id) { }
    }

    private sealed class ClaimingOverride : IHotkeyOverride
    {
        public bool TryClaim(int id, Hotkey hotkey) => true;
        public void Release(int id) { }
        public void Dispose() { }
    }

    private sealed class UnavailableStartupRegistration : IStartupRegistration
    {
        public int AccessCount { get; private set; }
        public bool IsEnabled() => throw Accessed();
        public StartupApplyResult Apply(bool desiredEnabled) => throw Accessed();
        public bool ReconcileOnStartup(bool settingEnabled) => throw Accessed();

        private InvalidOperationException Accessed()
        {
            AccessCount++;
            return new InvalidOperationException("Startup registration belongs to the welcome choice.");
        }
    }

    private sealed class Registrar(bool blocked) : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            bool conflict = blocked && hotkey == CoreShortcuts.OpenLibrary;
            errorCode = conflict ? 1409 : 0;
            errorMessage = conflict ? "Hot key is already registered." : string.Empty;
            return !conflict;
        }

        public void Unregister(int id) { }
    }

    private sealed class UnavailableOverride : IHotkeyOverride
    {
        public bool TryClaim(int id, Hotkey hotkey) => false;
        public void Release(int id) { }
        public void Dispose() { }
    }

    private sealed class MemoryRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }
}
