using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using Xunit;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;

namespace MyCapture.App.Tests;

/// <summary>
/// #226 robustness stratum: the same text sizes as the strata corpus, drawn with the user
/// interface structure a capture usually carries — a full table grid, bordered input boxes,
/// solid icons with a highlighted row, and bordered toolbar buttons. Structure that is not
/// text (rules, borders, fills) is what a text-height estimate must not mistake for text.
/// </summary>
internal static class Ocr226ClutterCorpus
{
    internal const string CorpusId = "ocr226-clutter-192-v1";
    internal const uint Seed = 20261107;
    internal static readonly string[] Kinds = ["grid-ko", "form-ko", "list-ko", "toolbar-mixed"];
    internal static readonly double[] Sizes = [9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 40];

    internal static Data.Manifest Generate(string root)
    {
        var samples = new List<Data.Sample>();
        var fonts = new Dictionary<string, Data.FontEvidence>(StringComparer.Ordinal);
        int scene = 0;
        foreach (string kind in Kinds)
        for (int size = 0; size < Sizes.Length; size++)
        foreach (bool dark in new[] { false, true })
        foreach (bool tight in new[] { true, false })
        {
            string split = dark == tight ? "held-out" : "development";
            samples.Add(Render(root, kind, Sizes[size], dark, tight, split, scene++, fonts));
        }
        var manifest = new Data.Manifest(1, CorpusId, Seed, samples.Count,
            "4 interface kinds x 12 text sizes (9-40 px) x light/dark x tight/wide crop = 192 images with grids, boxes, icons and " +
            "highlights. Synthetic text only; no captures or user text.",
            samples.ToArray(), fonts.Values.OrderBy(font => font.Uri, StringComparer.Ordinal).ToArray());
        Data.Write(root, "corpus.json", manifest);
        return manifest;
    }

    private sealed record Cell(string Text, double X, double Y, bool RightAligned);

