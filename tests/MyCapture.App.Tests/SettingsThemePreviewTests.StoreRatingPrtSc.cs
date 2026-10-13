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
    private sealed class FixedPrintScreenSetting(int? value) : IWindowsPrintScreenSetting
    {
        public int? ReadValue() => value;
    }

    [Fact]
    public Task StoreRating_ButtonAppearsOnlyInTheStoreBuild() =>
        RunIsolated(nameof(StoreRating_ButtonAppearsOnlyInTheStoreBuild), VerifyStoreRatingButton);

    [Fact]
    public Task PrtScGuide_FollowsTheShortcutsAndTheWindowsSetting() =>
        RunIsolated(nameof(PrtScGuide_FollowsTheShortcutsAndTheWindowsSetting), VerifyPrintScreenGuide);

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

    private static SettingsWindow CreateOffscreenSettingsWindow(AppSettings saved, IWindowsPrintScreenSetting? printScreen = null)
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
        if (printScreen is not null) window.PrintScreenSetting = printScreen;
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

    private static void VerifyPrintScreenGuide()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                var saved = new AppSettings();
                Assert.True(Hotkey.TryParse("PrintScreen", out Hotkey printScreen));
                saved.Hotkeys.CaptureFullScreen = printScreen;
                SettingsWindow window = CreateOffscreenSettingsWindow(saved, new FixedPrintScreenSetting(1));
                try
                {
                    window.Show();
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    var panel = Assert.IsType<Border>(window.FindName("PrtScGuidePanel"));
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => ReferenceEquals(
                        LogicalTreeHelper.FindLogicalNode(tab, "PrtScGuidePanel"), panel));
                    Drain(window);

                    Assert.True(window.IsPrintScreenGuideVisible);
                    Assert.True(panel.IsVisible);
                    Assert.Equal(UiText.Get("PrtScGuide_Heading"), AutomationProperties.GetName(panel));
                    // The message keeps Korean words whole when it wraps; readers get the plain text.
                    TextBlock message = Assert.IsType<TextBlock>(window.FindName("PrtScGuideMessage"));
                    Assert.Equal(UiText.Get("PrtScGuide_Message"), KoreanWordWrap.Strip(message.Text));
                    string spoken = AutomationProperties.GetName(message);
                    Assert.Equal(UiText.Get("PrtScGuide_Message"), spoken.Length > 0 ? spoken : message.Text);
                    var open = Assert.IsType<Button>(window.FindName("PrtScGuideOpenSettings"));
                    Assert.Equal(UiText.Get("PrtScGuide_OpenSettings"), AutomationProperties.GetName(open));
                    Assert.True(open.IsVisible && open.IsEnabled && open.Focusable);

                    // Turning the Windows option off hides the guide; turning it on shows it again.
                    window.PrintScreenSetting = new FixedPrintScreenSetting(0);
                    Assert.False(window.IsPrintScreenGuideVisible);
                    window.PrintScreenSetting = new FixedPrintScreenSetting(1);
                    Assert.True(window.IsPrintScreenGuideVisible);

                    // The guide follows the edited, unsaved shortcuts.
                    var draft = Assert.IsType<SettingsDraft>(window.DataContext);
                    draft.CaptureFullScreenHotkey = "F8";
                    Assert.False(window.IsPrintScreenGuideVisible);
                    draft.RecordRegionHotkey = "Shift+PrintScreen";
                    Assert.True(window.IsPrintScreenGuideVisible);
                    draft.RecordRegionHotkey = "Ctrl+PrintScreen";
                    Assert.False(window.IsPrintScreenGuideVisible);
                    Drain(window);
                    Assert.False(panel.IsVisible);
                }
                finally
                {
                    window.CloseForExit();
                }
            }
        }
        finally
        {
            application.Shutdown();
        }
    }
}
