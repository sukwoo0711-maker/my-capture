using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task SettingsFonts_RecommendedPreviewExpansionAndSearchPlaceholder() =>
        RunIsolated(nameof(SettingsFonts_RecommendedPreviewExpansionAndSearchPlaceholder), () =>
        {
            Application application = CreateThemedApplication();
            try
            {
                // Korean first once failed fast on some CI runners ("managed code after the runtime
                // thread state has been destroyed"), on .NET 10.0.11 through 10.0.13 alike. The
                // Korean window's text editors were collected before the test thread ended, so their
                // TSF text stores stayed registered and msctf called them while the thread exited
                // (dotnet/wpf#11785). RunIsolated now keeps that thread until the process ends, and
                // WpfTestThread unregisters such stores first, so the order no longer matters.
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    var saved = new AppSettings();
                    saved.Annotation.FontFamily = "Private Font";
                    SettingsWindow window = CreateSettingsUxWindow(saved).Window;
                    try
                    {
                        window.ShowSettings();
                        Drain(window);
                        var tabs = (TabControl)window.FindName("CategoryTabs");
                        var search = (TextBox)tabs.Template.FindName("SettingsSearchBox", tabs);
                        var placeholder = (TextBlock)tabs.Template.FindName("SettingsSearchPlaceholder", tabs);
                        var icon = (SymbolIcon)tabs.Template.FindName("SettingsSearchIcon", tabs);
                        Assert.Equal(UiText.Get("Settings_Search"), AutomationProperties.GetName(search));
                        Assert.Equal(UiText.Get("EditorText_SettingsSearchPlaceholder"), placeholder.Text);
                        Assert.True(placeholder.IsVisible);
                        Assert.False(placeholder.IsHitTestVisible);
                        Assert.Equal(window.FindResource("Icon.Search"), icon.Data);
                        Assert.True(icon.IsVisible);
                        search.Text = UiText.Get("Settings_FontName");
                        Drain(window);
                        Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
                        Assert.Equal(search.Text, window.SearchQuery);
                        Assert.NotNull(window.FirstSearchMatch);
                        search.Clear();
                        Drain(window);
                        Assert.True(placeholder.IsVisible);
                        tabs.SelectedIndex = 3;
                        Drain(window);
                        var fonts = (ComboBox)window.FindName("FontFamilyBox");
                        Assert.Equal(FontCatalog.ChoicesFor("Private Font", FontCatalog.RecommendedFonts(UiText.Culture)), fonts.Items.Cast<FontChoice>());
                        Assert.Equal("Private Font", fonts.SelectedValue);
                        var preview = new ContentPresenter { ContentTemplate = fonts.ItemTemplate };
                        foreach (FontChoice choice in fonts.Items)
                        {
                            preview.Content = choice;
                            preview.Measure(new Size(220, 80));
                            preview.Arrange(new Rect(preview.DesiredSize));
                            TextBlock label = Assert.Single(Descendants(preview).OfType<TextBlock>());
                            Assert.Equal(choice.Label, label.Text);
                            Assert.Same(choice.PreviewFamily, label.FontFamily);
                        }
                        SaveEvidence(window, $"settings-fonts-{language}.png");
                        RenderFontSearchEvidence(window, language);
                        fonts.SelectedItem = fonts.Items.Cast<FontChoice>().Single(font => font.IsMore);
                        Drain(window);
                        Assert.Equal("Private Font", Draft(window).FontFamily);
                        Assert.Equal("Private Font", fonts.SelectedValue);
                        Assert.True(fonts.IsDropDownOpen);
                        Assert.Equal(FontCatalog.ChoicesFor("Private Font", FontCatalog.AvailableFonts(UiText.Culture)), fonts.Items.Cast<FontChoice>());
                        Assert.DoesNotContain(fonts.Items.Cast<FontChoice>(), font => font.IsMore);
                        FontChoice installed = fonts.Items.Cast<FontChoice>().First(font => font.Value != "Private Font");
                        fonts.SelectedItem = installed;
                        Assert.Equal(installed.Value, Draft(window).FontFamily);
                        fonts.SelectedItem = fonts.Items.Cast<FontChoice>().Single(font => font.Value == "Maple Mono");
                        Assert.Equal("Maple Mono", Draft(window).FontFamily);
                        fonts.IsDropDownOpen = false;
                    }
                    finally { window.CloseForExit(); }
                }
            }
            finally { application.Shutdown(); }
        });

    private static void RenderFontSearchEvidence(SettingsWindow window, string language)
    {
        string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_THEME_PREVIEW_EVIDENCE");
        string? baseline = Environment.GetEnvironmentVariable("MYCAPTURE_FONTS_BASELINE");
        if (string.IsNullOrWhiteSpace(evidence) || string.IsNullOrEmpty(baseline)) return;
        string directory = DiagnosticOutputPaths.Create(evidence);
        using Stream current = typeof(SettingsThemePreviewTests).Assembly.GetManifestResourceStream("SettingsFixture.Window.xaml")!;
        RenderSearchMarkup(XDocument.Load(baseline), "before");
        RenderSearchMarkup(XDocument.Load(current), "after");

        void RenderSearchMarkup(XDocument source, string stage)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XElement input = source.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "SettingsSearchBox");
            var markup = new XElement(input.Ancestors(wpf + "StackPanel").First());
            foreach (XAttribute declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            {
                string value = declaration.Value;
                if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                    value += ";assembly=MyCapture";
                markup.SetAttributeValue(declaration.Name, value);
            }
            foreach (XElement element in markup.DescendantsAndSelf())
            {
                element.Attribute("TextChanged")?.Remove();
                string ns = element.Name.NamespaceName;
                if (ns.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !ns.Contains(";assembly=", StringComparison.Ordinal))
                    element.Name = XName.Get(element.Name.LocalName, ns + ";assembly=MyCapture");
            }
            var resources = new ResourceDictionary();
            foreach (object key in window.Resources.Keys) resources.Add(key, window.Resources[key]);
            applicationResources().MergedDictionaries.Add(resources);
            StackPanel panel;
            try { panel = (StackPanel)XamlReader.Parse(markup.ToString()); }
            finally { applicationResources().MergedDictionaries.Remove(resources); }
            var border = new Border { Width = 200, Height = 120, Padding = new Thickness(10), Child = panel,
                Style = (Style)window.FindResource("Card"), Resources = resources };
            System.Windows.Documents.TextElement.SetFontFamily(border, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(border, window.FontSize);
            System.Windows.Documents.TextElement.SetForeground(border, window.Foreground);
            border.Measure(new Size(200, 120));
            border.Arrange(new Rect(0, 0, 200, 120));
            border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(200, 120, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(border);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(DiagnosticOutputPaths.Child(directory, $"settings-search-{stage}-{language}.png"));
            encoder.Save(file);
        }

        static ResourceDictionary applicationResources() => Application.Current.Resources;
    }
}
