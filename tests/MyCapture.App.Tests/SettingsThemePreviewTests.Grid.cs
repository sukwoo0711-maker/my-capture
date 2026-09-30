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
    public Task SelectionGridCheckbox_PersistsOnlyAfterApply_AndCancelRestores() =>
        RunIsolated(nameof(SelectionGridCheckbox_PersistsOnlyAfterApply_AndCancelRestores), VerifyGridCheckbox);

    private static void VerifyGridCheckbox()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string root = TestRecycleBin.CreateTempSubdirectory("mc-grid-settings-").FullName;
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
            tabs.SelectedIndex = 1; // General, capture.
            Drain(window);
            var checkbox = Assert.IsType<CheckBox>(window.FindName("ShowSelectionGridCheck"));
            Assert.True(checkbox.IsVisible);
            Assert.True(checkbox.IsChecked);
            Assert.Equal(UiText.Get("Settings.ShowSelectionGrid"), AutomationProperties.GetName(checkbox));
            Assert.NotEqual("Settings.ShowSelectionGrid", checkbox.Content);

            checkbox.IsChecked = false;
            Drain(window);
            Assert.False(Assert.IsType<SettingsDraft>(window.DataContext).ShowSelectionGrid);
            Assert.True(saved.Capture.ShowSelectionGrid);
            Assert.True(store.Load().Capture.ShowSelectionGrid);
            SettingsWindow.CancelCommand.Execute(null, window);
            window.ShowSettings();
            Drain(window);
            Assert.True(checkbox.IsChecked);

            checkbox.IsChecked = false;
            Drain(window);
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.False(saved.Capture.ShowSelectionGrid);
            Assert.False(store.Load().Capture.ShowSelectionGrid);
            window.ShowSettings();
            Drain(window);
            Assert.False(checkbox.IsChecked);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
            // Retain the isolated fixture for diagnosis; no permanent cleanup.
        }
    }
}