    private static Data.Sample Render(string root, string kind, double size, bool dark, bool tight, string split, int scene,
        Dictionary<string, Data.FontEvidence> fonts)
    {
        uint state = Seed ^ (uint)(scene + 1) * 0x9E3779B9u;
        string id = $"{kind}-{size:00}-{(dark ? "dark" : "light")}-{(tight ? "tight" : "wide")}";
        Color inkColor = dark ? Color.FromRgb(232, 234, 238) : Color.FromRgb(20, 20, 20);
        Brush ink = new SolidColorBrush(inkColor);
        Brush background = new SolidColorBrush(dark ? Color.FromRgb(24, 27, 33) : Colors.White);
        Pen line = new(new SolidColorBrush(dark ? Color.FromRgb(96, 100, 110) : Color.FromRgb(160, 160, 160)), 1);
        Brush highlight = new SolidColorBrush(dark ? Color.FromRgb(38, 79, 120) : Color.FromRgb(204, 228, 247));
        Brush[] icons = [new SolidColorBrush(Color.FromRgb(0, 120, 212)), new SolidColorBrush(Color.FromRgb(16, 124, 16)),
            new SolidColorBrush(Color.FromRgb(202, 80, 16)), new SolidColorBrush(Color.FromRgb(135, 100, 184))];
        var formatting = scene % 3 == 0 ? TextFormattingMode.Ideal : TextFormattingMode.Display;
        string font = "Malgun Gothic";
        FormattedText Text(string value) => new(value, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
            new Typeface(font), size, ink, null, formatting, 1.0);

        double pad = tight ? Math.Round(size * 0.7) + 4 : 32;
        var rows = new List<Cell[]>();
        var rules = new List<(Point A, Point B)>();
        var boxes = new List<(Rect Box, Brush? Fill, Pen? Border, double Radius)>();
        double contentRight, contentBottom;
        switch (kind)
        {
            case "grid-ko":
            {
                string[][] values = TableValues(ref state);
                double cellPad = Math.Round(size * 0.8), rowHeight = Math.Round(size * 2.2);
                double[] widths = Enumerable.Range(0, 3).Select(column => values.Max(row => Text(row[column]).Width) + cellPad * 2).ToArray();
                double[] lefts = [pad, pad + widths[0], pad + widths[0] + widths[1]];
                double right = lefts[2] + widths[2];
                for (int row = 0; row < values.Length; row++)
                {
                    double top = pad + row * rowHeight;
                    double y = Math.Round(top + (rowHeight - size * 1.33) / 2);
                    rows.Add(Enumerable.Range(0, 3).Select(column => column == 0
                        ? new Cell(values[row][column], lefts[0] + cellPad, y, false)
                        : new Cell(values[row][column], lefts[column] + widths[column] - cellPad, y, true)).ToArray());
                }
                double bottom = pad + values.Length * rowHeight;
                for (int row = 0; row <= values.Length; row++)
                    rules.Add((new Point(pad, pad + row * rowHeight + 0.5), new Point(right, pad + row * rowHeight + 0.5)));
                foreach (double x in new[] { lefts[0], lefts[1], lefts[2], right })
                    rules.Add((new Point(Math.Round(x) + 0.5, pad), new Point(Math.Round(x) + 0.5, bottom)));
                contentRight = right;
                contentBottom = bottom;
                break;
            }
            case "form-ko":
            {
                string[] labels = ["파일 이름", "저장 위치", "형식", "품질", "배율"];
                string[][] pools =
                [
                    ["캡처 20261106.png", "회의 자료 3쪽.png", "견적서 1,204,000원.png"],
                    ["문서\\MyCapture", "사진\\화면 캡처", "바탕 화면"],
                    ["PNG", "JPEG", "WebP"],
                    ["92%", "85%", "100%"],
                    ["125%", "150%", "1.5배"],
                ];
                int count = 3 + (int)(Next(ref state) % 3);
                double labelWidth = labels.Take(count).Max(label => Text(label).Width);
                string[] chosen = Enumerable.Range(0, count).Select(index => Pick(pools[index], ref state)).ToArray();
                double boxPad = Math.Round(size * 0.5), boxHeight = Math.Round(size * 1.9), pitch = Math.Round(size * 2.6);
                double boxLeft = pad + labelWidth + Math.Round(size * 1.2);
                double boxWidth = Math.Max(chosen.Max(value => Text(value).Width) + boxPad * 2, size * 10);
                for (int row = 0; row < count; row++)
                {
                    double top = pad + row * pitch;
                    double y = Math.Round(top + (boxHeight - size * 1.33) / 2);
                    rows.Add([new Cell(labels[row], pad, y, false), new Cell(chosen[row], boxLeft + boxPad, y, false)]);
                    boxes.Add((new Rect(Math.Round(boxLeft) + 0.5, Math.Round(top) + 0.5, Math.Round(boxWidth), boxHeight), null, line, 2));
                }
                contentRight = boxLeft + boxWidth + 1;
                contentBottom = pad + (count - 1) * pitch + boxHeight + 1;
                break;
            }
            case "list-ko":
            {
                string[] names = ["보고서 초안.docx", "회의록 11월.txt", "견적서.xlsx", "화면 캡처 12.png", "발표 자료.pptx", "설치 안내.pdf"];
                int count = 4;
                var used = new HashSet<string>();
                string[][] values = new string[count][];
                for (int row = 0; row < count; row++)
                {
                    string name;
                    do name = Pick(names, ref state); while (!used.Add(name));
                    values[row] = [name, $"{(Next(ref state) % 9000 + 100) / 100.0:0.0} MB"];
                }
                double icon = Math.Round(size * 1.1), pitch = Math.Round(size * 2.2);
                double textLeft = pad + icon + Math.Round(size * 0.8);
                double nameWidth = values.Max(row => Text(row[0]).Width);
                double sizeRight = textLeft + nameWidth + Math.Round(size * 3) + values.Max(row => Text(row[1]).Width);
                int highlighted = (int)(Next(ref state) % (uint)count);
                for (int row = 0; row < count; row++)
                {
                    double top = pad + row * pitch;
                    double y = Math.Round(top + (pitch - size * 1.33) / 2);
                    if (row == highlighted)
                        boxes.Add((new Rect(pad - Math.Round(size * 0.4), top, sizeRight - pad + Math.Round(size * 0.8), pitch), highlight, null, 3));
                    boxes.Add((new Rect(pad, Math.Round(top + (pitch - icon) / 2), icon, icon), icons[row % icons.Length], null, Math.Round(icon * 0.2)));
                    rows.Add([new Cell(values[row][0], textLeft, y, false), new Cell(values[row][1], sizeRight, y, true)]);
                }
                contentRight = sizeRight + Math.Round(size * 0.4);
                contentBottom = pad + count * pitch;
                break;
            }
            case "toolbar-mixed":
            {
                string[][] toolbars = [["저장", "복사", "Undo", "PNG", "125%"], ["열기", "핀", "OCR", "Redo", "150%"], ["자르기", "Blur", "펜", "GIF", "200%"]];
                string[] buttons = toolbars[Next(ref state) % (uint)toolbars.Length];
                string[] status =
                [
                    $"선택 영역 {1000 + Next(ref state) % 2000:#,0} x {500 + Next(ref state) % 900}",
                    $"남은 공간 {(Next(ref state) % 9000 + 100) / 10.0:0.0} GB, 확대 {100 + Next(ref state) % 4 * 25}%",
                ];
                double buttonPad = Math.Round(size * 0.6), buttonHeight = Math.Round(size * 2.0), gap = Math.Round(size * 0.6);
                double x = pad;
                var toolbar = new List<Cell>();
                double y0 = Math.Round(pad + (buttonHeight - size * 1.33) / 2);
                foreach (string label in buttons)
                {
                    double buttonWidth = Text(label).Width + buttonPad * 2;
                    boxes.Add((new Rect(Math.Round(x) + 0.5, pad + 0.5, Math.Round(buttonWidth), buttonHeight), null, line, 3));
                    toolbar.Add(new Cell(label, x + buttonPad, y0, false));
                    x += buttonWidth + gap;
                }
                rows.Add(toolbar.ToArray());
                double statusTop = pad + buttonHeight + Math.Round(size * 1.2);
                for (int index = 0; index < status.Length; index++)
                    rows.Add([new Cell(status[index], pad, Math.Round(statusTop + index * size * 1.7), false)]);
                double separator = Math.Round(pad + buttonHeight + size * 0.6) + 0.5;
                rules.Add((new Point(pad, separator), new Point(x - gap, separator)));
                contentRight = Math.Max(x - gap, pad + status.Max(value => Text(value).Width));
                contentBottom = statusTop + (status.Length - 1) * size * 1.7 + size * 1.4;
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        int width = (int)Math.Ceiling(tight ? contentRight + pad : Math.Max(1200, contentRight + pad));
        int height = (int)Math.Ceiling(contentBottom + pad + (tight ? 0 : 120));
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, formatting);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        var lines = new List<Data.TruthLine>();
        var cells = new List<Rect>();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            foreach (var box in boxes) dc.DrawRoundedRectangle(box.Fill, box.Border, box.Box, box.Radius, box.Radius);
            foreach (var rule in rules) dc.DrawLine(line, rule.A, rule.B);
            foreach (Cell[] row in rows)
            {
                Rect lineBounds = Rect.Empty;
                foreach (Cell cell in row)
                {
                    FormattedText text = Text(cell.Text);
                    var origin = new Point(cell.RightAligned ? cell.X - text.Width : cell.X, cell.Y);
                    Rect bounds = text.BuildGeometry(origin).Bounds;
                    Assert.False(bounds.IsEmpty, "No visible glyphs: " + id);
                    Assert.True(bounds.Left >= 1 && bounds.Top >= 1 && bounds.Right <= width - 1 && bounds.Bottom <= height - 1, "Clipped truth: " + id);
                    Assert.All(cells, previous => Assert.False(bounds.IntersectsWith(previous), "Overlapping truth: " + id));
                    cells.Add(bounds);
                    lineBounds.Union(bounds);
                    dc.DrawText(text, origin);
                }
                lines.Add(new Data.TruthLine(string.Join('\t', row.Select(cell => cell.Text)), Data.Box.From(lineBounds), row.Select(cell => cell.Text).ToArray()));
            }
        }
        GlyphRun[] runs = Ocr226StrataCorpus.GlyphRuns(visual.Drawing).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, run => Assert.DoesNotContain((ushort)0, run.GlyphIndices));
        string[] uris = runs.Select(run => run.GlyphTypeface.FontUri.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (GlyphRun run in runs)
        {
            string uri = run.GlyphTypeface.FontUri.ToString();
            if (!fonts.ContainsKey(uri) && run.GlyphTypeface.FontUri.IsFile)
                fonts.Add(uri, new Data.FontEvidence(uri, Data.FileHash(run.GlyphTypeface.FontUri.LocalPath)));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        string filename = id + ".png";
        string path = DiagnosticOutputPaths.Child(root, filename);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        string[] languages = kind == "toolbar-mixed" ? ["ko-KR", "en-US"] : ["ko-KR"];
        var sample = new Data.Sample(id, $"{kind}-{scene:000}", kind, split, languages, 1.0, dark ? "dark" : "light", size, font,
            false, width, height, string.Join('\n', lines.Select(truth => truth.Text)), lines.ToArray(), uris, filename,
            Data.FileHash(path), Data.PixelHash(bitmap), "");
        return sample with { TruthSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sample, Data.Json))) };
    }

    private static string[][] TableValues(ref uint state)
    {
        string[] items = ["키보드", "마우스", "모니터", "USB 케이블", "노트북 거치대", "외장 하드", "프린터 토너", "무선 충전기"];
        string[][] headers = [["항목", "수량", "금액"], ["품목", "개수", "가격"], ["구분", "건수", "합계"]];
        var rows = new List<string[]> { headers[Next(ref state) % (uint)headers.Length] };
        var used = new HashSet<string>();
        while (rows.Count < 4)
        {
            string item = Pick(items, ref state);
            if (!used.Add(item)) continue;
            int digits = 3 + (int)(Next(ref state) % 5);
            long value = (long)(Math.Pow(10, digits - 1) + Next(ref state) % (uint)(9 * Math.Pow(10, digits - 1)));
            rows.Add([item, (1 + Next(ref state) % 24).ToString(CultureInfo.InvariantCulture), value.ToString("#,0", CultureInfo.InvariantCulture)]);
        }
        return rows.ToArray();
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state;
    }

    private static string Pick(string[] values, ref uint state) => values[Next(ref state) % (uint)values.Length];
}
