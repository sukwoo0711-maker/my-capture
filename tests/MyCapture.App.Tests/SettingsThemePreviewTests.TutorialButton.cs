using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public void TutorialButton_SystemContrastKeepsTheGlyphAndStateLayersReadable() => SystemHighContrastThemeTests.Simulate(() =>
    {
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: true);
        var button = new Button { Resources = resources, Style = (Style)resources["Button.Tutorial"] };
        AutomationProperties.SetName(button, "Region capture tutorial");
        button.Measure(new Size(100, 100));
        button.Arrange(new Rect(button.DesiredSize));
        SymbolIcon icon = Assert.Single(Descendants(button).OfType<SymbolIcon>());
        Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.WindowText],
            Assert.IsType<SolidColorBrush>(icon.Foreground).Color);
        foreach ((string part, string token) in new[]
            { ("HoverLayer", "Surface.Hover"), ("PressedLayer", "Surface.Pressed"), ("FocusRing", "Border.Focus") })
        {
            var layer = Assert.IsType<Border>(button.Template.FindName(part, button));
            Brush brush = part == "FocusRing" ? layer.BorderBrush : layer.Background;
            Assert.Equal(SystemHighContrastThemeTests.NightSky[HighContrastPalette.RoleFor(token)],
                Assert.IsType<SolidColorBrush>(brush).Color);
        }
    });

    [Fact]
    public Task TutorialButton_RowsKeepIconNamesFocusAndFeatureRouting() =>
        RunIsolated(nameof(TutorialButton_RowsKeepIconNamesFocusAndFeatureRouting), VerifyTutorialButtonRows);

    private static void VerifyTutorialButtonRows()
    {
        Application application = CreateThemedApplication();
        Func<ITutorialMedia>? previousMedia = FeatureTutorialWindow.MediaFactoryOverrideForTest;
        FeatureTutorialWindow.MediaFactoryOverrideForTest = () => new FakeTutorialMedia();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight, AppTheme.HighContrast })
                {
                    var settings = CreateSettingsUxWindow(new AppSettings
                        { General = { Theme = AppThemeNames.ToSetting(theme) } }).Window;
                    try
                    {
                        settings.Show();
                        ((TabControl)settings.FindName("CategoryTabs")).SelectedIndex = 4;
                        foreach (Size size in new[] { new Size(940, 700), new Size(780, 560) })
                        {
                            settings.Width = size.Width;
                            settings.Height = size.Height;
                            Drain(settings);
                            Button[] buttons = TutorialButtons(settings);
                            Assert.Equal(13, buttons.Length);
                            foreach (Button button in buttons)
                                VerifyTutorialButton(settings, button, button.Tag as string ?? "text-copy");
                            VerifyTutorialRouting(settings, buttons);
                        }
                    }
                    finally { settings.CloseForExit(); }

                    ThemeService.Apply(theme);
                    var help = new ShortcutHelpWindow { WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                    FluidMotion.SetWindowEntrance(help, false);
                    try
                    {
                        help.Show();
                        var view = ShortcutHelpSearchTests.View(help);
                        int total = 0;
                        foreach (TabItem tab in view.Tabs.Items)
                        {
                            view.Tabs.SelectedItem = tab;
                            Drain(help);
                            Button[] buttons = TutorialButtons((DependencyObject)tab.Content);
                            total += buttons.Length;
                            foreach (Button button in buttons)
                                VerifyTutorialButton(help, button, (string)button.Tag);
                            VerifyTutorialRouting(help, buttons);
                        }
                        Assert.Equal(42, total);
                        view.Search.Text = "Ctrl+C";
                        Drain(help);
                        Border[] rows = view.VisibleResults();
                        Assert.Equal(3, rows.Length);
                        Border originalText = Assert.Single(rows, row => Descendants(row).OfType<TextBlock>()
                            .Any(text => text.Text == UiText.Get("Text_976A6C92B870")));
                        Assert.Empty(Descendants(originalText).OfType<Button>());
                        Assert.Contains(Descendants(originalText).OfType<WrapPanel>(),
                            keys => AutomationProperties.GetName(keys) == "Ctrl+C / " + UiText.Get("Text_C7011B8D345C"));
                        foreach (Border row in rows.Where(row => !ReferenceEquals(row, originalText)))
                        {
                            Button button = Assert.Single(TutorialButtons(row));
                            VerifyTutorialButton(help, button, (string)button.Tag);
                            VerifyTutorialRouting(help, [button]);
                        }
                    }
                    finally { help.Close(); }
                }
            }
        }
        finally
        {
            FeatureTutorialWindow.DismissForCapture();
            FeatureTutorialWindow.MediaFactoryOverrideForTest = previousMedia;
            application.Shutdown();
        }
    }

    private static Button[] TutorialButtons(DependencyObject root) => Descendants(root).OfType<Button>()
        .Where(button => ReferenceEquals(button.Style, button.TryFindResource("Button.Tutorial"))).ToArray();

    private static void VerifyTutorialButton(Window owner, Button button, string featureId)
    {
        var line = Assert.IsType<DockPanel>(button.Parent);
        Assert.Single(line.Children.OfType<Button>());
        TextBlock title = Assert.Single(line.Children.OfType<TextBlock>());
        Assert.Equal(FeatureTutorialWindow.OpenButtonName(TutorialCatalog.Get(featureId)), AutomationProperties.GetName(button));
        Assert.Equal(AutomationProperties.GetName(button), button.ToolTip);
        Assert.Null(button.Content);
        SymbolIcon icon = Assert.Single(Descendants(button).OfType<SymbolIcon>());
        Assert.Same(button.FindResource("Icon.PlayCircle"), icon.Data);
        Assert.Equal(SymbolIcon.CommandSize, icon.Size);
        Assert.Same(button.Foreground, icon.Foreground);
        Assert.Equal((double)button.FindResource("CommandButton.Height.Compact"), button.ActualWidth);
        Assert.Equal(button.ActualWidth, button.ActualHeight);
        Assert.True(button.Focusable && button.IsTabStop);
        Assert.NotNull(button.Template.FindName("FocusRing", button));
        Assert.NotNull(button.Template.FindName("HoverLayer", button));
        Assert.NotNull(button.Template.FindName("PressedLayer", button));
        Rect bounds = button.TransformToAncestor(line).TransformBounds(new Rect(button.RenderSize));
        Rect caption = title.TransformToAncestor(line).TransformBounds(new Rect(title.RenderSize));
        Assert.InRange(bounds.Right, 0, line.ActualWidth + 0.5);
        Assert.InRange(bounds.Bottom, 0, line.ActualHeight + 0.5);
        var row = Assert.IsAssignableFrom<FrameworkElement>(line.Parent);
        Rect lineBounds = line.TransformToAncestor(row).TransformBounds(new Rect(line.RenderSize));
        Assert.InRange(lineBounds.Right, 0, row.ActualWidth + 0.5);
        Assert.InRange(lineBounds.Bottom, 0, row.ActualHeight + 0.5);
        Assert.InRange(bounds.Left - caption.Right, 7, 9);
        Assert.InRange(Math.Abs((bounds.Top + bounds.Bottom - caption.Top - caption.Bottom) / 2), 0, 1.5);
        Assert.True(title.DesiredSize.Width <= title.ActualWidth + 1, $"Clipped title: {title.Text}");
        // WPF's Tab traversal must enter the guide, not just advertise IsTabStop.
        button.BringIntoView();
        Drain(owner);
        line.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Assert.Same(button, FocusManager.GetFocusedElement(owner));
        button.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        var next = Assert.IsAssignableFrom<UIElement>(FocusManager.GetFocusedElement(owner));
        Assert.NotSame(button, next);
        next.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
        Assert.Same(button, FocusManager.GetFocusedElement(owner));
    }

    private static void VerifyTutorialRouting(Window owner, Button[] buttons)
    {
        FeatureTutorialWindow? first = null;
        foreach (Button button in buttons)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FeatureTutorialWindow tutorial = Assert.Single(owner.OwnedWindows.OfType<FeatureTutorialWindow>());
            Assert.True(tutorial.IsVisible);
            Assert.Equal(button.Tag as string ?? "text-copy", tutorial.SelectedFeatureId);
            if (first is not null) Assert.Same(first, tutorial);
            first = tutorial;
        }
        first?.Close();
    }

    [Fact]
    public Task TutorialButton_ReviewImages() =>
        RunIsolated(nameof(TutorialButton_ReviewImages), RenderTutorialButtons);

    private static void RenderTutorialButtons()
    {
        Application application = CreateThemedApplication();
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_PHASE") ?? "after";
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight })
                {
                    var fixture = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.ToSetting(theme) } });
                    var settings = fixture.Window;
                    try
                    {
                        settings.Show();
                        ((TabControl)settings.FindName("CategoryTabs")).SelectedIndex = 4;
                        foreach (var size in new[] { new Size(940, 700), new Size(780, 560) })
                        {
                            settings.Width = size.Width;
                            settings.Height = size.Height;
                            Drain(settings);
                            SaveSettingsUxEvidence(RenderAtDpi(settings, 96),
                                $"settings-{locale}-{AppThemeNames.ToSetting(theme)}-{size.Width}x{size.Height}-{phase}.png");
                        }
                    }
                    finally { settings.CloseForExit(); }

                    ThemeService.Apply(theme);
                    var help = new ShortcutHelpWindow
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                    };
                    FluidMotion.SetWindowEntrance(help, false);
                    try
                    {
                        help.Show();
                        Drain(help);
                        SaveSettingsUxEvidence(RenderAtDpi(help, 96),
                            $"help-{locale}-{AppThemeNames.ToSetting(theme)}-{phase}.png");
                    }
                    finally { help.Close(); }
                }
            }
        }
        finally { application.Shutdown(); }
    }
}
