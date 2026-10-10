using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private static readonly AppTheme[] SettingsUxEvidenceThemes =
        [AppTheme.Glass, AppTheme.GlassLight, AppTheme.Workspace, AppTheme.Midnight, AppTheme.Daylight, AppTheme.HighContrast];

    /// <summary>
    /// Renders every settings category and a sheet of the shared button tiers (enabled and
    /// disabled) so a change to the settings layout or the shared button styles can be compared.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_SETTINGS_UX_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_SETTINGS_UX_PHASE</c> to label them (for example before and after a fix).
    /// Without the directory the scenes still render, so the layout code runs in every build.
    /// </remarks>
    [Fact]
    public Task SettingsUx_EveryCategoryAndButtonTierRendersInEvidenceThemes() =>
        RunIsolated(nameof(SettingsUx_EveryCategoryAndButtonTierRendersInEvidenceThemes), VerifySettingsUxEvidence);

    private static void VerifySettingsUxEvidence()
    {
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_PHASE") is { Length: > 0 } label ? label : "after";
        try
        {
            foreach (AppTheme theme in SettingsUxEvidenceThemes)
            {
                ThemeService.Apply(theme);
                RenderButtonTierSheet(AppThemeNames.ToSetting(theme), phase);
            }

            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.HighContrast })
                RenderSettingsCategories(theme, phase);
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void RenderSettingsCategories(AppTheme theme, string phase)
    {
        AppSettings saved = new() { General = { Theme = AppThemeNames.ToSetting(theme) } };
        ThemeService.ApplyFromSettings(saved.General.Theme);
        var window = new SettingsWindow(() => saved, _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 940, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
            for (int index = 0; index < items.Length; index++)
            {
                tabs.SelectedItem = items[index];
                Drain(window);
                SaveSettingsUxEvidence(RenderEditorWindow(window),
                    $"settings-tab-{index + 1:00}-{AppThemeNames.ToSetting(theme)}-{phase}.png");
            }
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static void RenderButtonTierSheet(string theme, string phase)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        int row = 0;
        void Header(string first, string second, string third)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach ((string text, int column) in new[] { (first, 0), (second, 1), (third, 2) })
            {
                var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
                block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
                Grid.SetRow(block, row);
                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }
            row++;
        }
        void Sample(string caption, Func<bool, Button> create)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            for (int column = 1; column <= 2; column++)
            {
                Button button = create(column == 1);
                button.IsEnabled = column == 1;
                button.HorizontalAlignment = HorizontalAlignment.Left;
                button.Margin = new Thickness(0, 4, 0, 4);
                Grid.SetRow(button, row);
                Grid.SetColumn(button, column);
                grid.Children.Add(button);
            }
            row++;
        }
        Button Styled(string style, object content)
        {
            var button = new Button { Content = content };
            button.SetResourceReference(FrameworkElement.StyleProperty, style);
            return button;
        }
        Header("단계", "활성", "비활성");
        Sample("주 (Primary)", _ => Styled("Button.Primary", "적용"));
        Sample("보조 (Secondary)", _ => Styled("Button.Secondary", "취소"));
        Sample("도구 (Ghost)", _ => Styled("Button.Ghost", "여백 자동 제거"));
        Sample("도구 작게 (GhostCompact)", _ => Styled("Button.GhostCompact", "다시 실행"));
        Sample("보조 작게 (Compact)", _ => Styled("Button.Compact", "찾아보기"));
        Sample("위험 (Danger)", _ => Styled("Button.Danger", "삭제"));
        Sample("아이콘 (Icon)", _ =>
        {
            var glyph = new System.Windows.Shapes.Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.6 };
            glyph.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "Icon.Undo");
            glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
            return Styled("Button.Icon", glyph);
        });
        Sample("도구 + 단축키 (편집기)", _ =>
        {
            Button button = Styled("Button.Ghost", "다시 실행");
            button.Style = (Style)Application.Current.FindResource("Button.Ghost");
            ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Redo");
            return button;
        });
        Sample("보조 + 단축키 (편집기)", _ =>
        {
            Button button = Styled("Button.Secondary", "개인정보 가리기");
            button.Style = (Style)Application.Current.FindResource("Button.Secondary");
            ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Privacy");
            return button;
        });

        var surface = new Border { Child = grid };
        surface.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        var window = new Window
        {
            Content = surface,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            Drain(window);
            Assert.All(grid.Children.OfType<Button>(), button => Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0));
            SaveSettingsUxEvidence(RenderEditorWindow(window), $"buttons-{theme}-{phase}.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static void SaveSettingsUxEvidence(BitmapSource bitmap, string name)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
