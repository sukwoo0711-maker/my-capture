using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace MyCapture.Ocr;

public static class OcrTableExport
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static string Csv(OcrTable table) => Delimited(table, ',');
    public static string Tsv(OcrTable table) => Delimited(table, '\t');

    private static string Delimited(OcrTable table, char separator)
    {
        ArgumentNullException.ThrowIfNull(table);
        return string.Join("\r\n", table.Rows.Select(row => string.Join(separator,
            row.Cells.Select(cell => Quote(SpreadsheetLiteral(cell.Text), separator)))));
    }

    public static string SpreadsheetLiteral(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0])
            && !OcrTableReconstructor.IsNumeric(trimmed) ? "'" + text : text;
    }

    private static string Quote(string text, char separator) =>
        text.Contains(separator) || text.IndexOfAny(['"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public static string Markdown(OcrTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Columns.Count == 0 || table.Rows.Count == 0) return "";
        IEnumerable<string> header = table.HasHeader
            ? table.Rows[0].Cells.Select(cell => cell.Text)
            : Enumerable.Range(0, table.Columns.Count).Select(ColumnName);
        var rows = new List<string>
        {
            MarkdownRow(header),
            "| " + string.Join(" | ", table.Columns.Select(column => column.IsNumeric ? "---:" : "---")) + " |",
        };
        rows.AddRange(table.Rows.Skip(table.HasHeader ? 1 : 0)
            .Select(row => MarkdownRow(row.Cells.Select(cell => cell.Text))));
        return string.Join("\n", rows);
    }

    private static string MarkdownRow(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(text =>
        text.Replace("\\", "\\\\").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("|", "\\|").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>"))) + " |";

    public static string ColumnName(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var name = new StringBuilder();
        long remaining = (long)index + 1;
        while (remaining > 0)
        {
            remaining--;
            name.Insert(0, (char)('A' + remaining % 26));
            remaining /= 26;
        }
        return name.ToString();
    }

    /// <summary>Values are inline strings, never formulas, links or executable spreadsheet content.</summary>
    public static void WriteWorkbook(Stream destination, OcrTable table)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Columns.Count > 16_384 || table.Rows.Count > 1_048_576)
            throw new InvalidDataException("The table exceeds the worksheet dimensions.");
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WritePart(archive, "[Content_Types].xml",
            """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
        WritePart(archive, "_rels/.rels",
            """<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        WritePart(archive, "xl/workbook.xml",
            """<?xml version="1.0" encoding="utf-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="OCR" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        WritePart(archive, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        WritePart(archive, "xl/styles.xml",
            """<?xml version="1.0" encoding="utf-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="right"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");
        using Stream sheet = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
        using XmlWriter xml = XmlWriter.Create(sheet, new XmlWriterSettings
            { Encoding = new UTF8Encoding(false), CloseOutput = false });
        xml.WriteStartDocument();
        xml.WriteStartElement("worksheet", SpreadsheetNamespace);
        xml.WriteStartElement("cols", SpreadsheetNamespace);
        for (int column = 0; column < table.Columns.Count; column++)
        {
            xml.WriteStartElement("col", SpreadsheetNamespace);
            xml.WriteAttributeString("min", (column + 1).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("max", (column + 1).ToString(CultureInfo.InvariantCulture));
            int length = table.Rows.Select(row => OcrGeometry.CharacterColumns(row.Cells[column].Text))
                .DefaultIfEmpty(8).Max();
            xml.WriteAttributeString("width", Math.Clamp(length + 2, 8, 80).ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteStartElement("sheetData", SpreadsheetNamespace);
        for (int row = 0; row < table.Rows.Count; row++)
        {
            xml.WriteStartElement("row", SpreadsheetNamespace);
            xml.WriteAttributeString("r", (row + 1).ToString(CultureInfo.InvariantCulture));
            for (int column = 0; column < table.Columns.Count; column++)
            {
                xml.WriteStartElement("c", SpreadsheetNamespace);
                xml.WriteAttributeString("r", ColumnName(column) + (row + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteAttributeString("s", table.HasHeader && row == 0 ? "1"
                    : table.Columns[column].IsNumeric ? "2" : "0");
                xml.WriteStartElement("is", SpreadsheetNamespace);
                xml.WriteStartElement("t", SpreadsheetNamespace);
                xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                xml.WriteString(XmlText(table.Rows[row].Cells[column].Text));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static string XmlText(string value)
    {
        var result = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes())
            if (rune.Value is 9 or 10 or 13 or >= 0x20 and <= 0xd7ff
                or >= 0xe000 and <= 0xfffd or >= 0x10000 and <= 0x10ffff)
                result.Append(rune.ToString());
        return result.ToString();
    }

    private static void WritePart(ZipArchive archive, string name, string xml)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }
}
