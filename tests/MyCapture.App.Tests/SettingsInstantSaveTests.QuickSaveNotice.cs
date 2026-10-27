using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsInstantSaveTests
{
    [Fact]
    public Task QuickSaveNoticeCheckboxSitsBesideTheCaptureSoundAndSavesImmediately() =>
        Isolated(nameof(QuickSaveNoticeCheckboxSitsBesideTheCaptureSoundAndSavesImmediately), () =>
        {
            Application app = CreateApplication();
            try
            {
                foreach ((string languageName, string label) in new[] { ("ko-KR", "빠른 저장 뒤 알림"), ("en", "Notify after quick save") })
                {
                    using var language = UiText.UseLanguage(languageName);
                    string root = TestRecycleBin.CreateTempSubdirectory("mc-quick-save-notice-").FullName;
                    try
                    {
                        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
                        AppSettings saved = new();
                        store.Save(saved);
                        var window = new SettingsWindow(() => saved, candidate =>
                        {
                            store.Save(candidate);
                            saved = candidate;
                            return new SettingsApplyResult(true, true, true, false, []);
                        }, NullLogger.Instance);
                        try
                        {
                            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab =>
                                Equals(tab.Header, UiText.Get("SettingsUx_TabCapture")));
                            Render(window, $"quick-save-notice-settings-{languageName}.png", 940, 700);
                            SettingsDraft draft = Assert.IsType<SettingsDraft>(window.DataContext);
                            CheckBox notice = Descendants(window).OfType<CheckBox>().Single(box =>
                                box.GetBindingExpression(CheckBox.IsCheckedProperty)?.ParentBinding.Path.Path
                                    == nameof(SettingsDraft.NotifyOnQuickSave));
                            Assert.Equal(label, notice.Content);
                            Assert.Equal(label, AutomationProperties.GetName(notice));
                            Assert.Same(window.FindResource("Settings.CheckBox"), notice.Style);
                            var group = Assert.IsType<StackPanel>(LogicalTreeHelper.GetParent(notice));
                            CheckBox previous = Assert.IsType<CheckBox>(group.Children[group.Children.IndexOf(notice) - 1]);
                            Assert.Equal(nameof(SettingsDraft.PlayCaptureSound), previous
                                .GetBindingExpression(CheckBox.IsCheckedProperty)!.ParentBinding.Path.Path);
                            // On by default, as the setting always was.
                            Assert.True(notice.IsChecked);

                            notice.IsChecked = false;
                            Pump();
                            Assert.False(draft.NotifyOnQuickSave);
                            Assert.False(saved.General.NotifyOnQuickSave);
                            Assert.False(store.Load().General.NotifyOnQuickSave);

                            notice.IsChecked = true;
                            Pump();
                            Assert.True(store.Load().General.NotifyOnQuickSave);
                        }
                        finally { window.CloseForExit(); }
                    }
                    finally { TestRecycleBin.DeleteDirectory(root, recursive: true); }
                }
            }
            finally { app.Shutdown(); }
        });
}
