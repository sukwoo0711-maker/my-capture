using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Applying the settings window keeps the start-guide count the selectors recorded meanwhile.</summary>
public sealed class CaptureStartGuideApplyTests
{
    private sealed class AcceptingRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id)
        {
        }
    }

    private sealed class MemoryRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }

    [Fact]
    public void SettingsApplyKeepsTheLiveStartGuideCount() => StaTestHost.Run(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-start-guide-apply-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings published = store.Load();
        store.CompleteFirstRun(published);
        using var window = new NativeMessageWindow();
        using var hotkeys = new GlobalHotkeyService(window, new AcceptingRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
        hotkeys.Initialize(published.Hotkeys);
        var startup = new StartupRegistrationService(new MemoryRunKeyStore(), @"C:\Apps\MyCapture.exe");
        var apply = new SettingsApplyService(store, () => published, next => published = next, null, hotkeys, startup, NullLogger.Instance);

        published.Capture.StartGuideShownCount = 1;
        published.Capture.KeyPanelShownCount = 2;
        var draft = new SettingsDraft(published) { DelaySeconds = "5" };
        // Two selectors open while the settings window is still open.
        published.Capture.StartGuideShownCount = 3;
        published.Capture.KeyPanelShownCount = 5;

        SettingsApplyResult result = apply.Apply(draft.ToAppSettings());
        Assert.True(result.Saved);
        Assert.Equal(5, published.Capture.DelaySeconds);
        Assert.Equal(3, published.Capture.StartGuideShownCount);
        Assert.Equal(3, store.Load().Capture.StartGuideShownCount);
        Assert.Equal(5, published.Capture.KeyPanelShownCount);
        Assert.Equal(5, store.Load().Capture.KeyPanelShownCount);
    });
}
