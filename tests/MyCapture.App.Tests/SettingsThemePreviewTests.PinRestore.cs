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
    public Task PinRestore_SettingsWindowExposesHistoryCountAndHotkey() =>
        RunIsolated(nameof(PinRestore_SettingsWindowExposesHistoryCountAndHotkey), VerifyPinRestoreSettings);

    private static void VerifyPinRestoreSettings()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-pin-restore-settings-").FullName;
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

            // Editing tab, screen pin group: the history count is a validated number field.
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabEditing")));
            Drain(window);
            TextBox limit = Descendants(window).OfType<TextBox>().Single(box =>
                box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(SettingsDraft.ClosedWindowRestoreLimit));
            Assert.True(limit.IsVisible);
            Assert.Equal("닫은 화면 고정 기록 개수", AutomationProperties.GetName(limit));
            Assert.Equal("20", limit.Text);

            limit.Text = "101";
            Drain(window);
            Assert.True(Validation.GetHasError(limit));
            Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.ClosedWindowRestoreLimit)).Cast<string>());

            limit.Text = "5";
            Drain(window);
            Assert.False(Validation.GetHasError(limit));

            // Shortcuts tab: an optional recorder bound to the restore command, empty by default.
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
            Drain(window);
            HotkeyRecorder recorder = Descendants(window).OfType<HotkeyRecorder>().Single(control =>
                control.GetBindingExpression(HotkeyRecorder.TextProperty)?.ParentBinding.Path.Path == nameof(SettingsDraft.RestoreClosedPinHotkey));
            Assert.Equal("닫은 화면 고정 다시 열기 단축키", AutomationProperties.GetName(recorder));
            Assert.Equal(string.Empty, recorder.Text);

            draft.RestoreClosedPinHotkey = "Ctrl+Alt+F3";
            Drain(window);
            SettingsWindow.ApplyCommand.Execute(null, window);

            Assert.Equal(5, saved.Pin.ClosedWindowRestoreLimit);
            Assert.Equal("Ctrl+Alt+F3", saved.Hotkeys.RestoreClosedPin.ToString());
            AppSettings persisted = store.Load();
            Assert.Equal(5, persisted.Pin.ClosedWindowRestoreLimit);
            Assert.Equal("Ctrl+Alt+F3", persisted.Hotkeys.RestoreClosedPin.ToString());
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
            // Retain the isolated fixture for diagnosis, like the other settings window tests.
        }
    }
}
