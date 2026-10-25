using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Loads the shipped command bars without a Window, queue, file fixture, or native input.</summary>
public sealed class LibraryCommandLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<object[]> Layouts() =>
        from language in new[] { "ko-KR", "en-US" }
        from width in new[] { 980d, 1280d }
        from theme in new[] { AppTheme.Midnight, AppTheme.Daylight, AppTheme.HighContrast }
        select new object[] { language, width, theme };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void SelectionKeepsCaptureSearchAndListGeometryStable(string language, double width, AppTheme theme) => StaTestHost.Run(() =>
    {
        using var languageScope = UiText.UseLanguage(language);
        ResourceDictionary resources = LoadCommandBars();
        ThemeService.ApplyResources(theme, resources, systemHighContrast: false, accent: AppAccent.Red);
        var model = new CommandsModel();
        var header = (Border)((DataTemplate)resources["Library.HeaderFixture"]).LoadContent();
        var commands = (Grid)((DataTemplate)resources["Library.CommandsFixture"]).LoadContent();
        var root = new Grid { Width = width - 24, Height = 600, Resources = resources, DataContext = model };
        root.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Ui");
        root.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Body");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        Grid.SetRow(commands, 1);
        commands.Width = width - 24 - 224 - 232 - 16;
        commands.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(commands);
        var list = new Border();
        Grid.SetRow(list, 2);
        root.Children.Add(list);
        Layout(root);

        Button capture = Named<Button>(header, "CaptureRegionButton");
        Button record = Named<Button>(header, "RecordRegionButton");
        TextBox search = Named<TextBox>(header, "SearchBox");
        ComboBox sort = Named<ComboBox>(commands, "SortBox");
        Rect captureBefore = Bounds(capture, root), recordBefore = Bounds(record, root), searchBefore = Bounds(search, root);
        Rect listBefore = Bounds(list, root), commandsBefore = Bounds(commands, root);
        Assert.True(search.ActualWidth >= 400, $"{language}/{width}/{theme}: search collapsed to {search.ActualWidth}");
        Assert.True(capture.ActualWidth >= 64 && record.ActualWidth >= 64);
        Assert.Contains(Descendants(capture).OfType<TextBlock>(), text => text.Text == UiText.Get("Text_2C20ADDA2018"));
        Assert.Contains(Descendants(record).OfType<TextBlock>(), text => text.Text == UiText.Get("Text_4F5AF20FAA6E"));
        Assert.Equal(Visibility.Visible, Named<StackPanel>(commands, "LibraryCommands").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(commands, "SelectionCommands").Visibility);

        search.Text = "review";
        sort.SelectedIndex = 2;
        model.HasSelection = true;
        Layout(root);

        Assert.Equal(Visibility.Visible, Named<StackPanel>(header, "CaptureCommands").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(commands, "LibraryCommands").Visibility);
        Assert.Equal(Visibility.Visible, Named<StackPanel>(commands, "SelectionCommands").Visibility);
        Assert.Equal(captureBefore, Bounds(capture, root));
        Assert.Equal(recordBefore, Bounds(record, root));
        Assert.Equal(searchBefore, Bounds(search, root));
        Assert.Equal(listBefore, Bounds(list, root));
        Assert.Equal(commandsBefore, Bounds(commands, root));
        Assert.Equal("review", search.Text);
        Assert.Equal(2, model.SortIndex);
        Assert.True(capture.Focusable && capture.IsTabStop && search.Focusable && search.IsTabStop);
        Assert.True(Named<Button>(commands, "ClearSelectionButton").IsTabStop);
        Button more = Named<Button>(commands, "MoreCommandsButton");
        Assert.True(more.IsTabStop);
        Assert.Equal(Visibility.Visible, more.Visibility);
        Assert.True(more.ActualWidth >= 24 && more.ActualHeight >= 24);
        Rect moreBounds = Bounds(more, commands);
        Assert.True(moreBounds.Left >= 0 && moreBounds.Right <= commands.ActualWidth + .5
            && moreBounds.Top >= 0 && moreBounds.Bottom <= commands.ActualHeight + .5,
            $"More commands escaped the content toolbar: {moreBounds} in {commands.RenderSize}");

        model.HasSelection = false;
        Layout(root);
        Assert.Equal(2, sort.SelectedIndex);
        Assert.Equal("review", search.Text);
        Assert.Equal(listBefore, Bounds(list, root));
        Assert.Null(PresentationSource.FromVisual(root));
    });

    [Fact]
    public void ReparentedCommandsKeepHandlersAndThemeBoundaries()
    {
        XElement product = Product();
        XElement global = Named(product, "LibraryGlobalCommandSurface");
        XElement content = Named(product, "LibraryItemsSurface");
        XElement commands = Named(product, "LibraryContentCommandBar");
        Assert.Contains(commands, content.Elements());
        Assert.DoesNotContain(global.Descendants(), element => (string?)element.Attribute(X + "Name") == "SelectionCommands");
        Assert.Equal("{DynamicResource Border.Subtle}", (string?)global.Attribute("BorderBrush"));
        Assert.Equal("0,0,0,1", (string?)global.Attribute("BorderThickness"));
        Assert.Equal("0,0,1,0", (string?)Named(product, "LibraryNavigationSurface").Attribute("BorderThickness"));
        Assert.Equal("1,0,0,0", (string?)Named(product, "GalleryDetailsPanel").Attribute("BorderThickness"));
        Assert.Equal("1,0,0,0", (string?)Named(product, "GalleryDetailsRail").Attribute("BorderThickness"));
        foreach ((string name, string handler) in new[]
        {
            ("ArchiveExportButton", "OnExportArchiveClick"), ("ArchiveOverflowMenu", "OnExportArchiveClick"),
            ("MoveCapturesButton", "OnMoveCapturesClick"), ("MoveOverflowMenu", "OnMoveCapturesClick"),
            ("DeleteSelectedButton", "OnDeleteSelectedClick"), ("ClearSelectionButton", "OnClearSelectionClick"),
            ("MoreCommandsButton", "OnMoreCommandsClick"), ("ImportMenu", "OnImportClick"),
        })
            Assert.Equal(handler, (string?)Named(commands, name).Attribute("Click"));
        Assert.Equal("{x:Static gallery:GalleryWindow.CompareCommand}", (string?)Named(commands, "CompareSelectedButton").Attribute("Command"));
        Assert.Equal("OnSearchTextChanged", (string?)Named(global, "SearchBox").Attribute("TextChanged"));
        Assert.Equal("OnLibraryItemsKeyDown", (string?)content.Attribute("PreviewKeyDown"));
        Assert.Equal("2", (string?)Named(content, "RowsList").Attribute("Grid.Row"));
        Assert.Equal("2", (string?)Named(content, "ImportOverlay").Attribute("Grid.Row"));
        foreach (XElement actions in product.Descendants().Where(element => (string?)element.Attribute(X + "Name") == "TileActions"))
        {
            string[] conditions = actions.Descendants(P + "DataTrigger").Select(trigger => (string?)trigger.Attribute("Binding") ?? "").ToArray();
            Assert.DoesNotContain("{Binding IsSelected}", conditions);
            Assert.Contains(conditions, condition => condition.Contains("IsMouseOver", StringComparison.Ordinal));
            Assert.Contains(conditions, condition => condition.Contains("IsKeyboardFocusWithin", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolbarMenuKeyIsNotInterceptedByTheCanvasRouter(bool fromButtonContent) => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadCommandBars();
        var commands = (Grid)((DataTemplate)resources["Library.CommandsFixture"]).LoadContent();
        commands.Resources = resources;
        commands.DataContext = new CommandsModel { HasSelection = true };
        commands.Width = 760;
        commands.Height = 48;
        Layout(commands);
        Button more = Named<Button>(commands, "MoreCommandsButton");
        Assert.NotNull(more.ContextMenu);

        // Bind the actual production event handler to the shipped toolbar. No gallery
        // constructor, queue, stores, HWND or filesystem fixture is needed for this guard.
        var gallery = (GalleryWindow)RuntimeHelpers.GetUninitializedObject(typeof(GalleryWindow));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(GalleryWindow).GetField("LibraryContentCommandBar", flags)!.SetValue(gallery, commands);
        var handler = (KeyEventHandler)typeof(GalleryWindow).GetMethod("OnLibraryItemsKeyDown", flags)!
            .CreateDelegate(typeof(KeyEventHandler), gallery);
        commands.AddHandler(Keyboard.PreviewKeyDownEvent, handler);
        UIElement source = fromButtonContent ? (UIElement)more.Content : more;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Apps)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        Assert.False(args.Handled);
        Assert.False(more.ContextMenu.IsOpen);
        commands.RemoveHandler(Keyboard.PreviewKeyDownEvent, handler);
    });

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static ResourceDictionary LoadCommandBars()
    {
        var combined = new XElement(P + "ResourceDictionary");
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
        {
            using Stream source = typeof(LibraryCommandLayoutTests).Assembly.GetManifestResourceStream($"ThemeFixture.{name}.xaml")!;
            XElement dictionary = XDocument.Load(source).Root!;
            CopyNamespaces(dictionary, combined);
            foreach (XElement entry in dictionary.Elements()) combined.Add(new XElement(entry));
        }
        XElement product = Product();
        CopyNamespaces(product, combined);
        string[] keys = ["LibraryUx.RestEdge", "LibraryUx.CommandButton", "LibraryUx.CommandIcon", "LibraryUx.CommandGlyph", "LibraryUx.CommandSeparator"];
        foreach (XElement entry in product.Element(P + "Window.Resources")!.Element(P + "ResourceDictionary")!.Elements()
                     .Where(element => keys.Contains((string?)element.Attribute(X + "Key"))))
            combined.Add(new XElement(entry));
        combined.Add(new XElement(XName.Get(nameof(BoolToVisibilityConverter), "clr-namespace:MyCapture.App.Gallery;assembly=MyCapture"),
            new XAttribute(X + "Key", "BoolToVisibility")));
        foreach ((string name, string key) in new[] { ("LibraryGlobalCommandSurface", "Library.HeaderFixture"), ("LibraryContentCommandBar", "Library.CommandsFixture") })
        {
            var markup = new XElement(Named(product, name));
            CopyNamespaces(product, markup);
            // Event contracts are asserted separately. This fixture only measures the shipped controls.
            foreach (XAttribute attribute in markup.DescendantsAndSelf().Attributes()
                         .Where(attribute => attribute.Name.LocalName is "Click" or "TextChanged" or "Command").ToArray())
                attribute.Remove();
            // The placeholder is an internal TextBlock subclass. Its fitting behavior has its own tests;
            // use the same bound caption here without instantiating internal markup from another assembly.
            foreach (XElement placeholder in markup.Descendants().Where(element => element.Name.LocalName == "LibrarySearchPlaceholder").ToArray())
            {
                placeholder.Name = P + "TextBlock";
                placeholder.Attribute("FullText")?.Remove();
                placeholder.Attribute("CompactText")?.Remove();
                foreach (XElement setter in placeholder.Descendants(P + "Setter").Where(setter => (string?)setter.Attribute("Property") == "Text"))
                    setter.SetAttributeValue("Value", "{Binding SearchPlaceholder}");
            }
            combined.Add(new XElement(P + "DataTemplate", new XAttribute(X + "Key", key), markup));
        }
        // Expanded XML names also need the assembly qualifier; changing an xmlns declaration
        // alone does not retarget SymbolIcon elements or FluidMotion attached properties.
        foreach (XElement element in combined.DescendantsAndSelf())
        {
            element.Name = WithAssembly(element.Name);
            foreach (XAttribute attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray())
            {
                XName qualified = WithAssembly(attribute.Name);
                if (qualified == attribute.Name) continue;
                string value = attribute.Value;
                attribute.Remove();
                element.SetAttributeValue(qualified, value);
            }
        }
        return (ResourceDictionary)XamlReader.Parse(combined.ToString(SaveOptions.DisableFormatting));
    }

    private static XName WithAssembly(XName name) =>
        name.NamespaceName.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal)
            && !name.NamespaceName.Contains(";assembly=", StringComparison.Ordinal)
            ? XName.Get(name.LocalName, name.NamespaceName + ";assembly=MyCapture") : name;

    private static XElement Product()
    {
        using Stream source = typeof(LibraryCommandLayoutTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        return XDocument.Load(source).Root!;
    }
    private static XElement Named(XElement parent, string name) =>
        Assert.Single(parent.DescendantsAndSelf(), element => (string?)element.Attribute(X + "Name") == name);
    private static T Named<T>(DependencyObject parent, string name) where T : FrameworkElement =>
        Assert.Single(Descendants(parent).OfType<T>(), element => element.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
    private static void CopyNamespaces(XElement source, XElement destination)
    {
        foreach (XAttribute attribute in source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = attribute.Value;
            if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal)) value += ";assembly=MyCapture";
            destination.SetAttributeValue(attribute.Name, value);
        }
    }
    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(static () => { }));
        root.UpdateLayout();
    }

    public sealed class CommandsModel : INotifyPropertyChanged
    {
        private bool _hasSelection;
        public bool HasSelection { get => _hasSelection; set { _hasSelection = value; PropertyChanged?.Invoke(this, new(nameof(HasSelection))); } }
        public bool CanCompareSelected => false;
        public string SelectionText => UiText.Format("LibrarySelection_Count", 2);
        public string SearchPlaceholder => "Search captures";
        public string CompactSearchPlaceholder => "Search";
        public string SearchToolTip => "Search";
        public int SortIndex { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
