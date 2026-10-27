using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShortcutConsistencyTests
{
    [Theory]
    [InlineData("Annotation")]
    [InlineData("Crop")]
    [InlineData("QuickAccess")]
    [InlineData("Selector")]
    [InlineData("Library")]
    public void EveryCatalogBindingHasExactlyOneHelpRow(string scopeName)
    {
        ShortcutScope scope = Enum.Parse<ShortcutScope>(scopeName);
        ShortcutHelpEntry[] rows = ShortcutCatalog.Help(scope).ToArray();
        foreach (ShortcutDefinition binding in ShortcutCatalog.ForScope(scope))
        {
            ShortcutHelpEntry row = Assert.Single(rows, candidate => candidate.Shortcuts.Contains(binding));
            Assert.Contains(binding.Hint, row.Keys.Split(" / "));
        }
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void HelpUsesCatalogRowsIncludingEveryAliasAndLocalizedLabel(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        var help = new ShortcutHelpWindow();
        try
        {
            TabControl tabs = ShortcutHelpSearchTests.View(help).Tabs;
            foreach (var (scope, category) in new[] { (ShortcutScope.Annotation, "Help.Annotation"),
                (ShortcutScope.Crop, "Help.Annotation"), (ShortcutScope.Selector, "Help.Capture"),
                (ShortcutScope.QuickAccess, "Help.QuickAccess"), (ShortcutScope.Library, "Workspace.Library") })
            {
                TabItem tab = Assert.Single(tabs.Items.OfType<TabItem>(), item => Equals(item.Header, UiText.Get(category)));
                UniformGrid[] rows = ((StackPanel)((ScrollViewer)tab.Content).Content).Children.OfType<UniformGrid>().ToArray();
                foreach (ShortcutHelpEntry expected in ShortcutCatalog.Help(scope))
                {
                    string label = UiText.Get(expected.Action);
                    Assert.NotEqual(expected.Action, label);
                    Assert.Contains(rows, row => ShortcutHelpRowAssertions.Title(row) == label
                        && ShortcutHelpRowAssertions.Keys(row) == expected.Keys);
                }
            }
        }
        finally { help.Close(); }
    });

    [Theory]
    [InlineData("ShortcutFixture.Help.cs")]
    [InlineData("ShortcutFixture.Tutorial.cs")]
    public void HelpAndTutorialHaveNoIndependentKeyLiterals(string resource)
    {
        using var stream = typeof(ShortcutConsistencyTests).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd();
        Assert.Contains("ShortcutCatalog.", source);
        // Command IDs such as "Delete" are catalog references, not displayed key literals.
        string withoutCatalogReferences = Regex.Replace(source, @"ShortcutCatalog\.(?:Get|Hints)\([^)]*\)", string.Empty);
        Assert.DoesNotMatch(new Regex("\"(?:(?:Ctrl|Alt|Shift)\\+)*(?:[A-Z]|F[0-9]{1,2}|Enter|Esc|Del|Delete|Space|Backspace|Menu|[←→↑↓])\""), withoutCatalogReferences);
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void EditorTooltipsAndSaveMenuDescribeTheirActualCommands(string language) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        BitmapSource image = Image();
        var region = new RectD(0, 0, 64, 48);
        var editor = new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image);
        var elements = FirstRunWelcomeTests.Descendants(editor).ToArray();
        Button done = Assert.Single(elements.OfType<Button>(), item => AutomationProperties.GetAutomationId(item) == "EditorFlowDone");
        Assert.Equal(UiText.Get("EditorFlow_CopyHint") + " (Ctrl+C / Ctrl+Enter)", done.ToolTip);
        Button save = Assert.Single(elements.OfType<Button>(), item => AutomationProperties.GetAutomationId(item) == "EditorFlowSaveToFile");
        Assert.Empty(AutomationProperties.GetAcceleratorKey(save));
        Assert.DoesNotContain(FirstRunWelcomeTests.Descendants(save).OfType<TextBlock>(), item => item.Text.Contains("Ctrl+S"));
        Assert.Equal(UiText.Get("EditorFlow_SaveToFileHint"), save.ToolTip);
        MenuItem quickSave = Assert.Single(save.ContextMenu!.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Text_DFC084A111D0")));
        MenuItem saveAs = Assert.Single(save.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Text_57950AFF46BC")));
        Assert.Equal("Ctrl+S", quickSave.InputGestureText);
        Assert.Equal("Ctrl+Shift+S", saveAs.InputGestureText);
        Button delete = Assert.Single(elements.OfType<Button>(), item => AutomationProperties.GetAutomationId(item) == "AnnotationDeleteSelection");
        Assert.Equal("Del", AutomationProperties.GetAcceleratorKey(delete));
        Assert.EndsWith(" (Del)", Assert.IsType<string>(delete.ToolTip));
        foreach (ShortcutHelpEntry row in ShortcutCatalog.Help(ShortcutScope.Annotation).Where(row =>
            row.Shortcuts.Count == 1 && row.Shortcuts[0].Modifiers == ModifierKeys.None && row.Shortcuts[0].Key is >= Key.A and <= Key.Z
            && row.Shortcuts[0].Command is not ("RotateLeft" or "RotateRight")))
        {
            ButtonBase tool = Assert.Single(elements.OfType<ButtonBase>(), item => AutomationProperties.GetAcceleratorKey(item) == row.Keys);
            if (row.Shortcuts[0].Command == "Stamp")
            {
                // The stamp keeps the hint that its options are on the right button; it still names its key last.
                Assert.Equal(UiText.Get("Stamp_ToolTip"), tool.ToolTip);
                Assert.EndsWith($" ({row.Keys})", Assert.IsType<string>(tool.ToolTip));
                continue;
            }
            Assert.Equal($"{UiText.Get(row.Action)} ({row.Keys})", tool.ToolTip);
        }
    });

    [Theory]
    [InlineData(Key.C, ModifierKeys.None, "Copy")]
    [InlineData(Key.C, ModifierKeys.Control, "Copy")]
    [InlineData(Key.S, ModifierKeys.None, "Save")]
    [InlineData(Key.S, ModifierKeys.Control, "Save")]
    public void QuickAccessBareAndControlKeysExecuteTheSameAction(Key key, ModifierKeys modifiers, string command) => StaTestHost.Run(() =>
    {
        int copied = 0, saved = 0;
        // Inject callbacks: this test never uses the real clipboard or writes a capture.
        var card = new QuickAccessWindow(Image(), () => { copied++; return Task.FromResult(true); }, () => { },
            () => Task.CompletedTask, () => { saved++; return Task.FromResult(true); }, () => { }, () => Task.FromResult("unused.png"));
        try
        {
            Assert.True(card.HandleShortcut(key, modifiers));
            Assert.Equal(command == "Copy" ? 1 : 0, copied);
            Assert.Equal(command == "Save" ? 1 : 0, saved);
            Assert.False(card.HandleShortcut(key, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.False(card.HandleShortcut(key, ModifierKeys.Alt));
            Assert.Equal(command == "Copy" ? 1 : 0, copied);
            Assert.Equal(command == "Save" ? 1 : 0, saved);
        }
        finally { card.Close(); }
    });

    [Fact]
    public void SelectorKeepsControlForFastMovementAndShiftForResizing()
    {
        foreach (Key key in new[] { Key.Left, Key.Right, Key.Up, Key.Down })
        {
            string Command(ModifierKeys modifiers) => ShortcutCatalog.ForScope(ShortcutScope.Selector)
                .Single(item => item.Key == key && item.Modifiers == modifiers).Command;
            Assert.Equal("Move", Command(ModifierKeys.None));
            Assert.Equal("MoveFast", Command(ModifierKeys.Control));
            Assert.Equal("Resize", Command(ModifierKeys.Shift));
            Assert.Equal("ResizeFast", Command(ModifierKeys.Control | ModifierKeys.Shift));
        }
        Assert.Equal("LeftFast", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.Left, ModifierKeys.Shift));
        Assert.Equal("Copy", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.C, ModifierKeys.Control));
        Assert.Equal("Done", ShortcutCatalog.Match(ShortcutScope.Annotation, Key.Enter, ModifierKeys.Control));
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 48 * 4], 64 * 4);
        image.Freeze();
        return image;
    }
}
