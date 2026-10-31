using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class KoreanWordWrapPolicyTests
{
    private const char J = KoreanWordWrap.WordJoiner;

    // A word joiner is a zero-weight character for culture-aware comparison, so these checks
    // compare the exact strings (or use ordinal comparison); a culture-aware Contains would see
    // " " and " ⁠" as the same text.
    [Theory]
    [InlineData("자동 저장 켬", "자J동 저J장 J켬")]
    [InlineData("자동 저장 켬.", "자J동 저J장 J켬J.")]
    [InlineData("자동 저장  켬!  ", "자J동 저J장 J J켬J!  ")]
    [InlineData("자동 저장 (켬)", "자J동 저J장 J(J켬J)")]
    [InlineData("자동 저장 “켬”", "자J동 저J장 J“J켬J”")]
    [InlineData("Auto save 켬", "Auto save J켬")]
    public void FinalSingleSyllableJoinsThePreviousWordAndRoundTrips(string text, string expectedWithJ)
    {
        string expected = expectedWithJ.Replace('J', J);
        string shown = KoreanWordWrap.KeepWords(text);
        Assert.Equal(expected, shown);
        Assert.Equal(text, KoreanWordWrap.Strip(shown));
        Assert.Equal(shown, KoreanWordWrap.KeepWords(shown));
    }

    [Theory]
    [InlineData("평가할 사진을 골라보세요", "평J가J할 사J진J을 골J라J보J세J요")]
    [InlineData("Automatic save on.", "Automatic save on.")]
    [InlineData("자동 저장\n켬", "자J동 저J장\n켬")]
    [InlineData("자동 저장\r\n 켬", "자J동 저J장\r\n 켬")]
    [InlineData("켬", "켬")]
    [InlineData(" 켬", " 켬")]
    [InlineData("자동 저장 켬됨", "자J동 저J장 켬J됨")]
    [InlineData("자동 저장 켬1", "자J동 저J장 켬J1")]
    public void OtherTextPreservesItsWordAndParagraphBoundaries(string text, string expectedWithJ)
    {
        string expected = expectedWithJ.Replace('J', J);
        string shown = KoreanWordWrap.KeepWords(text);
        Assert.Equal(expected, shown);
        Assert.Equal(text, KoreanWordWrap.Strip(shown));
        // The last-word rule adds a joiner after whitespace; none of these texts qualifies.
        Assert.DoesNotContain(" " + J, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalSyllableDoesNotOccupyItsOwnMeasuredLine() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        const string text = "자동 저장 켬";
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap,
            FontFamily = ThemeFonts.Ui, FontSize = TypeScale.Body,
        };
        ThemeFonts.ApplyText(block);
        _ = block.ContentStart;
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fullWidth = block.DesiredSize.Width;
        double found = 0;
        for (double width = Math.Ceiling(fullWidth); width >= fullWidth / 2; width -= 0.25)
        {
            Layout(width);
            if (KoreanWordWrapProbe.Inspect(block) is not null) { found = width; break; }
        }
        Assert.True(found > 0, "The measured plain fixture must leave 켬 on its own line.");
        KoreanWordWrap.Show(block, text);
        Layout(found);
        Assert.Null(KoreanWordWrapProbe.Inspect(block));
        Assert.Equal(text, AutomationProperties.GetName(block));

        void Layout(double width)
        {
            block.Width = width;
            block.Measure(new Size(width, double.PositiveInfinity));
            block.Arrange(new Rect(new Size(width, block.DesiredSize.Height)));
            block.UpdateLayout();
        }
    });

    [Fact]
    public void InlineJoiningPreservesRunsLinksEmphasisAndPlainAccessibleName() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var first = new Run("자동 ");
        var emphasized = new Run("저장 ");
        var final = new Run("켬.");
        var bold = new Bold(emphasized);
        var link = new Hyperlink(final) { NavigateUri = new Uri("https://example.invalid/") };
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(first);
        block.Inlines.Add(bold);
        block.Inlines.Add(link);
        KoreanWordWrap.ShowInlines(block);
        // A TextRange over the block leaves out a hyperlink's text, so the runs are read directly.
        string display = KoreanWordWrapProbe.TextOf(block);
        Assert.Equal(KoreanWordWrap.KeepWords("자동 저장 켬."), display);
        Assert.Equal($"자{J}동 ", first.Text);
        Assert.Equal($"저{J}장 {J}", emphasized.Text);
        Assert.Equal($"켬{J}.", final.Text);
        Assert.Same(bold, block.Inlines.ElementAt(1));
        Assert.Same(link, block.Inlines.ElementAt(2));
        Assert.Same(final, link.Inlines.FirstInline);
        Assert.Equal("https://example.invalid/", link.NavigateUri.AbsoluteUri);
        Assert.Equal("자동 저장 켬.", AutomationProperties.GetName(block));
        KoreanWordWrap.ShowInlines(block);
        Assert.Equal(display, KoreanWordWrapProbe.TextOf(block));
        using var english = UiText.UseLanguage("en-US");
        KoreanWordWrap.ShowInlines(block);
        Assert.Equal("자동 저장 켬.", KoreanWordWrapProbe.TextOf(block));
        Assert.Equal("자동 저장 켬.", AutomationProperties.GetName(block));
    });

    [Fact]
    public void InlineJoiningKeepsLineBreaksAndStaysOutOfNonwrappingBlocks() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var upper = new Run("자동 저장");
        var lower = new Run("켬");
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(upper);
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(lower);
        KoreanWordWrap.ShowInlines(block);
        // The paragraph break keeps "켬" on its own line, so no last-word joiner crosses it.
        Assert.Equal($"자{J}동 저{J}장", upper.Text);
        Assert.Equal("켬", lower.Text);
        Assert.Equal("자동 저장\n켬", AutomationProperties.GetName(block));

        block.TextWrapping = TextWrapping.NoWrap;
        KoreanWordWrap.ShowInlines(block);
        Assert.Equal("자동 저장", upper.Text);
    });

    [Fact]
    public void AttachedTextHonorsLateWrapStyleAndLaterBindingValues() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var block = new TextBlock();
        block.BeginInit();
        KoreanWordWrap.SetText(block, "자동 저장 켬");
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.WrapWithOverflow));
        block.Style = style;
        block.EndInit();
        Assert.Equal(KoreanWordWrap.KeepWords("자동 저장 켬"), block.Text);
        Assert.Equal("자동 저장 켬", AutomationProperties.GetName(block));
        var source = new TextBlock { Text = "새 상태입니다" };
        BindingOperations.SetBinding(block, KoreanWordWrap.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = source });
        Assert.Equal(KoreanWordWrap.KeepWords(source.Text), block.Text);
        source.Text = "";
        Assert.Equal("", block.Text);
        Assert.Equal("", AutomationProperties.GetName(block));
        source.Text = "English state";
        Assert.Equal(source.Text, AutomationProperties.GetName(block));
        using var english = UiText.UseLanguage("en-US");
        source.Text = "한국어 콘텐츠";
        Assert.Equal(source.Text, block.Text);
    });

    // Code that reads the accessible name of a status line (and a screen reader) gets the text in
    // every language: the attached text names its block even when no joiner was added.
    [Theory]
    [InlineData("ko-KR", "자동 저장 켬", "다음 상태")]
    [InlineData("en-US", "Auto save on", "Next state")]
    public void AttachedTextNamesItsBlockInEveryLanguageAndFollowsLaterText(string language, string first, string second) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        KoreanWordWrap.SetText(block, first);
        Assert.Equal(first, AutomationProperties.GetName(block));
        KoreanWordWrap.SetText(block, second);
        Assert.Equal(second, AutomationProperties.GetName(block));
        // A name the code set on its own is kept.
        var named = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(named, "고정 이름");
        KoreanWordWrap.SetText(named, first);
        Assert.Equal("고정 이름", AutomationProperties.GetName(named));
    });

    [Fact]
    public void NonwrappingCodeAndInlineLabelsKeepPlainText() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        const string text = "자동 저장 켬";
        var label = KoreanWordWrap.Show(new TextBlock(), text);
        Assert.Equal(text, label.Text);
        var inline = new TextBlock(new Run(text));
        KoreanWordWrap.ShowInlines(inline);
        Assert.Equal(text, KoreanWordWrapProbe.TextOf(inline));
    });
}
