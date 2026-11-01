using System.Windows;
using System.Windows.Controls;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorFontPixelUnitTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void FontFieldShowsPixelsWithoutConvertingStoredValue(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "Font size / 글자 크기";
        Invoke(host.Editor, "CommitActiveText");
        Controller(host.Editor).ApplyFontSize(24);
        host.Window.UpdateLayout();
        FrameworkElement section = Field<FrameworkElement>(host.Editor, "_fontSizeSection");
        string[] labels = Descendants(section).OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("px", labels);
        Assert.DoesNotContain("pt", labels);
        TextAnnotation stored = Assert.Single(host.Editor.CreateCurrentImageSnapshot().Items.OfType<TextAnnotation>());
        Assert.Equal(24, stored.FontSize);
        Assert.Equal(24, AnnotationDocument.TryFromJson(host.Editor.CreateCurrentImageSnapshot().ToJson())!.Items.OfType<TextAnnotation>().Single().FontSize);
    });
}
