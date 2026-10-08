using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task StoreBuildUpdateTab_ShowsStoreNotice_AndOffersNoGitHubInstall() =>
        RunIsolated(nameof(StoreBuildUpdateTab_ShowsStoreNotice_AndOffersNoGitHubInstall), VerifyStoreUpdateTab);

    private static void VerifyStoreUpdateTab()
    {
        // The isolated child process owns this override, so no other test sees a packaged app.
        AppPackage.IsPackagedOverride = true;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                VerifyStoreUpdateTabForLanguage();
            }
        }
        finally
        {
            application.Shutdown();
            AppPackage.IsPackagedOverride = null;
        }
    }

    private static void VerifyStoreUpdateTabForLanguage()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mc-store-update-").FullName;
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
            tabs.SelectedItem = window.FindName("UpdateTab");
            Drain(window);

            Assert.Equal(UpdateStrings.StoreManaged, Assert.IsType<TextBlock>(window.FindName("UpdateDescription")).Text);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("UpdateAction")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("UpdateCancel")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<ProgressBar>(window.FindName("UpdateDownloadProgress")).Visibility);
            // Without a resolved target the GitHub download and installer handoff stay unreachable.
            FieldInfo target = typeof(SettingsWindow).GetField("_updateTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Null(target.GetValue(window));
        }
        finally
        {
            window.CloseForExit();
        }
    }
}
