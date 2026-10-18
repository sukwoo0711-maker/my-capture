using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryUx_SearchPlaceholderFitsMinimumWideAndScreenConstrainedWidths() =>
        RunIsolated(nameof(LibraryUx_SearchPlaceholderFitsMinimumWideAndScreenConstrainedWidths), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                foreach (string language in new[] { "en-US", "ko-KR" })
                {
                    using var scope = UiText.UseLanguage(language);
                    ThemeService.Apply(AppTheme.Glass);
                    LibraryUxFixture fixture = CreateLibraryUxFixture("mc-search-fit-");
                    GalleryWindow window = fixture.Window;
                    try
                    {
                        window.Show();
                        // Reuse the same controls through theme, DPI and width changes.
                        foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Daylight })
                        {
                            ThemeService.Apply(theme);
                            foreach (double scale in new[] { 1.0, 1.25, 1.5 })
                            {
                                VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                                foreach (UIElement element in Descendants(window).Prepend(window).OfType<UIElement>())
                                {
                                    element.InvalidateMeasure();
                                    element.InvalidateArrange();
                                }
                                string label = $"{language}/{theme}/{scale}";
                                var placeholder = (TextBlock)window.FindName("SearchPlaceholderText");
                                var search = (TextBox)window.FindName("SearchBox");
                                Assert.Contains(language == "en-US" ? "app:" : "앱:", (string)search.ToolTip, StringComparison.Ordinal);
                                Assert.Contains(language == "en-US" ? "tag:" : "태그:", (string)search.ToolTip, StringComparison.Ordinal);
                                foreach (double width in new[] { window.MinWidth, 1100.0, window.MinWidth })
                                {
                                    window.Width = width;
                                    SettleGalleryLayout(window);
                                    SaveLibraryUxEvidence(window, theme, $"search-fit-{language}-{theme}-dpi{scale}-window{width}.png");
                                    AssertSearchPlaceholderFit(window, label + $"/window-{width}");
                                }

                                WithHostedSearchCommandBar(window, host =>
                                {
                                    // The host is not capped by the VM's virtual screen. 916 is the
                                    // 980-DIP window minus its 40-DIP frame and 24-DIP library margin.
                                    foreach (double width in new[] { 916.0, 1036.0, 916.0, 900.0 })
                                    {
                                        ArrangeSearchHost(host, width);
                                        AssertSearchPlaceholderFit(window, label + $"/host-{width}");
                                        if (width == 1036)
                                            Assert.Equal(fixture.ViewModel.SearchPlaceholder, placeholder.Text);
                                    }
                                    Assert.True(host.ActualWidth < window.MinWidth);

                                    // Reproduce the CI failure's actual placeholder allocation, even
                                    // when this machine has different command-label font metrics.
                                    double runnerWidth = host.ActualWidth + 363.2 - placeholder.ActualWidth;
                                    ArrangeSearchHost(host, runnerWidth);
                                    Assert.InRange(placeholder.ActualWidth, 363.2 - 1 / scale, 363.2 + 1 / scale);
                                    AssertSearchPlaceholderFit(window, label + "/CI-363.2");
                                    if (language == "en-US" && scale == 1.25)
                                    {
                                        Assert.True(MeasureSearchHint(placeholder, fixture.ViewModel.SearchPlaceholder) > placeholder.ActualWidth);
                                        Assert.Equal(UiText.Get("LibraryUx_SearchPlaceholderCompact"), placeholder.Text);
                                    }

                                    // A compact/full swap must leave the star column and its neighbours fixed.
                                    double fieldWidth = ((FrameworkElement)window.FindName("SearchField")).ActualWidth;
                                    double sortLeft = ((FrameworkElement)window.FindName("SortBox")).TranslatePoint(new Point(), host).X;
                                    var fitting = (LibrarySearchPlaceholder)placeholder;
                                    string original = fitting.FullText;
                                    fitting.SetCurrentValue(LibrarySearchPlaceholder.FullTextProperty, "Search");
                                    ArrangeSearchHost(host, runnerWidth);
                                    Assert.Equal("Search", placeholder.Text);
                                    Assert.Equal(fieldWidth, ((FrameworkElement)window.FindName("SearchField")).ActualWidth, 3);
                                    Assert.Equal(sortLeft, ((FrameworkElement)window.FindName("SortBox")).TranslatePoint(new Point(), host).X, 3);
                                    fitting.SetCurrentValue(LibrarySearchPlaceholder.FullTextProperty, original);
                                    ArrangeSearchHost(host, runnerWidth);
                                    AssertSearchPlaceholderFit(window, label + "/restored");
                                });

                                window.Width = window.MinWidth;
                                SettleGalleryLayout(window);
                                fixture.ViewModel.Select(fixture.Records[0].Id);
                                SettleGalleryLayout(window);
                                Assert.Equal(UiText.Get("Text_4B8D541EBE6D"), placeholder.Text);
                                AssertNotTrimmed(placeholder, label + "/selection");
                                fixture.ViewModel.ClearSelection();
                                SettleGalleryLayout(window);
                                AssertSearchPlaceholderFit(window, label + "/cleared");
                                fixture.ViewModel.SelectScope(CaptureFolderScope.ForFolder(fixture.Design.Id));
                                SettleGalleryLayout(window);
                                Assert.Equal(fixture.ViewModel.SearchPlaceholder, placeholder.Text);
                                Assert.Equal(string.Empty, fixture.ViewModel.CompactSearchPlaceholder);
                                Assert.Equal(TextTrimming.CharacterEllipsis, placeholder.TextTrimming);
                                fixture.ViewModel.SelectScope(CaptureFolderScope.All);
                                SettleGalleryLayout(window);
                                AssertSearchPlaceholderFit(window, label + "/root");
                            }
                        }
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static double MeasureSearchHint(TextBlock text, string value)
    {
        var probe = new TextBlock
        {
            Text = value, FontFamily = text.FontFamily, FontSize = text.FontSize,
            FontWeight = text.FontWeight, FontStyle = text.FontStyle, FontStretch = text.FontStretch,
            Language = text.Language, FlowDirection = text.FlowDirection, Padding = text.Padding,
            UseLayoutRounding = text.UseLayoutRounding,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(text));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static void AssertSearchPlaceholderFit(GalleryWindow window, string label)
    {
        var placeholder = (TextBlock)window.FindName("SearchPlaceholderText");
        var vm = (GalleryViewModel)window.DataContext;
        Assert.Equal(MeasureSearchHint(placeholder, vm.SearchPlaceholder) > placeholder.ActualWidth
            ? UiText.Get("LibraryUx_SearchPlaceholderCompact") : vm.SearchPlaceholder, placeholder.Text);
        AssertNotTrimmed(placeholder, label);
    }

    private static void ArrangeSearchHost(FrameworkElement host, double width)
    {
        // Allow the size notification and the fitted-text binding to settle without a HWND.
        for (int pass = 0; pass < 3; pass++)
        {
            host.Measure(new Size(width, 54));
            host.Arrange(new Rect(0, 0, width, 54));
            host.UpdateLayout();
        }
    }

    private static void WithHostedSearchCommandBar(GalleryWindow window, Action<Decorator> verify)
    {
        var bar = (Grid)window.FindName("LibraryCommandBar");
        var parent = (Panel)bar.Parent;
        int index = parent.Children.IndexOf(bar);
        var host = new Decorator
        {
            DataContext = window.DataContext, Resources = window.Resources,
            UseLayoutRounding = bar.UseLayoutRounding,
        };
        TextElement.SetFontFamily(host, TextElement.GetFontFamily(bar));
        TextElement.SetFontSize(host, TextElement.GetFontSize(bar));
        TextElement.SetFontWeight(host, TextElement.GetFontWeight(bar));
        TextOptions.SetTextFormattingMode(host, TextOptions.GetTextFormattingMode(bar));
        VisualTreeHelper.SetRootDpi(host, VisualTreeHelper.GetDpi(bar));
        NameScope.SetNameScope(host, NameScope.GetNameScope(window));
        parent.Children.Remove(bar);
        host.Child = bar;
        try { verify(host); }
        finally
        {
            host.Child = null;
            parent.Children.Insert(index, bar);
            SettleGalleryLayout(window);
        }
    }

    private static void AssertDefaultSearchPlaceholder(GalleryWindow window, string label)
    {
        WithHostedSearchCommandBar(window, host =>
        {
            // Check the authored 1280-DIP default separately from its screen-capped HWND.
            ArrangeSearchHost(host, 1280 - 40 - 24);
            Assert.Equal(((GalleryViewModel)window.DataContext).SearchPlaceholder,
                ((TextBlock)window.FindName("SearchPlaceholderText")).Text);
            AssertNotTrimmed((TextBlock)window.FindName("SearchPlaceholderText"), label + "/default-1280");
        });
    }
}
