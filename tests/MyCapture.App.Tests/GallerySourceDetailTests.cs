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
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The library details panel's source section: app, window, and page link or folder.</summary>
public sealed class GallerySourceDetailTests
{
    private const string Url = "https://example.com/projects/MyCapture/pull/313?view=split&discussion=long-capture-filename-review#discussion_r42";
    private const string Folder = @"C:\Users\test\Documents\계약 검토\2026 하반기 프로젝트 자료\최종 보고서 초안";
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string ExplorerPath = @"C:\Windows\explorer.exe";
    private const string LongTitle = "Pull request #313 — 캡처 출처 메타데이터를 라이브러리 상세 정보에 표시하고 폴더 열기와 경로 복사를 제공 — Microsoft Edge";

    public static IEnumerable<object[]> Layouts() =>
        from language in new[] { "ko-KR", "en-US" }
        from width in new[] { 188d, 204d, 252d }
        select new object[] { language, width };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void WebPageSourceShowsAppWindowAndReachableLinkActions(string language, double width) =>
        Render(language, width, Image(EdgeSource(), LongTitle, Url), (window, panel, tile) =>
        {
            Assert.Equal(Visibility.Visible, panel.Visibility);
            AssertShown(panel, "LibrarySourceApp", "LibrarySourceWindow", "LibrarySourcePage");
            AssertHidden(panel, "LibrarySourceLocation");
            TextBlock app = Named<TextBlock>(panel, "LibrarySourceApp");
            Assert.Equal("Microsoft Edge", app.Text);
            Assert.Equal(UiText.Format("Library.Source.AppAccessible", "Microsoft Edge"), AutomationProperties.GetName(app));
            Assert.Equal(EdgePath, app.ToolTip);
            TextBlock title = Named<TextBlock>(panel, "LibrarySourceWindow");
            Assert.Equal(LongTitle, title.Text);
            Assert.Equal(UiText.Format("Library.Source.WindowAccessible", LongTitle), AutomationProperties.GetName(title));
            Assert.True(title.ActualHeight <= 58.5, $"window title grew to {title.ActualHeight}");

            StackPanel page = Named<StackPanel>(panel, "LibrarySourcePage");
            TextBox link = Assert.Single(Descendants(page).OfType<TextBox>());
            Assert.Equal(Url, link.Text);
            Assert.True(link.IsReadOnly);
            Assert.Equal(UiText.Get("Library.SourcePage.Label"), AutomationProperties.GetName(link));
            Button[] actions = Descendants(page).OfType<Button>().ToArray();
            Assert.Equal(new[] { UiText.Get("Library.SourcePage.Open"), UiText.Get("Library.SourcePage.Copy") },
                actions.Select(action => action.Content?.ToString() ?? string.Empty));
            AssertReachable(panel, actions, language);
            Assert.All(actions, action => Assert.Equal(Url, ResolveAction("SourceUrlForAction", action)));
            Assert.All(actions, action => Assert.Empty(ResolveAction("SourcePathForAction", action)));
            link.SelectAll();
            Assert.Equal(Url, link.SelectedText);
            link.Select(0, 0);
            SaveEvidence(window, $"gallery-source-page-{language}-{width:0}.png");

            // Retargeting to another record moves both the text and the action target.
            const string secondUrl = "https://example.org/other-page?version=2#details";
            var second = Tile(Image(EdgeSource(), "Other", secondUrl));
            window.DataContext = Tile(Image(EdgeSource(), "Ambient", "https://wrong-ambient.example/"));
            panel.DataContext = second;
            Drain(window);
            Assert.Equal(secondUrl, link.Text);
            Assert.All(actions, action => Assert.Equal(secondUrl, ResolveAction("SourceUrlForAction", action)));

            foreach (string? invalid in new[] { "", "javascript:alert(1)", "file:///C:/private.txt", "https://user:secret@example.com/", null })
            {
                second.Record.SourcePageUrl = invalid!;
                second.RaiseMetaChanged();
                Drain(window);
                AssertHidden(panel, "LibrarySourcePage");
                Assert.Equal(Visibility.Visible, panel.Visibility); // The app and window remain known.
                Assert.All(actions, action => Assert.Empty(ResolveAction("SourceUrlForAction", action)));
            }
        });

