using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            // No text box or scroller of its own: the details panel's information area is the one scrolling surface.
            Assert.Empty(Descendants(panel).OfType<TextBox>());
            Assert.Empty(Descendants(panel).OfType<ScrollViewer>());
            TextBlock link = Named<TextBlock>(page, "LibrarySourcePageValue");
            Assert.Equal(Url, link.Text);
            Assert.Equal(Url, link.ToolTip);
            // A screen reader hears the label and the whole address, never the two-line summary.
            Assert.Equal(UiText.Format("Library.SourcePage.Accessible", Url), new TextBlockAutomationPeer(link).GetName());
            ToggleButton showAll = Named<ToggleButton>(page, "LibrarySourcePageToggle");
            AssertTwoLineSummary(link, showAll, UiText.Get("Library.SourcePage.ShowFull"), language);
            Assert.True(GalleryTextClamp.GetIsOverflowing(link), $"{language}: the long address must be summarized");
            Button[] actions = Descendants(page).OfType<Button>().ToArray();
            Assert.Equal(new[] { UiText.Get("Library.SourcePage.Open"), UiText.Get("Library.SourcePage.Copy") },
                actions.Select(action => action.Content?.ToString() ?? string.Empty));
            AssertReachable(panel, actions.Append<ButtonBase>(showAll), language);
            Assert.All(actions, action => Assert.Equal(Url, ResolveAction("SourceUrlForAction", action)));
            Assert.All(actions, action => Assert.Empty(ResolveAction("SourcePathForAction", action)));
            SaveEvidence(window, $"gallery-source-page-{language}-{width:0}.png");
            showAll.IsChecked = true;
            Drain(window);
            AssertWholeValueShown(link, showAll, language);
            SaveEvidence(window, $"gallery-source-page-expanded-{language}-{width:0}.png");

            // Retargeting to another record moves both the text and the action target, and the
            // new address starts as a summary again.
            const string secondUrl = "https://example.org/other-page?version=2#details";
            var second = Tile(Image(EdgeSource(), "Other", secondUrl));
            window.DataContext = Tile(Image(EdgeSource(), "Ambient", "https://wrong-ambient.example/"));
            panel.DataContext = second;
            Drain(window);
            Assert.Equal(secondUrl, link.Text);
            Assert.Equal(UiText.Format("Library.SourcePage.Accessible", secondUrl), AutomationProperties.GetName(link));
            Assert.False(showAll.IsChecked);
            Assert.False(GalleryTextClamp.GetIsExpanded(link));
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
            Assert.Empty(Descendants(panel).OfType<TextBox>());
            Assert.Empty(Descendants(panel).OfType<ScrollViewer>());
            TextBlock path = Named<TextBlock>(location, "LibrarySourcePathValue");
            Assert.Equal(Folder, path.Text);
            Assert.Equal(Folder, path.ToolTip);
            Assert.Equal(UiText.Format("Library.Source.LocationAccessible", Folder), new TextBlockAutomationPeer(path).GetName());
            ToggleButton showAll = Named<ToggleButton>(location, "LibrarySourcePathToggle");
            AssertTwoLineSummary(path, showAll, UiText.Get("Library.Source.ShowFullPath"), language);
            Button[] actions = Descendants(location).OfType<Button>().ToArray();
            Assert.Equal(new[] { UiText.Get("Library.Source.OpenFolder"), UiText.Get("Library.Source.CopyPath") },
                actions.Select(action => action.Content?.ToString() ?? string.Empty));
            // At the widest panel the path fits its two lines and offers no toggle.
            AssertReachable(panel, showAll.IsVisible ? actions.Append<ButtonBase>(showAll) : actions, language);
            Assert.All(actions, action => Assert.Equal(Folder, ResolveAction("SourcePathForAction", action)));
            Assert.All(actions, action => Assert.Empty(ResolveAction("SourceUrlForAction", action)));
            SaveEvidence(window, $"gallery-source-folder-{language}-{width:0}.png");
            showAll.IsChecked = true;
            Drain(window);
            AssertWholeValueShown(path, showAll, language);
            // Collapsing returns to the two-line summary.
            showAll.IsChecked = false;
            Drain(window);
            AssertTwoLineSummary(path, showAll, UiText.Get("Library.Source.ShowFullPath"), language);
        });

    [Fact]
    public void ShowAllHoverKeepsTheWholeWashAndAddsTheContrastOutline() =>
        Render("ko-KR", 204, Image(EdgeSource(), LongTitle, Url), (window, panel, tile) =>
        {
            ToggleButton showAll = Named<ToggleButton>(panel, "LibrarySourcePageToggle");
            var wash = (Border)showAll.Template.FindName("HoverLayer", showAll);
            var outline = (Border)showAll.Template.FindName("HoverOutline", showAll);
            // The outline is its own layer, so the hover wash still fills the whole chip.
            Assert.Equal(new Thickness(0), wash.BorderThickness);
            Assert.Equal(new Thickness(1), outline.BorderThickness);
            Assert.Null(outline.Background);
            Trigger hover = showAll.Template.Triggers.OfType<Trigger>().Single(trigger => trigger.Property == UIElement.IsMouseOverProperty);
            Assert.Contains(hover.Setters.OfType<Setter>(), setter => setter.TargetName == "HoverOutline"
                && setter.Property == FluidMotion.FadeToProperty && Equals(setter.Value, 1d));
            // App palettes keep the outline clear; a Windows contrast theme's Border.Hover is Highlight.
            Assert.Equal(0, ((SolidColorBrush)outline.BorderBrush).Color.A);
            var highlight = Color.FromRgb(0xD6, 0xB4, 0xFD);
            window.Resources["Border.Hover"] = new SolidColorBrush(highlight);
            Assert.Equal(highlight, ((SolidColorBrush)outline.BorderBrush).Color);
        });

    [Fact]
    public void ShortValuesFitTheirLinesWithoutAToggle() =>
        Render("ko-KR", 252, Image(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, @"C:\Work"), "Work"),
            (window, panel, tile) =>
        {
            TextBlock path = Named<TextBlock>(panel, "LibrarySourcePathValue");
            Assert.Equal(@"C:\Work", path.Text);
            Assert.False(GalleryTextClamp.GetIsOverflowing(path));
            Assert.Equal(Visibility.Collapsed, Named<ToggleButton>(panel, "LibrarySourcePathToggle").Visibility);
            Assert.True(path.ActualHeight <= path.LineHeight + 0.5, $"a one-line path is {path.ActualHeight} DIP tall");
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
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-source-target-").FullName;
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
        finally { TestRecycleBin.DeleteDirectory(root, recursive: true); }
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

    /// <summary>
    /// A value longer than two lines shows exactly two, ending in an ellipsis, with "show all"
    /// beside its label; a value that fits shows whole and offers no toggle.
    /// </summary>
    private static void AssertTwoLineSummary(TextBlock value, ToggleButton showAll, string toggleName, string language)
    {
        Assert.Equal(2, GalleryTextClamp.GetMaxLines(value));
        Assert.Equal(TextTrimming.CharacterEllipsis, value.TextTrimming);
        bool longer = FullValueHeight(value) > 2 * value.LineHeight + .5;
        Assert.Equal(longer, GalleryTextClamp.GetIsOverflowing(value));
        Assert.Equal(longer, showAll.IsVisible);
        Assert.Equal(Math.Min(FullValueHeight(value), 2 * value.LineHeight), value.ActualHeight, 1);
        Assert.False(showAll.IsChecked);
        Assert.Equal(UiText.Get("Library.Source.ShowAll"), showAll.Content);
        Assert.Equal(toggleName, AutomationProperties.GetName(showAll));
        Assert.Equal(ToggleState.Off, ((IToggleProvider)new ToggleButtonAutomationPeer(showAll)).ToggleState);
    }

    /// <summary>Expanded, the value takes every line it needs and the toggle offers to collapse.</summary>
    private static void AssertWholeValueShown(TextBlock value, ToggleButton showAll, string language)
    {
        Assert.True(GalleryTextClamp.GetIsExpanded(value));
        Assert.True(value.ActualHeight + .5 >= FullValueHeight(value),
            $"{language}: the expanded value is {value.ActualHeight:0.#} DIP of the {FullValueHeight(value):0.#} DIP it needs");
        Assert.Equal(UiText.Get("Library.Source.ShowLess"), showAll.Content);
        Assert.Equal(ToggleState.On, ((IToggleProvider)new ToggleButtonAutomationPeer(showAll)).ToggleState);
    }

    private static double FullValueHeight(TextBlock value)
    {
        var probe = new TextBlock
        {
            Text = value.Text, FontFamily = value.FontFamily, FontSize = value.FontSize, FontWeight = value.FontWeight,
            TextWrapping = TextWrapping.Wrap, LineHeight = value.LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(value));
        probe.Measure(new Size(value.ActualWidth, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }

    private static void AssertReachable(StackPanel panel, IEnumerable<ButtonBase> actions, string language)
    {
        foreach (TextBlock value in Descendants(panel).OfType<TextBlock>().Where(text => text.IsVisible))
        {
            Point origin = value.TranslatePoint(new Point(), panel);
            Assert.True(origin.X >= 0 && origin.X + value.ActualWidth <= panel.ActualWidth + .5,
                $"{language}: '{value.Text}' escaped panel width");
        }

        foreach (ButtonBase action in actions)
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
        // The inline detail action template uses the library's local hover wash.
        // Keep the product style and its shared StateLayer.Hover base in this fixture.
        XElement hoverWash = Assert.Single(product.Element(p + "Window.Resources")!
            .Element(p + "ResourceDictionary")!.Elements(), element =>
            (string?)element.Attribute(x + "Key") == "LibraryUx.HoverWash");
        combined.Add(new XElement(hoverWash));
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
        // Attached properties such as GalleryTextClamp keep the window's assembly-less namespace.
        foreach (XElement element in panel.DescendantsAndSelf())
            foreach (XAttribute attached in element.Attributes()
                         .Where(attribute => !attribute.IsNamespaceDeclaration && WithAssembly(attribute.Name.NamespaceName) != attribute.Name.NamespaceName)
                         .ToArray())
            {
                attached.Remove();
                element.SetAttributeValue(XName.Get(attached.Name.LocalName, WithAssembly(attached.Name.NamespaceName)), attached.Value);
            }
        // A binding path such as (gallery:GalleryTextClamp.IsOverflowing) resolves its prefix inside
        // the template, so the panel declares the window's prefixes itself, as the window's BAML does.
        CopyNamespaces(product, panel);
        combined.Add(new XElement(p + "DataTemplate", new XAttribute(x + "Key", "Gallery.SourceFixture"), panel));
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(combined.ToString(SaveOptions.DisableFormatting)));
    }

    private static void CopyNamespaces(XElement source, XElement destination)
    {
        foreach (XAttribute attribute in source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            destination.SetAttributeValue(attribute.Name, WithAssembly(attribute.Value));
    }

    private static string WithAssembly(string xmlNamespace) =>
        xmlNamespace.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
            && !xmlNamespace.Contains(";assembly=", StringComparison.Ordinal)
            ? xmlNamespace + ";assembly=MyCapture" : xmlNamespace;

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
