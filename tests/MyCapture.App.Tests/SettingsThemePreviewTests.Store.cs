using System.ComponentModel;
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
    public Task UpdateTab_ShowsStoreNoticeAndOpensStorePage_InEveryBuild() =>
        RunIsolated(nameof(UpdateTab_ShowsStoreNoticeAndOpensStorePage_InEveryBuild), VerifyStoreUpdateTab);

    private static void VerifyStoreUpdateTab()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        try
        {
            // The isolated child process owns this override, so no other test sees a packaged app.
            foreach (bool packaged in new[] { false, true })
            {
                AppPackage.IsPackagedOverride = packaged;
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    VerifyStoreUpdateTabForLanguage();
                }
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
        var opened = new List<string>();
        window.OpenStorePage = opened.Add;
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            tabs.SelectedItem = window.FindName("UpdateTab");
            Drain(window);

            Assert.Equal(UiText.Get("Update.StoreManaged"), Assert.IsType<TextBlock>(window.FindName("UpdateDescription")).Text);
            // The GitHub check, download and installer handoff controls are gone in every build.
            Assert.Null(window.FindName("UpdateAction"));
            Assert.Null(window.FindName("UpdateCancel"));
            Assert.Null(window.FindName("UpdateDownloadProgress"));

            Button openStore = Assert.IsType<Button>(window.FindName("UpdateOpenStore"));
            Assert.True(openStore.IsVisible);
            Assert.True(openStore.IsEnabled);
            Assert.Equal(UiText.Get("Update.OpenStore"), openStore.Content);
            Assert.Equal(UiText.Get("Update.OpenStore"), AutomationProperties.GetName(openStore));
            AssertFits(window, openStore);

            TextBlock status = Assert.IsType<TextBlock>(window.FindName("UpdateStatus"));
            openStore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("ms-windows-store://pdp/?productid=9PB33MC095VR", Assert.Single(opened));
            Assert.Equal(string.Empty, status.Text);

            window.OpenStorePage = _ => throw new Win32Exception(2);
            openStore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Drain(window);
            Assert.Equal(UiText.Get("Update.OpenStoreFailed"), status.Text);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            AssertFits(window, status);

            window.OpenStorePage = opened.Add;
            openStore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, opened.Count);
            Assert.Equal(string.Empty, status.Text);
        }
        finally
        {
            window.CloseForExit();
        }
    }
}
