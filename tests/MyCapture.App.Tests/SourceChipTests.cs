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
    public void SourceChipIsBoundedKeyboardReachableAndDoesNotExposeUrlSecretsInItsLabel(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        CaptureProvenance source = CaptureProvenance.Create("https://www.example.com/spec?token=secret#private", appName: "Browser");
        Button chip = SourceChip.CreateButton(source, actions: NoDesktop());
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal(320, chip.MaxWidth);
        Assert.True(chip.IsTabStop);
        Assert.True(chip.Focusable);
        Assert.Equal(source.Tooltip, chip.ToolTip);
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
        Assert.Equal(new[] { UiText.Get("Source.Open"), UiText.Get("Source.CopyPath"), UiText.Get("Source.OpenLocation") },
            chip.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header));
    });

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
