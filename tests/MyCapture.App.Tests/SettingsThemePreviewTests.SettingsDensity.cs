using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Settings pages stay dense at the minimum window size: short values sit on one row
/// (label, input, range), inputs share the 32-high ComboBox size, and the recording size is
/// one width × height row behind an explicit dragged/fixed choice. Nothing clips or overlaps on
/// any page, in either language, at the default or the minimum size.
/// </summary>
/// <remarks>
/// With <c>MYCAPTURE_SETTINGS_UX_EVIDENCE</c> set, every page is also written as PNG at 96 and
/// 144 dpi (top, and bottom when the page scrolls), labelled by <c>MYCAPTURE_SETTINGS_UX_PHASE</c>.
/// </remarks>
public sealed partial class SettingsThemePreviewTests
{
    private static readonly (string Name, double Width, double Height)[] SettingsDensitySizes =
        [("normal", 940, 700), ("compact", 780, 560)];

    [Fact]
    public Task SettingsDensity_EveryPageFitsWithoutClippingOrOverlap_InBothLanguagesAndSizes() =>
        RunIsolated(nameof(SettingsDensity_EveryPageFitsWithoutClippingOrOverlap_InBothLanguagesAndSizes), VerifySettingsDensityPages);

    [Fact]
    public Task SettingsDensity_RecordingSizeIsOneRowBehindAnExplicitChoice() =>
        RunIsolated(nameof(SettingsDensity_RecordingSizeIsOneRowBehindAnExplicitChoice), VerifyRecordingSizeChoice);

