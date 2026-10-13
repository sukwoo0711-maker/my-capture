using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using MyCapture.App.Localization;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class KoreanWordWrapTests
{
    private const char Joiner = KoreanWordWrap.WordJoiner;

    [Fact]
    public void KoreanWords_KeepTheirLettersTogether_AndSpacesStayBreakable()
    {
        string wrapped = KoreanWordWrap.KeepWords("필요한 항목은 보관하십시오.");

        Assert.Equal($"필{Joiner}요{Joiner}한 항{Joiner}목{Joiner}은 보{Joiner}관{Joiner}하{Joiner}십{Joiner}시{Joiner}오{Joiner}.", wrapped);
        Assert.Equal("필요한 항목은 보관하십시오.", wrapped.Replace(Joiner.ToString(), string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void CountersStayAttachedToTheirNumbers()
    {
        // Digits never break between themselves; only the digit-to-Hangul edge needs a joiner.
        Assert.Equal($"전{Joiner}체 24{Joiner}개 중 10{Joiner}개", KoreanWordWrap.KeepWords("전체 24개 중 10개"));
    }

    [Theory]
    [InlineData("Images are cleaned up after their retention period.")]
    [InlineData("24 of 24 items")]
    [InlineData("")]
    public void TextWithoutHangul_IsUnchanged(string text) => Assert.Equal(text, KoreanWordWrap.KeepWords(text));

    [Fact]
    public void LocTextKeepsKoreanWordsWholeOnlyWhereATextBlockShowsThem() => StaTestHost.Run(() =>
    {
        const string key = "Dialog.Storage";
        using (UiText.UseLanguage("ko-KR"))
        {
            string original = UiText.Get(key);
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            string shown = (string)Provide(key, block, TextBlock.TextProperty);
            block.Text = shown;
            Assert.Contains(Joiner, shown);
            Assert.Equal(original, Strip(shown));
            // The accessible name is the text without joiners, and follows later code-behind updates.
            Assert.Equal(original, AutomationProperties.GetName(block));
            block.Text = KoreanWordWrap.KeepWords("새 상태입니다");
            Assert.Equal("새 상태입니다", AutomationProperties.GetName(block));

            // Joined text costs more to lay out, so a label that never wraps keeps the plain text;
            // a wrapping set later in the markup (or by a style) is picked up at initialization.
            var label = new TextBlock();
            label.BeginInit();
            label.Text = (string)Provide(key, label, TextBlock.TextProperty);
            label.EndInit();
            Assert.Equal(original, label.Text);
            var later = new TextBlock();
            later.BeginInit();
            later.Text = (string)Provide(key, later, TextBlock.TextProperty);
            Assert.Equal(original, later.Text);
            later.TextWrapping = TextWrapping.Wrap;
            later.EndInit();
            Assert.Equal(KoreanWordWrap.KeepWords(original), later.Text);
            Assert.Equal(original, AutomationProperties.GetName(later));

            // A name the markup set first is kept.
            var named = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(named, "고정 이름");
            named.Text = (string)Provide(key, named, TextBlock.TextProperty);
            Assert.Equal("고정 이름", AutomationProperties.GetName(named));

            // Accessible names, tooltips, captions and editable text get the resource unchanged.
            Assert.Equal(original, Provide(key, new TextBlock { TextWrapping = TextWrapping.Wrap }, AutomationProperties.NameProperty));
            Assert.Equal(original, Provide(key, new Button(), FrameworkElement.ToolTipProperty));
            Assert.Equal(original, Provide(key, new Button(), ContentControl.ContentProperty));
            Assert.Equal(original, Provide(key, new TextBox(), TextBox.TextProperty));
            Assert.Equal(original, Provide(key, new TabItem(), HeaderedContentControl.HeaderProperty));
            // A settings row's label and note are shown in wrapping text blocks.
            Assert.Equal(KoreanWordWrap.KeepWords(original), Provide(key, new SettingsField(), SettingsField.NoteProperty));
            Assert.Equal(KoreanWordWrap.KeepWords(original), Provide(key, new SettingsField(), HeaderedContentControl.HeaderProperty));
            // Without a target (a Setter value, code) the text is unchanged.
            Assert.Equal(original, new TextExtension(key).ProvideValue(null!));
        }

        using (UiText.UseLanguage("en-US"))
        {
            var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Assert.Equal(UiText.Get(key), Provide(key, block, TextBlock.TextProperty));
            Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(AutomationProperties.NameProperty));
        }
    });

    [Fact]
    public void DialogsKeepKoreanWordsWholeButDetailsAndNamesStayExact() => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage("ko-KR");
        const string body = "다른 프로그램이 파일을 사용하고 있습니다.";
        const string details = "System.IO.IOException: 파일을 사용하고 있습니다";
        var dialog = new ThemedDialog(body, "저장할 수 없음", MessageBoxButton.OK, MessageBoxResult.OK, details);
        var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
        TextBlock message = panel.Children.OfType<TextBlock>().ElementAt(1);
        Assert.Equal(KoreanWordWrap.KeepWords(body), message.Text);
        Assert.Equal(body, AutomationProperties.GetName(message));
        TextBox technical = ((StackPanel)panel.Children.OfType<Expander>().Single().Content).Children.OfType<TextBox>().Single();
        Assert.Equal(details, technical.Text);
        dialog.Close();

        using var english = UiText.UseLanguage("en-US");
        var plain = new ThemedDialog(body, "Title", MessageBoxButton.OK, MessageBoxResult.OK);
        TextBlock untouched = ((StackPanel)((ScrollViewer)plain.Content).Content).Children.OfType<TextBlock>().ElementAt(1);
        Assert.Equal(body, untouched.Text);
        plain.Close();
    });

    [Fact]
    public void JoinedKoreanTextWrapsOnlyAtSpaces() => StaTestHost.Run(() =>
    {
        const string text = "설정을 저장하지 못했습니다. 저장 폴더의 쓰기 권한과 디스크 공간을 확인한 뒤 다시 시도하십시오.";
        foreach (double width in new[] { 140d, 180d, 230d })
        {
            var block = new TextBlock { Text = KoreanWordWrap.KeepWords(text), TextWrapping = TextWrapping.Wrap, Width = width, FontSize = 14 };
            _ = block.ContentStart;
            block.Measure(new Size(width, double.PositiveInfinity));
            block.Arrange(new Rect(block.DesiredSize));
            string[] lines = Lines(block).ToArray();
            Assert.True(lines.Length > 2, $"{width}: expected the sample to wrap, got {lines.Length} line(s)");
            foreach (string line in lines)
            {
                string visible = Strip(line).TrimEnd();
                Assert.True(text.Contains(visible, StringComparison.Ordinal), visible);
                // Every line ends at a word boundary: the next source character is a space or the end.
                int end = text.IndexOf(visible, StringComparison.Ordinal) + visible.Length;
                Assert.True(end == text.Length || text[end] == ' ', $"{width}: '{visible}' breaks inside a word");
            }
        }
    });

    private static IEnumerable<string> Lines(TextBlock block)
    {
        // Line breaks from the formatted layout: walk caret positions and group by baseline.
        var lines = new List<string>();
        System.Windows.Documents.TextPointer? start = block.ContentStart.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Forward);
        System.Windows.Documents.TextPointer? cursor = start;
        while (cursor is not null && start is not null)
        {
            System.Windows.Documents.TextPointer? next = cursor.GetLineStartPosition(1);
            System.Windows.Documents.TextPointer end = next ?? block.ContentEnd;
            lines.Add(new System.Windows.Documents.TextRange(cursor, end).Text);
            cursor = next;
        }

        return lines;
    }

    private static object Provide(string key, DependencyObject target, DependencyProperty property) =>
        new TextExtension(key).ProvideValue(new Target(target, property));

    private sealed class Target(object target, object property) : IServiceProvider, IProvideValueTarget
    {
        public object TargetObject => target;
        public object TargetProperty => property;
        public object? GetService(Type serviceType) => serviceType == typeof(IProvideValueTarget) ? this : null;
    }

    private static string Strip(string text) => KoreanWordWrap.Strip(text);
}
