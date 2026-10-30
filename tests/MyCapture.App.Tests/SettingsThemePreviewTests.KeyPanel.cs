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
    public Task KeyPanelCheckbox_PersistsImmediatelyAndReopens() =>
        RunIsolated(nameof(KeyPanelCheckbox_PersistsImmediatelyAndReopens), VerifyKeyPanelCheckbox);

    private static void VerifyKeyPanelCheckbox()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = OwnedTestDirectory.Create("mc-key-panel-settings-");
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
            Assert.True(window.NavigateToSetting(nameof(SettingsDraft.AlwaysShowKeyPanel)));
            Drain(window);
            var checkbox = Assert.IsType<CheckBox>(window.FindName("AlwaysShowKeyPanelCheck"));
            Assert.True(checkbox.IsVisible);
            Assert.False(checkbox.IsChecked);
            Assert.Equal(UiText.Get("Settings.AlwaysShowKeyPanel"), AutomationProperties.GetName(checkbox));
            checkbox.IsChecked = true;
            Drain(window);
            Assert.True(Assert.IsType<SettingsDraft>(window.DataContext).AlwaysShowKeyPanel);
            Assert.True(saved.Capture.AlwaysShowKeyPanel);
            Assert.True(store.Load().Capture.AlwaysShowKeyPanel);
            SettingsWindow.CloseCommand.Execute(null, window);
            window.ShowSettings();
            Drain(window);
            Assert.True(checkbox.IsChecked);
            checkbox.IsChecked = false;
            Drain(window);
            Assert.False(store.Load().Capture.AlwaysShowKeyPanel);
        }
        finally { window.CloseForExit(); application.Shutdown(); }
    }
}
