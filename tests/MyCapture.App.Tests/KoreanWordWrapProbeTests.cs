using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class KoreanWordWrapProbeTests
{
    [Fact]
    public void MeasuredPlainOrphanIsDetectedAndJoinedCopyIsNot() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        const string text = "평가할 사진을 골라보세요";
        var block = Create(text);
        double width = FindOrphanWidth(block);
        KoreanWordWrapProbe.Orphan orphan = Assert.IsType<KoreanWordWrapProbe.Orphan>(KoreanWordWrapProbe.Inspect(block));
        Assert.Equal("요", orphan.LastLine);
        TextPointer last = block.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Assert.Equal("요", new TextRange(last.GetLineStartPosition(0)!, block.ContentEnd).Text.Trim());
        Assert.True(orphan.LineCount > 1);
        Assert.Equal(text, orphan.Text);
        // The tree walk the self-test report uses finds the same block.
        Assert.Same(block, Assert.Single(KoreanWordWrapProbe.FindOrphans(block)).Block);

        KoreanWordWrap.Show(block, text);
        Layout(block, width);
        Assert.Null(KoreanWordWrapProbe.Inspect(block));
        Assert.Empty(KoreanWordWrapProbe.FindOrphans(block));
    });

    [Fact]
    public void DifferentInlineSizesOnOneLineAreNotAnOrphan() => StaTestHost.Run(() =>
    {
        var block = Create(string.Empty);
        block.Inlines.Add(new Run("앞") { FontSize = TypeScale.Title });
        block.Inlines.Add(new Run("켬") { FontSize = TypeScale.Body });
        Layout(block, 300);
        Assert.Null(KoreanWordWrapProbe.Inspect(block));
    });

    [Fact]
    public void InlineOffsetsAndTrailingPunctuationAreReadFromLayout() => StaTestHost.Run(() =>
    {
        var block = Create(string.Empty);
        block.Inlines.Add(new Run("평가할 사진을 "));
        block.Inlines.Add(new Bold(new Run("골라보세요.")));
        FindOrphanWidth(block);
        KoreanWordWrapProbe.Orphan orphan = Assert.IsType<KoreanWordWrapProbe.Orphan>(KoreanWordWrapProbe.Inspect(block));
        Assert.Equal("요.", orphan.LastLine);
        Assert.Equal("평가할 사진을 골라보세요.", orphan.Text);
    });

    [Fact]
    public void AOneLineSyllableOrANonwrappingBlockIsNotAnOrphan() => StaTestHost.Run(() =>
    {
        var oneLine = Create("켬");
        Layout(oneLine, 200);
        Assert.Null(KoreanWordWrapProbe.Inspect(oneLine));
        var noWrap = Create("평가할 사진을 골라보세요");
        noWrap.TextWrapping = TextWrapping.NoWrap;
        Layout(noWrap, 40);
        Assert.Null(KoreanWordWrapProbe.Inspect(noWrap));
    });

    private static TextBlock Create(string text)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = ThemeFonts.Ui, FontSize = TypeScale.Body,
            TextWrapping = TextWrapping.Wrap,
        };
        ThemeFonts.ApplyText(block);
        _ = block.ContentStart;
        return block;
    }

    private static double FindOrphanWidth(TextBlock block)
    {
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double fullWidth = block.DesiredSize.Width;
        for (double width = Math.Ceiling(fullWidth); width >= fullWidth / 2; width -= 0.25)
        {
            Layout(block, width);
            if (KoreanWordWrapProbe.Inspect(block) is not null) return width;
        }
        throw new Xunit.Sdk.XunitException($"No orphan width found from measured width {fullWidth:0.##}: {block.Text}");
    }

    private static void Layout(TextBlock block, double width)
    {
        block.Width = width;
        block.Measure(new Size(width, double.PositiveInfinity));
        block.Arrange(new Rect(new Size(width, block.DesiredSize.Height)));
        block.UpdateLayout();
    }
}
