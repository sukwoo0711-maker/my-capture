using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task RecordingPauseHotkey_SettingsWindowExposesAnOptionalRecorder() =>
        RunIsolated(nameof(RecordingPauseHotkey_SettingsWindowExposesAnOptionalRecorder), VerifyRecordingPauseHotkeySettings);

    private static void VerifyRecordingPauseHotkeySettings()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-pause-hotkey-settings-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings saved = new();
        store.Save(saved);
        ThemeService.ApplyFromSettings(saved.General.Theme);
        var window = new SettingsWindow(() => saved, candidate =>
        {
            store.Save(candidate);
            saved = candidate;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 940, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);

            // Shortcuts tab: an optional recorder bound to the pause command, empty by default.
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
            Drain(window);
            HotkeyRecorder recorder = Descendants(window).OfType<HotkeyRecorder>().Single(control =>
                control.GetBindingExpression(HotkeyRecorder.TextProperty)?.ParentBinding.Path.Path == nameof(SettingsDraft.PauseRecordingHotkey));
            Assert.True(recorder.IsVisible);
            Assert.Equal("녹화 일시 정지/재개 단축키", AutomationProperties.GetName(recorder));
            Assert.Contains("녹화 중이 아닐 때는 동작하지 않습니다", AutomationProperties.GetHelpText(recorder), StringComparison.Ordinal);
            Assert.Equal(string.Empty, recorder.Text);

            // The region-recording chord is already taken, so it is rejected like any duplicate.
            draft.PauseRecordingHotkey = "Ctrl+Shift+X";
            Drain(window);
            Assert.True(Validation.GetHasError(recorder));

            draft.PauseRecordingHotkey = "Ctrl+Alt+P";
            Drain(window);
            Assert.False(Validation.GetHasError(recorder));
            SettingsWindow.ApplyCommand.Execute(null, window);

            Assert.Equal("Ctrl+Alt+P", saved.Hotkeys.PauseRecording.ToString());
            Assert.Equal("Ctrl+Alt+P", store.Load().Hotkeys.PauseRecording.ToString());
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
            // Retain the isolated fixture for diagnosis, like the other settings window tests.
        }
    }
}