    [Theory]
    [MemberData(nameof(Layouts))]
    public void FolderSourceShowsLocationWithFolderActions(string language, double width) =>
        Render(language, width, Image(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder), "최종 보고서 초안"),
            (window, panel, tile) =>
        {
            AssertShown(panel, "LibrarySourceApp", "LibrarySourceWindow", "LibrarySourceLocation");
            AssertHidden(panel, "LibrarySourcePage");
            StackPanel location = Named<StackPanel>(panel, "LibrarySourceLocation");
            TextBox path = Assert.Single(Descendants(location).OfType<TextBox>());
            Assert.Equal(Folder, path.Text);
            Assert.True(path.IsReadOnly);
            Assert.Equal(UiText.Get("Library.Source.Location"), AutomationProperties.GetName(path));
            Button[] actions = Descendants(location).OfType<Button>().ToArray();
            Assert.Equal(new[] { UiText.Get("Library.Source.OpenFolder"), UiText.Get("Library.Source.CopyPath") },
                actions.Select(action => action.Content?.ToString() ?? string.Empty));
            AssertReachable(panel, actions, language);
            Assert.All(actions, action => Assert.Equal(Folder, ResolveAction("SourcePathForAction", action)));
            Assert.All(actions, action => Assert.Empty(ResolveAction("SourceUrlForAction", action)));
            SaveEvidence(window, $"gallery-source-folder-{language}-{width:0}.png");
        });

    [Fact]
    public void AppOnlySourceAndEmptyOrVideoRecordsFollowTheVisibilityRules() =>
        Render("ko-KR", 204, Image(new CaptureSourceDetails("메모장", @"C:\Windows\System32\notepad.exe"), "회의록.txt - 메모장"),
            (window, panel, tile) =>
        {
            AssertShown(panel, "LibrarySourceApp", "LibrarySourceWindow");
            AssertHidden(panel, "LibrarySourcePage", "LibrarySourceLocation");
            SaveEvidence(window, "gallery-source-app-ko-KR-204.png");

            panel.DataContext = Tile(new CaptureRecord { SourceAppName = "이름만 남은 앱" });
            Drain(window);
            AssertShown(panel, "LibrarySourceApp");
            Assert.Null(Named<TextBlock>(panel, "LibrarySourceApp").ToolTip); // No empty tooltip without a path.

            panel.DataContext = Tile(new CaptureRecord());
            Drain(window);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);

            var titleOnly = Tile(new CaptureRecord { SourceWindowTitle = "제목만 있는 이전 캡처" });
            panel.DataContext = titleOnly;
            Drain(window);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            AssertShown(panel, "LibrarySourceWindow");
            AssertHidden(panel, "LibrarySourceApp", "LibrarySourcePage", "LibrarySourceLocation");

            // A recording stores its generic label as the window title; it has no capture source.
            var video = new CaptureRecord { MediaKind = CaptureMediaKind.Video, SourceWindowTitle = "화면 녹화" };
            video.SetSourceDetails(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder));
            panel.DataContext = Tile(video);
            Drain(window);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);

            // A page link wins over a folder; both are never offered at once.
            CaptureRecord both = Image(new CaptureSourceDetails("Microsoft Edge", EdgePath, Folder), "Docs", Url);
            panel.DataContext = Tile(both);
            Drain(window);
            AssertShown(panel, "LibrarySourcePage");
            AssertHidden(panel, "LibrarySourceLocation");
        });

    [Fact]
    public void FolderActionTargetDistinguishesFoldersFilesAndMissingPaths()
    {
        string root = Directory.CreateTempSubdirectory("mycapture-source-target-").FullName;
        try
        {
            string file = Path.Combine(root, "보고서.txt");
            File.WriteAllText(file, "x");
            Assert.Equal(new GalleryWindow.SourceFolderTarget(GalleryWindow.SourceFolderTargetKind.Folder, root),
                GalleryWindow.ResolveFolderTarget(root + Path.DirectorySeparatorChar));
            Assert.Equal(GalleryWindow.SourceFolderTargetKind.File, GalleryWindow.ResolveFolderTarget(file).Kind);
            Assert.Equal(GalleryWindow.SourceFolderTargetKind.Missing, GalleryWindow.ResolveFolderTarget(Path.Combine(root, "gone")).Kind);
            Assert.Equal(GalleryWindow.SourceFolderTargetKind.Missing, GalleryWindow.ResolveFolderTarget("relative").Kind);
            Assert.Equal(GalleryWindow.SourceFolderTargetKind.Missing, GalleryWindow.ResolveFolderTarget("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}").Kind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CaptureSourceDetails EdgeSource() => new("Microsoft Edge", EdgePath);

    private static CaptureRecord Image(CaptureSourceDetails source, string title, string url = "")
    {
        var record = new CaptureRecord { SourceWindowTitle = title, SourcePageUrl = url };
        record.SetSourceDetails(source);
        return record;
    }

    private static GalleryItemViewModel Tile(CaptureRecord record) => new(record, _ => "unused-synthetic-thumbnail.png", 64);

    private static void Render(string language, double width, CaptureRecord record,
        Action<Window, StackPanel, GalleryItemViewModel> verify) => StaTestHost.Run(() =>
    {
        using var localization = UiText.UseLanguage(language);
        var window = new Window
        {
            // Room for the frame a resizable borderless window still reserves around its client area.
            Width = width + 64, Height = 560, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
        };
        LoadProductPanel(window);
        FluidMotion.SetWindowEntrance(window, false);
        window.SetResourceReference(Window.BackgroundProperty, "Surface.Raised");
        var template = Assert.IsType<DataTemplate>(window.FindResource("Gallery.SourceFixture"));
        var panel = Assert.IsType<StackPanel>(template.LoadContent());
        panel.Width = width;
        GalleryItemViewModel tile = Tile(record);
        panel.DataContext = tile;
        window.Content = new Border { Padding = new Thickness(16), Child = panel, VerticalAlignment = VerticalAlignment.Top };
        try
        {
            window.Show();
            Drain(window);
            verify(window, panel, tile);
        }
        finally { window.Close(); }
    });

    private static void AssertReachable(StackPanel panel, IEnumerable<Button> actions, string language)
    {
        foreach (TextBox box in Descendants(panel).OfType<TextBox>().Where(box => box.IsVisible))
        {
            Point origin = box.TranslatePoint(new Point(), panel);
            Assert.True(origin.X >= 0 && origin.X + box.ActualWidth <= panel.ActualWidth + .5,
                $"{language}: {AutomationProperties.GetName(box)} escaped panel width");
        }

        foreach (Button action in actions)
        {
            Assert.True(action.IsVisible && action.ActualHeight >= 32, $"{language}: {action.Content} is not a full-size target");
            Assert.True(action.Focusable && action.IsTabStop, $"{language}: {action.Content} is not keyboard reachable");
            Point origin = action.TranslatePoint(new Point(), panel);
            Assert.True(origin.X >= 0 && origin.X + action.ActualWidth <= panel.ActualWidth + .5,
                $"{language} source action escaped panel width: {action.Content}; {origin.X}+{action.ActualWidth}>{panel.ActualWidth}");
        }
    }

    private static void AssertShown(StackPanel panel, params string[] ids) =>
        Assert.All(ids, id => Assert.True(Named<FrameworkElement>(panel, id).IsVisible, $"{id} should be visible"));

    private static void AssertHidden(StackPanel panel, params string[] ids) =>
        Assert.All(ids, id => Assert.Equal(Visibility.Collapsed, Named<FrameworkElement>(panel, id).Visibility));

    private static T Named<T>(DependencyObject root, string automationId) where T : DependencyObject =>
        Assert.Single(Descendants(root).OfType<T>(), element => AutomationProperties.GetAutomationId(element) == automationId);

    private static string ResolveAction(string resolver, Button action)
    {
        MethodInfo method = typeof(GalleryWindow).GetMethod(resolver, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production action resolver " + resolver);
        return Assert.IsType<string>(method.Invoke(null, [action]));
    }

    private static void LoadProductPanel(Window window)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var combined = new XElement(p + "ResourceDictionary");
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(GallerySourceDetailTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            CopyNamespaces(dictionary, combined);
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        using Stream gallery = typeof(GallerySourceDetailTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        XElement product = XDocument.Load(gallery).Root!;
        CopyNamespaces(product, combined);
        combined.Add(new XElement(XName.Get("BoolToVisibilityConverter", "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"),
            new XAttribute(x + "Key", "BoolToVisibility")));
        XElement sourcePanel = Assert.Single(product.Descendants(p + "StackPanel"), element =>
            (string?)element.Attribute(XName.Get("AutomationProperties.AutomationId")) == "LibrarySourceSection");
        XElement[] actionMarkup = sourcePanel.Descendants(p + "Button").ToArray();
        string[] handlers = ["OnOpenSourcePageClick", "OnCopySourcePageClick", "OnOpenSourceFolderClick", "OnCopySourcePathClick"];
        Assert.Equal(handlers, actionMarkup.Select(button => (string?)button.Attribute("Click") ?? string.Empty));
        foreach (string name in handlers)
        {
            MethodInfo handler = typeof(GalleryWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Product XAML action points to a missing handler.");
            Assert.Equal(typeof(void), handler.ReturnType);
            Assert.Equal(new[] { typeof(object), typeof(RoutedEventArgs) }, handler.GetParameters().Select(parameter => parameter.ParameterType));
        }
        var panel = new XElement(sourcePanel);
        // The real XAML's event hookups and production target resolvers are checked above. Rendering
        // this bounded fixture never opens a browser or Explorer and never touches the clipboard.
        foreach (XAttribute handler in panel.Descendants().Attributes("Click").ToArray()) handler.Remove();
        combined.Add(new XElement(p + "DataTemplate", new XAttribute(x + "Key", "Gallery.SourceFixture"), panel));
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

    /// <summary>Opt-in: set MYCAPTURE_SOURCE_PAGE_EVIDENCE to a folder to keep a PNG of each panel state.</summary>
    private static void SaveEvidence(Window window, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_SOURCE_PAGE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        double scale = 2;
        var pixels = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        pixels.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var output = File.Create(Path.Combine(root, name));
        encoder.Save(output);
    }
}
