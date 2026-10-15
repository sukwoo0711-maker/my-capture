using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task BenchmarkContrast_ExportOptionsFollowLivePalette() =>
        RunIsolated(nameof(BenchmarkContrast_ExportOptionsFollowLivePalette), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var panel = new ImageExportOptionsPanel(new ImageExportOptions(), full: true) { Margin = new Thickness(24) };
            var window = new Window
            {
                Content = panel, Width = 380, Height = 780,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            };
            var failures = new List<string>();
            try
            {
                window.Show();
                Assert.Single(panel.Children.OfType<Expander>()).IsExpanded = true;
                foreach ((AppTheme theme, bool system) in BenchmarkPalettes())
                {
                    ApplyBenchmarkPalette(theme, system);
                    Drain(window);
                    SaveEvidence(window, $"export-options-{theme}{(system ? "-system" : string.Empty)}.png");
                    var rendered = RenderEditorWindow(window);
                    Assert.True(rendered.PixelWidth > 0);
                    Label[] labels = Descendants(panel).OfType<Label>().ToArray();
                    Assert.Equal(6, labels.Length);
                    foreach (Label label in labels)
                        CheckBenchmarkContrast(label.Foreground, window.Background, $"{theme}: {label.Content}", failures);
                    Expander expander = Assert.Single(panel.Children.OfType<Expander>());
                    CheckBenchmarkContrast(expander.Foreground, window.Background, $"{theme}: {expander.Header}", failures);
                    var header = Assert.IsType<ToggleButton>(expander.Template.FindName("HeaderToggle", expander));
                    Assert.Same(application.FindResource(typeof(ToggleButton)), header.Style);
                    expander.IsExpanded = false;
                    Assert.False(header.IsChecked);
                    expander.IsExpanded = true;
                    Assert.True(header.IsChecked);
                    foreach (TextBlock text in Descendants(panel).OfType<TextBlock>().Where(text => text.IsVisible && text.Text.Length > 0))
                        CheckBenchmarkContrast(text.Foreground, window.Background, $"{theme}: {text.Text}", failures);
                    foreach (Control control in Descendants(panel).OfType<Control>().Where(control => control is ComboBox or CheckBox or Slider))
                        Assert.NotNull(control.Style);
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                window.Close();
                ThemeService.SystemHighContrastOverrideForTest = null;
                ThemeService.SystemColorsOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                application.Shutdown();
            }
        });

    [Fact]
    public Task BenchmarkContrast_GalleryLabelsMixedTagsAndProgressFollowLivePalette() =>
        RunIsolated(nameof(BenchmarkContrast_GalleryLabelsMixedTagsAndProgressFollowLivePalette), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = CreateLibraryMotionApplication();
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            var tags = new GalleryBatchTagsDialog([new CaptureRecord { Tags = "all, mixed" }, new CaptureRecord { Tags = "all" }],
                _ => throw new InvalidOperationException("The contrast fixture must not save."));
            var contact = new GalleryContactSheetDialog([], 2, () => true);
            LibraryUxFixture gallery = CreateLibraryUxFixture("mc-benchmark-contrast-");
            var progress = new MediaExportProgressRow();
            progress.Start("Test export");
            var progressWindow = new Window { Content = progress, Width = 500, Height = 120 };
            var failures = new List<string>();
            AppAccent previousAccent = ThemeService.CurrentAccent;
            try
            {
                foreach (Window window in new Window[] { tags, contact, gallery.Window, progressWindow })
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -10000; window.Top = -10000;
                    window.ShowActivated = false; window.ShowInTaskbar = false;
                    window.Show();
                }
                // The empty contact-sheet fixture completes before starting a renderer or opening a file.
                Assert.True(contact.PendingPreview.IsCompleted);
                foreach (AppAccent accent in Enum.GetValues<AppAccent>())
                foreach ((AppTheme theme, bool system) in BenchmarkPalettes())
                {
                    ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(theme), accentColor: AppAccentNames.ToSetting(accent));
                    ApplyBenchmarkPalette(theme, system);
                    Drain(gallery.Window);
                    foreach (Window dialog in new Window[] { tags, contact })
                    {
                        var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                        Drain(dialog);
                        Label[] labels = Descendants(content).OfType<Label>().ToArray();
                        Assert.Equal(dialog == tags ? 1 : 3, labels.Length);
                        foreach (Label label in labels)
                        {
                            Assert.Same(application.FindResource("Text.Primary"), label.Foreground);
                            CheckBenchmarkContrast(label.Foreground, dialog.Background, $"{theme}: {label.Content}", failures);
                        }
                    }
                    CheckBox mixed = tags.Rows["mixed"];
                    Assert.Same(application.FindResource(typeof(CheckBox)), mixed.Style);
                    Assert.Same(application.FindResource("Text.Primary"), mixed.Foreground);
                    Assert.Null(mixed.IsChecked);
                    var mark = Assert.IsType<System.Windows.Shapes.Path>(mixed.Template.FindName("Check", mixed));
                    Assert.Equal(0, mark.Data.Bounds.Height);
                    CheckBenchmarkContrast(mark.Stroke, (Brush)application.FindResource("Accent.Default"), $"{theme}: mixed tag", failures);
                    mixed.IsChecked = true;
                    Assert.True(mark.Data.Bounds.Height > 0);
                    mixed.IsChecked = null;

                    ProgressBar contactProgress = BenchmarkLogicalDescendants(contact).OfType<ProgressBar>().Single();
                    ProgressBar importProgress = Assert.IsType<ProgressBar>(gallery.Window.FindName("ImportProgress"));
                    ProgressBar exportProgress = progress.Children.OfType<ProgressBar>().Single();
                    foreach (ProgressBar bar in new[] { contactProgress, importProgress, exportProgress })
                    {
                        Assert.Equal(Assert.IsType<SolidColorBrush>(bar.FindResource("Accent.Indicator")).Color,
                            Assert.IsType<SolidColorBrush>(bar.Foreground).Color);
                        Assert.Equal(Assert.IsType<SolidColorBrush>(bar.FindResource("Surface.Sunken")).Color,
                            Assert.IsType<SolidColorBrush>(bar.Background).Color);
                        CheckBenchmarkContrast(bar.Foreground, bar.Background,
                            $"{theme}/{accent}: progress", failures, minimum: 3);
                    }
                }
                Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
            }
            finally
            {
                gallery.Window.Close();
                progressWindow.Close();
                contact.Close();
                tags.Close();
                ThemeService.SystemHighContrastOverrideForTest = null;
                ThemeService.SystemColorsOverrideForTest = null;
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                ThemeService.ApplyFromSettings(AppThemeNames.ToSetting(ThemeService.Current),
                    accentColor: AppAccentNames.ToSetting(previousAccent));
                application.Shutdown();
            }
        });

    private static IEnumerable<(AppTheme Theme, bool System)> BenchmarkPalettes() =>
        Enum.GetValues<AppTheme>().Select(theme => (theme, false))
            .Concat([(AppTheme.Midnight, true), (AppTheme.Daylight, false)]);

    private static void ApplyBenchmarkPalette(AppTheme theme, bool system)
    {
        ThemeService.SystemHighContrastOverrideForTest = system;
        ThemeService.SystemColorsOverrideForTest = role => SystemHighContrastThemeTests.NightSky[role];
        ThemeService.Apply(theme);
    }

    private static IEnumerable<DependencyObject> BenchmarkLogicalDescendants(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (DependencyObject descendant in BenchmarkLogicalDescendants(child)) yield return descendant;
        }
    }

    private static void CheckBenchmarkContrast(Brush foreground, Brush background, string label, List<string> failures,
        double minimum = 4.5)
    {
        Color front = Assert.IsType<SolidColorBrush>(foreground).Color;
        Color back = Assert.IsType<SolidColorBrush>(background).Color;
        double ratio = ThemeContrast.Ratio(ThemeColor.Rgb(front.R, front.G, front.B), ThemeColor.Rgb(back.R, back.G, back.B));
        if (ratio < minimum) failures.Add($"{label}: {ratio:F2}:1 (expected {minimum}:1)");
    }
}
