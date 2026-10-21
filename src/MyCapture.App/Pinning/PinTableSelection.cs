using System.Text;

namespace MyCapture.App.Pinning;

internal sealed class PinTableSelection
{
    internal const int MaxRows = 200;
    internal const int MaxColumns = 32;
    internal const int MaxCellPreviewCharacters = 1024;

    private readonly string _original;
    // Source spans retain complete cell values without duplicating the table for its preview.
    private readonly IReadOnlyList<CellRange[]> _cells;
    private CellSelection? _selection;
    private bool _selectAll;

    internal PinTableSelection(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        _original = original;
        var rows = new List<IReadOnlyList<string>>();
        var cells = new List<CellRange[]>();
        int index = 0;
        bool complete = false;
        bool truncated = false;

        while (!complete && rows.Count < MaxRows)
        {
            var row = new List<string>();
            var cellRow = new List<CellRange>();
            while (true)
            {
                CellRange cell = ReadCellRange(ref index);
                if (row.Count < MaxColumns)
                {
                    row.Add(ReadCell(cell, MaxCellPreviewCharacters, out bool cellTruncated));
                    cellRow.Add(cell);
                    truncated |= cellTruncated;
                }
                else
                {
                    truncated = true;
                }

                if (index == _original.Length)
                {
                    complete = true;
                    break;
                }

                char separator = _original[index++];
                if (separator == '\t')
                {
                    continue;
                }

                if (separator == '\r' && index < _original.Length && _original[index] == '\n')
                {
                    index++;
                }

                complete = index == _original.Length;
                break;
            }

            rows.Add(row.ToArray());
            cells.Add(cellRow.ToArray());
        }

        Rows = rows;
        _cells = cells;
        ColumnCount = rows.Max(static row => row.Count);
        IsTruncated = truncated || !complete;
    }

    internal IReadOnlyList<IReadOnlyList<string>> Rows { get; }
    internal int RowCount => Rows.Count;
    internal int ColumnCount { get; }
    internal bool IsTruncated { get; }

    internal string? SelectedText
    {
        get
        {
            if (_selectAll)
            {
                return _original;
            }

            if (_selection is not { } selection)
            {
                return null;
            }

            if (selection.FirstRow == selection.LastRow && selection.FirstColumn == selection.LastColumn)
            {
                return GetCellValue(selection.FirstRow, selection.FirstColumn);
            }

            var text = new StringBuilder();
            for (int row = selection.FirstRow; row <= selection.LastRow; row++)
            {
                if (row > selection.FirstRow)
                {
                    text.Append("\r\n");
                }

                for (int column = selection.FirstColumn; column <= selection.LastColumn; column++)
                {
                    if (column > selection.FirstColumn)
                    {
                        text.Append('\t');
                    }

                    AppendTsvCell(text, GetCellValue(row, column));
                }
            }

            return text.ToString();
        }
    }

    internal void Select(int row, int column, int endRow, int endColumn)
    {
        row = Math.Clamp(row, 0, RowCount - 1);
        endRow = Math.Clamp(endRow, 0, RowCount - 1);
        column = Math.Clamp(column, 0, ColumnCount - 1);
        endColumn = Math.Clamp(endColumn, 0, ColumnCount - 1);
        _selection = new(
            Math.Min(row, endRow),
            Math.Min(column, endColumn),
            Math.Max(row, endRow),
            Math.Max(column, endColumn));
        _selectAll = false;
    }

    internal void SelectAll()
    {
        _selectAll = true;
        _selection = null;
    }

    internal void ClearSelection()
    {
        _selectAll = false;
        _selection = null;
    }

    private CellRange ReadCellRange(ref int index)
    {
        int start = index;
        bool quoted = index < _original.Length && _original[index] == '"';
        bool inQuotes = quoted;
        if (quoted)
        {
            index++;
        }

        while (index < _original.Length)
        {
            char current = _original[index];
            if (inQuotes && current == '"')
            {
                if (index + 1 < _original.Length && _original[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                inQuotes = false;
            }
            else if (!inQuotes && current is '\t' or '\r' or '\n')
            {
                break;
            }

            index++;
        }

        return new(start, index - start, quoted);
    }

    private string ReadCell(CellRange cell, int maxCharacters, out bool truncated)
    {
        int end = cell.Start + cell.Length;
        int index = cell.Start + (cell.Quoted ? 1 : 0);
        bool inQuotes = cell.Quoted;
        var value = new StringBuilder(Math.Min(cell.Length, Math.Min(maxCharacters, 4096)));
        truncated = false;

        while (index < end)
        {
            char current = _original[index++];
            if (inQuotes && current == '"')
            {
                if (index < end && _original[index] == '"')
                {
                    index++;
                }
                else
                {
                    inQuotes = false;
                    continue;
                }
            }

            if (value.Length == maxCharacters)
            {
                truncated = true;
                break;
            }

            value.Append(current);
        }

        if (truncated)
        {
            if (value.Length > 0 && char.IsHighSurrogate(value[^1]))
            {
                value.Length--;
            }

            value.Append('\u2026');
        }

        return value.ToString();
    }

    private string GetCellValue(int row, int column) =>
        column < _cells[row].Length
            ? ReadCell(_cells[row][column], int.MaxValue, out _)
            : string.Empty;

    private static void AppendTsvCell(StringBuilder text, string value)
    {
        if (value.IndexOfAny(['\t', '\r', '\n', '"']) < 0)
        {
            text.Append(value);
            return;
        }

        text.Append('"');
        text.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        text.Append('"');
    }

    private readonly record struct CellRange(int Start, int Length, bool Quoted);
    private readonly record struct CellSelection(int FirstRow, int FirstColumn, int LastRow, int LastColumn);
}
