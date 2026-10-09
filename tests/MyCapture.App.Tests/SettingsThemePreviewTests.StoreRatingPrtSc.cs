using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task StoreRating_ButtonAppearsOnlyInTheStoreBuild() =>
        RunIsolated(nameof(StoreRating_ButtonAppearsOnlyInTheStoreBuild), VerifyStoreRatingButton);

    private static Application CreateThemedApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        return application;
    }

    private static SettingsWindow CreateOffscreenSettingsWindow(AppSettings saved)
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-store-rating-prtsc-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
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
        return window;
    }

    private static void VerifyStoreRatingButton()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool packaged in new[] { true, false })
            {
                // The isolated child process owns this override, so no other test sees it.
                AppPackage.IsPackagedOverride = packaged;
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
                    try
                    {
                        window.Show();
                        var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                        tabs.SelectedItem = window.FindName("UpdateTab");
                        Drain(window);

                        var button = Assert.IsType<Button>(window.FindName("StoreRatingButton"));
                        Assert.Equal(packaged ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
                        Assert.Equal(UiText.Get("StoreRating_Button"), button.Content);
                        Assert.Equal(UiText.Get("StoreRating_Button"), AutomationProperties.GetName(button));
                        Assert.Equal(UiText.Get("StoreRating_ButtonHelp"), AutomationProperties.GetHelpText(button));
                        if (packaged) Assert.True(button.IsVisible && button.IsEnabled && button.Focusable);
                    }
                    finally
                    {
                        window.CloseForExit();
                    }
                }
            }
        }
        finally
        {
            application.Shutdown();
            AppPackage.IsPackagedOverride = null;
        }
    }
}
