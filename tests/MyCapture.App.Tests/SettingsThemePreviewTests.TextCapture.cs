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
    public Task TextCaptureSettings_WindowDetectColorFormatAndHotkeyApplyThroughTheWindow() =>
        RunIsolated(nameof(TextCaptureSettings_WindowDetectColorFormatAndHotkeyApplyThroughTheWindow), VerifyTextCaptureSettings);

    private static void VerifyTextCaptureSettings()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-text-capture-settings-").FullName;
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
            Assert.True(window.NavigateToSetting(nameof(SettingsDraft.ColorFormat)));
            Drain(window);
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            var detect = Assert.IsType<CheckBox>(window.FindName("AutoDetectWindowsCheck"));
            var format = Assert.IsType<ComboBox>(window.FindName("ColorFormatSelector"));
            Assert.True(detect.IsVisible);
            Assert.True(detect.IsChecked);
            Assert.Equal(UiText.Get("WindowDetect_Setting"), AutomationProperties.GetName(detect));
            Assert.Equal(ColorFormat.Hex, format.SelectedValue);
            Assert.Equal(UiText.Get("ColorPick_FormatLabel"), AutomationProperties.GetName(format));

            detect.IsChecked = false;
            format.SelectedIndex = 2;
            draft.CaptureTextHotkey = "Ctrl+Alt+T";
            Drain(window);
            Assert.False(draft.AutoDetectWindows);
            Assert.Equal(ColorFormat.Hsl, draft.ColorFormat);
            var recorder = Assert.IsType<HotkeyRecorder>(window.FindName("CaptureTextHotkeyRecorder"));
            Assert.Equal("Ctrl+Alt+T", recorder.Text);
            Assert.Equal(UiText.Get("TextCapture_Title"), AutomationProperties.GetName(recorder));
            Assert.True(window.NavigateToSection(SettingsSection.Text));
            Drain(window);
            var tutorialButton = Assert.IsType<Button>(window.FindName("TextCopyTutorialButton"));
            Assert.True(tutorialButton.IsVisible && tutorialButton.IsEnabled && tutorialButton.IsTabStop);
            Assert.Equal(AutomationProperties.GetName(tutorialButton), tutorialButton.Content);
            Assert.Same(window.FindResource("Button.Secondary"), tutorialButton.Style);
            Assert.Equal("텍스트 복사 사용법 보기", AutomationProperties.GetName(tutorialButton));
            // Repeated shortcut-row tutorials now live in shortcut help; text capture keeps its own entry.
            Button[] featureTutorials = Descendants(window).OfType<Button>().Where(button => button.Tag is string id
                && TutorialCatalog.All.Any(feature => feature.Id == id)).ToArray();
            Assert.Empty(featureTutorials);
            Assert.False(saved.Capture.AutoDetectWindows);

            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.False(saved.Capture.AutoDetectWindows);
            Assert.Equal(ColorFormat.Hsl, saved.Capture.ColorFormat);
            Assert.Equal("Ctrl+Alt+T", saved.Hotkeys.CaptureText.ToString());
            AppSettings reloaded = store.Load();
            Assert.False(reloaded.Capture.AutoDetectWindows);
            Assert.Equal(ColorFormat.Hsl, reloaded.Capture.ColorFormat);
            Assert.Equal("Ctrl+Alt+T", reloaded.Hotkeys.CaptureText.ToString());

            window.ShowSettings();
            Drain(window);
            Assert.True(window.NavigateToSection(SettingsSection.Text));
            Drain(window);
            tutorialButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            TextCopyTutorialWindow tutorial = Assert.Single(window.OwnedWindows.OfType<TextCopyTutorialWindow>());
            Assert.True(tutorial.IsVisible);
            Assert.Contains(FirstRunWelcomeTests.Descendants(tutorial).OfType<TextBlock>(),
                text => text.Text == UiText.Format("TextCapture_TutorialDirect", "Ctrl+Alt+T"));
            tutorial.Close();
            Assert.True(window.NavigateToSetting(nameof(SettingsDraft.ColorFormat)));
            Drain(window);
            Assert.False(detect.IsChecked);
            Assert.Equal(ColorFormat.Hsl, format.SelectedValue);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }
}
