using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsCopyRegressionTests
{
    [Theory]
    [InlineData(1920, 1080, 2, 940, 516, 780, 516)]
    [InlineData(1920, 1080, 1, 940, 700, 780, 560)]
    [InlineData(2560, 1440, 1.5, 940, 700, 780, 560)]
    [InlineData(1920, 1080, 1.5, 940, 688, 780, 560)]
    [InlineData(1366, 768, 2, 683, 360, 683, 360)]
    public void SettingsSizeAndMinimumFitTheTargetWorkArea(double width, double height, double scale,
        double expectedWidth, double expectedHeight, double expectedMinWidth, double expectedMinHeight)
    {
        using Stream stream = typeof(SettingsCopyRegressionTests).Assembly.GetManifestResourceStream("SettingsFixture.Window.xaml")!;
        XElement window = XDocument.Load(stream).Root!;
        var desired = new Size((double)window.Attribute("Width")!, (double)window.Attribute("Height")!);
        var minimum = new Size((double)window.Attribute("MinWidth")!, (double)window.Attribute("MinHeight")!);
        var work = new RectD(-width, 80, width, height - 48);
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(work, scale, desired, minimum, 0);
        Assert.Equal(new Size(expectedWidth, expectedHeight), fit.Size);
        Assert.Equal(new Size(expectedMinWidth, expectedMinHeight), fit.Minimum);
        Assert.True(fit.Bounds.Left >= work.Left && fit.Bounds.Top >= work.Top);
        Assert.True(fit.Bounds.Right <= work.Right && fit.Bounds.Bottom <= work.Bottom);
    }

    [Theory]
    [InlineData("ko-KR", HotkeyModifiers.None, Hotkey.VkF3, "복사한 표와 코드를 F3 키로 다른 창 위에 띄워 참고할 수 있습니다.")]
    [InlineData("en-US", HotkeyModifiers.None, Hotkey.VkF3, "Press F3 to keep copied tables and code above other windows for reference.")]
    [InlineData("ko-KR", HotkeyModifiers.Control, 0x4B, "복사한 표와 코드를 Ctrl+K 키로 다른 창 위에 띄워 참고할 수 있습니다.")]
    [InlineData("en-US", HotkeyModifiers.Control, 0x4B, "Press Ctrl+K to keep copied tables and code above other windows for reference.")]
    public void WelcomeUsesTheAssignedPinShortcut(string language, HotkeyModifiers modifiers, uint key, string expected) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var welcome = new WelcomeWindow(new HotkeySettings { PasteToScreen = new Hotkey(modifiers, key) }, AfterCaptureAction.Editor);
        try
        {
            TextBlock hint = Assert.Single(FirstRunWelcomeTests.Descendants(welcome).OfType<TextBlock>(),
                text => KoreanWordWrap.Strip(text.Text) == expected);
            Assert.Equal(Visibility.Visible, hint.Visibility);
            Assert.Equal(12, hint.FontSize);
        }
        finally { welcome.Close(); }
    });

    [Fact]
    public void WelcomeHidesPinHintWithoutAnAssignedShortcut() => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage("en-US");
        var welcome = new WelcomeWindow(new HotkeySettings { PasteToScreen = Hotkey.None }, AfterCaptureAction.Editor);
        try
        {
            Assert.DoesNotContain(FirstRunWelcomeTests.Descendants(welcome).OfType<TextBlock>(),
                text => text.Visibility == Visibility.Visible
                    && text.Text.Contains("copied tables and code above other windows", StringComparison.Ordinal));
            Assert.DoesNotContain(FirstRunWelcomeTests.Descendants(welcome).OfType<TextBlock>(), text => text.Text.Contains("F3", StringComparison.Ordinal));
        }
        finally { welcome.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void OrganizationHintsKeepWordsAndTheirAccessibleOriginalText(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var dialog = new GalleryOrganizationDialog(new CaptureRecord(), (_, _) => Task.FromResult(true));
        try
        {
            foreach (string key in new[] { "Library.Organize.Hint", "Library.Organize.TagsHint" })
            {
                string original = UiText.Get(key);
                TextBlock text = Assert.Single(FirstRunWelcomeTests.Descendants(dialog).OfType<TextBlock>(),
                    block => KoreanWordWrap.Strip(block.Text) == original);
                if (language == "ko-KR")
                {
                    Assert.Contains(KoreanWordWrap.WordJoiner, text.Text);
                    Assert.Equal(original, AutomationProperties.GetName(text));
                }
                else Assert.Equal(original, text.Text);
            }
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void SourceIconKeepsItsSizeWhileTileActionsRequireHoverOrFocus()
    {
        using Stream stream = typeof(SettingsCopyRegressionTests).Assembly.GetManifestResourceStream("GalleryFixture.Window.xaml")!;
        XDocument document = XDocument.Load(stream);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement style = Assert.Single(document.Descendants(p + "Style"), node => (string?)node.Attribute(x + "Key") == "Gallery.SourceBadge");
        AssertSetters(style, "32", "32", "32", "Text.Secondary", "0");
        string[] interactionBindings =
        [
            "{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ListBoxItem}}",
            "{Binding IsKeyboardFocusWithin, RelativeSource={RelativeSource AncestorType=ListBoxItem}}",
        ];
        Assert.Empty(style.Descendants(p + "DataTrigger"));
        XElement[] triggers = style.Descendants(p + "Trigger").ToArray();
        Assert.Equal(new[] { "IsMouseOver", "IsKeyboardFocused" },
            triggers.Select(trigger => (string?)trigger.Attribute("Property")));
        foreach (XElement trigger in triggers)
        {
            Assert.Equal("True", (string?)trigger.Attribute("Value"));
            Assert.DoesNotContain(trigger.Elements(p + "Setter"),
                setter => (string?)setter.Attribute("Property") is "Height" or "Width" or "MaxWidth" or "Padding");
        }
        foreach (string key in new[] { "Gallery.TileTemplate", "Gallery.CompactTileTemplate" })
        {
            XElement template = Assert.Single(document.Descendants(p + "DataTemplate"), node => (string?)node.Attribute(x + "Key") == key);
            XElement badge = Assert.Single(template.Descendants(p + "Button"), node => (string?)node.Attribute(x + "Name") == "SourceBadge");
            Assert.Equal("{StaticResource Gallery.SourceBadge}", (string?)badge.Attribute("Style"));
            foreach (string property in new[] { "Height", "MinHeight", "MaxWidth", "Foreground", "BorderThickness" })
                Assert.Null(badge.Attribute(property));
            XElement actions = Assert.Single(template.Descendants(p + "Border"), node => (string?)node.Attribute(x + "Name") == "TileActions");
            XElement actionsStyle = Assert.Single(actions.Element(p + "Border.Style")!.Elements(p + "Style"));
            Dictionary<string, string> defaults = actionsStyle.Elements(p + "Setter").ToDictionary(
                node => (string)node.Attribute("Property")!, node => (string)node.Attribute("Value")!);
            Assert.Equal("0", defaults["Opacity"]);
            Assert.Equal("False", defaults["IsHitTestVisible"]);
            Assert.Equal("Fast", defaults["themes:FluidMotion.Transition"]);
            XElement[] actionTriggers = actionsStyle.Descendants(p + "DataTrigger").ToArray();
            Assert.Equal(interactionBindings, actionTriggers.Select(trigger => (string?)trigger.Attribute("Binding")));
            foreach (XElement trigger in actionTriggers)
            {
                Assert.Equal("True", (string?)trigger.Attribute("Value"));
                Dictionary<string, string> values = trigger.Elements(p + "Setter").ToDictionary(
                    node => (string)node.Attribute("Property")!, node => (string)node.Attribute("Value")!);
                Assert.Equal("1", values["themes:FluidMotion.FadeTo"]);
                Assert.Equal("True", values["IsHitTestVisible"]);
            }
            XElement video = Assert.Single(template.Descendants(p + "Border"), node => (string?)node.Attribute(x + "Name") == "VideoBadge");
            Assert.NotSame(badge.Parent, video.Parent);
            Assert.Equal("1", (string?)video.Attribute("Grid.Column"));
        }

        void AssertSetters(XElement owner, string height, string minHeight, string width, string foreground, string border)
        {
            Dictionary<string, string> values = owner.Elements(p + "Setter").ToDictionary(node => (string)node.Attribute("Property")!, node => (string)node.Attribute("Value")!);
            Assert.Equal(height, values["Height"]);
            Assert.Equal(minHeight, values["MinHeight"]);
            Assert.Equal(width, values["MaxWidth"]);
            Assert.Equal("{DynamicResource " + foreground + "}", values["Foreground"]);
            Assert.Equal(border, values["BorderThickness"]);
        }
    }
}
