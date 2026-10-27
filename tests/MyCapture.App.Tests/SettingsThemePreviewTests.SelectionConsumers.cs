using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task SelectionSurfaceConsumersKeepReadableTextAndVisibleChecks() =>
        RunIsolated(nameof(SelectionSurfaceConsumersKeepReadableTextAndVisibleChecks), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            try
            {
                foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Daylight, AppTheme.HighContrast, AppTheme.Workspace, AppTheme.Glass, AppTheme.GlassLight })
                {
                    ThemeService.Apply(theme);
                    var stack = new StackPanel { Margin = new Thickness(28) };
                    var check = new CheckBox { Content = "저장 위치 기억", IsChecked = true, Margin = new Thickness(0, 0, 0, 20) };
                    var tool = new ToggleButton { Content = "T", IsChecked = true,
                        Style = (Style)application.FindResource("Rail.ToolButton"), HorizontalAlignment = HorizontalAlignment.Left };
                    stack.Children.Add(check);
                    stack.Children.Add(tool);
                    var window = new Window { Title = "설정 체크 · 편집 도구 선택", Content = stack,
                        Width = 500, Height = 250, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                    window.SetResourceReference(Control.BackgroundProperty, "Surface.Base");
                    FluidMotion.SetWindowEntrance(window, false);
                    try
                    {
                        window.Show();
                        SettleGalleryLayout(window);
                        var checkboxFill = (Border)check.Template.FindName("CheckedLayer", check);
                        var toolFill = (Border)tool.Template.FindName("CheckedLayer", tool);
                        PumpLibraryUntil(window, () => checkboxFill.Opacity >= 0.99 && toolFill.Opacity >= 0.99, "selected control fills");
                        object selectedBrush = application.Resources["Surface.Selected"];
                        Color previous = AppThemeNames.IsLight(theme) ? Color.FromRgb(0xDD, 0xE3, 0xEB) : Color.FromRgb(0x30, 0x36, 0x42);
                        application.Resources["Surface.Selected"] = new SolidColorBrush(previous);
                        SettleGalleryLayout(window);
                        SaveLibraryUxEvidence(window, theme, $"selection-consumers-before-{theme}.png");
                        application.Resources["Surface.Selected"] = selectedBrush;
                        SettleGalleryLayout(window);
                        Color selected = ((SolidColorBrush)selectedBrush).Color;
                        Assert.Equal(selected, ((SolidColorBrush)checkboxFill.Background).Color);
                        Assert.Equal(selected, ((SolidColorBrush)toolFill.Background).Color);
                        var glyph = (System.Windows.Shapes.Path)check.Template.FindName("Check", check);
                        Assert.True(glyph.IsVisible);
                        Assert.True(check.IsChecked);
                        Assert.True(tool.IsChecked);
                        AssertInsideClient(check, stack, theme.ToString());
                        AssertInsideClient(tool, stack, theme.ToString());
                        SaveLibraryUxEvidence(window, theme, $"selection-consumers-after-{theme}.png");
                    }
                    finally { window.Close(); }
                }
            }
            finally { application.Shutdown(); }
        });
}
