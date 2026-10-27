using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>Settings → About carries the same "first-use guide" entry as the tray menu, in both languages.</summary>
    [Fact]
    public Task FirstUseGuide_AboutTabOffersTheGuideAgain() =>
        RunIsolated(nameof(FirstUseGuide_AboutTabOffersTheGuideAgain), () =>
        {
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_ONBOARD_GUIDE_EVIDENCE");
            try
            {
                foreach (string locale in new[] { "ko-KR", "en-US" })
                {
                    using var language = UiText.UseLanguage(locale);
                    var settings = new SettingsWindow(() => new AppSettings(),
                        _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                        ShowActivated = false, ShowInTaskbar = false,
                    };
                    try
                    {
                        int requested = 0;
                        settings.FirstUseGuideRequested += (_, _) => requested++;
                        settings.Show(); Drain(settings);
                        var tabs = (TabControl)settings.FindName("CategoryTabs");
                        tabs.SelectedItem = settings.FindName("UpdateTab");
                        Drain(settings);
                        var button = (Button)settings.FindName("FirstUseGuideButton");
                        Assert.True(button.IsVisible);
                        Assert.Equal(locale == "ko-KR" ? "처음 사용 안내" : "First-use guide", AutomationProperties.GetName(button));
                        Assert.True(button.ActualHeight >= 24);
                        if (!string.IsNullOrWhiteSpace(evidence))
                            SaveBitmap(RenderWindow(settings), Path.Combine(Directory.CreateDirectory(evidence).FullName, $"settings-about-{locale[..2]}.png"));
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(1, requested);
                    }
                    finally { settings.CloseForExit(); }
                }
            }
            finally { FluidMotion.AnimationsEnabledOverrideForTest = null; application.Shutdown(); }
        });
}
