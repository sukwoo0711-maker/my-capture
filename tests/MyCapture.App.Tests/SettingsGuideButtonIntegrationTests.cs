using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideButtonIntegrationTests
{
    [Fact]
    public Task KoreanButtonsOpenEveryIntegratedGuide() => SettingsThemePreviewTests.RunIsolated(
        nameof(KoreanButtonsOpenEveryIntegratedGuide), () => VerifyButtons("ko-KR"), typeof(SettingsGuideButtonIntegrationTests));

    [Fact]
    public Task EnglishButtonsOpenEveryIntegratedGuide() => SettingsThemePreviewTests.RunIsolated(
        nameof(EnglishButtonsOpenEveryIntegratedGuide), () => VerifyButtons("en"), typeof(SettingsGuideButtonIntegrationTests));

    private static void VerifyButtons(string language)
    {
        using var culture = UiText.UseLanguage(language);
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        Action<SettingsGuideWindow>? previousHook = SettingsGuide.BeforeShowForTest;
        Application app = SettingsStructureEvidenceTests.CreateApplication();
        SettingsWindow? window = null;
        SettingsGuideWindow? opened = null;
        try
        {
            window = SettingsStructureEvidenceTests.CreateWindow();
            window.Show();
            SettingsStructureEvidenceTests.Layout(window);
            Assert.IsType<SettingsGuidePresenter>(window.GuidePresenter);
            Assert.True(window.GuidePresenter.IsAvailable);

            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            string before = JsonSerializer.Serialize(draft.ToAppSettings());
            var tabs = (TabControl)window.FindName("CategoryTabs");
            Button[] buttons = SettingsStructureEvidenceTests.Logical(tabs).OfType<Button>()
                .Where(button => button.Tag is string id && Regex.IsMatch(id, @"^G\d\d$")).ToArray();
            Assert.Equal(23, buttons.Length);
            Assert.Equal(Enumerable.Range(1, 13).Select(number => $"G{number:00}"),
                buttons.Select(button => (string)button.Tag).Distinct().Order());

            int shown = 0;
            SettingsGuide.BeforeShowForTest = guide =>
            {
                opened = guide;
                shown++;
                guide.WindowStartupLocation = WindowStartupLocation.Manual;
                guide.Left = -10000;
                guide.Top = -10000;
                guide.ShowActivated = false;
                guide.ShowInTaskbar = false;
                FluidMotion.SetWindowEntrance(guide, false);
            };

            foreach (Button button in buttons)
            {
                RevealButton(window, button);
                string id = (string)button.Tag;
                Assert.True(button.IsVisible, id);
                Assert.True(button.IsEnabled, id);
                Assert.Equal(SettingsGuideCatalog.Get(id).ButtonLabel, button.Content);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.Empty(window.OwnedWindows.OfType<SettingsGuideWindow>());

                opened = null;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SettingsGuideWindow guide = Assert.IsType<SettingsGuideWindow>(opened);
                try
                {
                    SettingsStructureEvidenceTests.Layout(guide);
                    Assert.True(guide.IsVisible, id);
                    Assert.Same(window, guide.Owner);
                    Assert.Same(guide, Assert.Single(window.OwnedWindows.OfType<SettingsGuideWindow>()));
                    Assert.Equal(id, guide.Guide.Id);
                    Assert.Equal("MyCapture — " + guide.Guide.Title, guide.Title);
                    Assert.Equal(language == "ko-KR" ? "닫기" : "Close", guide.CloseButton.Content);
                    Assert.True(guide.HasBothImages, id);
                    Assert.False(guide.IsPlaying);
                    guide.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(guide.IsVisible, id);
                    Assert.Empty(window.OwnedWindows.OfType<SettingsGuideWindow>());
                }
                finally
                {
                    if (guide.IsVisible) guide.Close();
                }
            }

            Assert.Equal(buttons.Length, shown);
            Assert.Same(draft, window.DataContext);
            Assert.Equal(before, JsonSerializer.Serialize(draft.ToAppSettings()));
        }
        finally
        {
            if (opened?.IsVisible == true) opened.Close();
            SettingsGuide.BeforeShowForTest = previousHook;
            window?.CloseForExit();
            app.Shutdown();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
        }
    }

    private static void RevealButton(SettingsWindow window, Button button)
    {
        TabItem? tab = null;
        var groups = new List<Expander>();
        for (DependencyObject? parent = LogicalTreeHelper.GetParent(button); parent is not null;
            parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is Expander group) groups.Add(group);
            if (parent is TabItem category) { tab = category; break; }
        }
        Assert.NotNull(tab);
        SettingsSectionDefinition section = Assert.Single(SettingsSectionCatalog.Sections, item => item.TabName == tab.Name);
        Assert.True(window.NavigateToSection(section.Section));
        foreach (Expander group in groups) group.IsExpanded = true;
        SettingsStructureEvidenceTests.Layout(window);
        button.BringIntoView();
        SettingsStructureEvidenceTests.Layout(window);
    }
}
