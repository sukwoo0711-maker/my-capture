using System.IO;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationPlannerTableTests
{
    private static string Table => PlannerSamples.Inline["table"];

    [Fact]
    public void RaggedTabSeparatedRowsAreATableButCodeIndentationIsNot()
    {
        Assert.Equal(PinTextFormat.Table, PinTranslationPlanner.Classify(Table, false, false));
        Assert.Equal(PinTextFormat.Table, PinTranslationPlanner.Classify(PlannerSamples.Inline["korean-table"], false, false));
        Assert.Equal(PinTextFormat.Table, PinTranslationPlanner.Classify("name\nvalue one\nvalue two\n", false, true));
        Assert.NotEqual(PinTextFormat.Table, PinTranslationPlanner.Classify("\tfoo();\n\tbar();\n", false, false));
        Assert.NotEqual(PinTextFormat.Table, PinTranslationPlanner.Classify("one\ttwo\nthree\tfour\tfive\n", false, false));
        Assert.NotEqual(PinTextFormat.Table, PinTranslationPlanner.Classify("only a single line\twith one tab\n", false, false));
    }

    [Fact]
    public void OnlyWordCellsAreTranslated()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Table, PinTranslationDirection.EnglishToKorean);
        Assert.All(plan.Pieces.Where(static piece => piece.Translate), static piece => Assert.Equal(PlanKind.TableCell, piece.Kind));
        Assert.Equal(
        [
            "Name", "Status", "Price", "Note",
            "Widget A", "In stock", "Ships within two days",
            "Widget B", "Back ordered", "Multi-line\nnote with \"quotes\"",
            "active",
        ], plan.Sources);
    }

    [Fact]
    public void TabsAndRowTerminatorsStayLiteral()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Table, PinTranslationDirection.EnglishToKorean);
        string output = plan.Assemble([.. plan.Sources.Select(static _ => (string?)"X")]);
        Assert.Equal("X\tX\tX\tX\r\nX\tX\t$12.50\tX\r\nX\tX\t1,200\t\"X\"\r\nID-1234\tX\t2025-01-05\thttps://example.com\r\n", output);
    }

    [Fact]
    public void QuotedCellKeepsItsQuotesAndTranslatesTheInside()
    {
        PinTranslationPlan plan = PlannerSamples.Plan(Table, PinTranslationDirection.EnglishToKorean);
        PlanPiece cell = Assert.Single(plan.Pieces, static piece => piece.Translate && piece.Raw.StartsWith("Multi-line", StringComparison.Ordinal));
        Assert.Equal("Multi-line\nnote with \"\"quotes\"\"", cell.Raw);
        Assert.Equal("Multi-line\nnote with \"quotes\"", cell.Content);
        int index = plan.Pieces.ToList().IndexOf(cell);
        Assert.EndsWith("\"", plan.Pieces[index - 1].Raw, StringComparison.Ordinal);
        Assert.StartsWith("\"", plan.Pieces[index + 1].Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCellsAndRaggedRowsAreKept()
    {
        string source = PlannerSamples.Inline["table-ragged"];
        PinTranslationPlan plan = PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Table);
        Assert.Empty(plan.Sources);
        Assert.Equal(source, plan.Assemble([]));
        PlannerSamples.AssertInvariants(source, plan);
    }

    [Fact]
    public void KoreanTableCellsAreTranslatedForKoreanToEnglish()
    {
        Assert.Equal(["이름", "상태", "메모", "위젯", "재고 있음", "이틀 안에 배송됩니다"],
            PlannerSamples.Sources(PlannerSamples.Inline["korean-table"], PinTranslationDirection.KoreanToEnglish));
        Assert.Empty(PlannerSamples.Sources(PlannerSamples.Inline["korean-table"], PinTranslationDirection.EnglishToKorean));
    }

    [Fact]
    public void DataCellsAreNotTranslated()
    {
        const string source = "Item\tValue\nA\t42\nB\t12:30 PM\nC\tJan 5, 2025\nD\t75%\nE\tSKU12345\nF\tsomeone@example.com\nG\tx = foo(1);\nH\tuser_profile_id\nI\tuserProfileId\nJ\t€1,200\nK\tUSD\n";
        Assert.Equal(["Item", "Value"], PlannerSamples.Sources(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Table));
    }

    [Fact]
    public void SampleFileRoundTrips()
    {
        if (PlannerSamples.Load("A5-excel-table.tsv") is not { } source) return;
        Assert.Equal(PinTextFormat.Table, PinTranslationPlanner.Classify(source, false, true));
        PlannerSamples.AssertInvariants(source, PlannerSamples.Plan(source, PinTranslationDirection.EnglishToKorean, PinTextFormat.Table));
    }
}
