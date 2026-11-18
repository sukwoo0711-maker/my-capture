using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SourceChipTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en")]
    public void SourceChipIsBoundedKeyboardReachableAndDoesNotExposeUrlSecretsInPresentation(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        CaptureProvenance source = CaptureProvenance.Create("https://www.example.com/spec?token=secret#private", appName: "Browser");
        Button chip = SourceChip.CreateButton(source, actions: NoDesktop());
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal(320, chip.MaxWidth);
        Assert.True(chip.IsTabStop);
        Assert.True(chip.Focusable);
        Assert.Equal(source.Tooltip, chip.ToolTip);
        Assert.Equal("https://www.example.com/spec" + Environment.NewLine + "Browser", chip.ToolTip);
        Assert.Equal(chip.ToolTip, AutomationProperties.GetHelpText(chip));
        Assert.Equal(UiText.Format("Source.OpenAutomation", "example.com/spec"), AutomationProperties.GetName(chip));
        var content = Assert.IsType<Grid>(chip.Content);
        TextBlock label = Assert.Single(content.Children.OfType<TextBlock>());
        Assert.Equal("example.com/spec", label.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.DoesNotContain("secret", label.Text);
    });

    [Fact]
    public void EmptyProvenanceDoesNotShowChipOrActions() => StaTestHost.Run(() =>
    {
        Button chip = SourceChip.CreateButton(CaptureProvenance.None, actions: NoDesktop());
        Assert.Equal(Visibility.Collapsed, chip.Visibility);
        Assert.Empty(chip.ContextMenu.Items);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en")]
    public void DocumentChipIncludesExtensionAndSharedFileActions(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Private\사양서.xlsx", appName: "Excel");
        Button chip = SourceChip.CreateButton(source, actions: NoDesktop());
        var content = Assert.IsType<Grid>(chip.Content);
        var symbol = Assert.Single(content.Children.OfType<StackPanel>());
        Assert.Contains(symbol.Children.OfType<TextBlock>(), block => block.Text == "XLSX");
        // Explorer's order: open, open file location, then copy path.
        Assert.Equal(new[] { UiText.Get("Source.Open"), UiText.Get("Source.OpenLocation"), UiText.Get("Source.CopyPath") },
            chip.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header));
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en")]
    public void MenusFollowExplorerOrderWithTheLibraryViewsLast(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        bool detailsShown = false;
        CaptureProvenance document = CaptureProvenance.Create(documentPath: @"C:\Work\spec.xlsx", appExecutablePath: @"C:\Office\EXCEL.EXE");
        ContextMenu menu = SourceChip.CreateMenu(document, search: _ => { }, actions: NoDesktop(), details: () => detailsShown = true);
        Assert.Equal(new object[]
        {
            UiText.Get("Source.Open"), UiText.Get("Source.OpenLocation"), UiText.Get("Source.CopyPath"), "|",
            UiText.Get("Source.ShowSame"), UiText.Get("LibraryUx_SourceDetails"),
        }, Shape(menu));
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(detailsShown);

        CaptureProvenance web = CaptureProvenance.Create("https://example.com/a", appExecutablePath: @"C:\Edge\msedge.exe");
        Assert.Equal(new object[] { UiText.Get("Source.Open"), UiText.Get("Source.CopyUrl"), "|", UiText.Get("Source.ShowSame") },
            Shape(SourceChip.CreateMenu(web, search: _ => { }, actions: NoDesktop())));

        CaptureProvenance app = CaptureProvenance.Create(appName: "Tool", appExecutablePath: @"C:\Apps\tool.exe");
        Assert.Equal(new object[] { UiText.Get("Source.Open") }, Shape(SourceChip.CreateMenu(app, actions: NoDesktop())));
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en")]
    public void AppOnlySourcesSayOnlyTheAppIsKnown(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        CaptureProvenance app = CaptureProvenance.Create(appName: "Tool", appExecutablePath: @"C:\Apps\tool.exe");
        Button chip = SourceChip.CreateButton(app, actions: NoDesktop());
        Assert.Equal(@"C:\Apps\tool.exe" + Environment.NewLine + "Tool" + Environment.NewLine + UiText.Get("Source.AppOnly"), chip.ToolTip);
        Assert.Equal(chip.ToolTip, AutomationProperties.GetHelpText(chip));
        Assert.DoesNotContain(UiText.Get("Source.AppOnly"),
            SourceChip.TooltipFor(CaptureProvenance.Create(documentPath: @"C:\Work\a.txt", appExecutablePath: @"C:\Apps\tool.exe")));
    });

    [Fact]
    public void TheGlyphStaysUntilTheIconArrivesAndKeepsItsPlace() => StaTestHost.Run(() =>
    {
        var icon = SourceIconCacheTests.Icon();
        using var gate = new ManualResetEventSlim(false);
        using var cache = new SourceIconCache(null, readExecutable: _ => { gate.Wait(TimeSpan.FromSeconds(10)); return icon; },
            readPackagedApp: _ => null, stamp: _ => new SourceIconCache.FileStamp(1, 1));
        CaptureProvenance source = CaptureProvenance.Create(appName: "Tool", appExecutablePath: @"C:\Apps\tool.exe");
        Grid symbol = SourceAppIcon.Create(source, 14, cache);
        TextBlock glyph = symbol.Children.OfType<TextBlock>().Single();
        Image image = symbol.Children.OfType<Image>().Single();
        Assert.Equal(SourceChip.GlyphFor(source.Kind), glyph.Text);
        Assert.Equal(Visibility.Visible, glyph.Visibility);
        Assert.Equal(Visibility.Collapsed, image.Visibility);
        symbol.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size before = symbol.DesiredSize;

        gate.Set();
        SourceIconCacheTests.PumpUntil(() => image.Visibility == Visibility.Visible);
        Assert.Same(icon, image.Source);
        // Hidden, not collapsed: the glyph still measures, so the swap moves nothing.
        Assert.Equal(Visibility.Hidden, glyph.Visibility);
        Assert.True(image.Width <= before.Width && image.Height <= before.Height);
        symbol.InvalidateMeasure();
        symbol.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(before, symbol.DesiredSize);

        // A second symbol for the same application reads memory at once.
        Grid again = SourceAppIcon.Create(source, 14, cache);
        Assert.Equal(Visibility.Visible, again.Children.OfType<Image>().Single().Visibility);
    });

    [Fact]
    public void AnApplicationWithoutAnIconKeepsTheGlyph() => StaTestHost.Run(() =>
    {
        int reads = 0;
        using var cache = new SourceIconCache(null, readExecutable: _ => { reads++; return null; },
            readPackagedApp: _ => null, stamp: _ => new SourceIconCache.FileStamp(1, 1));
        CaptureProvenance source = CaptureProvenance.Create(appName: "Tool", appExecutablePath: @"C:\Apps\tool.exe");
        Grid symbol = SourceAppIcon.Create(source, 14, cache);
        SourceIconCacheTests.PumpUntil(() => cache.TryGet(source, out _));
        Assert.Equal(Visibility.Visible, symbol.Children.OfType<TextBlock>().Single().Visibility);
        Assert.Equal(Visibility.Collapsed, symbol.Children.OfType<Image>().Single().Visibility);
        _ = SourceAppIcon.Create(source, 14, cache);
        Assert.Equal(1, reads);
        // A source with no executable or ID asks nothing.
        _ = SourceAppIcon.Create(CaptureProvenance.Create(appName: "Tool"), 14, cache);
        Assert.Equal(1, cache.MemoryCount);
    });

    private static IEnumerable<object> Shape(ContextMenu menu) =>
        menu.Items.Cast<object>().Select(item => item is Separator ? "|" : ((MenuItem)item).Header);

    [Fact]
    public void AppNameWithoutExecutableKeepsSourceAvailableButOpenActionDisabled() => StaTestHost.Run(() =>
    {
        CaptureProvenance source = CaptureProvenance.Create(appName: "Editor");
        Button chip = SourceChip.CreateButton(source, actions: NoDesktop());
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.True(chip.IsEnabled);
        Assert.False(Assert.Single(chip.ContextMenu.Items.OfType<MenuItem>()).IsEnabled);
        Assert.Equal(UiText.Format("Source.Label", "Editor"), AutomationProperties.GetName(chip));
    });

    [Fact]
    public void SameSourceActionUsesPreparedQueryAndDoesNotReadTheFileSystem() => StaTestHost.Run(() =>
    {
        string? searched = null;
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Work\spec table.xlsx");
        ContextMenu menu = SourceChip.CreateMenu(source, search: value => searched = value, actions: NoDesktop());
        MenuItem search = menu.Items.OfType<MenuItem>().Last();
        Assert.Equal(UiText.Get("Source.ShowSame"), search.Header);
        search.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("folder:\"C:\\Work\\spec table.xlsx\"", searched);
    });

    [Fact]
    public void WebButtonAndMenuWorkWithNoStatusCallbackAndOnlyInjectedDesktopActions() => StaTestHost.Run(() =>
    {
        var launched = new List<ProcessStartInfo>();
        var copied = new List<string>();
        var actions = new CaptureSourceActions(launched.Add, _ => throw new InvalidOperationException("Unexpected file access"),
            _ => throw new InvalidOperationException("Unexpected folder access"), copied.Add, @"C:\Windows");
        CaptureProvenance source = CaptureProvenance.Create("https://example.com/spec?token=secret#private");
        Button chip = SourceChip.CreateButton(source, actions: actions);
        Assert.Equal("https://example.com/spec", chip.ToolTip);
        Assert.Equal("https://example.com/spec", AutomationProperties.GetHelpText(chip));
        chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(source.Url, Assert.Single(launched).FileName);
        MenuItem[] items = chip.ContextMenu.Items.OfType<MenuItem>().ToArray();
        items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(2, launched.Count);
        Assert.Equal(source.Url, Assert.Single(copied));
    });

    private static CaptureSourceActions NoDesktop() => new(_ => throw new InvalidOperationException("Unexpected launch"),
        _ => throw new InvalidOperationException("Unexpected file access"),
        _ => throw new InvalidOperationException("Unexpected folder access"),
        _ => throw new InvalidOperationException("Unexpected clipboard access"), @"C:\Windows");
}
