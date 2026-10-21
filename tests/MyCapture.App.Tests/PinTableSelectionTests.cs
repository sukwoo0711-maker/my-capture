using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTableSelectionTests
{
    [Fact]
    public void QuotedFields_PreserveTabsLineBreaksAndDoubledQuotes()
    {
        var table = new PinTableSelection("\"a\tb\"\t\"line 1\r\nline 2\"\r\n\"say \"\"hello\"\"\"\t\"  value  \"");

        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(["a\tb", "line 1\r\nline 2"], table.Rows[0]);
        Assert.Equal(["say \"hello\"", "  value  "], table.Rows[1]);
        Assert.False(table.IsTruncated);
    }

    [Theory]
    [InlineData("\"a\tb\"", "a\tb")]
    [InlineData("\"line 1\r\nline 2\"", "line 1\r\nline 2")]
    [InlineData("\"line 1\rline 2\"", "line 1\rline 2")]
    [InlineData("\"say \"\"hello\"\"\"", "say \"hello\"")]
    [InlineData("  value  ", "  value  ")]
    [InlineData("unquoted\"quote", "unquoted\"quote")]
    [InlineData("\"\"", "")]
    public void OneCell_CopiesExactUnquotedValue(string original, string expected)
    {
        var table = new PinTableSelection(original);

        table.Select(0, 0, 0, 0);

        Assert.Equal(expected, table.SelectedText);
    }

    [Fact]
    public void RectangularRange_CopiesTsvWithCrLfAndOnlyNecessaryQuotes()
    {
        var table = new PinTableSelection("\"ordinary\"\t\"a\tb\"\n\"line 1\nline 2\"\t\"say \"\"hello\"\"\"");

        table.Select(0, 0, 1, 1);

        Assert.Equal("ordinary\t\"a\tb\"\r\n\"line 1\nline 2\"\t\"say \"\"hello\"\"\"", table.SelectedText);
    }

    [Fact]
    public void Range_PreservesSpacesAndEmptyCellsWithoutQuotingThem()
    {
        var table = new PinTableSelection("  left  \t\r\n\tright ");

        table.Select(1, 1, 0, 0);

        Assert.Equal("  left  \t\r\n\tright ", table.SelectedText);
    }

    [Fact]
    public void MixedRowDelimitersAndTrailingEmptyCells_ArePreserved()
    {
        var table = new PinTableSelection("a\t\rb\tc\n\td\r\n");

        Assert.Equal(3, table.RowCount);
        Assert.Equal(["a", ""], table.Rows[0]);
        Assert.Equal(["b", "c"], table.Rows[1]);
        Assert.Equal(["", "d"], table.Rows[2]);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void FinalRowTerminator_DoesNotAddAnExtraEmptyRow(string terminator)
    {
        var table = new PinTableSelection("a\tb" + terminator);

        Assert.Single(table.Rows);
        Assert.Equal(["a", "b"], table.Rows[0]);
    }

    [Fact]
    public void RepeatedFinalRowTerminator_PreservesExplicitEmptyRow()
    {
        var table = new PinTableSelection("a\r\n\r\n");

        Assert.Equal(2, table.RowCount);
        Assert.Equal(["a"], table.Rows[0]);
        Assert.Equal([""], table.Rows[1]);
    }

    [Fact]
    public void MissingCellsInRaggedRows_CopyAsEmptyCells()
    {
        var table = new PinTableSelection("a\tb\r\nc");

        table.Select(0, 0, 1, 1);
        Assert.Equal("a\tb\r\nc\t", table.SelectedText);

        table.Select(1, 1, 1, 1);
        Assert.Equal(string.Empty, table.SelectedText);
    }

    [Fact]
    public void SelectAll_ReturnsOriginalStringIncludingQuotesAndLineEndings()
    {
        const string original = "\"one\"\t\"two\"\n  three  \tfour\r\n";
        var table = new PinTableSelection(original);

        table.SelectAll();

        Assert.Same(original, table.SelectedText);
    }

    [Fact]
    public void NoSelectionAndSelectedEmptyCell_AreDistinct()
    {
        var table = new PinTableSelection(string.Empty);

        Assert.Null(table.SelectedText);
        table.Select(0, 0, 0, 0);
        Assert.Equal(string.Empty, table.SelectedText);
        table.ClearSelection();
        Assert.Null(table.SelectedText);
        table.SelectAll();
        Assert.Equal(string.Empty, table.SelectedText);
        table.ClearSelection();
        Assert.Null(table.SelectedText);
    }

    [Fact]
    public void CellSelectionAfterSelectAll_ReplacesFullSelection()
    {
        var table = new PinTableSelection("a\tb\r\nc\td");

        table.SelectAll();
        table.Select(1, 0, 1, 0);

        Assert.Equal("c", table.SelectedText);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LargeTable_BoundsDisplayedRowsAndColumnsWhileSelectAllKeepsOriginal(bool extraRow, bool extraColumn)
    {
        string row = string.Join('\t', Enumerable.Range(0, PinTableSelection.MaxColumns + (extraColumn ? 1 : 0)));
        string original = string.Join("\r\n", Enumerable.Repeat(row, PinTableSelection.MaxRows + (extraRow ? 1 : 0)));
        var table = new PinTableSelection(original);

        Assert.Equal(PinTableSelection.MaxRows, table.RowCount);
        Assert.Equal(PinTableSelection.MaxColumns, table.ColumnCount);
        Assert.All(table.Rows, displayedRow => Assert.Equal(PinTableSelection.MaxColumns, displayedRow.Count));
        Assert.True(table.IsTruncated);
        table.SelectAll();
        Assert.Same(original, table.SelectedText);
    }

    [Fact]
    public void HiddenQuotedColumn_DoesNotSplitTheFollowingVisibleRow()
    {
        string row = string.Join('\t', Enumerable.Repeat("visible", PinTableSelection.MaxColumns));
        var table = new PinTableSelection(row + "\t\"hidden\r\nwith\ttab\"\r\nnext");

        Assert.True(table.IsTruncated);
        Assert.Equal(2, table.RowCount);
        Assert.Equal("next", table.Rows[1][0]);
        table.Select(1, 0, 1, 0);
        Assert.Equal("next", table.SelectedText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableExactlyAtDisplayBounds_IsNotTruncated(bool terminalNewline)
    {
        string row = string.Join('\t', Enumerable.Repeat("x", PinTableSelection.MaxColumns));
        string original = string.Join("\r\n", Enumerable.Repeat(row, PinTableSelection.MaxRows));
        if (terminalNewline)
        {
            original += "\r\n";
        }
        var table = new PinTableSelection(original);

        Assert.Equal(PinTableSelection.MaxRows, table.RowCount);
        Assert.Equal(PinTableSelection.MaxColumns, table.ColumnCount);
        Assert.False(table.IsTruncated);
    }

    [Fact]
    public void LargeQuotedCell_HasBoundedPreviewAndCopiesItsEntireValue()
    {
        string value = new('a', PinTableSelection.MaxCellPreviewCharacters + 50);
        value += "\t\"quoted\"\r\nlast";
        string original = "\"" + value.Replace("\"", "\"\"") + "\"\tend";
        var table = new PinTableSelection(original);

        Assert.True(table.IsTruncated);
        Assert.Equal(new string('a', PinTableSelection.MaxCellPreviewCharacters) + "\u2026", table.Rows[0][0]);
        table.Select(0, 0, 0, 0);
        Assert.Equal(value, table.SelectedText);
        table.Select(0, 0, 0, 1);
        Assert.Equal(original, table.SelectedText);
    }

    [Fact]
    public void CellAtPreviewBoundary_IsNotTruncated()
    {
        string value = new('a', PinTableSelection.MaxCellPreviewCharacters);
        var table = new PinTableSelection("\"" + value + "\"");

        Assert.False(table.IsTruncated);
        Assert.Equal(value, table.Rows[0][0]);
    }

    [Fact]
    public void TruncatedPreview_DoesNotSplitSurrogatePair()
    {
        string value = new string('a', PinTableSelection.MaxCellPreviewCharacters - 1) + "😀tail";
        var table = new PinTableSelection(value);

        Assert.Equal(new string('a', PinTableSelection.MaxCellPreviewCharacters - 1) + "\u2026", table.Rows[0][0]);
        table.Select(0, 0, 0, 0);
        Assert.Equal(value, table.SelectedText);
    }

    [Fact]
    public void SelectionBeyondDisplayedBounds_ClampsToVisibleCells()
    {
        var table = new PinTableSelection("a\tb\r\nc\td");

        table.Select(-1, -1, int.MaxValue, int.MaxValue);

        Assert.Equal("a\tb\r\nc\td", table.SelectedText);
    }
}
