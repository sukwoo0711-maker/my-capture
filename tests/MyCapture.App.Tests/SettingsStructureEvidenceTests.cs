using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Renders preserved markup and the implemented window with synthetic defaults.</summary>
public sealed class SettingsStructureEvidenceTests
{
    [Fact]
    public Task KoreanBeforeAndAfterAllCategories() => Isolated(nameof(KoreanBeforeAndAfterAllCategories), () => Render("ko-KR", all: true));

    [Fact]
    public Task EnglishBeforeAndAfterCaptureAndStorage() => Isolated(nameof(EnglishBeforeAndAfterCaptureAndStorage), () => Render("en", all: false));

    private static Task Isolated(string name, Action action) => SettingsThemePreviewTests.RunIsolated(name, action, typeof(SettingsStructureEvidenceTests));

    internal static string Root
    {
        get
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MyCapture.sln")) || Directory.Exists(Path.Combine(directory.FullName, "src", "MyCapture.App")))
                    return directory.FullName;
            throw new IOException("Repository root not found.");
        }
    }
    internal static string Output => Path.Combine(Root, "work", "settings-structure");
    internal static AppPaths FixturePaths => AppPaths.CreateForRoot(Path.Combine(Output, "isolated-profile"));
    private static string RenderOutput => Path.Combine(Output, "final");

    internal static Application CreateApplication()
    {
        Assert.Equal(Path.Combine(Output, "isolated-profile"), FixturePaths.DataRoot);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.Apply(AppTheme.GlassLight);
        return app;
    }

    internal static SettingsWindow CreateWindow()
    {
        AppPaths paths = FixturePaths;
        AppSettings saved = NewInstallDefaults.Create();
        saved.General.Theme = AppThemeNames.GlassLight;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saved = candidate;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance, defaultCapturesRoot: () => paths.CapturesRoot);
        Configure(window);
        return window;
    }

    private static void Configure(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Width = 940; window.Height = 700;
        FluidMotion.SetWindowEntrance(window, false);
    }

    private static Window Before(SettingsWindow reference)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        using Stream baselineMarkup = typeof(SettingsStructureEvidenceTests).Assembly.GetManifestResourceStream("SettingsStructure.Before.xaml")!;
        var document = XDocument.Load(baselineMarkup);
        XElement markup = document.Root!;
        markup.Attribute(x + "Class")!.Remove();
        markup.Attribute(x + "ClassModifier")!.Remove();
        foreach (XAttribute declaration in markup.Attributes().Where(a => a.IsNamespaceDeclaration))
            if (declaration.Value.StartsWith("clr-namespace:MyCapture.") && !declaration.Value.Contains(";assembly=")) declaration.Value += ";assembly=MyCapture";
        foreach (XAttribute attribute in markup.DescendantsAndSelf().Attributes().ToArray())
            if (attribute.Value.StartsWith("On", StringComparison.Ordinal) && attribute.Name.LocalName != "Content") attribute.Remove();
        foreach (XElement element in markup.DescendantsAndSelf())
        {
            string ns = element.Name.NamespaceName;
            if (ns.StartsWith("clr-namespace:MyCapture.") && !ns.Contains(";assembly=")) element.Name = XName.Get(element.Name.LocalName, ns + ";assembly=MyCapture");
            foreach (XAttribute attribute in element.Attributes().Where(a => a.Name.NamespaceName.StartsWith("clr-namespace:MyCapture.") && !a.Name.NamespaceName.Contains(";assembly=")).ToArray())
            { element.SetAttributeValue(XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=MyCapture"), attribute.Value); attribute.Remove(); }
        }
        var window = (Window)XamlReader.Parse(markup.ToString());
        AppSettings saved = NewInstallDefaults.Create();
        saved.General.Theme = AppThemeNames.GlassLight;
        var draft = new SettingsDraft(saved);
        window.DataContext = draft;
        Configure(window);
        void Text(string name, string value) => ((TextBlock)window.FindName(name)).Text = value;
        Text("CurrentVersionLabel", ApplicationVersion.DisplayText);
        Text("RetentionSummary", SettingsUxText.RetentionSummary(draft.ImageRetentionHours));
        var retention = (ComboBox)window.FindName("RetentionChoiceBox");
        retention.ItemsSource = SettingsUxText.RetentionChoices(draft.ImageRetentionHours, draft.ImageRetentionHours);
        retention.SelectedValue = draft.ImageRetentionHours;
        Text("TranslationModelStatus", UiText.Get("Settings_TranslationNotInstalled"));
        ((Button)window.FindName("TranslationModelDelete")).IsEnabled = false;
        var font = (ComboBox)window.FindName("FontFamilyBox");
        font.ItemsSource = SettingsWindow.FontChoicesFor(draft.FontFamily, []);
        font.ItemTemplate = FontCatalog.PreviewTemplate();
        font.SelectedValue = draft.FontFamily;
        Text("PrivacyModelText", UiText.Format("About_PrivacyModel", SettingsWindow.OcrModelMegabytes));
        Text("PrivacyTranslationText", UiText.Format("About_PrivacyTranslation", TranslationModelCatalog.TotalMegabytes));
        Text("OcrQualityHintText", SettingsWindow.OcrQualityDescription);
        Text("UpdateVersionLabel", "MyCapture · 4.11.0");
        Text("UpdateDescription", UiText.Get("StoreUpdate_UnpackagedDescription") + Environment.NewLine + Environment.NewLine + UiText.Get("StoreUpdate_UnpackagedNote"));
        ((Button)window.FindName("UpdateAction")).Content = UiText.Get("StoreUpdate_OpenStore");
        // Use the same conditional channel/model/platform state on both layouts.
        foreach (XElement element in markup.Descendants())
        {
            if ((string?)element.Attribute(x + "Name") is not { } name
                || window.FindName(name) is not FrameworkElement baseline
                || reference.FindName(name) is not FrameworkElement current) continue;
            baseline.Visibility = current.Visibility;
            if (baseline is TextBlock oldText && current is TextBlock currentText) oldText.Text = currentText.Text;
        }
        return window;
    }

    private static void Render(string languageName, bool all)
    {
        Directory.CreateDirectory(RenderOutput);
        using var language = UiText.UseLanguage(languageName);
        UiText.Configure(languageName);
        Application app = CreateApplication();
        var manifest = new List<object>();
        try
        {
            SettingsWindow after = CreateWindow();
            try
            {
                Window before = Before(after);
                try { RenderPages(before, "before", languageName, all, manifest); }
                finally { before.Close(); }
                RenderPages(after, "after", languageName, all, manifest);
                if (all)
                {
                    after.Width = 780; after.Height = 560;
                    var tabs = (TabControl)after.FindName("CategoryTabs");
                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i;
                        ((ScrollViewer)((TabItem)tabs.Items[i]).Content).ScrollToTop();
                        Layout(after);
                        Save(after, $"after-minimum-{languageName}-{i + 1:00}-192dpi.png", 192);
                    }
                }
            }
            finally { after.CloseForExit(); }
            File.WriteAllText(Path.Combine(RenderOutput, $"render-manifest-{languageName}.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { app.Shutdown(); FluidMotion.AnimationsEnabledOverrideForTest = null; }
    }

    private static void RenderPages(Window window, string version, string language, bool all, List<object> manifest)
    {
        var tabs = (TabControl)window.FindName("CategoryTabs");
        Assert.Equal(version == "before" ? 7 : 9, tabs.Items.Count);
        window.Show();
        for (int index = 0; index < tabs.Items.Count; index++)
        {
            if (!all && !(version == "before" ? new[] { 1, 5 } : new[] { 0, 5 }).Contains(index)) continue;
            tabs.SelectedIndex = index;
            var tab = (TabItem)tabs.Items[index];
            var scroll = (ScrollViewer)tab.Content;
            scroll.ScrollToTop(); Layout(window);
            int page = 0;
            while (true)
            {
                string file = $"{version}-{language}-{index + 1:00}-{++page:00}.png";
                Save(window, file);
                manifest.Add(new { version, language, category = tab.Header?.ToString(), file, offset = scroll.VerticalOffset, extent = scroll.ExtentHeight, viewport = scroll.ViewportHeight });
                if (scroll.VerticalOffset >= scroll.ScrollableHeight - 1) break;
                double previous = scroll.VerticalOffset;
                scroll.ScrollToVerticalOffset(Math.Min(scroll.ScrollableHeight, previous + scroll.ViewportHeight - 35));
                Layout(window);
                Assert.True(scroll.VerticalOffset > previous);
                Assert.True(page < 15);
            }
            if (version == "after")
            {
                Expander[] groups = Logical(tab).OfType<Expander>().ToArray();
                foreach (Expander group in groups) group.IsExpanded = true;
                if (groups.Length > 0)
                {
                    scroll.ScrollToTop(); Layout(window);
                    int expandedPage = 0;
                    while (true)
                    {
                        string file = $"after-expanded-{language}-{index + 1:00}-{++expandedPage:00}.png";
                        Save(window, file);
                        manifest.Add(new { version, language, category = tab.Header?.ToString(), file, offset = scroll.VerticalOffset, extent = scroll.ExtentHeight, viewport = scroll.ViewportHeight });
                        if (scroll.VerticalOffset >= scroll.ScrollableHeight - 1) break;
                        scroll.ScrollToVerticalOffset(Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + scroll.ViewportHeight - 35));
                        Layout(window);
                        Assert.True(expandedPage < 15);
                    }
                    foreach (Expander group in groups) group.IsExpanded = false;
                }
            }
        }
    }

    internal static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { yield return child; foreach (DependencyObject nested in Logical(child)) yield return nested; }
    }

    internal static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }
    private static void Save(Window window, string file, int dpi = 96)
    {
        double scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(RenderOutput, file)); encoder.Save(stream);
    }
}
