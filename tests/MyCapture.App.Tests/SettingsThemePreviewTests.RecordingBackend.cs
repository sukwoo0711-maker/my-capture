using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task RecordingCaptureMode_SettingsWindowSwitchesToCompatibilityMode() =>
        RunIsolated(nameof(RecordingCaptureMode_SettingsWindowSwitchesToCompatibilityMode), VerifyRecordingCaptureModeSetting);

    private static void VerifyRecordingCaptureModeSetting()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-recording-backend-settings-").FullName;
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
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabRecording")));
            Drain(window);

            var selector = Assert.IsType<ComboBox>(window.FindName("RecordingCaptureModeSelector"));
            Assert.True(selector.IsVisible);
            Assert.Equal("녹화 캡처 방식", AutomationProperties.GetName(selector));
            Assert.Contains("호환 모드를 선택하십시오", AutomationProperties.GetHelpText(selector), StringComparison.Ordinal);
            Assert.Equal(
                new[] { "자동(권장)", "호환 모드" },
                selector.Items.OfType<ComboBoxItem>().Select(item => (string)item.Content).ToArray());
            Assert.Equal("auto", selector.SelectedValue);

            selector.SelectedValue = "gdi";
            Drain(window);
            SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
            Assert.Equal("gdi", draft.RecordingCaptureMode);
            Assert.False(draft.HasErrors);
            SettingsWindow.ApplyCommand.Execute(null, window);

            Assert.Equal(RecordingCaptureMode.Gdi, saved.Recording.CaptureMode);
            Assert.Equal(RecordingCaptureMode.Gdi, store.Load().Recording.CaptureMode);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }
}
