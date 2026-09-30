using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GallerySourcePageDetailTests
{
    private const string Url = "https://example.com/projects/MyCapture/pull/313?view=split&discussion=long-capture-filename-review#discussion_r42";

    [Theory]
    [InlineData("ko-KR", 210)]
    [InlineData("en-US", 210)]
    [InlineData("ko-KR", 252)]
    [InlineData("en-US", 252)]
    public void SourcePagePanelUsesExactUrlAndReachableActionsWithoutLeakingOldSelection(string language, double width) =>
        StaTestHost.Run(() =>
        {
            using var localization = UiText.UseLanguage(language);
            var window = new Window
            {
                Width = width + 48, Height = 400, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            LoadProductPanel(window);
            FluidMotion.SetWindowEntrance(window, false);
            window.SetResourceReference(Window.BackgroundProperty, "Surface.Raised");
            var template = Assert.IsType<DataTemplate>(window.FindResource("Gallery.SourcePageFixture"));
            var panel = Assert.IsType<StackPanel>(template.LoadContent());
            panel.Width = width;
            var tile = Tile(Url);
            panel.DataContext = tile;
            window.Content = new Border { Padding = new Thickness(16), Child = panel };
            try
            {
                window.Show();
                Drain(window);
                var input = Assert.Single(Descendants(panel).OfType<TextBox>());
                Button[] actions = Descendants(panel).OfType<Button>().ToArray();
                Assert.Equal(2, actions.Length);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal(Url, input.Text);
                Assert.True(input.IsReadOnly);
                Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
                Assert.Equal(UiText.Get("Library.SourcePage.Label"), AutomationProperties.GetName(input));
                Assert.Equal(UiText.Get("Library.SourcePage.Open"), actions[0].Content);
                Assert.Equal(UiText.Get("Library.SourcePage.Copy"), actions[1].Content);
                foreach (Button action in actions)
                {
                    Assert.Same(tile, action.DataContext);
                    Assert.Equal(Url, ResolveActionUrl(action));
                    Assert.True(action.IsVisible && action.ActualHeight >= 32);
                    Point origin = action.TranslatePoint(new Point(), panel);
                    Assert.True(origin.X >= 0 && origin.X + action.ActualWidth <= panel.ActualWidth + .5,
                        $"{language} source action escaped panel width: {action.Content}; {origin.X}+{action.ActualWidth}>{panel.ActualWidth}");
                }
                input.SelectAll();
                Assert.Equal(Url, input.SelectedText); // Full path/query/fragment remain available for copying.
                input.Select(0, 0);
                SaveEvidence(window, $"gallery-source-page-{language}-{width:0}.png");

                // Changing selected records must retarget both text and action source;
                // the window's unrelated ambient DataContext is never an action target.
                const string secondUrl = "https://example.org/other-page?version=2#details";
                var second = Tile(secondUrl);
                window.DataContext = Tile("https://wrong-ambient.example/");
                panel.DataContext = second;
                Drain(window);
                Assert.Equal(secondUrl, input.Text);
                Assert.All(actions, action => Assert.Equal(secondUrl, ResolveActionUrl(action)));

                foreach (string? invalid in new[] { "", "javascript:alert(1)", "file:///C:/private.txt", "https://user:secret@example.com/", null })
                {
                    second.Record.SourcePageUrl = invalid!;
                    second.RaiseMetaChanged();
                    Drain(window);
                    Assert.Equal(Visibility.Collapsed, panel.Visibility);
                    Assert.Equal(string.Empty, input.Text);
                    Assert.All(actions, action => Assert.Empty(ResolveActionUrl(action)));
                }
                second.Record.SourcePageUrl = Url;
                second.RaiseMetaChanged();
                Drain(window);
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal(Url, input.Text);
            }
            finally { window.Close(); }
        });

    private static string ResolveActionUrl(Button action)
    {
        MethodInfo method = typeof(GalleryWindow).GetMethod("SourceUrlForAction", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production URL action resolver.");
        return Assert.IsType<string>(method.Invoke(null, [action]));
    }

    private static GalleryItemViewModel Tile(string url) =>
        new(new CaptureRecord { SourcePageUrl = url }, _ => "unused-synthetic-thumbnail.png", 64);

    private static void LoadProductPanel(Window window)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(GallerySourcePageDetailTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            CopyNamespaces(dictionary, combined);
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        using Stream gallery = typeof(GallerySourcePageDetailTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        XElement product = XDocument.Load(gallery).Root!;
        CopyNamespaces(product, combined);
        combined.Add(new XElement(XName.Get("BoolToVisibilityConverter", "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"),
            new XAttribute(x + "Key", "BoolToVisibility")));
        XElement sourcePanel = Assert.Single(product.Descendants(p + "StackPanel"), element =>
            ((string?)element.Attribute("Visibility"))?.Contains("HasSourcePageUrl", StringComparison.Ordinal) == true);
        XElement[] actionMarkup = sourcePanel.Descendants(p + "Button").ToArray();
        Assert.Equal(2, actionMarkup.Length);
        string[] handlers = ["OnOpenSourcePageClick", "OnCopySourcePageClick"];
        for (int index = 0; index < handlers.Length; index++)
        {
            Assert.Equal(handlers[index], (string?)actionMarkup[index].Attribute("Click"));
            MethodInfo handler = typeof(GalleryWindow).GetMethod(handlers[index], BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Product XAML action points to a missing handler.");
            Assert.Equal(typeof(void), handler.ReturnType);
            Assert.Equal(new[] { typeof(object), typeof(RoutedEventArgs) }, handler.GetParameters().Select(parameter => parameter.ParameterType));
        }
        var panel = new XElement(sourcePanel);
        // The actual XAML's event hookups and production target resolver were checked
        // above. Rendering this bounded fixture does not open a browser or touch the clipboard.
        foreach (XAttribute handler in panel.Descendants().Attributes("Click").ToArray()) handler.Remove();
        combined.Add(new XElement(p + "DataTemplate", new XAttribute(x + "Key", "Gallery.SourcePageFixture"), panel));
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined.ToString(SaveOptions.DisableFormatting)));
    }

    private static void CopyNamespaces(XElement source, XElement destination)
    {
        foreach (XAttribute attribute in source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = attribute.Value;
            if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                value += ";assembly=MyCapture";
            destination.SetAttributeValue(attribute.Name, value);
        }
    }

    private static void Drain(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveEvidence(Window window, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_SOURCE_PAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var pixels = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        pixels.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var output = File.Create(Path.Combine(root, name));
        encoder.Save(output);
    }
}