    private static void VerifySettingsDensityPages()
    {
        Application application = CreateThemedApplication();
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_PHASE") is { Length: > 0 } label ? label : "after";
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                foreach (AppTheme theme in locale == "ko-KR" ? new[] { AppTheme.Glass, AppTheme.HighContrast } : new[] { AppTheme.Glass })
                {
                    SettingsWindow window = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.ToSetting(theme) } }).Window;
                    try
                    {
                        window.Show();
                        var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                        TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
                        foreach ((string sizeName, double width, double height) in SettingsDensitySizes)
                        {
                            window.Width = width;
                            window.Height = height;
                            for (int index = 0; index < items.Length; index++)
                            {
                                tabs.SelectedItem = items[index];
                                Drain(window);
                                var page = Assert.IsType<ScrollViewer>(items[index].Content);
                                string where = $"{locale} {AppThemeNames.ToSetting(theme)} {sizeName} page {index + 1}";
                                AssertNoClippingOrOverlap(page, where);
                                AssertSettingsInputsAreCompact(page, where);
                                if (index == 3) AssertEditingDefaultsFitWithoutScrolling(page, where);

                                string name = $"settings-density-{locale}-{AppThemeNames.ToSetting(theme)}-{sizeName}-page-{index + 1}";
                                SaveSettingsDensityEvidence(window, $"{name}-top-{phase}");
                                if (page.ScrollableHeight > 0)
                                {
                                    page.ScrollToEnd();
                                    Drain(window);
                                    SaveSettingsDensityEvidence(window, $"{name}-bottom-{phase}");
                                    page.ScrollToTop();
                                }
                            }
                        }
                        SettingsWindow.CancelCommand.Execute(null, window);
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
        }
    }

    private static void VerifyRecordingSizeChoice()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                // Settings from an older version: 0 meant "no fixed size".
                SettingsUxFixture fixture = CreateSettingsUxWindow(new AppSettings { Recording = { PresetWidth = 0, PresetHeight = 0 } });
                SettingsWindow window = fixture.Window;
                try
                {
                    window.ShowSettings();
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabRecording")));
                    Drain(window);
                    var dragged = Assert.IsType<RadioButton>(window.FindName("RecordingSizeDraggedOption"));
                    var fixedSize = Assert.IsType<RadioButton>(window.FindName("RecordingSizeFixedOption"));
                    var row = Assert.IsType<StackPanel>(window.FindName("RecordingSizeRow"));
                    var widthBox = Assert.IsType<TextBox>(window.FindName("RecordingPresetWidthBox"));
                    var heightBox = Assert.IsType<TextBox>(window.FindName("RecordingPresetHeightBox"));
                    Assert.Equal(UiText.Get("Recording_SizeDragged"), System.Windows.Automation.AutomationProperties.GetName(dragged));
                    Assert.Equal(UiText.Get("Recording_SizeFixed"), System.Windows.Automation.AutomationProperties.GetName(fixedSize));
                    Assert.Equal(UiText.Get("Recording_PresetWidth"), System.Windows.Automation.AutomationProperties.GetName(widthBox));
                    Assert.Equal(UiText.Get("Recording_PresetHeight"), System.Windows.Automation.AutomationProperties.GetName(heightBox));

                    // The legacy 0 loads as the dragged size; the boxes are visible but take no input.
                    // The previous locale's closed window must not uncheck this one (no shared GroupName).
                    Assert.True(dragged.IsChecked);
                    Assert.Same(dragged.Parent, fixedSize.Parent);
                    Assert.False(fixedSize.IsChecked);
                    Assert.False(widthBox.IsEnabled || heightBox.IsEnabled);
                    Assert.Equal(("1280", "720"), (widthBox.Text, heightBox.Text));

                    foreach ((string _, double width, double height) in SettingsDensitySizes)
                    {
                        window.Width = width;
                        window.Height = height;
                        Drain(window);
                        // Width × height px on one row, beside the fixed choice, at the settings input height.
                        Rect widthBounds = BoundsIn(widthBox, window);
                        Rect heightBounds = BoundsIn(heightBox, window);
                        Rect fixedBounds = BoundsIn(fixedSize, window);
                        Assert.Equal(widthBounds.Top, heightBounds.Top, 0.5);
                        Assert.Equal(widthBounds.Height, heightBounds.Height, 0.5);
                        Assert.InRange(widthBounds.Height, 32, 34);
                        Assert.True(widthBounds.Right < heightBounds.Left, $"{widthBounds} / {heightBounds}");
                        Assert.Equal(fixedBounds.Top + fixedBounds.Height / 2, widthBounds.Top + widthBounds.Height / 2, 1);
                        Assert.True(fixedBounds.Right <= widthBounds.Left);
                        TextBlock times = Descendants(row).OfType<TextBlock>().Single(text => text.Text == "×");
                        Rect timesBounds = BoundsIn(times, window);
                        Assert.True(widthBounds.Right <= timesBounds.Left && timesBounds.Right <= heightBounds.Left);
                        Assert.Equal("px", Descendants(row).OfType<TextBlock>().Last().Text);
                        Assert.True(BoundsIn(dragged, window).Bottom <= fixedBounds.Top + 0.5);
                    }

                    // Choosing the fixed size enables the boxes; Apply saves them.
                    fixedSize.IsChecked = true;
                    Assert.True(Draft(window).UseRecordingFixedSize);
                    Assert.False(dragged.IsChecked);
                    Assert.True(row.IsEnabled && widthBox.IsEnabled && heightBox.IsEnabled);
                    widthBox.Text = "800";
                    heightBox.Text = "600";
                    SettingsWindow.ApplyCommand.Execute(null, window);
                    Assert.Equal(800, fixture.Saved().Recording.PresetWidth);
                    Assert.Equal(600, fixture.Saved().Recording.PresetHeight);

                    // Reopening shows the fixed size as saved.
                    window.ShowSettings();
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabRecording")));
                    Drain(window);
                    Assert.True(fixedSize.IsChecked);
                    Assert.Equal(("800", "600"), (widthBox.Text, heightBox.Text));

                    // A size out of range keeps the window open with its message; the dragged size clears it.
                    widthBox.Text = "50";
                    Drain(window);
                    Assert.True(Validation.GetHasError(widthBox));
                    SettingsWindow.ApplyCommand.Execute(null, window);
                    Assert.True(window.IsVisible);
                    Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ErrorSummary")).Visibility);
                    dragged.IsChecked = true;
                    Drain(window);
                    Assert.False(fixedSize.IsChecked);
                    Assert.False(Validation.GetHasError(widthBox));
                    Assert.False(widthBox.IsEnabled);
                    SettingsWindow.ApplyCommand.Execute(null, window);
                    Assert.Null(fixture.Saved().Recording.PresetWidth);
                    Assert.Null(fixture.Saved().Recording.PresetHeight);
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

    /// <summary>
    /// Every control and loose text on the page stays inside the page horizontally, shows its
    /// whole text, and covers no other control or text.
    /// </summary>
    private static void AssertNoClippingOrOverlap(ScrollViewer page, string where)
    {
        var leaves = new List<(FrameworkElement Element, Rect Bounds)>();
        void Collect(DependencyObject node)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                if (VisualTreeHelper.GetChild(node, index) is not FrameworkElement child || !child.IsVisible) continue;
                if (child is TextBox or PasswordBox or ComboBox or ButtonBase or TextBlock) leaves.Add((child, BoundsIn(child, Viewport(page))));
                else Collect(child);
            }
        }
        ScrollContentPresenter viewport = Viewport(page);
        Collect((DependencyObject)page.Content);
        Assert.NotEmpty(leaves);

        foreach ((FrameworkElement element, Rect bounds) in leaves)
        {
            string name = Describe(element);
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= viewport.ActualWidth + 0.5,
                $"{where}: {name} {bounds} leaves the {viewport.ActualWidth:0}-wide page.");
            // An element shown through a text template would read as its type name.
            if (element is TextBlock shown)
                Assert.False(shown.Text.StartsWith("System.", StringComparison.Ordinal), $"{where}: {name}");
            // DesiredSize includes the margin; ActualWidth does not.
            if (element is TextBlock text)
                Assert.True(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth + 1
                    && text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom <= text.ActualHeight + 1,
                    $"{where}: \"{text.Text}\" is clipped ({text.DesiredSize} in {text.ActualWidth:0.#}x{text.ActualHeight:0.#}).");
        }

        for (int first = 0; first < leaves.Count; first++)
            for (int second = first + 1; second < leaves.Count; second++)
            {
                Rect overlap = Rect.Intersect(leaves[first].Bounds, leaves[second].Bounds);
                Assert.False(!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1,
                    $"{where}: {Describe(leaves[first].Element)} {leaves[first].Bounds} overlaps {Describe(leaves[second].Element)} {leaves[second].Bounds}.");
            }

        static string Describe(FrameworkElement element) => element switch
        {
            TextBlock text => $"\"{text.Text}\"",
            _ => $"{element.GetType().Name} {System.Windows.Automation.AutomationProperties.GetName(element)}",
        };
    }

    /// <summary>Every settings text input is as high as the settings ComboBoxes (32).</summary>
    private static void AssertSettingsInputsAreCompact(ScrollViewer page, string where)
    {
        foreach (TextBox box in Descendants(page).OfType<TextBox>().Where(box => box.IsVisible))
            Assert.True(box.ActualHeight is >= 32 and <= 34, $"{where}: {System.Windows.Automation.AutomationProperties.GetName(box)} is {box.ActualHeight:0.#} high.");
        foreach (PasswordBox box in Descendants(page).OfType<PasswordBox>().Where(box => box.IsVisible))
            Assert.True(box.ActualHeight is >= 32 and <= 34, $"{where}: the token box is {box.ActualHeight:0.#} high.");

        // Within a page, every one-row field starts its input at the same x.
        double[] inputLefts = Descendants(page).OfType<SettingsField>().Where(field => field.IsVisible)
            .Select(field => BoundsIn((FrameworkElement)field.Content, page).Left).Distinct().ToArray();
        Assert.True(inputLefts.Length <= 1 || inputLefts.Max() - inputLefts.Min() < 0.5, $"{where}: inputs start at {string.Join(", ", inputLefts)}.");
    }

    /// <summary>The audit's case: at the minimum size all six editor defaults are visible at once.</summary>
    private static void AssertEditingDefaultsFitWithoutScrolling(ScrollViewer page, string where)
    {
        ScrollContentPresenter viewport = Viewport(page);
        SettingsField[] defaults = Descendants(page).OfType<SettingsField>().Take(6).ToArray();
        Assert.Equal(6, defaults.Length);
        foreach (SettingsField field in defaults)
        {
            Rect bounds = BoundsIn((FrameworkElement)field.Content, viewport);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= viewport.ActualHeight,
                $"{where}: {field.Header} input {bounds} is outside the {viewport.ActualHeight:0}-high page.");
        }
    }

    private static ScrollContentPresenter Viewport(ScrollViewer page) =>
        Descendants(page).OfType<ScrollContentPresenter>().First();

    private static Rect BoundsIn(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void SaveSettingsDensityEvidence(Window window, string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_UX_EVIDENCE"))) return;
        foreach (int dpi in new[] { 96, 144 })
            SaveSettingsUxEvidence(RenderAtDpi(window, dpi), $"{name}-{dpi}dpi.png");
    }

    /// <summary>The window's client area at a fixed raster density, on an opaque base.</summary>
    private static BitmapSource RenderAtDpi(Window window, int dpi)
    {
        Drain(window);
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext context = backdrop.RenderOpen())
        {
            context.DrawRectangle((Brush)window.FindResource("Surface.Base"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        bitmap.Render(backdrop);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }
}
